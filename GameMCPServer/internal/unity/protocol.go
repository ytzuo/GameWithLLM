// Package unity 实现 Unity Gateway 的 JSON-RPC 协议、连接注册和主线程工具调度桥接。
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
	ProtocolVersion int                 `json:"protocolVersion"`
	InstanceID      string              `json:"instanceId"`
	Tools           []ToolDefinition    `json:"tools"`
	NPCs            []string            `json:"npcs"`
	NPCTools        map[string][]string `json:"npcTools"`
}

// Validate 校验协议版本、实例标识和完整能力快照。
func (r UnityRegistration) Validate() error {
	if r.ProtocolVersion != unityProtocolVersion {
		return fmt.Errorf("unsupported protocolVersion: %d", r.ProtocolVersion)
	}
	if r.InstanceID == "" {
		return fmt.Errorf("instanceId is required")
	}
	npcs := make(map[string]struct{}, len(r.NPCs))
	for _, npcID := range r.NPCs {
		if npcID == "" {
			return fmt.Errorf("npcId cannot be empty")
		}
		if _, duplicate := npcs[npcID]; duplicate {
			return fmt.Errorf("duplicate npcId %q", npcID)
		}
		npcs[npcID] = struct{}{}
	}
	if err := validateCapabilitySnapshot(r.Tools, r.NPCTools); err != nil {
		return err
	}
	for npcID := range npcs {
		if _, ok := r.NPCTools[npcID]; !ok {
			return fmt.Errorf("npcTools must include npcId %q", npcID)
		}
	}
	for npcID := range r.NPCTools {
		if _, ok := npcs[npcID]; !ok {
			return fmt.Errorf("npcTools contains unknown npcId %q", npcID)
		}
	}
	return nil
}

// Validate 确保工具名称非空且 inputSchema 是 JSON 对象。
func (t ToolDefinition) Validate() error {
	if t.Name == "" {
		return fmt.Errorf("tool name is required")
	}
	if !isJSONObject(t.InputSchema) {
		return fmt.Errorf("tool %q inputSchema must be a JSON object", t.Name)
	}
	return nil
}

// UnityRegistrationResult 确认 Unity 注册是否被接受及服务端协议版本。
type UnityRegistrationResult struct {
	Accepted        bool `json:"accepted"`
	ProtocolVersion int  `json:"protocolVersion"`
}

// UnityNPCChangedParams 描述已注册实例中单个 NPC 的上下线变化。
type UnityNPCChangedParams struct {
	InstanceID string `json:"instanceId"`
	NPCID      string `json:"npcId"`
	Online     bool   `json:"online"`
}

// UnityToolsChangedParams 携带 Unity 实例最新的完整工具能力快照。
type UnityToolsChangedParams struct {
	InstanceID string              `json:"instanceId"`
	Tools      []ToolDefinition    `json:"tools"`
	NPCTools   map[string][]string `json:"npcTools"`
}

// Validate 校验工具目录和每 NPC 工具名映射的内部一致性。
func (p UnityToolsChangedParams) Validate() error {
	if p.InstanceID == "" {
		return fmt.Errorf("instanceId is required")
	}
	return validateCapabilitySnapshot(p.Tools, p.NPCTools)
}

// ConversationStartParams 标识要建立对话的玩家和 NPC。
type ConversationStartParams struct {
	PlayerID string `json:"playerId"`
	NPCID    string `json:"npcId"`
}

// ConversationStartResult 返回新建 Session 及其绑定的 NPC。
type ConversationStartResult struct {
	SessionID string `json:"sessionId"`
	NPCID     string `json:"npcId"`
}

// PlayerMessageParams 携带玩家向已有 Session 提交的消息。
type PlayerMessageParams struct {
	Type      string `json:"type,omitempty"`
	SessionID string `json:"sessionId"`
	Text      string `json:"text"`
}

// ConversationEndParams 指定要结束并从内存删除的 Session。
type ConversationEndParams struct {
	SessionID string `json:"sessionId"`
}

// AssistantStatusParams 向 Unity 推送 thinking 等非文本状态。
type AssistantStatusParams struct {
	Type      string `json:"type"`
	SessionID string `json:"sessionId"`
	Status    string `json:"status"`
}

// AssistantDeltaParams 推送文本增量；Reset 表示撤回当前未完成草稿。
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

// Validate 确保 NPC、工具名和对象形式 arguments 均满足协议要求。
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

// UnityToolCancelParams 指定需要在 Unity 主线程侧取消的工具请求。
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

func validateCapabilitySnapshot(
	tools []ToolDefinition,
	npcTools map[string][]string,
) error {
	if npcTools == nil {
		return fmt.Errorf("npcTools is required")
	}
	toolNames := make(map[string]struct{}, len(tools))
	for _, tool := range tools {
		if err := tool.Validate(); err != nil {
			return err
		}
		if _, duplicate := toolNames[tool.Name]; duplicate {
			return fmt.Errorf("duplicate tool name %q", tool.Name)
		}
		toolNames[tool.Name] = struct{}{}
	}
	for npcID, names := range npcTools {
		if npcID == "" {
			return fmt.Errorf("npcTools contains an empty npcId")
		}
		seen := make(map[string]struct{}, len(names))
		for _, name := range names {
			if name == "" {
				return fmt.Errorf("npcTools[%q] contains an empty tool name", npcID)
			}
			if _, ok := toolNames[name]; !ok {
				return fmt.Errorf("npcTools[%q] references unknown tool %q", npcID, name)
			}
			if _, duplicate := seen[name]; duplicate {
				return fmt.Errorf("npcTools[%q] contains duplicate tool %q", npcID, name)
			}
			seen[name] = struct{}{}
		}
	}
	return nil
}
