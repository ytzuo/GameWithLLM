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
	server := NewJSONRPCServer(3 * time.Second)
	require.NotNil(t, server.registry)
	assert.Empty(t, server.registry.ListTools())
}

func TestJSONRPCServerHandleRoot_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	recorder := httptest.NewRecorder()
	server.HandleRoot(recorder, httptest.NewRequest(http.MethodGet, "/", nil))
	assert.Equal(t, http.StatusOK, recorder.Code)
	assert.Equal(t, "Game Agent Host is running!", recorder.Body.String())
}

func TestJSONRPCServerHandleWebSocketRejectsPlainHTTP_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	recorder := httptest.NewRecorder()
	server.HandleWebSocket(recorder, httptest.NewRequest(http.MethodGet, "/unity/ws", nil))
	assert.Equal(t, http.StatusUpgradeRequired, recorder.Code)
}

func TestJSONRPCServerWebSocketV2Integration_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	httpServer := httptest.NewServer(http.HandlerFunc(server.HandleWebSocket))
	defer httpServer.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(httpServer.URL, "http"), nil)
	require.NoError(t, err)
	defer conn.CloseNow()

	registrationParams, err := json.Marshal(testRegistration("local-game-1", "Ryan_001"))
	require.NoError(t, err)
	require.NoError(t, wsjson.Write(ctx, conn, jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"register-e2e"`),
		Method:  methodUnityRegister,
		Params:  registrationParams,
	}))
	var response jsonRPCMessage
	require.NoError(t, wsjson.Read(ctx, conn, &response))
	assert.JSONEq(t, `{"accepted":true,"protocolVersion":2}`, string(response.Result))

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
	require.NoError(t, wsjson.Read(ctx, conn, &response))
	require.NotNil(t, response.Error)
	assert.Equal(t, -32601, response.Error.Code)
}

func TestJSONRPCServerShutdownClosesActiveConnections_BitsUT(t *testing.T) {
	server := NewJSONRPCServer(time.Second)
	httpServer := httptest.NewServer(http.HandlerFunc(server.HandleWebSocket))
	defer httpServer.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(httpServer.URL, "http"), nil)
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
