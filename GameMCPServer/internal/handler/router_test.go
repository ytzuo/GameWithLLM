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

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	wsURL := "ws" + strings.TrimPrefix(httpServer.URL, "http") + "/unity/ws"
	conn, _, err := websocket.Dial(ctx, wsURL, nil)
	require.NoError(t, err)
	defer conn.CloseNow()

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0",
		"id":      "list-1",
		"method":  "tools/list",
	}))
	var message struct {
		ID     json.RawMessage `json:"id"`
		Result json.RawMessage `json:"result"`
	}
	require.NoError(t, wsjson.Read(ctx, conn, &message))
	assert.JSONEq(t, `"list-1"`, string(message.ID))
	require.NotEmpty(t, message.Result)
}
