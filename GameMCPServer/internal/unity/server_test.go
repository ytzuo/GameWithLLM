package unity

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

func TestNewJSONRPCServer_BitsUT(t *testing.T) {
	timeout := 3 * time.Second
	server := NewJSONRPCServer(timeout)
	assert.Equal(t, timeout, server.timeout)
}

func TestJSONRPCServerHandleRoot_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/", nil)

	server.HandleRoot(recorder, request)

	assert.Equal(t, http.StatusOK, recorder.Code)
	assert.Equal(t, "Game MCP Server is running!", recorder.Body.String())
}

func TestJSONRPCServerHandleWebSocketRejectsPlainHTTP_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/ws", nil)

	server.HandleWebSocket(recorder, request)

	assert.Equal(t, http.StatusUpgradeRequired, recorder.Code)
}

func TestJSONRPCServerWebSocketIntegration_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	httpServer := httptest.NewServer(http.HandlerFunc(server.HandleWebSocket))
	defer httpServer.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	wsURL := "ws" + strings.TrimPrefix(httpServer.URL, "http")
	conn, _, err := websocket.Dial(ctx, wsURL, nil)
	require.NoError(t, err)
	defer conn.CloseNow()

	// coder/websocket 需要一个持续 Reader 来消费 Pong；先用独立连接验证
	// 服务端能处理控制帧，再用当前连接验证 JSON-RPC 消息。
	pingConn, _, err := websocket.Dial(ctx, wsURL, nil)
	require.NoError(t, err)
	pingReadCtx := pingConn.CloseRead(ctx)
	require.NoError(t, pingConn.Ping(pingReadCtx))
	require.NoError(t, pingConn.Close(websocket.StatusNormalClosure, ""))

	require.NoError(t, wsjson.Write(ctx, conn, jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"list-1"`),
		Method:  "tools/list",
	}))
	var listResponse jsonRPCMessage
	require.NoError(t, wsjson.Read(ctx, conn, &listResponse))
	require.NotEmpty(t, listResponse.Result)

	fragmentedMessage, err := json.Marshal(jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"large-unknown"`),
		Method:  strings.Repeat("x", 16_384),
	})
	require.NoError(t, err)
	writer, err := conn.Writer(ctx, websocket.MessageText)
	require.NoError(t, err)
	middle := len(fragmentedMessage) / 2
	_, err = writer.Write(fragmentedMessage[:middle])
	require.NoError(t, err)
	_, err = writer.Write(fragmentedMessage[middle:])
	require.NoError(t, err)
	require.NoError(t, writer.Close())
	var unknownResponse jsonRPCMessage
	require.NoError(t, wsjson.Read(ctx, conn, &unknownResponse))
	require.NotNil(t, unknownResponse.Error)
	assert.Equal(t, -32601, unknownResponse.Error.Code)
}

func TestJSONRPCServerRootWebSocketCompatibility_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	httpServer := httptest.NewServer(http.HandlerFunc(server.HandleRoot))
	defer httpServer.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	wsURL := "ws" + strings.TrimPrefix(httpServer.URL, "http")
	conn, _, err := websocket.Dial(ctx, wsURL, nil)
	require.NoError(t, err)
	conn.CloseNow()
}

func TestJSONRPCServerShutdownClosesActiveConnections_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	httpServer := httptest.NewServer(http.HandlerFunc(server.HandleWebSocket))
	defer httpServer.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	wsURL := "ws" + strings.TrimPrefix(httpServer.URL, "http")
	conn, _, err := websocket.Dial(ctx, wsURL, nil)
	require.NoError(t, err)
	defer conn.CloseNow()
	require.Eventually(t, func() bool {
		server.connectionsMu.Lock()
		defer server.connectionsMu.Unlock()
		return len(server.connections) == 1
	}, time.Second, time.Millisecond)

	readErr := make(chan error, 1)
	go func() {
		_, _, err := conn.Read(ctx)
		readErr <- err
	}()

	require.NoError(t, server.Shutdown(ctx))
	assert.Equal(t, websocket.StatusGoingAway, websocket.CloseStatus(<-readErr))
}
