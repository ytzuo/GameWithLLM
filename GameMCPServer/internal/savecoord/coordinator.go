// Package savecoord coordinates Unity world saves with Agent-owned snapshots.
package savecoord

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"sync"
	"time"

	"GameMCPServer/internal/agent"
)

type SnapshotService interface {
	SaveConversations(context.Context, agent.ConversationSaveRequest) agent.ConversationSaveResult
	LoadConversations(context.Context, agent.ConversationLoadRequest) agent.ConversationLoadResult
}
type request struct {
	InstanceID  string   `json:"instanceId"`
	PlayerID    string   `json:"playerId"`
	OperationID string   `json:"operationId,omitempty"`
	Mode        string   `json:"mode,omitempty"`
	NPCIDs      []string `json:"npcIds,omitempty"`
}
type operation struct {
	SaveID      string    `json:"saveId"`
	OperationID string    `json:"operationId"`
	State       string    `json:"state"`
	UpdatedAt   time.Time `json:"updatedAt"`
	Result      any       `json:"result,omitempty"`
}
type Coordinator struct {
	service     SnapshotService
	bearerToken string
	mu          sync.RWMutex
	operations  map[string]operation
}

func New(service SnapshotService, bearerToken string) *Coordinator {
	return &Coordinator{service: service, bearerToken: bearerToken, operations: make(map[string]operation)}
}

func (c *Coordinator) Handle(w http.ResponseWriter, r *http.Request) {
	if c.bearerToken == "" || r.Header.Get("Authorization") != "Bearer "+c.bearerToken {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	path := strings.TrimPrefix(r.URL.Path, "/game-saves/")
	saveID, action, ok := strings.Cut(path, "/agent-context:")
	if !ok || !agent.IsCanonicalUUID(saveID) {
		http.NotFound(w, r)
		return
	}
	if action == "status" {
		c.mu.RLock()
		value, found := c.operations[saveID]
		c.mu.RUnlock()
		if !found {
			write(w, http.StatusNotFound, map[string]any{"ok": false, "errorCode": "OPERATION_NOT_FOUND"})
			return
		}
		write(w, http.StatusOK, value)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var input request
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil {
		write(w, http.StatusBadRequest, map[string]any{"ok": false, "errorCode": "INVALID_REQUEST"})
		return
	}
	switch action {
	case "prepare":
		saveRequest := agent.ConversationSaveRequest{InstanceID: input.InstanceID, PlayerID: input.PlayerID, SaveID: saveID, OperationID: input.OperationID, Mode: input.Mode}
		if err := agent.ValidateSaveConversationRequest(saveRequest); err != nil {
			write(w, http.StatusBadRequest, map[string]any{"ok": false, "errorCode": "INVALID_REQUEST", "message": err.Error()})
			return
		}
		result := c.service.SaveConversations(r.Context(), saveRequest)
		state := "prepare-failed"
		if result.OK {
			state = "prepared"
		}
		value := operation{SaveID: saveID, OperationID: input.OperationID, State: state, UpdatedAt: time.Now().UTC(), Result: result}
		c.mu.Lock()
		c.operations[saveID] = value
		c.mu.Unlock()
		write(w, http.StatusOK, value)
	case "commit":
		c.mu.Lock()
		value, found := c.operations[saveID]
		if found && value.OperationID == input.OperationID && value.State == "prepared" {
			value.State = "committed"
			value.UpdatedAt = time.Now().UTC()
			c.operations[saveID] = value
		}
		c.mu.Unlock()
		if !found || value.OperationID != input.OperationID || value.State != "committed" {
			write(w, http.StatusConflict, map[string]any{"ok": false, "errorCode": "PREPARE_NOT_FOUND"})
			return
		}
		write(w, http.StatusOK, value)
	case "restore":
		loadRequest := agent.ConversationLoadRequest{InstanceID: input.InstanceID, PlayerID: input.PlayerID, SaveID: saveID, NPCIDs: input.NPCIDs}
		if err := agent.ValidateLoadConversationRequest(loadRequest); err != nil {
			write(w, http.StatusBadRequest, map[string]any{"ok": false, "errorCode": "INVALID_REQUEST", "message": err.Error()})
			return
		}
		result := c.service.LoadConversations(r.Context(), loadRequest)
		value := operation{SaveID: saveID, OperationID: input.OperationID, State: "restore-failed", UpdatedAt: time.Now().UTC(), Result: result}
		if result.OK {
			value.State = "restored"
		}
		c.mu.Lock()
		c.operations[saveID] = value
		c.mu.Unlock()
		write(w, http.StatusOK, value)
	default:
		http.NotFound(w, r)
	}
}
func write(w http.ResponseWriter, code int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(value)
}
