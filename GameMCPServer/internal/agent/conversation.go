package agent

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"strings"
	"time"

	gametools "GameMCPServer/internal/tools"
)

type Runtime interface {
	Capabilities(npcID string) (instanceID string, definitions []gametools.Definition, ok bool)
	Execute(ctx context.Context, instanceID, npcID, tool string, arguments json.RawMessage) (ToolExecutionResult, error)
}

type ConversationService interface {
	StartSession(ctx context.Context, playerID, npcID string) (*Session, error)
	SubmitMessage(ctx context.Context, sessionID, text string) (*AssistantReply, error)
	EndSession(ctx context.Context, sessionID string) error
}

type Service struct {
	llm     LLMClient
	store   SessionStore
	runtime Runtime
	policy  gametools.Policy
	model   string
}

func NewConversationService(llm LLMClient, store SessionStore, runtime Runtime, model string, maxToolRounds int) *Service {
	return &Service{llm: llm, store: store, runtime: runtime, policy: gametools.NewPolicy(maxToolRounds), model: model}
}

func (s *Service) StartSession(ctx context.Context, playerID, npcID string) (*Session, error) {
	if strings.TrimSpace(playerID) == "" {
		return nil, fmt.Errorf("playerId is required")
	}
	if strings.TrimSpace(npcID) == "" {
		return nil, fmt.Errorf("npcId is required")
	}
	instanceID, _, ok := s.runtime.Capabilities(npcID)
	if !ok {
		return nil, fmt.Errorf("NPC is not registered or offline: %s", npcID)
	}
	now := time.Now().UTC()
	session := &Session{
		ID: newSessionID(), PlayerID: playerID, NPCID: npcID, UnityInstanceID: instanceID,
		SystemPrompt: fmt.Sprintf("你是 Unity 游戏中的 NPC %s。根据玩家请求自然回复；需要改变游戏状态时使用提供的工具。", npcID),
		Model:        s.model, CreatedAt: now, LastActiveAt: now,
	}
	session.Messages = []Message{{Role: "system", Content: session.SystemPrompt}}
	if err := s.store.Save(ctx, session); err != nil {
		return nil, err
	}
	return session, nil
}

