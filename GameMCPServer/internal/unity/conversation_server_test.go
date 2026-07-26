package unity

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"GameMCPServer/internal/agent"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

type conversationScriptedLLM struct {
	mu      sync.Mutex
	results []*agent.CompletionResult
}

func (l *conversationScriptedLLM) Complete(_ context.Context, request agent.CompletionRequest) (*agent.CompletionResult, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	result := l.results[0]
	l.results = l.results[1:]
	if result.Content != "" && request.OnTextDelta != nil {
		if err := request.OnTextDelta(result.Content); err != nil {
			return nil, err
		}
	}
	return result, nil
}

func TestJSONRPCServer_GoAgentConversationToolLoop(t *testing.T) {
	llm := &conversationScriptedLLM{results: []*agent.CompletionResult{
		{ToolCalls: []agent.ToolCall{{ID: "llm-call-1", Name: "game_npc_move", Arguments: json.RawMessage(`{"targetId":"landmark:gate"}`)}}},
		{Content: "我现在去大门。"},
	}}
	server := NewJSONRPCServerWithAgentAndArchive(2*time.Second, llm, "test-model", 3, t.TempDir())
	mux := http.NewServeMux()
	mux.HandleFunc("/unity/ws", server.HandleWebSocket)
	httpServer := httptest.NewServer(mux)
	defer httpServer.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(httpServer.URL, "http")+"/unity/ws", nil)
	require.NoError(t, err)
	defer conn.CloseNow()

	var message jsonRPCMessage

	tool := ToolDefinition{Name: "game_npc_move", InputSchema: json.RawMessage(`{
		"type":"object","properties":{"targetId":{"type":"string","enum":["landmark:warehouse","landmark:gate"]}},
		"required":["targetId"]
	}`)}
	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": "register", "method": "unity.register",
		"params": UnityRegistration{
			ProtocolVersion: 2,
			InstanceID:      "game-1",
			NPCs:            []string{"Ryan_001"},
			Tools:           []ToolDefinition{tool},
			NPCTools:        map[string][]string{"Ryan_001": {"game_npc_move"}},
		},
	}))
	require.NoError(t, wsjson.Read(ctx, conn, &message))
	assert.JSONEq(t, `"register"`, string(message.ID))

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": "start", "method": "conversation.start",
		"params": ConversationStartParams{PlayerID: "player-1", NPCID: "Ryan_001"},
	}))
	require.NoError(t, wsjson.Read(ctx, conn, &message))
	var started ConversationStartResult
	require.NoError(t, json.Unmarshal(message.Result, &started))
	require.NotEmpty(t, started.SessionID)

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": "player-message", "method": "player.message",
		"params": PlayerMessageParams{Type: "player.message", SessionID: started.SessionID, Text: "去大门"},
	}))

	require.NoError(t, wsjson.Read(ctx, conn, &message))
	assert.Equal(t, "unity.tool.execute", message.Method)
	var execute UnityToolExecuteParams
	require.NoError(t, json.Unmarshal(message.Params, &execute))
	assert.Equal(t, "Ryan_001", execute.NPCID)
	assert.JSONEq(t, `{"targetId":"landmark:gate"}`, string(execute.Arguments))
	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": json.RawMessage(message.ID),
		"result": ToolResult{OK: true, Message: "movement started"},
	}))

	require.NoError(t, wsjson.Read(ctx, conn, &message))
	assert.Equal(t, "assistant.delta", message.Method)
	var delta AssistantDeltaParams
	require.NoError(t, json.Unmarshal(message.Params, &delta))
	assert.Equal(t, started.SessionID, delta.SessionID)
	assert.Equal(t, "我现在去大门。", delta.Text)

	require.NoError(t, wsjson.Read(ctx, conn, &message))
	assert.JSONEq(t, `"player-message"`, string(message.ID))
	var reply agent.AssistantReply
	require.NoError(t, json.Unmarshal(message.Result, &reply))
	assert.Equal(t, "assistant.message", reply.Type)
	assert.Equal(t, "我现在去大门。", reply.Text)

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": "save", "method": methodSavegameSave,
		"params": SavegameConversationSaveParams{ProtocolVersion: 2, InstanceID: "game-1", PlayerID: "player-1", SaveID: "3d594650-3436-4fe6-9d31-0f2e29c88f25", OperationID: "d40ef166-40ec-4402-b928-12eb53e18d5e", Mode: "create"},
	}))
	require.NoError(t, wsjson.Read(ctx, conn, &message))
	var saved agent.ConversationSaveResult
	require.NoError(t, json.Unmarshal(message.Result, &saved))
	assert.True(t, saved.OK)
	assert.Equal(t, 1, saved.ContextCount)

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": "end", "method": methodConversationEnd,
		"params": ConversationEndParams{SessionID: started.SessionID},
	}))
	require.NoError(t, wsjson.Read(ctx, conn, &message))

	require.NoError(t, wsjson.Write(ctx, conn, map[string]any{
		"jsonrpc": "2.0", "id": "load", "method": methodSavegameLoad,
		"params": SavegameConversationLoadParams{ProtocolVersion: 2, InstanceID: "game-1", PlayerID: "player-1", SaveID: "3d594650-3436-4fe6-9d31-0f2e29c88f25", NPCIDs: []string{"Ryan_001"}},
	}))
	require.NoError(t, wsjson.Read(ctx, conn, &message))
	var loaded agent.ConversationLoadResult
	require.NoError(t, json.Unmarshal(message.Result, &loaded))
	require.True(t, loaded.OK)
	require.Len(t, loaded.Contexts, 1)
	assert.NotEqual(t, started.SessionID, loaded.Contexts[0].SessionID)
	assert.Equal(t, []agent.VisibleMessage{{Index: 0, Role: "user", Text: "去大门"}, {Index: 1, Role: "assistant", Text: "我现在去大门。"}}, loaded.Contexts[0].VisibleMessages)
}
