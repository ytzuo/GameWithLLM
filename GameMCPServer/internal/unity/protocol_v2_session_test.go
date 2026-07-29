package unity

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestSessionUnityRegister_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	params, err := json.Marshal(testRegistration("local-game-1", "Ryan_001"))
	require.NoError(t, err)

	session.handleUnityRegister(jsonRPCMessage{ID: json.RawMessage(`"register-1"`), Method: methodUnityRegister, Params: params})
	response := mustReceiveMessage(t, conn.writes)
	require.Nil(t, response.Error)
	assert.JSONEq(t, `{"accepted":true,"protocolVersion":2}`, string(response.Result))
	instanceID, resolved, ok := session.registry.ResolveNPC("Ryan_001")
	assert.True(t, ok)
	assert.Equal(t, "local-game-1", instanceID)
	assert.Same(t, session, resolved)
}

func TestToolExecutorSendsV1ObjectArgumentsAndReturnsBusinessResult_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	_, err := session.registry.Register(session, testRegistration("local-game-1", "Ryan_001"))
	require.NoError(t, err)
	executor := NewToolExecutor(session.registry, time.Second)

	done := make(chan struct{})
	var result *ToolResult
	var execErr error
	go func() {
		defer close(done)
		result, execErr = executor.Execute(context.Background(), "local-game-1", "Ryan_001", "game_npc_move", json.RawMessage(`{"targetId":"landmark:warehouse"}`))
	}()

	request := mustReceiveMessage(t, conn.writes)
	assert.Equal(t, methodUnityToolExecute, request.Method)
	assert.JSONEq(t, `{"npcId":"Ryan_001","tool":"game_npc_move","arguments":{"targetId":"landmark:warehouse"}}`, string(request.Params))
	session.complete(jsonRPCMessage{ID: request.ID, Result: json.RawMessage(`{"ok":true,"message":"NPC 已开始移动"}`)})
	waitForDone(t, done)
	require.NoError(t, execErr)
	require.NotNil(t, result)
	assert.True(t, result.OK)
}

func TestToolExecutorRejectsUnknownNPC_BitsUT(t *testing.T) {
	session, _ := newTestSession(time.Second)
	executor := NewToolExecutor(session.registry, time.Second)
	_, err := executor.Execute(context.Background(), "local-game-1", "missing", "game_npc_move", json.RawMessage(`{}`))
	require.Error(t, err)
	assert.True(t, errors.Is(err, ErrNPCOffline))
}

func TestToolExecutorSendsCancelWhenExecutionTimesOut_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	_, err := session.registry.Register(session, testRegistration("local-game-1", "Ryan_001"))
	require.NoError(t, err)
	executor := NewToolExecutor(session.registry, 10*time.Millisecond)

	done := make(chan error, 1)
	go func() {
		_, execErr := executor.Execute(context.Background(), "local-game-1", "Ryan_001", "game_npc_move", json.RawMessage(`{"targetId":"landmark:warehouse"}`))
		done <- execErr
	}()

	execute := mustReceiveMessage(t, conn.writes)
	assert.Equal(t, methodUnityToolExecute, execute.Method)
	cancel := mustReceiveMessage(t, conn.writes)
	assert.Equal(t, methodUnityToolCancel, cancel.Method)
	var cancelParams UnityToolCancelParams
	require.NoError(t, json.Unmarshal(cancel.Params, &cancelParams))
	assert.Equal(t, "unity-exec-1", cancelParams.RequestID)

	select {
	case execErr := <-done:
		require.Error(t, execErr)
		assert.ErrorIs(t, execErr, context.DeadlineExceeded)
	case <-time.After(time.Second):
		t.Fatal("tool execution did not time out")
	}
}

func TestToolExecutorRejectsToolUnavailableForNPC_BitsUT(t *testing.T) {
	session, conn := newTestSession(time.Second)
	registration := testRegistration("local-game-1", "Ryan_001", "Mia_002")
	registration.NPCTools["Mia_002"] = []string{}
	_, err := session.registry.Register(session, registration)
	require.NoError(t, err)
	executor := NewToolExecutor(session.registry, time.Second)

	_, err = executor.Execute(
		context.Background(),
		"local-game-1",
		"Mia_002",
		"game_npc_move",
		json.RawMessage(`{"targetId":"landmark:warehouse"}`),
	)
	require.Error(t, err)
	assert.ErrorIs(t, err, ErrToolUnavailable)
	select {
	case message := <-conn.writes:
		t.Fatalf("unavailable tool should not be sent to Unity: %+v", message)
	default:
	}
}
