package unity

import (
	"encoding/json"
	"fmt"
	"log"
	"net"
	"sync"
	"time"
)

// JSON-RPC 2.0 协议的 WebSocket 会话
type jsonRPCSession struct {
	conn    net.Conn
	writeMu sync.Mutex
	mu      sync.Mutex
	// pending 用 JSON-RPC id 关联正在等待 Unity 工具执行结果的 goroutine。
	pending map[string]chan jsonRPCMessage
	timeout time.Duration
}

// readLoop 持续读取 JSON-RPC 消息并按 method 路由处理。
func (s *jsonRPCSession) readLoop() {
	for {
		payload, err := readTextFrame(s.conn)
		if err != nil {
			log.Printf("JSON-RPC websocket read failed: %v", err)
			return
		}

		var msg jsonRPCMessage
		if err := json.Unmarshal(payload, &msg); err != nil {
			log.Printf("JSON-RPC websocket invalid payload: %v", err)
			continue
		}

		if msg.Method == "" {
			// 没有 method 的消息表示这是对之前转发出去的工具调用的响应。
			s.complete(msg)
			continue
		}

		switch msg.Method {
		case "tools/list":
			if err := s.writeResult(msg.ID, map[string]any{"tools": unityClientTools()}); err != nil {
				log.Printf("JSON-RPC tools/list response failed: %v", err)
				return
			}
		case "tools/call":
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
		_ = s.writeError(msg.ID, -32000, "unity tool execution timed out")
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

	ch <- msg
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
	payload, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	return writeTextFrame(s.conn, payload)
}
