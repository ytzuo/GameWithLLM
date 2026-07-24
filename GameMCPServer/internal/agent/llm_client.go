package agent

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"time"
)

// LLMClient 抽象一次支持工具调用和文本流的模型补全。
type LLMClient interface {
	Complete(ctx context.Context, req CompletionRequest) (*CompletionResult, error)
}

// OpenAICompatibleClient 调用 OpenAI-compatible Chat Completions，并聚合 SSE 工具调用片段。
type OpenAICompatibleClient struct {
	endpoint   string
	apiKey     string
	model      string
	httpClient *http.Client
	maxRetries int
}

// NewOpenAICompatibleClient 创建模型客户端；可选 retryLimits 的首个值覆盖默认重试次数。
func NewOpenAICompatibleClient(endpoint, apiKey, model string, timeout time.Duration, retryLimits ...int) *OpenAICompatibleClient {
	endpoint = normalizeChatCompletionsEndpoint(endpoint)
	if timeout <= 0 {
		timeout = 60 * time.Second
	}
	maxRetries := 2
	if len(retryLimits) > 0 {
		maxRetries = retryLimits[0]
	}
	if maxRetries < 0 {
		maxRetries = 0
	}
	return &OpenAICompatibleClient{
		endpoint:   endpoint,
		apiKey:     apiKey,
		model:      model,
		httpClient: &http.Client{Timeout: timeout},
		maxRetries: maxRetries,
	}
}

// normalizeChatCompletionsEndpoint 接受供应商根地址、/v1 或完整端点，并只补齐已知缺失路径。
func normalizeChatCompletionsEndpoint(endpoint string) string {
	endpoint = strings.TrimSpace(endpoint)
	parsed, err := url.Parse(endpoint)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return strings.TrimRight(endpoint, "/")
	}

	path := strings.TrimRight(parsed.Path, "/")
	switch path {
	case "":
		parsed.Path = "/chat/completions"
	case "/v1":
		parsed.Path = "/v1/chat/completions"
	default:
		parsed.Path = path
	}
	return parsed.String()
}

// Complete 转换内部消息、执行安全重试，并返回聚合后的文本与工具调用。
func (c *OpenAICompatibleClient) Complete(ctx context.Context, request CompletionRequest) (*CompletionResult, error) {
	if c.endpoint == "" {
		return nil, fmt.Errorf("LLM_API_URL is not configured")
	}
	if c.apiKey == "" {
		return nil, fmt.Errorf("LLM_API_KEY is not configured")
	}

	model := request.Model
	if model == "" {
		model = c.model
	}
	payload := openAIRequest{Model: model, Stream: true}
	for _, message := range request.Messages {
		converted := openAIMessage{Role: message.Role, Content: message.Content, ToolCallID: message.ToolCallID}
		for _, call := range message.ToolCalls {
			converted.ToolCalls = append(converted.ToolCalls, openAIToolCall{
				ID:       call.ID,
				Type:     "function",
				Function: openAIFunctionCall{Name: call.Name, Arguments: string(call.Arguments)},
			})
		}
		payload.Messages = append(payload.Messages, converted)
	}
	for _, tool := range request.Tools {
		var parameters any
		if err := json.Unmarshal(tool.InputSchema, &parameters); err != nil {
			return nil, fmt.Errorf("invalid tool schema %q: %w", tool.Name, err)
		}
		payload.Tools = append(payload.Tools, openAITool{
			Type:     "function",
			Function: openAIToolDefinition{Name: tool.Name, Description: tool.Description, Parameters: parameters},
		})
	}

	body, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}
	// 每次尝试都创建新 HTTP 请求；请求体字节可安全复用。
	for attempt := 0; ; attempt++ {
		deliveredText := false
		onTextDelta := request.OnTextDelta
		if onTextDelta != nil {
			onTextDelta = func(delta string) error {
				if delta != "" {
					deliveredText = true
				}
				return request.OnTextDelta(delta)
			}
		}

		result, requestErr := c.completeOnce(ctx, body, onTextDelta)
		if requestErr == nil {
			return result, nil
		}
		// 已向 UI 输出文本后禁止重放整次请求，否则玩家会看到重复的开头。
		if deliveredText || attempt >= c.maxRetries || !IsTemporaryLLMError(requestErr) {
			return nil, requestErr
		}

		timer := time.NewTimer(retryDelay(attempt, requestErr))
		select {
		case <-ctx.Done():
			if !timer.Stop() {
				<-timer.C
			}
			return nil, ctx.Err()
		case <-timer.C:
		}
	}
}

