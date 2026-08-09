package mcp

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestHTTPClient_ListAndCallUseStandardMCPMethods(t *testing.T) {
	methods := make([]string, 0)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		require.Equal(t, "Bearer token", r.Header.Get("Authorization"))
		require.Equal(t, ProtocolVersion, r.Header.Get("MCP-Protocol-Version"))
		var request RPCRequest
		require.NoError(t, json.NewDecoder(r.Body).Decode(&request))
		methods = append(methods, request.Method)
		if request.ID == "" {
			w.WriteHeader(http.StatusAccepted)
			return
		}
		var result any
		switch request.Method {
		case "initialize":
			result = map[string]any{"protocolVersion": ProtocolVersion}
		case "tools/list":
			result = map[string]any{"tools": []Tool{{Name: "game_npc_move", InputSchema: json.RawMessage(`{"type":"object"}`)}}}
		case "tools/call":
			result = CallToolResult{Content: []Content{{Type: "text", Text: "ok"}}, StructuredContent: json.RawMessage(`{"ok":true}`)}
		}
		_ = json.NewEncoder(w).Encode(map[string]any{"jsonrpc": "2.0", "id": request.ID, "result": result})
	}))
	defer server.Close()
	client, err := NewHTTPClient(Endpoint{URL: server.URL, BearerToken: "token"}, time.Second)
	require.NoError(t, err)
	tools, err := client.ListTools(context.Background())
	require.NoError(t, err)
	require.Len(t, tools, 1)
	result, err := client.CallTool(context.Background(), "game_npc_move", json.RawMessage(`{"entityId":"npc-1"}`))
	require.NoError(t, err)
	assert.False(t, result.IsError)
	assert.Equal(t, []string{"initialize", "notifications/initialized", "tools/list", "tools/call"}, methods)
}

func TestBindEntityID(t *testing.T) {
	bound, err := BindEntityID(json.RawMessage(`{"targetId":"landmark:gate"}`), "npc-1")
	require.NoError(t, err)
	assert.JSONEq(t, `{"entityId":"npc-1","targetId":"landmark:gate"}`, string(bound))
	_, err = BindEntityID(json.RawMessage(`{"entityId":"npc-2"}`), "npc-1")
	assert.ErrorContains(t, err, "does not match")
}
