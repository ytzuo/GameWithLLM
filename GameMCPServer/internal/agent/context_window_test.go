package agent

import (
	"encoding/json"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestTrimConversationMessagesKeepsRecentAtomicTurns(t *testing.T) {
	system := Message{Role: "system", Content: "system"}
	oldTurn := []Message{
		{Role: "user", Content: "old question"},
		{Role: "assistant", Content: "old answer"},
	}
	toolTurn := []Message{
		{Role: "user", Content: "move"},
		{Role: "assistant", ToolCalls: []ToolCall{{
			ID: "call-1", Name: "game_npc_move", Arguments: json.RawMessage(`{"targetLandmark":"gate"}`),
		}}},
		{Role: "tool", ToolCallID: "call-1", Content: `{"ok":true}`},
		{Role: "assistant", Content: "moving"},
	}
	latestTurn := []Message{
		{Role: "user", Content: "status"},
		{Role: "assistant", Content: "ready"},
	}

	messages := []Message{system}
	messages = append(messages, oldTurn...)
	messages = append(messages, toolTurn...)
	messages = append(messages, latestTurn...)
	budget := messagesContextChars([]Message{system}) +
		messagesContextChars(toolTurn) + messagesContextChars(latestTurn)
	trimmed := trimConversationMessages(messages, budget)

	require.Len(t, trimmed, 1+len(toolTurn)+len(latestTurn))
	assert.Equal(t, "system", trimmed[0].Role)
	assert.Equal(t, "move", trimmed[1].Content)
	assert.Equal(t, "tool", trimmed[3].Role)
	assert.Equal(t, "status", trimmed[1+len(toolTurn)].Content)
}

func TestTrimConversationMessagesAlwaysKeepsNewestTurn(t *testing.T) {
	messages := []Message{
		{Role: "system", Content: "system"},
		{Role: "user", Content: "a message larger than the budget"},
		{Role: "assistant", Content: "answer"},
	}

	assert.Equal(t, messages, trimConversationMessages(messages, 1))
}
