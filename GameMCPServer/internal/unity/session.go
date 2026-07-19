package unity

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"sync"
	"sync/atomic"
	"time"

	"GameMCPServer/internal/agent"
)

type jsonRPCConnection interface {
	Read(context.Context, *jsonRPCMessage) error
	Write(context.Context, jsonRPCMessage) error
}

type jsonRPCSession struct {
	ctx             context.Context
	cancel          context.CancelFunc
	conn            jsonRPCConnection
	registry        *UnityRegistry
	conversations   agent.ConversationService
	conversationMu  sync.Mutex
	conversationIDs map[string]struct{}
	writeMu         sync.Mutex
	mu              sync.Mutex
	pending         map[string]chan jsonRPCMessage
	nextID          atomic.Uint64
}

func newJSONRPCSession(
	ctx context.Context,
	cancel context.CancelFunc,
	conn jsonRPCConnection,
	registry *UnityRegistry,
	conversationServices ...agent.ConversationService,
) *jsonRPCSession {
	var conversations agent.ConversationService
	if len(conversationServices) > 0 {
		conversations = conversationServices[0]
	}
	return &jsonRPCSession{
		ctx: ctx, cancel: cancel, conn: conn, registry: registry,
		conversations:   conversations,
		conversationIDs: make(map[string]struct{}),
		pending:         make(map[string]chan jsonRPCMessage),
	}
}

func (s *jsonRPCSession) readLoop() {
	defer s.registry.UnregisterSession(s)
	defer s.endConversations()
	defer s.cancel()

	for {
		var msg jsonRPCMessage
		if err := s.conn.Read(s.ctx, &msg); err != nil {
			log.Printf("event=jsonrpc_read_stopped error=%q", err)
			return
		}

		if msg.Method == "" {
			s.complete(msg)
			continue
		}

		log.Printf("event=jsonrpc_request_received method=%q id=%s", msg.Method, logID(msg.ID))
		switch msg.Method {
		case methodUnityRegister:
			s.handleUnityRegister(msg)
		case methodUnityNPCChanged:
			s.handleUnityNPCChanged(msg)
		case methodUnityToolsChanged:
			s.handleUnityToolsChanged(msg)
		case methodConversationStart:
			s.handleConversationStart(msg)
		case methodPlayerMessage:
			go s.handlePlayerMessage(msg)
		case methodConversationEnd:
			s.handleConversationEnd(msg)
		default:
			if err := s.writeError(msg.ID, -32601, fmt.Sprintf("method not found: %s", msg.Method)); err != nil {
				log.Printf("event=jsonrpc_error_response_failed method=%q error=%q", msg.Method, err)
				return
			}
		}
	}
}

func (s *jsonRPCSession) handleUnityRegister(msg jsonRPCMessage) {
	if len(msg.ID) == 0 {
		_ = s.writeError(msg.ID, -32600, "unity.register requires id")
		return
	}
	var registration UnityRegistration
	if err := decodeParams(msg.Params, &registration); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid unity.register params: %v", err))
		return
	}
	replaced, err := s.registry.Register(s, registration)
	if err != nil {
		_ = s.writeError(msg.ID, -32602, err.Error())
		return
	}
	instances, npcs := s.registry.Counts()
	log.Printf("event=unity_registered instance_id=%q protocol_version=%d npc_count=%d tool_count=%d replaced=%t online_instances=%d online_npcs=%d", registration.InstanceID, registration.ProtocolVersion, len(registration.NPCs), len(registration.Tools), replaced, instances, npcs)
	_ = s.writeResult(msg.ID, UnityRegistrationResult{Accepted: true, ProtocolVersion: unityProtocolVersion})
}

func (s *jsonRPCSession) handleUnityNPCChanged(msg jsonRPCMessage) {
	var change UnityNPCChangedParams
	if err := decodeParams(msg.Params, &change); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid unity.npc.changed params: %v", err))
		return
	}
	if err := s.registry.UpdateNPC(s, change); err != nil {
		_ = s.writeError(msg.ID, -32001, err.Error())
		return
	}
	_, npcs := s.registry.Counts()
	log.Printf("event=unity_npc_changed instance_id=%q npc_id=%q online=%t online_npcs=%d", change.InstanceID, change.NPCID, change.Online, npcs)
	if len(msg.ID) > 0 {
		_ = s.writeResult(msg.ID, map[string]bool{"ok": true})
	}
}

