package agent

import (
	"encoding/json"

	gametools "GameMCPServer/internal/tools"
)

type Message struct {
	Role       string     `json:"role"`
	Content    string     `json:"content,omitempty"`
	ToolCallID string     `json:"toolCallId,omitempty"`
	ToolCalls  []ToolCall `json:"toolCalls,omitempty"`
}

type ToolCall struct {
	ID        string          `json:"id"`
	Name      string          `json:"name"`
	Arguments json.RawMessage `json:"arguments"`
}

type CompletionRequest struct {
	Model       string
	Messages    []Message
	Tools       []gametools.Definition
	OnTextDelta func(string) error
}

type CompletionResult struct {
	Content   string
	ToolCalls []ToolCall
}

type ToolExecutionResult struct {
	OK        bool   `json:"ok"`
	ErrorCode string `json:"errorCode,omitempty"`
	Message   string `json:"message"`
}

type AssistantStreamEvent struct {
	Text  string
	Reset bool
}

type AssistantReply struct {
	Type      string `json:"type"`
	SessionID string `json:"sessionId"`
	NPCID     string `json:"npcId"`
	Text      string `json:"text"`
}
