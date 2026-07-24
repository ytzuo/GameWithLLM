package unity

import (
	"bytes"
	"encoding/json"
	"fmt"
)

const (
	jsonRPCVersion       = "2.0"
	unityProtocolVersion = 1

	methodUnityRegister     = "unity.register"
	methodUnityNPCChanged   = "unity.npc.changed"
	methodUnityToolsChanged = "unity.tools.changed"
	methodUnityToolExecute  = "unity.tool.execute"
	methodUnityToolCancel   = "unity.tool.cancel"
	methodConversationStart = "conversation.start"
	methodPlayerMessage     = "player.message"
	methodConversationEnd   = "conversation.end"
	methodAssistantStatus   = "assistant.status"
	methodAssistantDelta    = "assistant.delta"
)

// jsonRPCMessage 是内部执行通道的 JSON-RPC 2.0 信封。
type jsonRPCMessage struct {
	JSONRPC string          `json:"jsonrpc,omitempty"`
	Method  string          `json:"method,omitempty"`
	ID      json.RawMessage `json:"id,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *jsonRPCError   `json:"error,omitempty"`
}

type jsonRPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

// ToolDefinition 描述 Unity 当前实际可执行的工具。
type ToolDefinition struct {
	Name        string          `json:"name"`
	Description string          `json:"description,omitempty"`
	InputSchema json.RawMessage `json:"inputSchema"`
}

// UnityRegistration 是 Unity 实例建立连接后的完整能力快照。
type UnityRegistration struct {
	ProtocolVersion int              `json:"protocolVersion"`
	InstanceID      string           `json:"instanceId"`
	Tools           []ToolDefinition `json:"tools"`
	NPCs            []string         `json:"npcs"`
}

func (r UnityRegistration) Validate() error {
	if r.ProtocolVersion != unityProtocolVersion {
		return fmt.Errorf("unsupported protocolVersion: %d", r.ProtocolVersion)
	}
	if r.InstanceID == "" {
		return fmt.Errorf("instanceId is required")
	}
	for _, tool := range r.Tools {
		if err := tool.Validate(); err != nil {
			return err
		}
	}
	for _, npcID := range r.NPCs {
		if npcID == "" {
			return fmt.Errorf("npcId cannot be empty")
		}
	}
	return nil
}

func (t ToolDefinition) Validate() error {
	if t.Name == "" {
		return fmt.Errorf("tool name is required")
	}
	if !isJSONObject(t.InputSchema) {
		return fmt.Errorf("tool %q inputSchema must be a JSON object", t.Name)
	}
	return nil
}

type UnityRegistrationResult struct {
	Accepted        bool `json:"accepted"`
	ProtocolVersion int  `json:"protocolVersion"`
}

type UnityNPCChangedParams struct {
	InstanceID string `json:"instanceId"`
	NPCID      string `json:"npcId"`
	Online     bool   `json:"online"`
}

type UnityToolsChangedParams struct {
	InstanceID string           `json:"instanceId"`
	Tools      []ToolDefinition `json:"tools"`
}

type ConversationStartParams struct {
	PlayerID string `json:"playerId"`
	NPCID    string `json:"npcId"`
}

type ConversationStartResult struct {
	SessionID string `json:"sessionId"`
	NPCID     string `json:"npcId"`
}

type PlayerMessageParams struct {
	Type      string `json:"type,omitempty"`
	SessionID string `json:"sessionId"`
	Text      string `json:"text"`
}

type ConversationEndParams struct {
	SessionID string `json:"sessionId"`
}

type AssistantStatusParams struct {
	Type      string `json:"type"`
	SessionID string `json:"sessionId"`
	Status    string `json:"status"`
}

type AssistantDeltaParams struct {
	Type      string `json:"type"`
	SessionID string `json:"sessionId"`
	Text      string `json:"text,omitempty"`
	Reset     bool   `json:"reset,omitempty"`
}

// UnityToolExecuteParams 使用对象形式的 arguments，禁止 JSON 字符串二次编码。
type UnityToolExecuteParams struct {
	NPCID     string          `json:"npcId"`
	Tool      string          `json:"tool"`
	Arguments json.RawMessage `json:"arguments"`
}

func (p UnityToolExecuteParams) Validate() error {
	if p.NPCID == "" {
		return fmt.Errorf("npcId is required")
	}
	if p.Tool == "" {
		return fmt.Errorf("tool is required")
	}
	if !isJSONObject(p.Arguments) {
		return fmt.Errorf("arguments must be a JSON object")
	}
	return nil
}

type UnityToolCancelParams struct {
	RequestID string `json:"requestId"`
}

// ToolResult 表示游戏业务结果；协议错误仍通过 JSON-RPC error 返回。
type ToolResult struct {
	OK        bool            `json:"ok"`
	ErrorCode string          `json:"errorCode,omitempty"`
	Message   string          `json:"message,omitempty"`
	Data      json.RawMessage `json:"data,omitempty"`
}

func isJSONObject(raw json.RawMessage) bool {
	raw = bytes.TrimSpace(raw)
	if len(raw) < 2 || raw[0] != '{' || raw[len(raw)-1] != '}' {
		return false
	}
	var value map[string]any
	return json.Unmarshal(raw, &value) == nil && value != nil
}
