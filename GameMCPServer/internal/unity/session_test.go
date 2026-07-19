package unity

import (
	"encoding/json"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestJSONRPCSessionReadLoop_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	done := make(chan struct{})
	go func() {
		defer close(done)
		session.readLoop()
	}()

	conn.reads <- fakeRead{msg: jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"list-1"`),
		Method:  "tools/list",
	}}
	listResponse := mustReceiveMessage(t, conn.writes)
	assert.JSONEq(t, `{"tools":[{"description":"使 NPC 前往指定地标 (warehouse|gate)","inputSchema":{"properties":{"targetLandmark":{"description":"目标地标名称","enum":["warehouse","gate"],"type":"string"}},"required":["targetLandmark"],"type":"object"},"name":"game_npc_move"}]}`, string(listResponse.Result))

	conn.reads <- fakeRead{msg: jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"unknown-1"`),
		Method:  "not/found",
	}}
	unknownResponse := mustReceiveMessage(t, conn.writes)
	require.NotNil(t, unknownResponse.Error)
	assert.Equal(t, -32601, unknownResponse.Error.Code)

	stopReadLoop(conn)
	waitForDone(t, done)
}

func TestJSONRPCSessionHandleToolCallValidation_BitsUT(t *testing.T) {
	tests := []struct {
		name string
		msg  jsonRPCMessage
		code int
	}{
		{
			name: "missing id",
			msg:  jsonRPCMessage{Method: "tools/call", Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move","arguments":"{}"}`)},
			code: -32600,
		},
		{
			name: "missing params",
			msg:  jsonRPCMessage{ID: json.RawMessage(`"1"`), Method: "tools/call"},
			code: -32602,
		},
		{
			name: "invalid params",
			msg:  jsonRPCMessage{ID: json.RawMessage(`"1"`), Method: "tools/call", Params: json.RawMessage(`{`)},
			code: -32602,
		},
		{
			name: "missing npc",
			msg:  jsonRPCMessage{ID: json.RawMessage(`"1"`), Method: "tools/call", Params: json.RawMessage(`{"name":"game_npc_move","arguments":"{}"}`)},
			code: -32602,
		},
		{
			name: "missing name",
			msg:  jsonRPCMessage{ID: json.RawMessage(`"1"`), Method: "tools/call", Params: json.RawMessage(`{"npcId":"Ryan_001","arguments":"{}"}`)},
			code: -32602,
		},
		{
			name: "missing arguments",
			msg:  jsonRPCMessage{ID: json.RawMessage(`"1"`), Method: "tools/call", Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move"}`)},
			code: -32602,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			session, conn := newTestSession(time.Second)
			defer session.cancel()
			session.handleToolCall(tt.msg)
			response := mustReceiveMessage(t, conn.writes)
			require.NotNil(t, response.Error)
			assert.Equal(t, tt.code, response.Error.Code)
		})
	}
}

func TestJSONRPCSessionHandleToolCallSuccess_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	defer session.cancel()
	call := jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"call-1"`),
		Method:  "tools/call",
		Params:  json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move","arguments":"{\"targetLandmark\":\"warehouse\"}"}`),
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		session.handleToolCall(call)
	}()

	forwarded := mustReceiveMessage(t, conn.writes)
	assert.Equal(t, "tools/call", forwarded.Method)
	assert.JSONEq(t, string(call.Params), string(forwarded.Params))

	result := json.RawMessage(`{"content":[{"type":"text","text":"NPC开始移动"}],"isError":false}`)
	session.complete(jsonRPCMessage{JSONRPC: jsonRPCVersion, ID: call.ID, Result: result})
	response := mustReceiveMessage(t, conn.writes)
	assert.JSONEq(t, string(result), string(response.Result))
	waitForDone(t, done)
}

func TestJSONRPCSessionForwardsUnityError_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	defer session.cancel()
	call := jsonRPCMessage{
		ID:     json.RawMessage(`"call-error"`),
		Method: "tools/call",
		Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move","arguments":"{}"}`),
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		session.handleToolCall(call)
	}()

	_ = mustReceiveMessage(t, conn.writes)
	session.complete(jsonRPCMessage{
		ID:    call.ID,
		Error: &jsonRPCError{Code: -32001, Message: "movement failed"},
	})
	response := mustReceiveMessage(t, conn.writes)
	require.NotNil(t, response.Error)
	assert.Equal(t, -32001, response.Error.Code)
	waitForDone(t, done)
}

