package unity

import "encoding/json"

const (
	MessageTypeHello   = "hello"
	MessageTypeCommand = "command"
	MessageTypeResult  = "result"
	MessageTypePing    = "ping"
	MessageTypePong    = "pong"
)

type HelloMessage struct {
	Type         string   `json:"type"`
	ClientID     string   `json:"client_id"`
	Capabilities []string `json:"capabilities"`
}

type Command struct {
	Type      string         `json:"type"`
	CommandID string         `json:"command_id"`
	ToolName  string         `json:"tool_name"`
	NPCID     string         `json:"npc_id"`
	Arguments map[string]any `json:"arguments,omitempty"`
}

type Result struct {
	Type      string         `json:"type"`
	CommandID string         `json:"command_id"`
	ToolName  string         `json:"tool_name,omitempty"`
	OK        bool           `json:"ok"`
	ErrorCode string         `json:"error_code,omitempty"`
	Message   string         `json:"message"`
	Data      map[string]any `json:"data,omitempty"`
}

type envelope struct {
	Type      string          `json:"type"`
	CommandID string          `json:"command_id,omitempty"`
	ClientID  string          `json:"client_id,omitempty"`
	Raw       json.RawMessage `json:"-"`
}
