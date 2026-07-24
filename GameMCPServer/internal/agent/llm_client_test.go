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

func TestNormalizeChatCompletionsEndpoint(t *testing.T) {
	tests := map[string]string{
		"https://api.deepseek.com":                   "https://api.deepseek.com/chat/completions",
		"https://api.deepseek.com/":                  "https://api.deepseek.com/chat/completions",
		"https://api.openai.com/v1":                  "https://api.openai.com/v1/chat/completions",
		"https://api.openai.com/v1/":                 "https://api.openai.com/v1/chat/completions",
		"https://api.openai.com/v1/chat/completions": "https://api.openai.com/v1/chat/completions",
		"https://example.com/custom/completions":     "https://example.com/custom/completions",
	}
	for input, expected := range tests {
		t.Run(input, func(t *testing.T) {
			assert.Equal(t, expected, normalizeChatCompletionsEndpoint(input))
		})
	}
}

func TestOpenAICompatibleClient_Complete(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		assert.Equal(t, "Bearer secret", r.Header.Get("Authorization"))
		var request map[string]any
		require.NoError(t, json.NewDecoder(r.Body).Decode(&request))
		assert.Equal(t, "model-1", request["model"])
		assert.Len(t, request["tools"], 1)
		assert.Equal(t, true, request["stream"])
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

func TestOpenAICompatibleClient_CompleteStreamsTextAndAggregatesToolCalls(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = w.Write([]byte("data: {\"choices\":[{\"delta\":{\"content\":\"我\"}}]}\n\n"))
		_, _ = w.Write([]byte("data: {\"choices\":[{\"delta\":{\"content\":\"来了\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"game_\",\"arguments\":\"{\\\"target\\\":\"}}]}}]}\n\n"))
		_, _ = w.Write([]byte("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"move\",\"arguments\":\"\\\"gate\\\"}\"}}]}}]}\n\n"))
		_, _ = w.Write([]byte("data: [DONE]\n\n"))
	}))
	defer server.Close()

	var deltas []string
	client := NewOpenAICompatibleClient(server.URL, "secret", "model-1", time.Second)
	result, err := client.Complete(context.Background(), CompletionRequest{
		Messages: []Message{{Role: "user", Content: "move"}},
		OnTextDelta: func(delta string) error {
			deltas = append(deltas, delta)
			return nil
		},
	})

	require.NoError(t, err)
	assert.Equal(t, []string{"我", "来了"}, deltas)
	assert.Equal(t, "我来了", result.Content)
	require.Len(t, result.ToolCalls, 1)
	assert.Equal(t, "call-1", result.ToolCalls[0].ID)
	assert.Equal(t, "game_move", result.ToolCalls[0].Name)
	assert.JSONEq(t, `{"target":"gate"}`, string(result.ToolCalls[0].Arguments))
}
