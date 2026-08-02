package agent

import (
	"context"
	"encoding/json"
	"errors"
	"sync"
	"testing"

	gametools "GameMCPServer/internal/tools"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

type scriptedLLM struct {
	mu       sync.Mutex
	results  []*CompletionResult
	requests []CompletionRequest
}

func (l *scriptedLLM) Complete(_ context.Context, request CompletionRequest) (*CompletionResult, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.requests = append(l.requests, request)
	if len(l.results) == 0 {
		return nil, errors.New("unexpected completion")
	}
	result := l.results[0]
	l.results = l.results[1:]
	if result.Content != "" && request.OnTextDelta != nil {
		if err := request.OnTextDelta(result.Content); err != nil {
			return nil, err
		}
	}
	return result, nil
}

type fakeRuntime struct {
	executions []ToolCall
	result     *ToolExecutionResult
}

func (r *fakeRuntime) Capabilities(_ context.Context, _ string, npcID string) ([]gametools.Definition, error) {
	if npcID == "offline" {
		return nil, errors.New("offline")
	}
	return []gametools.Definition{{
		Name: "game_npc_move", InputSchema: json.RawMessage(`{
			"type":"object","properties":{"targetId":{"type":"string","enum":["landmark:gate","landmark:warehouse"]}},
			"required":["targetId"]
		}`),
	}}, nil
}

func (r *fakeRuntime) Execute(_ context.Context, _, _, name string, arguments json.RawMessage) (ToolExecutionResult, error) {
	r.executions = append(r.executions, ToolCall{Name: name, Arguments: arguments})
	if r.result != nil {
		return *r.result, nil
	}
	return ToolExecutionResult{OK: true, Message: "movement started"}, nil
}

func TestConversationService_NoToolReplyAndIsolation(t *testing.T) {
	llm := &scriptedLLM{results: []*CompletionResult{{Content: "hello"}, {Content: "second"}}}
	service := NewConversationService(llm, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1", "npc-2"), "test-model", 3)

	first, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)
	second, err := service.StartSession(context.Background(), "player", "npc-2")
	require.NoError(t, err)
	assert.NotEqual(t, first.ID, second.ID)

	reply, err := service.SubmitMessage(context.Background(), first.ID, "hi")
	require.NoError(t, err)
	assert.Equal(t, "hello", reply.Text)
	_, err = service.SubmitMessage(context.Background(), second.ID, "hi again")
	require.NoError(t, err)
	assert.Len(t, first.Messages, 3)
	assert.Len(t, second.Messages, 3)
}

func TestConversationService_ToolLoopKeepsAtomicPair(t *testing.T) {
	llm := &scriptedLLM{results: []*CompletionResult{
		{ToolCalls: []ToolCall{{ID: "call-1", Name: "game_npc_move", Arguments: json.RawMessage(`{"targetId":"landmark:gate"}`)}}},
		{Content: "我去大门。"},
	}}
	runtime := &fakeRuntime{}
	service := NewConversationService(llm, NewMemorySessionStore(), runtime, testProfileCatalog("npc-1"), "test-model", 3)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	reply, err := service.SubmitMessage(context.Background(), session.ID, "去大门")
	require.NoError(t, err)
	assert.Equal(t, "我去大门。", reply.Text)
	require.Len(t, runtime.executions, 1)
	require.Len(t, llm.requests, 2)
	lastMessages := llm.requests[1].Messages
	require.GreaterOrEqual(t, len(lastMessages), 4)
	assert.Equal(t, "assistant", lastMessages[len(lastMessages)-2].Role)
	assert.Equal(t, "tool", lastMessages[len(lastMessages)-1].Role)
	assert.Equal(t, "call-1", lastMessages[len(lastMessages)-1].ToolCallID)
}

func TestConversationService_PreservesStructuredToolResult(t *testing.T) {
	llm := &scriptedLLM{results: []*CompletionResult{
		{ToolCalls: []ToolCall{{ID: "call-1", Name: "game_npc_move", Arguments: json.RawMessage(`{"targetId":"landmark:gate"}`)}}},
		{Content: "完成。"},
	}}
	runtime := &fakeRuntime{result: &ToolExecutionResult{
		OK: true, Data: json.RawMessage(`{"target":"gate","distance":1.5}`),
	}}
	service := NewConversationService(llm, NewMemorySessionStore(), runtime, testProfileCatalog("npc-1"), "test-model", 3)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	_, err = service.SubmitMessage(context.Background(), session.ID, "去大门")
	require.NoError(t, err)
	require.Len(t, llm.requests, 2)
	messages := llm.requests[1].Messages
	require.Equal(t, "tool", messages[len(messages)-1].Role)
	assert.JSONEq(t, `{"ok":true,"data":{"target":"gate","distance":1.5}}`, messages[len(messages)-1].Content)
}

func TestConversationService_ResetsToolRoundTextBeforeFinalReply(t *testing.T) {
	llm := &scriptedLLM{results: []*CompletionResult{
		{Content: "我先看看。", ToolCalls: []ToolCall{{ID: "call-1", Name: "game_npc_move", Arguments: json.RawMessage(`{"targetId":"landmark:gate"}`)}}},
		{Content: "我已经出发了。"},
	}}
	service := NewConversationService(llm, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1", "npc-2"), "test-model", 3)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	var events []AssistantStreamEvent
	reply, err := service.SubmitMessageStream(context.Background(), session.ID, "去大门", func(event AssistantStreamEvent) error {
		events = append(events, event)
		return nil
	})

	require.NoError(t, err)
	assert.Equal(t, "我已经出发了。", reply.Text)
	assert.Equal(t, []AssistantStreamEvent{
		{Text: "我先看看。"},
		{Reset: true},
		{Text: "我已经出发了。"},
	}, events)
}

func TestConversationService_RejectsToolLoopPastLimit(t *testing.T) {
	call := &CompletionResult{ToolCalls: []ToolCall{{ID: "call", Name: "game_npc_move", Arguments: json.RawMessage(`{"targetId":"landmark:gate"}`)}}}
	llm := &scriptedLLM{results: []*CompletionResult{call, call}}
	service := NewConversationService(llm, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1"), "test-model", 1)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	_, err = service.SubmitMessage(context.Background(), session.ID, "keep moving")
	assert.ErrorContains(t, err, "maximum tool rounds")
}

type blockingLLM struct {
	started chan struct{}
}

func (l *blockingLLM) Complete(ctx context.Context, _ CompletionRequest) (*CompletionResult, error) {
	close(l.started)
	<-ctx.Done()
	return nil, ctx.Err()
}

func TestConversationService_EndSessionCancelsActiveCompletion(t *testing.T) {
	llm := &blockingLLM{started: make(chan struct{})}
	service := NewConversationService(llm, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1", "npc-2"), "test-model", 3)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	done := make(chan error, 1)
	go func() {
		_, submitErr := service.SubmitMessage(context.Background(), session.ID, "wait")
		done <- submitErr
	}()
	<-llm.started
	require.NoError(t, service.EndSession(context.Background(), session.ID))
	assert.ErrorIs(t, <-done, context.Canceled)
}

type toolThenContextLLM struct{}

func (toolThenContextLLM) Complete(ctx context.Context, request CompletionRequest) (*CompletionResult, error) {
	for _, message := range request.Messages {
		if message.Role == "tool" {
			return nil, ctx.Err()
		}
	}
	return &CompletionResult{ToolCalls: []ToolCall{{
		ID:        "call-blocking",
		Name:      "game_npc_move",
		Arguments: json.RawMessage(`{"targetId":"landmark:gate"}`),
	}}}, nil
}

type cancellationBlockingRuntime struct {
	started chan struct{}
}

func (r *cancellationBlockingRuntime) Capabilities(context.Context, string, string) ([]gametools.Definition, error) {
	return []gametools.Definition{{
		Name: "game_npc_move",
		InputSchema: json.RawMessage(
			`{"type":"object","properties":{"targetId":{"type":"string"}},"required":["targetId"]}`,
		),
	}}, nil
}

func (r *cancellationBlockingRuntime) Execute(
	ctx context.Context,
	_, _, _ string,
	_ json.RawMessage,
) (ToolExecutionResult, error) {
	close(r.started)
	<-ctx.Done()
	return ToolExecutionResult{}, ctx.Err()
}

func TestConversationService_EndSessionDuringToolDoesNotRestoreDeletedSession(t *testing.T) {
	store := NewMemorySessionStore()
	runtime := &cancellationBlockingRuntime{started: make(chan struct{})}
	service := NewConversationService(toolThenContextLLM{}, store, runtime, testProfileCatalog("npc-1"), "test-model", 3)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	submitDone := make(chan error, 1)
	go func() {
		_, submitErr := service.SubmitMessage(context.Background(), session.ID, "去大门")
		submitDone <- submitErr
	}()
	<-runtime.started

	require.NoError(t, service.EndSession(context.Background(), session.ID))
	assert.ErrorIs(t, <-submitDone, context.Canceled)
	_, err = store.Load(context.Background(), session.ID)
	assert.ErrorIs(t, err, ErrSessionNotFound)
}

func TestConversationService_SubmitRejectsClosedSession(t *testing.T) {
	store := NewMemorySessionStore()
	service := NewConversationService(
		&scriptedLLM{results: []*CompletionResult{{Content: "不应调用"}}},
		store,
		&fakeRuntime{},
		testProfileCatalog("npc-1"),
		"test-model",
		3,
	)
	session, err := service.StartSession(context.Background(), "player", "npc-1")
	require.NoError(t, err)

	session.cancelMu.Lock()
	session.closed = true
	session.cancelMu.Unlock()

	_, err = service.SubmitMessage(context.Background(), session.ID, "你好")
	assert.ErrorIs(t, err, ErrSessionNotFound)
}
