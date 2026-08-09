package a2a

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"GameMCPServer/internal/agent"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

type fakeConversations struct{}

func (fakeConversations) StartSessionForRuntime(context.Context, string, string, string) (*agent.Session, error) {
	return &agent.Session{ID: "context-1"}, nil
}
func (fakeConversations) ValidateSessionOwner(context.Context, string, string, string, string) error {
	return nil
}
func (fakeConversations) SubmitMessage(context.Context, string, string) (*agent.AssistantReply, error) {
	return &agent.AssistantReply{Text: "hello"}, nil
}
func (fakeConversations) SubmitMessageStream(_ context.Context, _ string, _ string, callback func(agent.AssistantStreamEvent) error) (*agent.AssistantReply, error) {
	_ = callback(agent.AssistantStreamEvent{Text: "hel"})
	_ = callback(agent.AssistantStreamEvent{Text: "lo"})
	return &agent.AssistantReply{Text: "hello"}, nil
}
func (fakeConversations) EndSession(context.Context, string) error { return nil }

func TestAgentCardAndStreamingMessage(t *testing.T) {
	server := NewServer(fakeConversations{}, "http://agent.test", "token")
	card := httptest.NewRecorder()
	server.HandleAgentCard(card, httptest.NewRequest(http.MethodGet, "/.well-known/agent-card.json", nil))
	assert.Equal(t, http.StatusOK, card.Code)
	assert.Contains(t, card.Body.String(), GameContextExtensionURI)
	assert.Contains(t, card.Body.String(), "preferredTransport")
	assert.Contains(t, card.Body.String(), "securitySchemes")

	payload := map[string]any{
		"jsonrpc": "2.0", "id": "1", "method": "message/stream",
		"params": map[string]any{"message": map[string]any{
			"messageId": "message-1", "role": "user",
			"parts": []map[string]any{{"kind": "text", "text": "hi"}},
			"metadata": map[string]any{GameContextExtensionURI: map[string]any{
				"instanceId": "runtime-1", "playerId": "player-1", "agentId": "npc-1",
			}},
		}},
	}
	body, err := json.Marshal(payload)
	require.NoError(t, err)
	request := httptest.NewRequest(http.MethodPost, "/a2a", bytes.NewReader(body))
	request.Header.Set("Authorization", "Bearer token")
	response := httptest.NewRecorder()
	server.Handle(response, request)
	assert.Equal(t, http.StatusOK, response.Code)
	assert.Equal(t, "text/event-stream", response.Header().Get("Content-Type"))
	assert.Contains(t, response.Body.String(), "artifact-update")
	assert.Contains(t, response.Body.String(), "completed")
}

func TestA2ARejectsMissingAuthentication(t *testing.T) {
	server := NewServer(fakeConversations{}, "http://agent.test", "token")
	response := httptest.NewRecorder()
	server.Handle(response, httptest.NewRequest(http.MethodPost, "/a2a", bytes.NewReader([]byte(`{"jsonrpc":"2.0"}`))))
	assert.Equal(t, http.StatusUnauthorized, response.Code)
}