func (s *jsonRPCSession) handleUnityToolsChanged(msg jsonRPCMessage) {
	var change UnityToolsChangedParams
	if err := decodeParams(msg.Params, &change); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid unity.tools.changed params: %v", err))
		return
	}
	if err := s.registry.UpdateTools(s, change); err != nil {
		_ = s.writeError(msg.ID, -32001, err.Error())
		return
	}
	log.Printf("event=unity_tools_changed instance_id=%q tool_count=%d", change.InstanceID, len(change.Tools))
	if len(msg.ID) > 0 {
		_ = s.writeResult(msg.ID, map[string]bool{"ok": true})
	}
}

func (s *jsonRPCSession) handleConversationStart(msg jsonRPCMessage) {
	if len(msg.ID) == 0 {
		_ = s.writeError(msg.ID, -32600, "conversation.start requires id")
		return
	}
	if s.conversations == nil {
		_ = s.writeError(msg.ID, -32010, "Go Agent Host is not configured")
		return
	}
	var params ConversationStartParams
	if err := decodeParams(msg.Params, &params); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid conversation.start params: %v", err))
		return
	}
	_, owner, online := s.registry.ResolveNPC(params.NPCID)
	if !online || owner != s {
		_ = s.writeError(msg.ID, -32004, fmt.Sprintf("NPC is not registered on this Unity connection: %s", params.NPCID))
		return
	}
	session, err := s.conversations.StartSession(s.ctx, params.PlayerID, params.NPCID)
	if err != nil {
		_ = s.writeError(msg.ID, -32020, err.Error())
		return
	}
	s.conversationMu.Lock()
	s.conversationIDs[session.ID] = struct{}{}
	s.conversationMu.Unlock()
	log.Printf("event=conversation_started session_id=%q player_id=%q npc_id=%q instance_id=%q", session.ID, session.PlayerID, session.NPCID, session.UnityInstanceID)
	_ = s.writeResult(msg.ID, ConversationStartResult{SessionID: session.ID, NPCID: session.NPCID})
}

func (s *jsonRPCSession) handlePlayerMessage(msg jsonRPCMessage) {
	startedAt := time.Now()
	if len(msg.ID) == 0 {
		_ = s.writeError(msg.ID, -32600, "player.message requires id")
		return
	}
	if s.conversations == nil {
		_ = s.writeError(msg.ID, -32010, "Go Agent Host is not configured")
		return
	}
	var params PlayerMessageParams
	if err := decodeParams(msg.Params, &params); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid player.message params: %v", err))
		return
	}
	if params.SessionID == "" || params.Text == "" {
		_ = s.writeError(msg.ID, -32602, "sessionId and text are required")
		return
	}
	if !s.ownsConversation(params.SessionID) {
		_ = s.writeError(msg.ID, -32011, "conversation session is not owned by this Unity connection")
		return
	}
	_ = s.writeNotification(methodAssistantStatus, AssistantStatusParams{
		Type: "assistant.status", SessionID: params.SessionID, Status: "thinking",
	})
	log.Printf("event=player_message_received session_id=%q text_length=%d", params.SessionID, len([]rune(params.Text)))
	reply, err := s.conversations.SubmitMessage(s.ctx, params.SessionID, params.Text)
	if err != nil {
		log.Printf("event=assistant_reply_failed session_id=%q duration_ms=%d error=%q", params.SessionID, time.Since(startedAt).Milliseconds(), err)
		_ = s.writeError(msg.ID, -32020, err.Error())
		return
	}
	log.Printf("event=assistant_reply_completed session_id=%q npc_id=%q duration_ms=%d text_length=%d", reply.SessionID, reply.NPCID, time.Since(startedAt).Milliseconds(), len([]rune(reply.Text)))
	_ = s.writeResult(msg.ID, reply)
}

func (s *jsonRPCSession) handleConversationEnd(msg jsonRPCMessage) {
	var params ConversationEndParams
	if err := decodeParams(msg.Params, &params); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid conversation.end params: %v", err))
		return
	}
	if !s.removeConversation(params.SessionID) {
		if len(msg.ID) > 0 {
			_ = s.writeError(msg.ID, -32011, "conversation session is not owned by this Unity connection")
		}
		return
	}
	if s.conversations != nil {
		_ = s.conversations.EndSession(s.ctx, params.SessionID)
	}
	log.Printf("event=conversation_ended session_id=%q", params.SessionID)
	if len(msg.ID) > 0 {
		_ = s.writeResult(msg.ID, map[string]bool{"ok": true})
	}
}

func (s *jsonRPCSession) ownsConversation(sessionID string) bool {
	s.conversationMu.Lock()
	defer s.conversationMu.Unlock()
	_, ok := s.conversationIDs[sessionID]
	return ok
}

func (s *jsonRPCSession) removeConversation(sessionID string) bool {
	s.conversationMu.Lock()
	defer s.conversationMu.Unlock()
	if _, ok := s.conversationIDs[sessionID]; !ok {
		return false
	}
	delete(s.conversationIDs, sessionID)
	return true
}

