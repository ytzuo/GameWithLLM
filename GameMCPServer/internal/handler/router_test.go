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

	// 消费服务端在连接建立后主动发送的 tools/list 请求。
	var toolsReq struct {
		JSONRPC string          `json:"jsonrpc"`
		ID      json.RawMessage `json:"id"`
		Method  string          `json:"method"`
	}
	require.NoError(t, wsjson.Read(ctx, conn, &toolsReq))
	assert.Equal(t, "tools/list", toolsReq.Method)
	assert.JSONEq(t, `"tools_sync_1"`, string(toolsReq.ID))

	// 回复 Unity 工具列表让 requestToolsFromUnity 完成。
	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0",
		"id":      "tools_sync_1",
		"result":  map[string]any{"tools": []map[string]any{}},
	}))

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
