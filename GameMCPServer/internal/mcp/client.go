package mcp

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"time"
)

const maxResponseBytes = 1 << 20

// Client 是 Agent Service 访问任意 MCP 工具提供方的最小边界。
type Client interface {
	ListTools(context.Context) ([]Tool, error)
	CallTool(context.Context, string, json.RawMessage) (CallToolResult, error)
}

// HTTPClient 实现可选的外部 MCP HTTP 客户端；内部 Runtime 调用不经过 HTTP 环回。
type HTTPClient struct {
	endpoint Endpoint
	http     *http.Client
	nextID   atomic.Uint64
}

func NewHTTPClient(endpoint Endpoint, timeout time.Duration) (*HTTPClient, error) {
	if strings.TrimSpace(endpoint.URL) == "" {
		return nil, errors.New("MCP endpoint URL is required")
	}
	if timeout <= 0 {
		timeout = 60 * time.Second
	}
	return &HTTPClient{endpoint: endpoint, http: &http.Client{Timeout: timeout}}, nil
}

func (c *HTTPClient) ListTools(ctx context.Context) ([]Tool, error) {
	if err := c.initialize(ctx); err != nil {
		return nil, err
	}
	var result struct {
		Tools []Tool `json:"tools"`
	}
	if err := c.call(ctx, "tools/list", map[string]any{}, &result); err != nil {
		return nil, err
	}
	return result.Tools, nil
}

// CallTool 保证 arguments 在协议边界上保持 JSON 对象而不是二次编码字符串。
func (c *HTTPClient) CallTool(ctx context.Context, name string, arguments json.RawMessage) (CallToolResult, error) {
	if strings.TrimSpace(name) == "" {
		return CallToolResult{}, errors.New("MCP tool name is required")
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(arguments, &object); err != nil || object == nil {
		return CallToolResult{}, errors.New("MCP tool arguments must be a JSON object")
	}
	var result CallToolResult
	if err := c.call(ctx, "tools/call", map[string]any{"name": name, "arguments": object}, &result); err != nil {
		return CallToolResult{}, err
	}
	return result, nil
}

// initialize 完成 MCP 版本协商，并发送 initialized 通知。
func (c *HTTPClient) initialize(ctx context.Context) error {
	var result struct {
		ProtocolVersion string `json:"protocolVersion"`
	}
	if err := c.call(ctx, "initialize", map[string]any{
		"protocolVersion": ProtocolVersion,
		"capabilities":    map[string]any{},
		"clientInfo":      map[string]string{"name": "game-agent-service", "version": "1.0.0"},
	}, &result); err != nil {
		return err
	}
	if result.ProtocolVersion == "" {
		return errors.New("MCP initialize response omitted protocolVersion")
	}
	return c.notify(ctx, "notifications/initialized", map[string]any{})
}

// call 为请求分配 ID，并在 tools/call 被取消时尽力发送取消通知。
func (c *HTTPClient) call(ctx context.Context, method string, params any, target any) error {
	id := fmt.Sprintf("mcp-%d", c.nextID.Add(1))
	if method == "tools/call" {
		if values, ok := params.(map[string]any); ok {
			withProgress := make(map[string]any, len(values)+1)
			for key, value := range values {
				withProgress[key] = value
			}
			withProgress["_meta"] = map[string]any{"progressToken": id}
			params = withProgress
		}
	}
	request := RPCRequest{JSONRPC: "2.0", ID: id, Method: method, Params: params}
	var response RPCResponse
	if err := c.post(ctx, request, &response); err != nil {
		if method == "tools/call" && errors.Is(err, context.Canceled) {
			cancelCtx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			_ = c.notify(cancelCtx, "notifications/cancelled", map[string]any{
				"requestId": id,
				"reason":    "agent request cancelled",
			})
		}
		return err
	}
	if response.Error != nil {
		return fmt.Errorf("MCP %s failed (%d): %s", method, response.Error.Code, response.Error.Message)
	}
	if len(response.Result) == 0 {
		return fmt.Errorf("MCP %s returned no result", method)
	}
	if err := json.Unmarshal(response.Result, target); err != nil {
		return fmt.Errorf("decode MCP %s result: %w", method, err)
	}
	return nil
}

func (c *HTTPClient) notify(ctx context.Context, method string, params any) error {
	return c.post(ctx, RPCRequest{JSONRPC: "2.0", Method: method, Params: params}, nil)
}

func (c *HTTPClient) post(ctx context.Context, payload RPCRequest, response *RPCResponse) error {
	body, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.endpoint.URL, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/event-stream")
	req.Header.Set("MCP-Protocol-Version", ProtocolVersion)
	if c.endpoint.BearerToken != "" {
		req.Header.Set("Authorization", "Bearer "+c.endpoint.BearerToken)
	}
	res, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		data, _ := io.ReadAll(io.LimitReader(res.Body, 4096))
		return fmt.Errorf("MCP HTTP status %d: %s", res.StatusCode, strings.TrimSpace(string(data)))
	}
	if response == nil || res.StatusCode == http.StatusAccepted {
		return nil
	}
	decoder := json.NewDecoder(io.LimitReader(res.Body, maxResponseBytes))
	if err := decoder.Decode(response); err != nil {
		return fmt.Errorf("decode MCP response: %w", err)
	}
	return nil
}
