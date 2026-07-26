package unity

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"sync"
	"sync/atomic"
	"time"

	"GameMCPServer/internal/agent"
)

// jsonRPCConnection 隔离具体 WebSocket 实现，便于对单连接协议循环进行测试。
type jsonRPCConnection interface {
	Read(context.Context, *jsonRPCMessage) error
	Write(context.Context, jsonRPCMessage) error
}

// jsonRPCSession 管理一条 Unity 连接的注册、对话所有权、pending 请求和串行写入。
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

// newJSONRPCSession 创建单连接状态；可选 ConversationService 供无 Agent 的协议测试复用。
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

// readLoop 是连接的唯一读循环；退出时会清理注册表、所属对话和连接 Context。
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
			// LLM 调用可能耗时，异步处理以保证读循环仍能接收工具响应和取消信号。
			go s.handlePlayerMessage(msg)
		case methodConversationEnd:
			s.handleConversationEnd(msg)
		case methodSavegameSave:
			go s.handleSavegameConversationSave(msg)
		case methodSavegameLoad:
			go s.handleSavegameConversationLoad(msg)
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
	log.Printf("event=player_message_received session_id=%q text_length=%d", params.SessionID, len([]rune(params.Text)))
	reply, err := s.conversations.SubmitMessageStream(
		s.ctx,
		params.SessionID,
		params.Text,
		func(event agent.AssistantStreamEvent) error {
			if event.Text == "" && !event.Reset {
				return nil
			}
			return s.writeNotification(methodAssistantDelta, AssistantDeltaParams{
				Type: "assistant.delta", SessionID: params.SessionID,
				Text: event.Text, Reset: event.Reset,
			})
		},
	)
	if err != nil {
		log.Printf("event=assistant_reply_failed session_id=%q duration_ms=%d error=%q", params.SessionID, time.Since(startedAt).Milliseconds(), err)
		_ = s.writeError(msg.ID, conversationErrorCode(err), err.Error())
		return
	}
	log.Printf("event=assistant_reply_completed session_id=%q npc_id=%q duration_ms=%d text_length=%d", reply.SessionID, reply.NPCID, time.Since(startedAt).Milliseconds(), len([]rune(reply.Text)))
	_ = s.writeResult(msg.ID, reply)
}

// conversationErrorCode 将内部错误稳定映射为 Unity 可据此恢复 Session 的 JSON-RPC 错误码。
func conversationErrorCode(err error) int {
	switch {
	case errors.Is(err, agent.ErrSessionNotFound):
		return -32012
	case errors.Is(err, agent.ErrNPCProfileNotFound):
		return -32013
	case agent.IsTemporaryLLMError(err):
		return -32022
	case agent.IsLLMRequestError(err):
		return -32021
	default:
		return -32020
	}
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

func (s *jsonRPCSession) handleSavegameConversationSave(msg jsonRPCMessage) {
	if len(msg.ID) == 0 {
		_ = s.writeError(msg.ID, -32600, "savegame.conversations.save requires id")
		return
	}
	if s.conversations == nil {
		_ = s.writeError(msg.ID, -32010, "Go Agent Host is not configured")
		return
	}
	var params SavegameConversationSaveParams
	if err := decodeParams(msg.Params, &params); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid savegame.conversations.save params: %v", err))
		return
	}
	if err := params.Validate(); err != nil {
		_ = s.writeError(msg.ID, -32602, err.Error())
		return
	}
	if !s.registry.OwnsInstance(s, params.InstanceID) {
		_ = s.writeError(msg.ID, -32602, "instanceId is not owned by this Unity connection")
		return
	}
	result := s.conversations.SaveConversations(s.ctx, agent.ConversationSaveRequest{
		InstanceID: params.InstanceID, PlayerID: params.PlayerID, SaveID: params.SaveID,
		OperationID: params.OperationID, Mode: params.Mode,
	})
	log.Printf("event=conversation_snapshot_save_completed save_id=%q outcome=%t context_count=%d error_code=%q", params.SaveID, result.OK, result.ContextCount, result.ErrorCode)
	_ = s.writeResult(msg.ID, result)
}

func (s *jsonRPCSession) handleSavegameConversationLoad(msg jsonRPCMessage) {
	if len(msg.ID) == 0 {
		_ = s.writeError(msg.ID, -32600, "savegame.conversations.load requires id")
		return
	}
	if s.conversations == nil {
		_ = s.writeError(msg.ID, -32010, "Go Agent Host is not configured")
		return
	}
	var params SavegameConversationLoadParams
	if err := decodeParams(msg.Params, &params); err != nil {
		_ = s.writeError(msg.ID, -32602, fmt.Sprintf("invalid savegame.conversations.load params: %v", err))
		return
	}
	if err := params.Validate(); err != nil {
		_ = s.writeError(msg.ID, -32602, err.Error())
		return
	}
	if !s.registry.OwnsInstance(s, params.InstanceID) {
		_ = s.writeError(msg.ID, -32602, "instanceId is not owned by this Unity connection")
		return
	}
	for _, npcID := range params.NPCIDs {
		_, owner, online := s.registry.ResolveNPC(npcID)
		if !online || owner != s {
			_ = s.writeError(msg.ID, -32602, fmt.Sprintf("npcId is not registered on this Unity connection: %s", npcID))
			return
		}
	}
	result := s.conversations.LoadConversations(s.ctx, agent.ConversationLoadRequest{
		InstanceID: params.InstanceID, PlayerID: params.PlayerID, SaveID: params.SaveID, NPCIDs: params.NPCIDs,
	})
	if result.OK {
		s.conversationMu.Lock()
		s.conversationIDs = make(map[string]struct{}, len(result.Contexts))
		for _, context := range result.Contexts {
			s.conversationIDs[context.SessionID] = struct{}{}
		}
		s.conversationMu.Unlock()
	}
	log.Printf("event=conversation_snapshot_load_completed save_id=%q outcome=%t context_count=%d error_code=%q", params.SaveID, result.OK, len(result.Contexts), result.ErrorCode)
	_ = s.writeResult(msg.ID, result)
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

// endConversations 在连接断开时使用独立 Context 清理该连接创建的全部内存 Session。
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
	// 容量为 1，读循环投递响应时不会被已超时或已取消的调用方阻塞。
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

// sendUnityToolCancel 以通知形式尽力告知 Unity 停止已超时的主线程工具。
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

// complete 将响应交给对应 pending 请求；未知或重复响应只记录并忽略。
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

// writeMessage 用单一发送锁保护所有 WebSocket 写入，避免帧并发交错。
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
