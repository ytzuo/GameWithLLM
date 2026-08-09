package savecoord

import (
	"bytes"
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"GameMCPServer/internal/agent"

	"github.com/stretchr/testify/assert"
)

type fakeSnapshots struct{}

func (fakeSnapshots) SaveConversations(_ context.Context, request agent.ConversationSaveRequest) agent.ConversationSaveResult {
	return agent.ConversationSaveResult{OK: true, SaveID: request.SaveID, OperationID: request.OperationID, SavedAt: time.Now().UTC()}
}
func (fakeSnapshots) LoadConversations(_ context.Context, request agent.ConversationLoadRequest) agent.ConversationLoadResult {
	return agent.ConversationLoadResult{OK: true, SaveID: request.SaveID, LoadedAt: time.Now().UTC()}
}

func TestPrepareCommitAndStatus(t *testing.T) {
	coordinator := New(fakeSnapshots{}, "token")
	saveID := "3d594650-3436-4fe6-9d31-0f2e29c88f25"
	operationID := "d40ef166-40ec-4402-b928-12eb53e18d5e"
	call := func(path, body string) *httptest.ResponseRecorder {
		request := httptest.NewRequest(http.MethodPost, path, bytes.NewBufferString(body))
		request.Header.Set("Authorization", "Bearer token")
		response := httptest.NewRecorder()
		coordinator.Handle(response, request)
		return response
	}
	prepared := call("/game-saves/"+saveID+"/agent-context:prepare", `{"instanceId":"runtime-1","playerId":"player-1","operationId":"d40ef166-40ec-4402-b928-12eb53e18d5e","mode":"create"}`)
	assert.Equal(t, http.StatusOK, prepared.Code)
	assert.Contains(t, prepared.Body.String(), "prepared")
	committed := call("/game-saves/"+saveID+"/agent-context:commit", `{"instanceId":"runtime-1","playerId":"player-1","operationId":"d40ef166-40ec-4402-b928-12eb53e18d5e"}`)
	assert.Contains(t, committed.Body.String(), "committed")

	request := httptest.NewRequest(http.MethodGet, "/game-saves/"+saveID+"/agent-context:status", nil)
	request.Header.Set("Authorization", "Bearer token")
	status := httptest.NewRecorder()
	coordinator.Handle(status, request)
	assert.Contains(t, status.Body.String(), operationID)
}