func (s *Service) SubmitMessage(ctx context.Context, sessionID, text string) (*AssistantReply, error) {
	if strings.TrimSpace(text) == "" {
		return nil, fmt.Errorf("message text is required")
	}
	session, err := s.store.Load(ctx, sessionID)
	if err != nil {
		return nil, err
	}
	session.mu.Lock()
	defer session.mu.Unlock()

	operationCtx, cancel := context.WithCancel(ctx)
	session.cancelMu.Lock()
	session.cancel = cancel
	session.cancelMu.Unlock()
	defer func() {
		cancel()
		session.cancelMu.Lock()
		session.cancel = nil
		session.cancelMu.Unlock()
		session.CurrentToolCallID = ""
	}()

	instanceID, definitions, ok := s.runtime.Capabilities(session.NPCID)
	if !ok || instanceID != session.UnityInstanceID {
		return nil, fmt.Errorf("Unity instance or NPC is offline for session %s", sessionID)
	}
	session.Messages = append(session.Messages, Message{Role: "user", Content: text})
	session.LastActiveAt = time.Now().UTC()
	if err := s.store.Save(operationCtx, session); err != nil {
		return nil, err
	}

	toolRounds := 0
	for {
		llmStartedAt := time.Now()
		log.Printf("event=llm_request_started session_id=%q npc_id=%q model=%q message_count=%d tool_count=%d tool_round=%d", session.ID, session.NPCID, session.Model, len(session.Messages), len(definitions), toolRounds)
		completion, err := s.llm.Complete(operationCtx, CompletionRequest{
			Model: session.Model, Messages: append([]Message(nil), session.Messages...), Tools: definitions,
		})
		if err != nil {
			log.Printf("event=llm_request_completed session_id=%q outcome=error duration_ms=%d error=%q", session.ID, time.Since(llmStartedAt).Milliseconds(), err)
			return nil, err
		}
		log.Printf("event=llm_request_completed session_id=%q outcome=success duration_ms=%d tool_call_count=%d text_length=%d", session.ID, time.Since(llmStartedAt).Milliseconds(), len(completion.ToolCalls), len([]rune(completion.Content)))
		if len(completion.ToolCalls) == 0 {
			content := strings.TrimSpace(completion.Content)
			if content == "" {
				content = "我暂时没有可回复的内容。"
			}
			session.Messages = append(session.Messages, Message{Role: "assistant", Content: content})
			session.LastActiveAt = time.Now().UTC()
			if err := s.store.Save(operationCtx, session); err != nil {
				return nil, err
			}
			return &AssistantReply{Type: "assistant.message", SessionID: session.ID, NPCID: session.NPCID, Text: content}, nil
		}

		if toolRounds >= s.policy.MaxToolRounds {
			return nil, fmt.Errorf("LLM exceeded maximum tool rounds (%d)", s.policy.MaxToolRounds)
		}
		toolRounds++
		session.Messages = append(session.Messages, Message{Role: "assistant", Content: completion.Content, ToolCalls: completion.ToolCalls})

		for _, call := range completion.ToolCalls {
			session.CurrentToolCallID = call.ID
			result := ToolExecutionResult{OK: false, ErrorCode: "TOOL_REJECTED"}
			if err := s.policy.Authorize(definitions, call.Name, call.Arguments); err != nil {
				result.Message = err.Error()
			} else {
				toolStartedAt := time.Now()
				log.Printf("event=agent_tool_call_started session_id=%q call_id=%q npc_id=%q tool=%q round=%d", session.ID, call.ID, session.NPCID, call.Name, toolRounds)
				executed, executeErr := s.runtime.Execute(operationCtx, session.UnityInstanceID, session.NPCID, call.Name, call.Arguments)
				if executeErr != nil {
					result.ErrorCode = "TOOL_EXECUTION_ERROR"
					result.Message = executeErr.Error()
					log.Printf("event=agent_tool_call_completed session_id=%q call_id=%q tool=%q outcome=host_error duration_ms=%d error=%q", session.ID, call.ID, call.Name, time.Since(toolStartedAt).Milliseconds(), executeErr)
				} else {
					result = executed
					log.Printf("event=agent_tool_call_completed session_id=%q call_id=%q tool=%q outcome=%q duration_ms=%d error_code=%q", session.ID, call.ID, call.Name, toolResultOutcome(result), time.Since(toolStartedAt).Milliseconds(), result.ErrorCode)
				}
			}
			encodedResult, _ := json.Marshal(result)
			session.Messages = append(session.Messages, Message{Role: "tool", ToolCallID: call.ID, Content: string(encodedResult)})
		}
		session.LastActiveAt = time.Now().UTC()
		if err := s.store.Save(operationCtx, session); err != nil {
			return nil, err
		}
	}
}

func (s *Service) EndSession(ctx context.Context, sessionID string) error {
	session, err := s.store.Load(ctx, sessionID)
	if err != nil {
		if errors.Is(err, ErrSessionNotFound) {
			return nil
		}
		return err
	}
	session.cancelMu.Lock()
	if session.cancel != nil {
		session.cancel()
	}
	session.cancelMu.Unlock()
	return s.store.Delete(ctx, sessionID)
}

func newSessionID() string {
	bytes := make([]byte, 16)
	if _, err := rand.Read(bytes); err == nil {
		return "session-" + hex.EncodeToString(bytes)
	}
	return fmt.Sprintf("session-%d", time.Now().UnixNano())
}

func toolResultOutcome(result ToolExecutionResult) string {
	if result.OK {
		return "success"
	}
	return "tool_error"
}
