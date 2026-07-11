package unity

import "encoding/json"

const jsonRPCVersion = "2.0"

// JSON-RPC 2.0 协议的消息结构体
type jsonRPCMessage struct {
	JSONRPC string          `json:"jsonrpc,omitempty"`
	Method  string          `json:"method,omitempty"`
	ID      json.RawMessage `json:"id,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *jsonRPCError   `json:"error,omitempty"`
}

// JSON-RPC 2.0 协议的错误结构体
type jsonRPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

// JSON-RPC 2.0 协议中 tool_call 方法的参数结构体
type jsonRPCToolCallParams struct {
	NPCID string `json:"npcId"`
	Name  string `json:"name"`
	// Arguments 保持原始 JSON，因为 Unity 客户端需要拿到原始参数字符串。
	Arguments json.RawMessage `json:"arguments"`
}
