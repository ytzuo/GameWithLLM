package agent

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

const (
	testSaveID = "3d594650-3436-4fe6-9d31-0f2e29c88f25"
	testOpID1  = "d40ef166-40ec-4402-b928-12eb53e18d5e"
	testOpID2  = "c7798c4a-af42-4e45-b751-4bddde5bb009"
)

func TestConversationArchive_CreateIdempotentOverwriteAndExplicitLoad(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	llm := &scriptedLLM{results: []*CompletionResult{{Content: "answer"}}}
	service := NewConversationServiceWithArchive(llm, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1", "npc-2", "npc-current", "npc-other"), "model-a", 3, NewFileConversationArchive(dir))
	session, err := service.StartSession(ctx, "player-1", "npc-1")
	require.NoError(t, err)
	_, err = service.SubmitMessage(ctx, session.ID, "question")
	require.NoError(t, err)

	request := ConversationSaveRequest{InstanceID: "game-1", PlayerID: "player-1", SaveID: testSaveID, OperationID: testOpID1, Mode: "create"}
	first := service.SaveConversations(ctx, request)
	require.True(t, first.OK)
	assert.Equal(t, 1, first.ContextCount)
	second := service.SaveConversations(ctx, request)
	assert.Equal(t, first, second)

	conflict := request
	conflict.OperationID = testOpID2
	assert.Equal(t, "SAVE_ALREADY_EXISTS", service.SaveConversations(ctx, conflict).ErrorCode)
	conflict.Mode = "overwrite"
	require.True(t, service.SaveConversations(ctx, conflict).OK)

	raw, err := os.ReadFile(filepath.Join(dir, testSaveID+".json"))
	require.NoError(t, err)
	assert.NotContains(t, string(raw), "session-")
	assert.NotContains(t, string(raw), "systemPrompt")
	assert.Contains(t, string(raw), "question")

	restoredStore := NewMemorySessionStore()
	updatedProfile := testNPCProfile("npc-1")
	updatedProfile.DisplayName = "更新后的名字"
	restoredProfiles, err := NewNPCProfileCatalog([]NPCProfile{updatedProfile, testNPCProfile("npc-2")})
	require.NoError(t, err)
	restored := NewConversationServiceWithArchive(&scriptedLLM{}, restoredStore, &fakeRuntime{}, restoredProfiles, "model-b", 3, NewFileConversationArchive(dir))
	loaded := restored.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player-1", SaveID: testSaveID, NPCIDs: []string{"npc-1", "npc-2"}})
	require.True(t, loaded.OK)
	require.Len(t, loaded.Contexts, 1)
	assert.NotEqual(t, session.ID, loaded.Contexts[0].SessionID)
	assert.Equal(t, []VisibleMessage{{Index: 0, Role: "user", Text: "question"}, {Index: 1, Role: "assistant", Text: "answer"}}, loaded.Contexts[0].VisibleMessages)
	restoredSession, err := restoredStore.Load(ctx, loaded.Contexts[0].SessionID)
	require.NoError(t, err)
	assert.Equal(t, "model-b", restoredSession.Model)
	assert.Equal(t, "system", restoredSession.Messages[0].Role)
	assert.Contains(t, restoredSession.SystemPrompt, "更新后的名字")
}

func TestConversationArchive_LoadValidationDoesNotReplaceCurrentSessions(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	service := NewConversationServiceWithArchive(&scriptedLLM{}, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1", "npc-2", "npc-current", "npc-other"), "model", 3, NewFileConversationArchive(dir))
	existing, err := service.StartSession(ctx, "player-1", "npc-current")
	require.NoError(t, err)

	snapshot := conversationSnapshot{SnapshotVersion: 1, SaveID: testSaveID, OperationID: testOpID1, PlayerID: "player-1", SavedAt: existing.CreatedAt, Contexts: []persistedConversationContext{{NPCID: "npc-other", HistoryMessages: []Message{{Role: "user", Content: "saved"}}, CreatedAt: existing.CreatedAt, LastActiveAt: existing.LastActiveAt}}}
	_, err = NewFileConversationArchive(dir).Save(snapshot, "create")
	require.NoError(t, err)

	result := service.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player-1", SaveID: testSaveID, NPCIDs: []string{"npc-current"}})
	assert.Equal(t, "NPC_SET_MISMATCH", result.ErrorCode)
	_, err = service.store.Load(ctx, existing.ID)
	assert.NoError(t, err)
}

func TestConversationArchive_BusyAndResponseShapes(t *testing.T) {
	ctx := context.Background()
	service := NewConversationServiceWithArchive(&scriptedLLM{}, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("npc-1", "npc-2", "npc-current", "npc-other"), "model", 3, NewFileConversationArchive(t.TempDir()))
	session, err := service.StartSession(ctx, "player-1", "npc-1")
	require.NoError(t, err)
	session.mu.Lock()
	result := service.SaveConversations(ctx, ConversationSaveRequest{InstanceID: "game-1", PlayerID: "player-1", SaveID: testSaveID, OperationID: testOpID1, Mode: "create"})
	session.mu.Unlock()
	assert.Equal(t, "CONVERSATION_BUSY", result.ErrorCode)

	successJSON, err := json.Marshal(ConversationSaveResult{OK: true, SaveID: testSaveID, OperationID: testOpID1, ContextCount: 0, SavedAt: session.CreatedAt})
	require.NoError(t, err)
	assert.JSONEq(t, `{"ok":true,"saveId":"3d594650-3436-4fe6-9d31-0f2e29c88f25","operationId":"d40ef166-40ec-4402-b928-12eb53e18d5e","contextCount":0,"savedAt":"`+session.CreatedAt.Format("2006-01-02T15:04:05.999999999Z07:00")+`"}`, string(successJSON))
	failureJSON, err := json.Marshal(failedLoad("SAVE_NOT_FOUND", "missing"))
	require.NoError(t, err)
	assert.JSONEq(t, `{"ok":false,"errorCode":"SAVE_NOT_FOUND","message":"missing"}`, string(failureJSON))
}
