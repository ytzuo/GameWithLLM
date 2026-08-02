// Package agent 负责 Session、系统提示词、LLM 调用和 Unity 工具循环的编排。
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
	"sync"
	"time"

	gametools "GameMCPServer/internal/tools"
)

// Runtime 是 Agent 访问 Unity 实时能力和执行游戏工具的唯一边界。
type Runtime interface {
	Capabilities(ctx context.Context, instanceID, entityID string) ([]gametools.Definition, error)
	Execute(ctx context.Context, instanceID, entityID, tool string, arguments json.RawMessage) (ToolExecutionResult, error)
}

// ConversationService 管理 Session 生命周期，并编排 LLM 与 Unity 工具循环。
type ConversationService interface {
	StartSession(ctx context.Context, playerID, npcID string) (*Session, error)
	SubmitMessage(ctx context.Context, sessionID, text string) (*AssistantReply, error)
	SubmitMessageStream(ctx context.Context, sessionID, text string, onStreamEvent func(AssistantStreamEvent) error) (*AssistantReply, error)
	EndSession(ctx context.Context, sessionID string) error
	SaveConversations(ctx context.Context, request ConversationSaveRequest) ConversationSaveResult
	LoadConversations(ctx context.Context, request ConversationLoadRequest) ConversationLoadResult
}

// Service 是 ConversationService 的内存实现；每个 Session 内部串行处理玩家消息。
type Service struct {
	llm             LLMClient
	store           SessionStore
	runtime         Runtime
	policy          gametools.Policy
	profiles        *NPCProfileCatalog
	model           string
	maxContextChars int
	archive         *FileConversationArchive
	lifecycleMu     sync.Mutex
}

// NewConversationService 装配模型、会话存储、Unity 运行时和工具/上下文预算。
func NewConversationService(llm LLMClient, store SessionStore, runtime Runtime, profiles *NPCProfileCatalog, model string, maxToolRounds int, contextBudgets ...int) *Service {
	return newConversationService(llm, store, runtime, profiles, model, maxToolRounds, nil, contextBudgets...)
}

// NewConversationServiceWithArchive 启用显式 save/load 请求使用的文件快照仓库。
func NewConversationServiceWithArchive(llm LLMClient, store SessionStore, runtime Runtime, profiles *NPCProfileCatalog, model string, maxToolRounds int, archive *FileConversationArchive, contextBudgets ...int) *Service {
	return newConversationService(llm, store, runtime, profiles, model, maxToolRounds, archive, contextBudgets...)
}

func newConversationService(llm LLMClient, store SessionStore, runtime Runtime, profiles *NPCProfileCatalog, model string, maxToolRounds int, archive *FileConversationArchive, contextBudgets ...int) *Service {
	maxContextChars := defaultMaxContextChars
	if len(contextBudgets) > 0 && contextBudgets[0] > 0 {
		maxContextChars = contextBudgets[0]
	}
	return &Service{
		llm: llm, store: store, runtime: runtime, policy: gametools.NewPolicy(maxToolRounds), profiles: profiles,
		model: model, maxContextChars: maxContextChars, archive: archive,
	}
}

// StartSession 校验 NPC 在线状态，生成系统提示词并创建内存 Session。
func (s *Service) StartSession(ctx context.Context, playerID, npcID string) (*Session, error) {
	return s.StartSessionForRuntime(ctx, "game-1", playerID, npcID)
}

// StartSessionForRuntime 创建绑定到单个 Unity Runtime、玩家和实体的 A2A Context。
func (s *Service) StartSessionForRuntime(ctx context.Context, instanceID, playerID, npcID string) (*Session, error) {
	s.lifecycleMu.Lock()
	defer s.lifecycleMu.Unlock()
	if strings.TrimSpace(instanceID) == "" {
		return nil, fmt.Errorf("instanceId is required")
	}
	if strings.TrimSpace(playerID) == "" {
		return nil, fmt.Errorf("playerId is required")
	}
	if strings.TrimSpace(npcID) == "" {
		return nil, fmt.Errorf("npcId is required")
	}
	profile, profileFound := s.profiles.Get(npcID)
	if !profileFound {
		return nil, fmt.Errorf("%w: %s", ErrNPCProfileNotFound, npcID)
	}
	if _, err := s.runtime.Capabilities(ctx, instanceID, npcID); err != nil {
		return nil, fmt.Errorf("runtime entity is unavailable: %w", err)
	}
	now := time.Now().UTC()
	session := &Session{
		ID: newSessionID(), PlayerID: playerID, NPCID: npcID, UnityInstanceID: instanceID,
		SystemPrompt: BuildSystemPrompt(profile),
		Model:        s.model, CreatedAt: now, LastActiveAt: now,
	}
	session.Messages = []Message{{Role: "system", Content: session.SystemPrompt}}
	if err := s.store.Save(ctx, session); err != nil {
		return nil, err
	}
	return session, nil
}