func (c *OpenAICompatibleClient) completeOnce(
	ctx context.Context,
	body []byte,
	onTextDelta func(string) error,
) (*CompletionResult, error) {
	httpRequest, err := http.NewRequestWithContext(ctx, http.MethodPost, c.endpoint, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	httpRequest.Header.Set("Content-Type", "application/json")
	httpRequest.Header.Set("Authorization", "Bearer "+c.apiKey)

	response, err := c.httpClient.Do(httpRequest)
	if err != nil {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return nil, &LLMRequestError{
			Message:   "LLM request failed",
			Temporary: true,
			Cause:     err,
		}
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		responseBody, readErr := io.ReadAll(io.LimitReader(response.Body, 4<<20))
		if readErr != nil {
			return nil, &LLMRequestError{
				StatusCode: response.StatusCode,
				Message:    "failed to read error response",
				Temporary:  response.StatusCode == http.StatusTooManyRequests || response.StatusCode >= 500,
				RetryAfter: parseRetryAfter(response.Header.Get("Retry-After")),
				Cause:      readErr,
			}
		}
		message := compactBody(responseBody)
		if message == "" {
			message = http.StatusText(response.StatusCode)
		}
		return nil, &LLMRequestError{
			StatusCode: response.StatusCode,
			Message:    message,
			Temporary:  response.StatusCode == http.StatusTooManyRequests || response.StatusCode >= 500,
			RetryAfter: parseRetryAfter(response.Header.Get("Retry-After")),
		}
	}
	if strings.Contains(strings.ToLower(response.Header.Get("Content-Type")), "text/event-stream") {
		return decodeOpenAIEventStream(response.Body, onTextDelta)
	}
	responseBody, err := io.ReadAll(io.LimitReader(response.Body, 4<<20))
	if err != nil {
		return nil, &LLMRequestError{Message: "read LLM response", Temporary: true, Cause: err}
	}

	var decoded openAIResponse
	if err := json.Unmarshal(responseBody, &decoded); err != nil {
		return nil, fmt.Errorf("decode LLM response: %w", err)
	}
	if len(decoded.Choices) == 0 {
		return nil, fmt.Errorf("LLM response contains no choices")
	}
	message := decoded.Choices[0].Message
	result := &CompletionResult{Content: message.Content}
	for _, call := range message.ToolCalls {
		arguments := json.RawMessage(call.Function.Arguments)
		if len(arguments) == 0 {
			arguments = json.RawMessage(`{}`)
		}
		result.ToolCalls = append(result.ToolCalls, ToolCall{ID: call.ID, Name: call.Function.Name, Arguments: arguments})
	}
	return result, nil
}

// retryDelay 优先采用 Retry-After，否则使用有上限的指数退避。
func retryDelay(attempt int, err error) time.Duration {
	var requestError *LLMRequestError
	if errors.As(err, &requestError) && requestError.RetryAfter > 0 {
		if requestError.RetryAfter > 5*time.Second {
			return 5 * time.Second
		}
		return requestError.RetryAfter
	}
	delay := 250 * time.Millisecond * time.Duration(1<<min(attempt, 4))
	return min(delay, 5*time.Second)
}

// parseRetryAfter 同时支持秒数和 HTTP 日期两种标准格式。
func parseRetryAfter(value string) time.Duration {
	value = strings.TrimSpace(value)
	if value == "" {
		return 0
	}
	if seconds, err := strconv.Atoi(value); err == nil {
		if seconds <= 0 {
			return 0
		}
		return time.Duration(seconds) * time.Second
	}
	if retryAt, err := http.ParseTime(value); err == nil {
		return time.Until(retryAt)
	}
	return 0
}

// decodeOpenAIEventStream 解析 SSE data 事件，并按 index 拼接被拆分的工具调用字段。
func decodeOpenAIEventStream(reader io.Reader, onTextDelta func(string) error) (*CompletionResult, error) {
	scanner := bufio.NewScanner(reader)
	scanner.Buffer(make([]byte, 64*1024), 4<<20)

	result := &CompletionResult{}
	toolCalls := make(map[int]*openAIToolCall)
	var dataLines []string
	done := false

	processEvent := func() error {
		if len(dataLines) == 0 {
			return nil
		}
		data := strings.Join(dataLines, "\n")
		dataLines = dataLines[:0]
		if strings.TrimSpace(data) == "[DONE]" {
			done = true
			return nil
		}

		var event openAIStreamResponse
		if err := json.Unmarshal([]byte(data), &event); err != nil {
			return fmt.Errorf("decode LLM stream event: %w", err)
		}
		if event.Error != nil {
			return fmt.Errorf("LLM stream error: %s", event.Error.Message)
		}
		if len(event.Choices) == 0 {
			return nil
		}

		delta := event.Choices[0].Delta
		if delta.Content != "" {
			result.Content += delta.Content
			if onTextDelta != nil {
				if err := onTextDelta(delta.Content); err != nil {
					return fmt.Errorf("deliver LLM text delta: %w", err)
				}
			}
		}
		// 工具名称和 arguments 可能跨多个 SSE chunk 到达，必须按 index 累积。
		for _, fragment := range delta.ToolCalls {
			call := toolCalls[fragment.Index]
			if call == nil {
				call = &openAIToolCall{Type: "function"}
				toolCalls[fragment.Index] = call
			}
			call.ID += fragment.ID
			if fragment.Type != "" {
				call.Type = fragment.Type
			}
			call.Function.Name += fragment.Function.Name
			call.Function.Arguments += fragment.Function.Arguments
		}
		return nil
	}

	for scanner.Scan() {
		line := strings.TrimSuffix(scanner.Text(), "\r")
		if line == "" {
			if err := processEvent(); err != nil {
				return nil, err
			}
			if done {
				break
			}
			continue
		}
		if strings.HasPrefix(line, "data:") {
			value := strings.TrimPrefix(line, "data:")
			value = strings.TrimPrefix(value, " ")
			dataLines = append(dataLines, value)
		}
	}
	if err := scanner.Err(); err != nil {
		return nil, &LLMRequestError{Message: "read LLM stream", Temporary: true, Cause: err}
	}
	if !done {
		if err := processEvent(); err != nil {
			return nil, err
		}
	}

	indices := make([]int, 0, len(toolCalls))
	for index := range toolCalls {
		indices = append(indices, index)
	}
	sort.Ints(indices)
	for _, index := range indices {
		call := toolCalls[index]
		arguments := json.RawMessage(call.Function.Arguments)
		if len(arguments) == 0 {
			arguments = json.RawMessage(`{}`)
		}
		result.ToolCalls = append(result.ToolCalls, ToolCall{
			ID: call.ID, Name: call.Function.Name, Arguments: arguments,
		})
	}
	return result, nil
}

func compactBody(body []byte) string {
	value := strings.TrimSpace(string(body))
	if len(value) > 512 {
		return value[:512] + "..."
	}
	return value
}

type openAIRequest struct {
	Model    string          `json:"model"`
	Messages []openAIMessage `json:"messages"`
	Tools    []openAITool    `json:"tools,omitempty"`
	Stream   bool            `json:"stream"`
}

type openAIMessage struct {
	Role       string           `json:"role"`
	Content    string           `json:"content,omitempty"`
	ToolCallID string           `json:"tool_call_id,omitempty"`
	ToolCalls  []openAIToolCall `json:"tool_calls,omitempty"`
}

type openAITool struct {
	Type     string               `json:"type"`
	Function openAIToolDefinition `json:"function"`
}

type openAIToolDefinition struct {
	Name        string `json:"name"`
	Description string `json:"description,omitempty"`
	Parameters  any    `json:"parameters"`
}

type openAIToolCall struct {
	ID       string             `json:"id"`
	Type     string             `json:"type"`
	Function openAIFunctionCall `json:"function"`
}

type openAIFunctionCall struct {
	Name      string `json:"name"`
	Arguments string `json:"arguments"`
}

type openAIResponse struct {
	Choices []struct {
		Message openAIMessage `json:"message"`
	} `json:"choices"`
}

type openAIStreamResponse struct {
	Choices []struct {
		Delta struct {
			Content   string                 `json:"content"`
			ToolCalls []openAIStreamToolCall `json:"tool_calls"`
		} `json:"delta"`
	} `json:"choices"`
	Error *struct {
		Message string `json:"message"`
	} `json:"error,omitempty"`
}

type openAIStreamToolCall struct {
	Index    int                `json:"index"`
	ID       string             `json:"id"`
	Type     string             `json:"type"`
	Function openAIFunctionCall `json:"function"`
}
