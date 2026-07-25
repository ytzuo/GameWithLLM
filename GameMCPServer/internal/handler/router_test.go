package handler

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestRegisterRoutesWithTimeout_BitsUT(t *testing.T) {
	mux := http.NewServeMux()
	RegisterRoutesWithTimeout(mux, time.Second)
	httpServer := httptest.NewServer(mux)
	defer httpServer.Close()

	response, err := http.Get(httpServer.URL + "/health")
	require.NoError(t, err)
	defer response.Body.Close()
	assert.Equal(t, http.StatusOK, response.StatusCode)

	removedResponse, err := http.Get(httpServer.URL + "/ws")
	require.NoError(t, err)
	defer removedResponse.Body.Close()
	assert.Equal(t, http.StatusNotFound, removedResponse.StatusCode)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(httpServer.URL, "http")+"/unity/ws", nil)
	require.NoError(t, err)
	defer conn.CloseNow()

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0",
		"id":      "register-1",
		"method":  "unity.register",
		"params": map[string]any{
			"protocolVersion": 1,
			"instanceId":      "router-test",
			"tools":           []any{},
			"npcs":            []string{"Ryan_001"},
			"npcTools":        map[string][]string{"Ryan_001": {}},
		},
	}))
	var message struct {
		ID     json.RawMessage `json:"id"`
		Result json.RawMessage `json:"result"`
	}
	require.NoError(t, wsjson.Read(ctx, conn, &message))
	assert.JSONEq(t, `"register-1"`, string(message.ID))
	assert.JSONEq(t, `{"accepted":true,"protocolVersion":1}`, string(message.Result))
}
