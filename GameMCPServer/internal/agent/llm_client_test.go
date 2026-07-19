package agent

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	gametools "GameMCPServer/internal/tools"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestOpenAICompatibleClient_Complete(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		assert.Equal(t, "Bearer secret", r.Header.Get("Authorization"))
		var request map[string]any
		require.NoError(t, json.NewDecoder(r.Body).Decode(&request))
		assert.Equal(t, "model-1", request["model"])
		assert.Len(t, request["tools"], 1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"move","arguments":"{\"target\":\"gate\"}"}}]}}]}`))
	}))
	defer server.Close()

	client := NewOpenAICompatibleClient(server.URL, "secret", "model-1", time.Second)
	result, err := client.Complete(context.Background(), CompletionRequest{
		Messages: []Message{{Role: "user", Content: "move"}},
		Tools:    []gametools.Definition{{Name: "move", InputSchema: json.RawMessage(`{"type":"object"}`)}},
	})
	require.NoError(t, err)
	require.Len(t, result.ToolCalls, 1)
	assert.Equal(t, "move", result.ToolCalls[0].Name)
	assert.JSONEq(t, `{"target":"gate"}`, string(result.ToolCalls[0].Arguments))
}