func (s *jsonRPCSession) endConversations() {
	if s.conversations == nil {
		return
	}
	s.conversationMu.Lock()
	ids := make([]string, 0, len(s.conversationIDs))
	for id := range s.conversationIDs {
		ids = append(ids, id)
	}
	s.conversationIDs = make(map[string]struct{})
	s.conversationMu.Unlock()
	for _, id := range ids {
		_ = s.conversations.EndSession(context.Background(), id)
	}
}

func (s *jsonRPCSession) writeNotification(method string, params any) error {
	payload, err := json.Marshal(params)
	if err != nil {
		return err
	}
	return s.writeMessage(jsonRPCMessage{JSONRPC: jsonRPCVersion, Method: method, Params: payload})
}

func (s *jsonRPCSession) executeUnityTool(ctx context.Context, params UnityToolExecuteParams) (*ToolResult, error) {
	if err := params.Validate(); err != nil {
		return nil, err
	}
	payload, err := json.Marshal(params)
	if err != nil {
		return nil, err
	}
	id := json.RawMessage(fmt.Sprintf(`"unity-exec-%d"`, s.nextID.Add(1)))
	key := string(id)
	ch := make(chan jsonRPCMessage, 1)
	if !s.addPending(key, ch) {
		return nil, fmt.Errorf("duplicate internal request id: %s", key)
	}
	defer s.removePending(key)

	if err := s.writeMessage(jsonRPCMessage{JSONRPC: jsonRPCVersion, Method: methodUnityToolExecute, ID: id, Params: payload}); err != nil {
		return nil, err
	}

	select {
	case response := <-ch:
		if response.Error != nil {
			return nil, fmt.Errorf("Unity protocol error %d: %s", response.Error.Code, response.Error.Message)
		}
		var result ToolResult
		if err := json.Unmarshal(response.Result, &result); err != nil {
			return nil, fmt.Errorf("invalid unity.tool.execute result: %w", err)
		}
		return &result, nil
	case <-ctx.Done():
		s.sendUnityToolCancel(id)
		return nil, ctx.Err()
	case <-s.ctx.Done():
		return nil, s.ctx.Err()
	}
}

func (s *jsonRPCSession) sendUnityToolCancel(id json.RawMessage) {
	var requestID string
	if err := json.Unmarshal(id, &requestID); err != nil {
		return
	}
	payload, err := json.Marshal(UnityToolCancelParams{RequestID: requestID})
	if err != nil {
		return
	}
	if err := s.writeMessage(jsonRPCMessage{JSONRPC: jsonRPCVersion, Method: methodUnityToolCancel, Params: payload}); err != nil {
		log.Printf("event=unity_tool_cancel_failed request_id=%q error=%q", requestID, err)
	}
}
func (s *jsonRPCSession) complete(msg jsonRPCMessage) {
	if len(msg.ID) == 0 {
		log.Print("event=jsonrpc_response_ignored reason=missing_id")
		return
	}
	key := string(msg.ID)
	s.mu.Lock()
	ch := s.pending[key]
	s.mu.Unlock()
	if ch == nil {
		log.Printf("event=jsonrpc_response_ignored reason=no_pending_request id=%s", key)
		return
	}
	select {
	case ch <- msg:
	default:
		log.Printf("event=jsonrpc_response_ignored reason=duplicate id=%s", key)
	}
}

func (s *jsonRPCSession) addPending(key string, ch chan jsonRPCMessage) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.pending[key] != nil {
		return false
	}
	s.pending[key] = ch
	return true
}

func (s *jsonRPCSession) removePending(key string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.pending, key)
}

func (s *jsonRPCSession) writeResult(id json.RawMessage, result any) error {
	payload, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return s.writeMessage(jsonRPCMessage{JSONRPC: jsonRPCVersion, ID: id, Result: payload})
}

func (s *jsonRPCSession) writeError(id json.RawMessage, code int, message string) error {
	return s.writeMessage(jsonRPCMessage{JSONRPC: jsonRPCVersion, ID: id, Error: &jsonRPCError{Code: code, Message: message}})
}

func (s *jsonRPCSession) writeMessage(msg jsonRPCMessage) error {
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	return s.conn.Write(s.ctx, msg)
}

func decodeParams(raw json.RawMessage, target any) error {
	if len(raw) == 0 {
		return fmt.Errorf("params are required")
	}
	return json.Unmarshal(raw, target)
}

func logID(id json.RawMessage) string {
	if len(id) == 0 {
		return "\"<missing>\""
	}
	return string(id)
}