func TestJSONRPCSessionRejectsDuplicateRequestID_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	defer session.cancel()
	call := jsonRPCMessage{
		ID:     json.RawMessage(`"duplicate"`),
		Method: "tools/call",
		Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move","arguments":"{}"}`),
	}
	require.True(t, session.addPending(string(call.ID), make(chan jsonRPCMessage, 1)))

	session.handleToolCall(call)
	response := mustReceiveMessage(t, conn.writes)
	require.NotNil(t, response.Error)
	assert.Equal(t, -32600, response.Error.Code)
}

func TestJSONRPCSessionHandleToolCallTimeout_BitsUT(t *testing.T) {
	session, conn := newTestSession(10 * time.Millisecond)
	defer session.cancel()
	call := jsonRPCMessage{
		ID:     json.RawMessage(`"call-timeout"`),
		Method: "tools/call",
		Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move","arguments":"{}"}`),
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		session.handleToolCall(call)
	}()

	_ = mustReceiveMessage(t, conn.writes)
	timeoutResponse := mustReceiveMessage(t, conn.writes)
	require.NotNil(t, timeoutResponse.Error)
	assert.Equal(t, -32000, timeoutResponse.Error.Code)
	waitForDone(t, done)
}

func TestJSONRPCSessionHandleToolCallStopsWhenConnectionCloses_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Minute)
	call := jsonRPCMessage{
		ID:     json.RawMessage(`"call-disconnect"`),
		Method: "tools/call",
		Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_npc_move","arguments":"{}"}`),
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		session.handleToolCall(call)
	}()

	_ = mustReceiveMessage(t, conn.writes)
	session.cancel()
	waitForDone(t, done)
}

func TestJSONRPCSessionRejectsUnknownTool_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	defer session.cancel()
	call := jsonRPCMessage{
		ID:     json.RawMessage(`"unknown-tool"`),
		Method: "tools/call",
		Params: json.RawMessage(`{"npcId":"Ryan_001","name":"game_does_not_exist","arguments":"{}"}`),
	}

	session.handleToolCall(call)
	response := mustReceiveMessage(t, conn.writes)
	require.NotNil(t, response.Error)
	assert.Equal(t, -32601, response.Error.Code)
}

func TestJSONRPCSessionCompleteDoesNotBlockOnDuplicateResponse_BitsUT(t *testing.T) {
	session, _ := newTestSession(time.Second)
	defer session.cancel()
	id := json.RawMessage(`"call-1"`)
	key := string(id)
	responseChannel := make(chan jsonRPCMessage, 1)
	require.True(t, session.addPending(key, responseChannel))

	session.complete(jsonRPCMessage{ID: id, Result: json.RawMessage(`{"ok":true}`)})
	secondDone := make(chan struct{})
	go func() {
		defer close(secondDone)
		session.complete(jsonRPCMessage{ID: id, Result: json.RawMessage(`{"ok":false}`)})
	}()
	waitForDone(t, secondDone)
}

func TestJSONRPCSessionPendingRegistry_BitsUT(t *testing.T) {
	session, _ := newTestSession(time.Second)
	defer session.cancel()
	first := make(chan jsonRPCMessage, 1)
	assert.True(t, session.addPending("1", first))
	assert.False(t, session.addPending("1", make(chan jsonRPCMessage, 1)))
	session.removePending("1")
	assert.True(t, session.addPending("1", make(chan jsonRPCMessage, 1)))
}

func TestJSONRPCSessionWriteHelpers_BitsUT(t *testing.T) {
	t.Run("result", func(t *testing.T) {
		session, conn := newTestSession(time.Second)
		defer session.cancel()
		require.NoError(t, session.writeResult(json.RawMessage(`"1"`), map[string]any{"ok": true}))
		response := mustReceiveMessage(t, conn.writes)
		assert.JSONEq(t, `{"ok":true}`, string(response.Result))
	})

	t.Run("error", func(t *testing.T) {
		session, conn := newTestSession(time.Second)
		defer session.cancel()
		require.NoError(t, session.writeError(json.RawMessage(`"1"`), -32601, "not found"))
		response := mustReceiveMessage(t, conn.writes)
		require.NotNil(t, response.Error)
		assert.Equal(t, -32601, response.Error.Code)
	})
}
