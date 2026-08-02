// Package a2a exposes the player/Agent conversation plane.
package a2a

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"GameMCPServer/internal/agent"
)

const GameContextExtensionURI = "https://gamewithllm.dev/extensions/game-context/v1"

type ConversationService interface {
	StartSessionForRuntime(context.Context, string, string, string) (*agent.Session, error)
	SubmitMessage(context.Context, string, string) (*agent.AssistantReply, error)
	SubmitMessageStream(context.Context, string, string, func(agent.AssistantStreamEvent) error) (*agent.AssistantReply, error)
	EndSession(context.Context, string) error
	ValidateSessionOwner(context.Context, string, string, string, string) error
}

type Server struct {
	service     ConversationService
	baseURL     string
	bearerToken string
	mu          sync.Mutex
	tasks       map[string]context.CancelFunc
}

func NewServer(service ConversationService, baseURL, bearerToken string) *Server {
	return &Server{service: service, baseURL: strings.TrimRight(baseURL, "/"), bearerToken: bearerToken, tasks: make(map[string]context.CancelFunc)}
}

type rpcRequest struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params"`
}
type rpcResponse struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Result  any             `json:"result,omitempty"`
	Error   *rpcError       `json:"error,omitempty"`
}
type rpcError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}
type messageParams struct {
	Message Message `json:"message"`
}
type cancelParams struct {
	ID string `json:"id"`
}

type Message struct {
	MessageID string         `json:"messageId,omitempty"`
	ContextID string         `json:"contextId,omitempty"`
	TaskID    string         `json:"taskId,omitempty"`
	Role      string         `json:"role"`
	Parts     []Part         `json:"parts"`
	Metadata  map[string]any `json:"metadata,omitempty"`
}
type Part struct {
	Kind string `json:"kind"`
	Text string `json:"text,omitempty"`
}
type GameContext struct {
	InstanceID string `json:"instanceId"`
	PlayerID   string `json:"playerId"`
	AgentID    string `json:"agentId"`
	SceneID    string `json:"sceneId,omitempty"`
}
type Task struct {
	ID        string     `json:"id"`
	ContextID string     `json:"contextId,omitempty"`
	Status    TaskStatus `json:"status"`
	Artifacts []Artifact `json:"artifacts,omitempty"`
}
type TaskStatus struct {
	State     string   `json:"state"`
	Message   *Message `json:"message,omitempty"`
	Timestamp string   `json:"timestamp"`
}
type Artifact struct {
	ArtifactID string `json:"artifactId"`
	Name       string `json:"name,omitempty"`
	Parts      []Part `json:"parts"`
}

func (s *Server) HandleAgentCard(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"name":        "Game NPC Agent Service",
		"description": "Routes game conversations to NPC profiles and Unity MCP runtimes.",
		"url":         s.baseURL + "/a2a", "version": "1.0.0", "protocolVersion": "0.3.0",
		"preferredTransport":   "JSONRPC",
		"additionalInterfaces": []map[string]any{{"url": s.baseURL + "/a2a", "transport": "JSONRPC"}},
		"capabilities":         map[string]any{"streaming": true},
		"securitySchemes":      map[string]any{"bearerAuth": map[string]any{"type": "http", "scheme": "bearer"}},
		"security":             []map[string]any{{"bearerAuth": []string{}}},
		"defaultInputModes":    []string{"text/plain"}, "defaultOutputModes": []string{"text/plain"},
		"skills":     []map[string]any{{"id": "game-npc-conversation", "name": "Game NPC conversation", "tags": []string{"game", "npc"}}},
		"extensions": []map[string]any{{"uri": GameContextExtensionURI, "required": true}},
	})
}