// ValidateSessionOwner 防止 A2A Context ID 跨 Runtime、玩家或实体复用。
func (s *Service) ValidateSessionOwner(ctx context.Context, sessionID, instanceID, playerID, npcID string) error {
	session, err := s.store.Load(ctx, sessionID)
	if err != nil {
		return err
	}
	if session.UnityInstanceID != instanceID || session.PlayerID != playerID || session.NPCID != npcID {
		return errors.New("A2A context ownership mismatch")
	}
	return nil
}

// SubmitMessage 以非流式回调方式处理一条玩家消息。
func (s *Service) SubmitMessage(ctx context.Context, sessionID, text string) (*AssistantReply, error) {
	return s.submitMessage(ctx, sessionID, text, nil)
}

// SubmitMessageStream 处理玩家消息，并向调用方推送文本增量或草稿重置事件。
func (s *Service) SubmitMessageStream(
	ctx context.Context,
	sessionID, text string,
	onStreamEvent func(AssistantStreamEvent) error,
) (*AssistantReply, error) {
	return s.submitMessage(ctx, sessionID, text, onStreamEvent)
}

// submitMessage 串行推进一次完整的 LLM/tool loop，并在每轮后持久化内存 Session。
func (s *Service) submitMessage(
	ctx context.Context,
	sessionID, text string,
	onStreamEvent func(AssistantStreamEvent) error,
) (*AssistantReply, error) {
	if strings.TrimSpace(text) == "" {
		return nil, fmt.Errorf("message text is required")
	}
	session, err := s.store.Load(ctx, sessionID)
	if err != nil {
		return nil, err
	}
	// 同一 Session 串行处理消息，避免两个 tool loop 交叉改写历史。
	session.mu.Lock()
	defer session.mu.Unlock()

	// 将当前操作的 cancel 暂存到 Session，使 A2A tasks/cancel 能中止 LLM 或 MCP 工具。
	operationCtx, cancel := context.WithCancel(ctx)
	session.cancelMu.Lock()
	if session.closed {
		session.cancelMu.Unlock()
		cancel()
		return nil, ErrSessionNotFound
	}
	session.cancel = cancel
	session.cancelMu.Unlock()
	defer func() {
		cancel()
		session.cancelMu.Lock()
		session.cancel = nil
		session.cancelMu.Unlock()
		session.CurrentToolCallID = ""
	}()

	definitions, err := s.runtime.Capabilities(operationCtx, session.UnityInstanceID, session.NPCID)
	if err != nil {
		return nil, fmt.Errorf("runtime or entity is offline for session %s: %w", sessionID, err)
	}
	session.Messages = append(session.Messages, Message{Role: "user", Content: text})
	s.trimSessionMessages(session)
	session.LastActiveAt = time.Now().UTC()
	if err := s.store.Save(operationCtx, session); err != nil {
		return nil, err
	}

	toolRounds := 0
	for {
		llmStartedAt := time.Now()
		messageCount := len(session.Messages)
		toolCount := len(definitions)
		streamedText := false
		var onTextDelta func(string) error
		if onStreamEvent != nil {
			onTextDelta = func(delta string) error {
				streamedText = true
				return onStreamEvent(AssistantStreamEvent{Text: delta})
			}
		}
		completion, err := s.llm.Complete(operationCtx, CompletionRequest{
			Model: session.Model, Messages: append([]Message(nil), session.Messages...),
			Tools: definitions, OnTextDelta: onTextDelta,
		})
		if err != nil {
			log.Printf("event=llm_request_completed session_id=%q npc_id=%q outcome=error duration_ms=%d message_count=%d tool_count=%d tool_round=%d error=%q", session.ID, session.NPCID, time.Since(llmStartedAt).Milliseconds(), messageCount, toolCount, toolRounds, err)
			return nil, err
		}
		log.Printf("event=llm_request_completed session_id=%q npc_id=%q outcome=success duration_ms=%d message_count=%d tool_count=%d tool_round=%d tool_call_count=%d text_length=%d", session.ID, session.NPCID, time.Since(llmStartedAt).Milliseconds(), messageCount, toolCount, toolRounds, len(completion.ToolCalls), len([]rune(completion.Content)))
		// 工具调用前的文本只是临时草稿，先通知 Unity 撤回，再执行工具。
		if len(completion.ToolCalls) > 0 && streamedText && onStreamEvent != nil {
			if err := onStreamEvent(AssistantStreamEvent{Reset: true}); err != nil {
				return nil, fmt.Errorf("reset provisional assistant text: %w", err)
			}
		}
		if len(completion.ToolCalls) == 0 {
			content := strings.TrimSpace(completion.Content)
			if content == "" {
				content = "我暂时没有可回复的内容。"
			}
			session.Messages = append(session.Messages, Message{Role: "assistant", Content: content})
			s.trimSessionMessages(session)
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

		// 每个工具结果紧跟对应 assistant tool call 写入，形成不可拆分的原子轮次。
		for _, call := range completion.ToolCalls {
			session.CurrentToolCallID = call.ID
			result := ToolExecutionResult{OK: false, ErrorCode: "TOOL_REJECTED"}
			if err := s.policy.Authorize(definitions, call.Name, call.Arguments); err != nil {
				result.Message = err.Error()
			} else {
				toolStartedAt := time.Now()

				executed, executeErr := s.runtime.Execute(operationCtx, session.UnityInstanceID, session.NPCID, call.Name, call.Arguments)
				if executeErr != nil {
					result.ErrorCode = "TOOL_EXECUTION_ERROR"
					result.Message = executeErr.Error()
					log.Printf("event=agent_tool_call_completed session_id=%q call_id=%q npc_id=%q tool=%q round=%d outcome=runtime_error duration_ms=%d error=%q", session.ID, call.ID, session.NPCID, call.Name, toolRounds, time.Since(toolStartedAt).Milliseconds(), executeErr)
				} else {
					result = executed
					if result.OK {
						log.Printf("event=agent_tool_call_completed session_id=%q call_id=%q npc_id=%q tool=%q round=%d outcome=success duration_ms=%d", session.ID, call.ID, session.NPCID, call.Name, toolRounds, time.Since(toolStartedAt).Milliseconds())
					} else {
						log.Printf("event=agent_tool_call_completed session_id=%q call_id=%q npc_id=%q tool=%q round=%d outcome=%q duration_ms=%d error_code=%q", session.ID, call.ID, session.NPCID, call.Name, toolRounds, toolResultOutcome(result), time.Since(toolStartedAt).Milliseconds(), result.ErrorCode)
					}
				}
			}
			encodedResult, _ := json.Marshal(result)
			session.Messages = append(session.Messages, Message{Role: "tool", ToolCallID: call.ID, Content: string(encodedResult)})
		}
		s.trimSessionMessages(session)
		session.LastActiveAt = time.Now().UTC()
		if err := s.store.Save(operationCtx, session); err != nil {
			return nil, err
		}
	}
}

// trimSessionMessages 在保留系统提示词和完整工具轮次的前提下限制上下文大小。
func (s *Service) trimSessionMessages(session *Session) {
	before := len(session.Messages)
	session.Messages = trimConversationMessages(session.Messages, s.maxContextChars)
	if removed := before - len(session.Messages); removed > 0 {
		log.Printf("event=conversation_context_trimmed session_id=%q removed_message_count=%d retained_message_count=%d max_context_chars=%d", session.ID, removed, len(session.Messages), s.maxContextChars)
	}
}

// EndSession 取消正在进行的模型/工具操作，并幂等删除内存 Session。
func (s *Service) EndSession(ctx context.Context, sessionID string) error {
	s.lifecycleMu.Lock()
	defer s.lifecycleMu.Unlock()
	session, err := s.store.Load(ctx, sessionID)
	if err != nil {
		if errors.Is(err, ErrSessionNotFound) {
			return nil
		}
		return err
	}
	session.cancelMu.Lock()
	session.closed = true
	if session.cancel != nil {
		session.cancel()
	}
	session.cancelMu.Unlock()

	// 等待进行中的消息处理退出后再删除，防止其在删除后保存共享 Session 指针。
	session.mu.Lock()
	defer session.mu.Unlock()
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
