package unity

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"sync"
	"time"
)

type jsonRPCConnection interface {
	Read(context.Context, *jsonRPCMessage) error
	Write(context.Context, jsonRPCMessage) error
}

// JSON-RPC 2.0 协议的 WebSocket 会话
type jsonRPCSession struct {
	ctx     context.Context
	cancel  context.CancelFunc
	conn    jsonRPCConnection
	tools   *ToolRegistry
	writeMu sync.Mutex
	mu      sync.Mutex
	// pending 用 JSON-RPC id 关联正在等待 Unity 工具执行结果的 goroutine。
	pending map[string]chan jsonRPCMessage
	timeout time.Duration
}

func newJSONRPCSession(ctx context.Context, cancel context.CancelFunc, conn jsonRPCConnection, tools *ToolRegistry, timeout time.Duration) *jsonRPCSession {
	return &jsonRPCSession{
		ctx:     ctx,
		cancel:  cancel,
		conn:    conn,
		tools:   tools,
		pending: make(map[string]chan jsonRPCMessage),
		timeout: timeout,
	}
}

// readLoop 持续读取 JSON-RPC 消息并按 method 路由处理。
func (s *jsonRPCSession) readLoop() {
	defer s.cancel()

	// 连接建立后向 Unity 请求工具列表（阻塞直到收到响应或超时），
	// 确保在开始处理其它请求之前工具已同步。
	s.requestToolsFromUnity()

	for {
		var msg jsonRPCMessage
		if err := s.conn.Read(s.ctx, &msg); err != nil {
			log.Printf("event=jsonrpc_read_stopped error=%q", err)
			return
		}

		if msg.Method == "" {
			// 没有 method 的消息表示这是对之前转发出去的工具调用的响应。
			s.complete(msg)
			continue
		}

		switch msg.Method {
		case "tools/list":
			log.Printf("event=jsonrpc_request_received method=%q id=%s", msg.Method, logID(msg.ID))
			if err := s.writeResult(msg.ID, map[string]any{"tools": s.tools.List()}); err != nil {
				log.Printf("JSON-RPC tools/list response failed: %v", err)
				return
			}
		case "tools/call":
			log.Printf("event=jsonrpc_request_received method=%q id=%s", msg.Method, logID(msg.ID))
			go s.handleToolCall(msg)
		default:
			if err := s.writeError(msg.ID, -32601, fmt.Sprintf("method not found: %s", msg.Method)); err != nil {
				log.Printf("JSON-RPC error response failed: %v", err)
				return
			}
		}
	}
}

// handleToolCall 校验工具调用参数，转发给 Unity，并等待同 id 结果。
func (s *jsonRPCSession) handleToolCall(msg jsonRPCMessage) {
	startedAt := time.Now()
	if len(msg.ID) == 0 {
		_ = s.writeError(msg.ID, -32600, "tools/call requires id")
		return
	}

	var params jsonRPCToolCallParams
	if len(msg.Params) == 0 {
		_ = s.writeError(msg.ID, -32602, "tools/call params are required")
		return
	}
	if err := json.Unmarshal(msg.Params, &params); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid tools/call params: %v", err))
		return
	}
	if params.NPCID == "" {
		_ = s.writeError(msg.ID, -32602, "npcId is required")
		return
	}
	if params.Name == "" {
		_ = s.writeError(msg.ID, -32602, "tool name is required")
		return
	}
	if !s.tools.Exists(params.Name) {
		_ = s.writeError(msg.ID, -32601, fmt.Sprintf("tool not found: %s", params.Name))
		return
	}
	if len(params.Arguments) == 0 {
		_ = s.writeError(msg.ID, -32602, "tool arguments are required")
		return
	}

	// 服务端在这里扮演 JSON-RPC 中转站：记录请求 id，把调用通过同一条连接发给 Unity，
	// 然后等待 Unity 用相同 id 返回执行结果。
	key := string(msg.ID)
	ch := make(chan jsonRPCMessage, 1)
	if !s.addPending(key, ch) {
		_ = s.writeError(msg.ID, -32600, "duplicate request id")
		return
	}
	defer s.removePending(key)
	log.Printf("event=tool_call_forwarding id=%s npc_id=%q tool=%q timeout_ms=%d", logID(msg.ID), params.NPCID, params.Name, s.timeout.Milliseconds())

	request := jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		Method:  "tools/call",
		ID:      msg.ID,
		Params:  msg.Params,
	}
	if err := s.writeMessage(request); err != nil {
		_ = s.writeError(msg.ID, -32603, fmt.Sprintf("forward tools/call failed: %v", err))
		return
	}

	timer := time.NewTimer(s.timeout)
	defer timer.Stop()

	select {
	case response := <-ch:
		log.Printf("event=tool_call_completed id=%s npc_id=%q tool=%q outcome=%q duration_ms=%d", logID(msg.ID), params.NPCID, params.Name, toolOutcome(response), time.Since(startedAt).Milliseconds())
		if response.Error != nil {
			_ = s.writeMessage(jsonRPCMessage{
				JSONRPC: jsonRPCVersion,
				ID:      msg.ID,
				Error:   response.Error,
			})
			return
		}
		_ = s.writeMessage(jsonRPCMessage{
			JSONRPC: jsonRPCVersion,
			ID:      msg.ID,
			Result:  response.Result,
		})
	case <-timer.C:
		log.Printf("event=tool_call_completed id=%s npc_id=%q tool=%q outcome=timeout duration_ms=%d", logID(msg.ID), params.NPCID, params.Name, time.Since(startedAt).Milliseconds())
		_ = s.writeError(msg.ID, -32000, "unity tool execution timed out")
	case <-s.ctx.Done():
		return
	}
}

