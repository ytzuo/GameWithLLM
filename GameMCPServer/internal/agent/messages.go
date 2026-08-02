package agent

import (
	"encoding/json"

	gametools "GameMCPServer/internal/tools"
)

// Message 是发送给 LLM 的统一对话消息，兼容普通文本、工具调用和工具结果。
type Message struct {
	Role       string     `json:"role"`
	Content    string     `json:"content,omitempty"`
	ToolCallID string     `json:"toolCallId,omitempty"`
	ToolCalls  []ToolCall `json:"toolCalls,omitempty"`
}

// ToolCall 描述模型请求执行的一次函数调用；Arguments 必须保持为 JSON 对象。
type ToolCall struct {
	ID        string          `json:"id"`
	Name      string          `json:"name"`
	Arguments json.RawMessage `json:"arguments"`
}

// CompletionRequest 汇集单次模型调用所需的历史、运行时工具和可选流式回调。
type CompletionRequest struct {
	Model       string
	Messages    []Message
	Tools       []gametools.Definition
	OnTextDelta func(string) error
}

// CompletionResult 是一次模型调用聚合后的文本和完整工具调用列表。
type CompletionResult struct {
	Content   string
	ToolCalls []ToolCall
}

// ToolExecutionResult 是写回 LLM 的游戏业务结果；结构化负载放在 Data，避免 JSON 二次编码。
type ToolExecutionResult struct {
	OK        bool            `json:"ok"`
	ErrorCode string          `json:"errorCode,omitempty"`
	Message   string          `json:"message,omitempty"`
	Data      json.RawMessage `json:"data,omitempty"`
}

// AssistantStreamEvent 表示 UI 可消费的流式事件；Reset 用于撤回工具调用前的临时草稿。
type AssistantStreamEvent struct {
	Text  string
	Reset bool
}

// AssistantReply 是一次 A2A message/send 或 message/stream 成功完成后的最终回复。
type AssistantReply struct {
	Type      string `json:"type"`
	SessionID string `json:"sessionId"`
	NPCID     string `json:"npcId"`
	Text      string `json:"text"`
}