func (s *Server) Handle(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if s.bearerToken == "" || r.Header.Get("Authorization") != "Bearer "+s.bearerToken {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var request rpcRequest
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil || request.JSONRPC != "2.0" {
		writeRPCError(w, request.ID, -32600, "invalid A2A JSON-RPC request")
		return
	}
	switch request.Method {
	case "message/send":
		s.handleMessage(w, r, request, false)
	case "message/stream":
		s.handleMessage(w, r, request, true)
	case "tasks/cancel":
		s.handleCancel(w, request)
	default:
		writeRPCError(w, request.ID, -32601, "A2A method not found")
	}
}

func (s *Server) handleMessage(w http.ResponseWriter, r *http.Request, request rpcRequest, stream bool) {
	var params messageParams
	if json.Unmarshal(request.Params, &params) != nil {
		writeRPCError(w, request.ID, -32602, "invalid message params")
		return
	}
	gameContext, text, err := validateMessage(params.Message)
	if err != nil {
		writeRPCError(w, request.ID, -32602, err.Error())
		return
	}
	contextID := strings.TrimSpace(params.Message.ContextID)
	if contextID == "" {
		session, startErr := s.service.StartSessionForRuntime(r.Context(), gameContext.InstanceID, gameContext.PlayerID, gameContext.AgentID)
		if startErr != nil {
			writeRPCError(w, request.ID, -32001, startErr.Error())
			return
		}
		contextID = session.ID
	} else if ownerErr := s.service.ValidateSessionOwner(
		r.Context(),
		contextID,
		gameContext.InstanceID,
		gameContext.PlayerID,
		gameContext.AgentID); ownerErr != nil {
		writeRPCError(w, request.ID, -32003, "A2A context ownership mismatch")
		return
	}
	taskID := newID("task")
	taskCtx, cancel := context.WithCancel(r.Context())
	s.mu.Lock()
	s.tasks[taskID] = cancel
	s.mu.Unlock()
	defer func() { cancel(); s.mu.Lock(); delete(s.tasks, taskID); s.mu.Unlock() }()
	started := Task{ID: taskID, ContextID: contextID, Status: status("working", nil)}
	if !stream {
		reply, submitErr := s.service.SubmitMessage(taskCtx, contextID, text)
		if submitErr != nil {
			writeRPCError(w, request.ID, -32002, submitErr.Error())
			return
		}
		writeJSON(w, http.StatusOK, rpcResponse{JSONRPC: "2.0", ID: request.ID, Result: completedTask(started, reply.Text)})
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	flusher, ok := w.(http.Flusher)
	if !ok {
		writeRPCError(w, request.ID, -32603, "streaming unavailable")
		return
	}
	_ = sendSSE(w, flusher, rpcResponse{JSONRPC: "2.0", ID: request.ID, Result: started})
	reply, submitErr := s.service.SubmitMessageStream(taskCtx, contextID, text, func(event agent.AssistantStreamEvent) error {
		artifact := Artifact{ArtifactID: taskID + "-text", Parts: []Part{{Kind: "text", Text: event.Text}}}
		update := map[string]any{"kind": "artifact-update", "taskId": taskID, "contextId": contextID, "artifact": artifact, "append": !event.Reset}
		return sendSSE(w, flusher, rpcResponse{JSONRPC: "2.0", ID: request.ID, Result: update})
	})
	if submitErr != nil {
		state := "failed"
		if errors.Is(submitErr, context.Canceled) {
			state = "cancelled"
		}
		update := map[string]any{"kind": "status-update", "taskId": taskID, "contextId": contextID, "status": status(state, &Message{Role: "agent", Parts: []Part{{Kind: "text", Text: safeError(submitErr)}}}), "final": true}
		_ = sendSSE(w, flusher, rpcResponse{JSONRPC: "2.0", ID: request.ID, Result: update})
		return
	}
	update := map[string]any{"kind": "status-update", "taskId": taskID, "contextId": contextID, "status": status("completed", &Message{MessageID: newID("message"), ContextID: contextID, TaskID: taskID, Role: "agent", Parts: []Part{{Kind: "text", Text: reply.Text}}}), "final": true}
	_ = sendSSE(w, flusher, rpcResponse{JSONRPC: "2.0", ID: request.ID, Result: update})
	log.Printf("event=a2a_task_completed task_id=%q context_id=%q instance_id=%q agent_id=%q text_length=%d", taskID, contextID, gameContext.InstanceID, gameContext.AgentID, len([]rune(reply.Text)))
}

func (s *Server) handleCancel(w http.ResponseWriter, request rpcRequest) {
	var params cancelParams
	if json.Unmarshal(request.Params, &params) != nil || strings.TrimSpace(params.ID) == "" {
		writeRPCError(w, request.ID, -32602, "task id is required")
		return
	}
	s.mu.Lock()
	cancel, found := s.tasks[params.ID]
	s.mu.Unlock()
	if !found {
		writeRPCError(w, request.ID, -32001, "task not found")
		return
	}
	cancel()
	writeJSON(w, http.StatusOK, rpcResponse{JSONRPC: "2.0", ID: request.ID, Result: Task{ID: params.ID, Status: status("cancelled", nil)}})
}

func validateMessage(message Message) (GameContext, string, error) {
	if message.Role != "user" {
		return GameContext{}, "", errors.New("A2A message role must be user")
	}
	var text strings.Builder
	for _, part := range message.Parts {
		if part.Kind != "text" {
			return GameContext{}, "", errors.New("only text A2A parts are supported")
		}
		text.WriteString(part.Text)
	}
	if strings.TrimSpace(text.String()) == "" {
		return GameContext{}, "", errors.New("message text is required")
	}
	raw, ok := message.Metadata[GameContextExtensionURI]
	if !ok {
		return GameContext{}, "", errors.New("game context extension is required")
	}
	encoded, _ := json.Marshal(raw)
	var gameContext GameContext
	if json.Unmarshal(encoded, &gameContext) != nil || strings.TrimSpace(gameContext.InstanceID) == "" || strings.TrimSpace(gameContext.PlayerID) == "" || strings.TrimSpace(gameContext.AgentID) == "" {
		return GameContext{}, "", errors.New("game context extension requires instanceId, playerId and agentId")
	}
	return gameContext, text.String(), nil
}

func completedTask(task Task, text string) Task {
	task.Status = status("completed", &Message{MessageID: newID("message"), ContextID: task.ContextID, TaskID: task.ID, Role: "agent", Parts: []Part{{Kind: "text", Text: text}}})
	task.Artifacts = []Artifact{{ArtifactID: task.ID + "-text", Name: "assistant-response", Parts: []Part{{Kind: "text", Text: text}}}}
	return task
}
func status(state string, message *Message) TaskStatus {
	return TaskStatus{State: state, Message: message, Timestamp: time.Now().UTC().Format(time.RFC3339Nano)}
}
func sendSSE(w http.ResponseWriter, flusher http.Flusher, value any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if _, err = fmt.Fprintf(w, "data: %s\n\n", data); err != nil {
		return err
	}
	flusher.Flush()
	return nil
}
func writeRPCError(w http.ResponseWriter, id json.RawMessage, code int, message string) {
	writeJSON(w, http.StatusOK, rpcResponse{JSONRPC: "2.0", ID: id, Error: &rpcError{Code: code, Message: message}})
}
func writeJSON(w http.ResponseWriter, code int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(value)
}
func newID(prefix string) string {
	data := make([]byte, 16)
	_, _ = rand.Read(data)
	return prefix + "-" + hex.EncodeToString(data)
}
func safeError(err error) string {
	if errors.Is(err, context.Canceled) {
		return "request cancelled"
	}
	return "agent request failed"
}
