package agent

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
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

func TestOpenAICompatibleClient_RetriesTemporaryHTTPFailure(t *testing.T) {
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if requests.Add(1) == 1 {
			http.Error(w, "busy", http.StatusTooManyRequests)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"choices":[{"message":{"role":"assistant","content":"好了"}}]}`))
	}))
	defer server.Close()

	client := NewOpenAICompatibleClient(server.URL, "secret", "model-1", time.Second, 1)
	result, err := client.Complete(context.Background(), CompletionRequest{
		Messages: []Message{{Role: "user", Content: "hello"}},
	})

	require.NoError(t, err)
	assert.Equal(t, int32(2), requests.Load())
	assert.Equal(t, "好了", result.Content)
}

func TestOpenAICompatibleClient_DoesNotRetryPermanentHTTPFailure(t *testing.T) {
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		w.WriteHeader(http.StatusBadRequest)
	}))
	defer server.Close()

	client := NewOpenAICompatibleClient(server.URL, "secret", "model-1", time.Second, 2)
	_, err := client.Complete(context.Background(), CompletionRequest{
		Messages: []Message{{Role: "user", Content: "hello"}},
	})

	var requestError *LLMRequestError
	require.ErrorAs(t, err, &requestError)
	assert.Equal(t, http.StatusBadRequest, requestError.StatusCode)
	assert.False(t, requestError.Temporary)
	assert.Equal(t, "Bad Request", requestError.Message)
	assert.Equal(t, int32(1), requests.Load())
}

func TestOpenAICompatibleClient_DoesNotRetryAfterVisibleText(t *testing.T) {
	var requests atomic.Int32
	client := NewOpenAICompatibleClient("https://llm.test/v1", "secret", "model-1", time.Second, 2)
	client.httpClient.Transport = roundTripperFunc(func(*http.Request) (*http.Response, error) {
		requests.Add(1)
		return &http.Response{
			StatusCode: http.StatusOK,
			Header:     http.Header{"Content-Type": []string{"text/event-stream"}},
			Body: &errorAfterReader{
				data: []byte("data: {\"choices\":[{\"delta\":{\"content\":\"半句\"}}]}\n\n"),
			},
		}, nil
	})

	var deltas []string
	_, err := client.Complete(context.Background(), CompletionRequest{
		Messages: []Message{{Role: "user", Content: "hello"}},
		OnTextDelta: func(delta string) error {
			deltas = append(deltas, delta)
			return nil
		},
	})

	require.Error(t, err)
	assert.Equal(t, []string{"半句"}, deltas)
	assert.Equal(t, int32(1), requests.Load())
}

type roundTripperFunc func(*http.Request) (*http.Response, error)

func (f roundTripperFunc) RoundTrip(request *http.Request) (*http.Response, error) {
	return f(request)
}

type errorAfterReader struct {
	data []byte
}

func (r *errorAfterReader) Read(buffer []byte) (int, error) {
	if len(r.data) == 0 {
		return 0, errors.New("stream interrupted")
	}
	read := copy(buffer, r.data)
	r.data = r.data[read:]
	return read, nil
}

func (r *errorAfterReader) Close() error {
	return nil
}

var _ io.ReadCloser = (*errorAfterReader)(nil)
