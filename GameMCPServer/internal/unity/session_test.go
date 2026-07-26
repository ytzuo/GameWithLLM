package unity

import (
	"encoding/json"
	"errors"
	"testing"
	"time"

	"GameMCPServer/internal/agent"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestJSONRPCSessionReadLoopStartsWithUnityRegistration_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	done := make(chan struct{})
	go func() {
		defer close(done)
		session.readLoop()
	}()

	select {
	case unexpected := <-conn.writes:
		t.Fatalf("server sent an unsolicited startup message: %#v", unexpected)
	case <-time.After(20 * time.Millisecond):
	}

	params, err := json.Marshal(testRegistration("game-1", "Ryan_001"))
	require.NoError(t, err)
	conn.reads <- fakeRead{msg: jsonRPCMessage{
		JSONRPC: jsonRPCVersion,
		ID:      json.RawMessage(`"register-1"`),
		Method:  methodUnityRegister,
		Params:  params,
	}}
	response := mustReceiveMessage(t, conn.writes)
	require.Nil(t, response.Error)
	assert.JSONEq(t, `{"accepted":true,"protocolVersion":2}`, string(response.Result))

	stopReadLoop(conn)
	waitForDone(t, done)
}

func TestJSONRPCSessionCompleteDoesNotBlockOnDuplicateResponse_BitsUT(t *testing.T) {
	session, _ := newTestSession(time.Second)
	defer session.cancel()
	id := json.RawMessage(`"call-1"`)
	responseChannel := make(chan jsonRPCMessage, 1)
	require.True(t, session.addPending(string(id), responseChannel))

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
	session, conn := newTestSession(time.Second)
	defer session.cancel()
	require.NoError(t, session.writeResult(json.RawMessage(`"1"`), map[string]any{"ok": true}))
	response := mustReceiveMessage(t, conn.writes)
	assert.JSONEq(t, `{"ok":true}`, string(response.Result))

	require.NoError(t, session.writeError(json.RawMessage(`"2"`), -32601, "not found"))
	response = mustReceiveMessage(t, conn.writes)
	require.NotNil(t, response.Error)
	assert.Equal(t, -32601, response.Error.Code)
}

func TestConversationErrorCode_BitsUT(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want int
	}{
		{name: "missing session", err: agent.ErrSessionNotFound, want: -32012},
		{name: "missing NPC profile", err: agent.ErrNPCProfileNotFound, want: -32013},
		{name: "permanent provider failure", err: &agent.LLMRequestError{StatusCode: 400}, want: -32021},
		{name: "temporary provider failure", err: &agent.LLMRequestError{StatusCode: 503, Temporary: true}, want: -32022},
		{name: "other failure", err: errors.New("boom"), want: -32020},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			assert.Equal(t, test.want, conversationErrorCode(test.err))
		})
	}
}