// complete 将 Unity 返回的 JSON-RPC 结果投递给对应等待者。
func (s *jsonRPCSession) complete(msg jsonRPCMessage) {
	if len(msg.ID) == 0 {
		log.Print("JSON-RPC response ignored: missing id")
		return
	}
	key := string(msg.ID)

	s.mu.Lock()
	ch := s.pending[key]
	s.mu.Unlock()
	if ch == nil {
		log.Printf("JSON-RPC response has no pending request: id=%s", key)
		return
	}

	select {
	case ch <- msg:
	default:
		log.Printf("JSON-RPC duplicate response ignored: id=%s", key)
	}
}

// addPending 注册一个正在等待结果的 JSON-RPC 请求。
func (s *jsonRPCSession) addPending(key string, ch chan jsonRPCMessage) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.pending[key] != nil {
		return false
	}
	s.pending[key] = ch
	return true
}

// removePending 移除已经完成或超时的等待请求。
func (s *jsonRPCSession) removePending(key string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.pending, key)
}

// writeResult 写回 JSON-RPC result 响应。
func (s *jsonRPCSession) writeResult(id json.RawMessage, result any) error {
	payload, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return s.writeMessage(jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      id,
		Result:  payload,
	})
}

// writeError 写回 JSON-RPC error 响应。
func (s *jsonRPCSession) writeError(id json.RawMessage, code int, message string) error {
	return s.writeMessage(jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      id,
		Error:   &jsonRPCError{Code: code, Message: message},
	})
}

// writeMessage 序列化 JSON-RPC 消息并写入 WebSocket 文本帧。
func (s *jsonRPCSession) writeMessage(msg jsonRPCMessage) error {
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	return s.conn.Write(s.ctx, msg)
}

// requestToolsFromUnity 向 Unity 发送 tools/list 请求并阻塞等待响应，
// 用 Unity 返回的工具列表替换当前的种子工具。
// 此方法在 readLoop 主循环启动前同步调用，确保在任何 tools/call 到达前工具已同步。
func (s *jsonRPCSession) requestToolsFromUnity() {
	startedAt := time.Now()
	reqID := json.RawMessage(`"tools_sync_1"`)

	req := jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		Method:  "tools/list",
		ID:      reqID,
	}

	if err := s.writeMessage(req); err != nil {
		log.Printf("failed to send tools/list request to Unity: %v", err)
		return
	}

	var resp jsonRPCMessage
	if err := s.conn.Read(s.ctx, &resp); err != nil {
		log.Printf("failed to read tools/list response from Unity: %v", err)
		return
	}

	var parsed struct {
		Tools []map[string]any `json:"tools"`
	}
	if err := json.Unmarshal(resp.Result, &parsed); err != nil {
		log.Printf("failed to parse tools/list response from Unity: %v", err)
		return
	}
	s.tools.ReplaceAll(parsed.Tools)
	log.Printf("event=tools_sync_completed tool_count=%d duration_ms=%d", len(parsed.Tools), time.Since(startedAt).Milliseconds())
}

// logID 只输出请求标识，不输出 params/result，避免业务内容进入控制台日志。
func logID(id json.RawMessage) string {
	if len(id) == 0 {
		return "\"<missing>\""
	}
	return string(id)
}

func toolOutcome(response jsonRPCMessage) string {
	if response.Error != nil {
		return "unity_error"
	}
	return "success"
}
