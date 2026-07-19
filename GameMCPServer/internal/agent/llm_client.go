package agent

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

type LLMClient interface {
	Complete(ctx context.Context, req CompletionRequest) (*CompletionResult, error)
}

type OpenAICompatibleClient struct {
	endpoint   string
	apiKey     string
	model      string
	httpClient *http.Client
}

func NewOpenAICompatibleClient(endpoint, apiKey, model string, timeout time.Duration) *OpenAICompatibleClient {
	endpoint = strings.TrimRight(strings.TrimSpace(endpoint), "/")
	if strings.HasSuffix(endpoint, "/v1") {
		endpoint += "/chat/completions"
	}
	if timeout <= 0 {
		timeout = 60 * time.Second
	}
	return &OpenAICompatibleClient{
		endpoint:   endpoint,
		apiKey:     apiKey,
		model:      model,
		httpClient: &http.Client{Timeout: timeout},
	}
}

func (c *OpenAICompatibleClient) Complete(ctx context.Context, request CompletionRequest) (*CompletionResult, error) {
	if c.endpoint == "" {
		return nil, fmt.Errorf("LLM_API_URL is not configured")
	}
	if c.apiKey == "" {
		return nil, fmt.Errorf("LLM_API_KEY or OPENAI_API_KEY is not configured")
	}

	model := request.Model
	if model == "" {
		model = c.model
	}
	payload := openAIRequest{Model: model}
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
	httpRequest, err := http.NewRequestWithContext(ctx, http.MethodPost, c.endpoint, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	httpRequest.Header.Set("Content-Type", "application/json")
	httpRequest.Header.Set("Authorization", "Bearer "+c.apiKey)

	response, err := c.httpClient.Do(httpRequest)
	if err != nil {
		return nil, fmt.Errorf("LLM request failed: %w", err)
	}
	defer response.Body.Close()
	responseBody, err := io.ReadAll(io.LimitReader(response.Body, 4<<20))
	if err != nil {
		return nil, fmt.Errorf("read LLM response: %w", err)
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return nil, fmt.Errorf("LLM returned HTTP %d: %s", response.StatusCode, compactBody(responseBody))
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
