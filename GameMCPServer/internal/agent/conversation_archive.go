package agent

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

const conversationSnapshotVersion = 1

var canonicalUUIDPattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)

type ConversationSaveRequest struct {
	InstanceID  string
	PlayerID    string
	SaveID      string
	OperationID string
	Mode        string
}

type ConversationLoadRequest struct {
	InstanceID string
	PlayerID   string
	SaveID     string
	NPCIDs     []string
}

type ConversationSaveResult struct {
	OK           bool      `json:"ok"`
	ErrorCode    string    `json:"errorCode,omitempty"`
	Message      string    `json:"message,omitempty"`
	SaveID       string    `json:"saveId,omitempty"`
	OperationID  string    `json:"operationId,omitempty"`
	ContextCount int       `json:"contextCount,omitempty"`
	SavedAt      time.Time `json:"savedAt,omitempty"`
}

func (r ConversationSaveResult) MarshalJSON() ([]byte, error) {
	if !r.OK {
		return json.Marshal(struct {
			OK        bool   `json:"ok"`
			ErrorCode string `json:"errorCode"`
			Message   string `json:"message"`
		}{false, r.ErrorCode, r.Message})
	}
	return json.Marshal(struct {
		OK           bool      `json:"ok"`
		SaveID       string    `json:"saveId"`
		OperationID  string    `json:"operationId"`
		ContextCount int       `json:"contextCount"`
		SavedAt      time.Time `json:"savedAt"`
	}{true, r.SaveID, r.OperationID, r.ContextCount, r.SavedAt})
}

type VisibleMessage struct {
	Index int    `json:"index"`
	Role  string `json:"role"`
	Text  string `json:"text"`
}

type LoadedConversationContext struct {
	NPCID           string           `json:"npcId"`
	SessionID       string           `json:"sessionId"`
	VisibleMessages []VisibleMessage `json:"visibleMessages"`
}

type ConversationLoadResult struct {
	OK        bool                        `json:"ok"`
	ErrorCode string                      `json:"errorCode,omitempty"`
	Message   string                      `json:"message,omitempty"`
	SaveID    string                      `json:"saveId,omitempty"`
	Contexts  []LoadedConversationContext `json:"contexts,omitempty"`
	LoadedAt  time.Time                   `json:"loadedAt,omitempty"`
}

func (r ConversationLoadResult) MarshalJSON() ([]byte, error) {
	if !r.OK {
		return json.Marshal(struct {
			OK        bool   `json:"ok"`
			ErrorCode string `json:"errorCode"`
			Message   string `json:"message"`
		}{false, r.ErrorCode, r.Message})
	}
	return json.Marshal(struct {
		OK       bool                        `json:"ok"`
		SaveID   string                      `json:"saveId"`
		Contexts []LoadedConversationContext `json:"contexts"`
		LoadedAt time.Time                   `json:"loadedAt"`
	}{true, r.SaveID, r.Contexts, r.LoadedAt})
}

type persistedConversationContext struct {
	NPCID           string    `json:"npcId"`
	HistoryMessages []Message `json:"historyMessages"`
	CreatedAt       time.Time `json:"createdAt"`
	LastActiveAt    time.Time `json:"lastActiveAt"`
}

type conversationSnapshot struct {
	SnapshotVersion int                            `json:"snapshotVersion"`
	SaveID          string                         `json:"saveId"`
	OperationID     string                         `json:"operationId"`
	PlayerID        string                         `json:"playerId"`
	SavedAt         time.Time                      `json:"savedAt"`
	Contexts        []persistedConversationContext `json:"contexts"`
}

type archiveFailure struct {
	code    string
	message string
}

func (e *archiveFailure) Error() string { return e.message }

// FileConversationArchive owns Go-side JSON snapshots. It never scans or restores files at startup.
type FileConversationArchive struct {
	dir string
	mu  sync.Mutex
}

func NewFileConversationArchive(dir string) *FileConversationArchive {
	return &FileConversationArchive{dir: filepath.Clean(dir)}
}

func IsCanonicalUUID(value string) bool { return canonicalUUIDPattern.MatchString(value) }

func (a *FileConversationArchive) Save(snapshot conversationSnapshot, mode string) (conversationSnapshot, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if err := os.MkdirAll(a.dir, 0o755); err != nil {
		return conversationSnapshot{}, &archiveFailure{"STORAGE_IO_ERROR", "Unable to create conversation snapshot directory."}
	}
	target := filepath.Join(a.dir, snapshot.SaveID+".json")
	existing, readErr := readConversationSnapshot(target)
	if readErr == nil {
		if existing.OperationID == snapshot.OperationID {
			return existing, nil
		}
		if mode == "create" {
			return conversationSnapshot{}, &archiveFailure{"SAVE_ALREADY_EXISTS", "Conversation snapshot already exists."}
		}
	} else {
		var failure *archiveFailure
		isMissing := errors.As(readErr, &failure) && failure.code == "SAVE_NOT_FOUND"
		if !isMissing {
			return conversationSnapshot{}, readErr
		}
		if mode == "overwrite" {
			return conversationSnapshot{}, &archiveFailure{"SAVE_NOT_FOUND", "Conversation snapshot does not exist."}
		}
	}

	temp, err := os.CreateTemp(a.dir, snapshot.SaveID+"-*.tmp")
	if err != nil {
		return conversationSnapshot{}, &archiveFailure{"STORAGE_IO_ERROR", "Unable to create conversation snapshot temp file."}
	}
	tempPath := temp.Name()
	committed := false
	defer func() {
		_ = temp.Close()
		if !committed {
			_ = os.Remove(tempPath)
		}
	}()
	writer := bufio.NewWriter(temp)
	encoder := json.NewEncoder(writer)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(snapshot); err != nil || writer.Flush() != nil || temp.Sync() != nil || temp.Close() != nil {
		return conversationSnapshot{}, &archiveFailure{"STORAGE_IO_ERROR", "Unable to write conversation snapshot."}
	}

	if mode == "overwrite" {
		if err := replaceFileAtomically(tempPath, target); err != nil {
			return conversationSnapshot{}, &archiveFailure{"STORAGE_IO_ERROR", "Unable to replace conversation snapshot."}
		}
	} else if err := os.Rename(tempPath, target); err != nil {
		return conversationSnapshot{}, &archiveFailure{"STORAGE_IO_ERROR", "Unable to commit conversation snapshot."}
	}
	committed = true
	return snapshot, nil
}

func (a *FileConversationArchive) Load(saveID string) (conversationSnapshot, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	return readConversationSnapshot(filepath.Join(a.dir, saveID+".json"))
}

func readConversationSnapshot(path string) (conversationSnapshot, error) {
	file, err := os.Open(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return conversationSnapshot{}, &archiveFailure{"SAVE_NOT_FOUND", "Conversation snapshot does not exist."}
		}
		return conversationSnapshot{}, &archiveFailure{"STORAGE_IO_ERROR", "Unable to read conversation snapshot."}
	}
	defer file.Close()
	decoder := json.NewDecoder(file)
	decoder.DisallowUnknownFields()
	var snapshot conversationSnapshot
	if err := decoder.Decode(&snapshot); err != nil {
		return conversationSnapshot{}, &archiveFailure{"SNAPSHOT_INVALID", "Conversation snapshot JSON is invalid."}
	}
	if err := ensureJSONEOF(decoder); err != nil {
		return conversationSnapshot{}, &archiveFailure{"SNAPSHOT_INVALID", "Conversation snapshot contains trailing data."}
	}
	if err := validateConversationSnapshot(snapshot); err != nil {
		return conversationSnapshot{}, &archiveFailure{"SNAPSHOT_INVALID", err.Error()}
	}
	return snapshot, nil
}

func ensureJSONEOF(decoder *json.Decoder) error {
	var extra any
	if err := decoder.Decode(&extra); errors.Is(err, io.EOF) {
		return nil
	}
	return errors.New("trailing JSON value")
}

func validateConversationSnapshot(snapshot conversationSnapshot) error {
	if snapshot.SnapshotVersion != conversationSnapshotVersion {
		return errors.New("unsupported conversation snapshot version")
	}
	if !IsCanonicalUUID(snapshot.SaveID) || !IsCanonicalUUID(snapshot.OperationID) {
		return errors.New("snapshot identifiers are invalid")
	}
	if strings.TrimSpace(snapshot.PlayerID) == "" || snapshot.SavedAt.IsZero() {
		return errors.New("snapshot owner or savedAt is invalid")
	}
	seenNPCs := make(map[string]struct{}, len(snapshot.Contexts))
	for _, context := range snapshot.Contexts {
		if strings.TrimSpace(context.NPCID) == "" || context.CreatedAt.IsZero() || context.LastActiveAt.IsZero() {
			return errors.New("snapshot context metadata is invalid")
		}
		if _, exists := seenNPCs[context.NPCID]; exists {
			return errors.New("snapshot contains duplicate npcId")
		}
		seenNPCs[context.NPCID] = struct{}{}
		if err := validateHistoryMessages(context.HistoryMessages); err != nil {
			return err
		}
	}
	return nil
}

func validateHistoryMessages(messages []Message) error {
	for index := 0; index < len(messages); index++ {
		message := messages[index]
		switch message.Role {
		case "user":
			if strings.TrimSpace(message.Content) == "" || message.ToolCallID != "" || len(message.ToolCalls) > 0 {
				return errors.New("invalid user history message")
			}
		case "assistant":
			if message.ToolCallID != "" || (strings.TrimSpace(message.Content) == "" && len(message.ToolCalls) == 0) {
				return errors.New("invalid assistant history message")
			}
			if len(message.ToolCalls) == 0 {
				continue
			}
			pending := make(map[string]struct{}, len(message.ToolCalls))
			for _, call := range message.ToolCalls {
				if strings.TrimSpace(call.ID) == "" || strings.TrimSpace(call.Name) == "" || !isRawJSONObject(call.Arguments) {
					return errors.New("invalid persisted tool call")
				}
				if _, exists := pending[call.ID]; exists {
					return errors.New("duplicate persisted tool call id")
				}
				pending[call.ID] = struct{}{}
			}
			for len(pending) > 0 {
				index++
				if index >= len(messages) || messages[index].Role != "tool" {
					return errors.New("incomplete persisted tool call chain")
				}
				toolMessage := messages[index]
				if strings.TrimSpace(toolMessage.Content) == "" {
					return errors.New("invalid persisted tool result")
				}
				if _, exists := pending[toolMessage.ToolCallID]; !exists {
					return errors.New("unexpected persisted tool result")
				}
				delete(pending, toolMessage.ToolCallID)
			}
		case "tool":
			return errors.New("persisted tool result has no preceding assistant tool call")
		default:
			return errors.New("unsupported persisted message role")
		}
	}
	return nil
}

func isRawJSONObject(raw json.RawMessage) bool {
	trimmed := bytes.TrimSpace(raw)
	if len(trimmed) < 2 || trimmed[0] != '{' || trimmed[len(trimmed)-1] != '}' {
		return false
	}
	var value map[string]any
	return json.Unmarshal(trimmed, &value) == nil && value != nil
}

func archiveFailureFields(err error) (string, string) {
	var failure *archiveFailure
	if errors.As(err, &failure) {
		return failure.code, failure.message
	}
	return "STORAGE_IO_ERROR", "Conversation snapshot storage operation failed."
}

func (s *Service) SaveConversations(ctx context.Context, request ConversationSaveRequest) ConversationSaveResult {
	if s.archive == nil {
		return failedSave("STORAGE_IO_ERROR", "Conversation snapshot storage is not configured.")
	}
	s.lifecycleMu.Lock()
	defer s.lifecycleMu.Unlock()
	sessions, err := s.store.ListByOwner(ctx, request.PlayerID, request.InstanceID)
	if err != nil {
		return failedSave("STORAGE_IO_ERROR", "Unable to enumerate current conversations.")
	}
	sort.Slice(sessions, func(i, j int) bool { return sessions[i].NPCID < sessions[j].NPCID })
	locked := make([]*Session, 0, len(sessions))
	defer func() {
		for i := len(locked) - 1; i >= 0; i-- {
			locked[i].mu.Unlock()
		}
	}()
	for _, session := range sessions {
		if !session.mu.TryLock() {
			return failedSave("CONVERSATION_BUSY", "A conversation is still processing.")
		}
		locked = append(locked, session)
	}
	now := time.Now().UTC()
	snapshot := conversationSnapshot{SnapshotVersion: conversationSnapshotVersion, SaveID: request.SaveID, OperationID: request.OperationID, PlayerID: request.PlayerID, SavedAt: now, Contexts: make([]persistedConversationContext, 0, len(sessions))}
	seenNPCs := make(map[string]struct{}, len(sessions))
	for _, session := range sessions {
		if _, exists := seenNPCs[session.NPCID]; exists {
			return failedSave("SNAPSHOT_INVALID", "Current conversations contain duplicate npcId values.")
		}
		seenNPCs[session.NPCID] = struct{}{}
		history := make([]Message, 0, len(session.Messages))
		for _, message := range session.Messages {
			if message.Role != "system" {
				history = append(history, cloneMessage(message))
			}
		}
		snapshot.Contexts = append(snapshot.Contexts, persistedConversationContext{NPCID: session.NPCID, HistoryMessages: history, CreatedAt: session.CreatedAt, LastActiveAt: session.LastActiveAt})
	}
	saved, err := s.archive.Save(snapshot, request.Mode)
	if err != nil {
		code, message := archiveFailureFields(err)
		return failedSave(code, message)
	}
	return ConversationSaveResult{OK: true, SaveID: saved.SaveID, OperationID: saved.OperationID, ContextCount: len(saved.Contexts), SavedAt: saved.SavedAt}
}

func (s *Service) LoadConversations(ctx context.Context, request ConversationLoadRequest) ConversationLoadResult {
	if s.archive == nil {
		return failedLoad("STORAGE_IO_ERROR", "Conversation snapshot storage is not configured.")
	}
	snapshot, err := s.archive.Load(request.SaveID)
	if err != nil {
		code, message := archiveFailureFields(err)
		return failedLoad(code, message)
	}
	if snapshot.SaveID != request.SaveID {
		return failedLoad("SNAPSHOT_INVALID", "Conversation snapshot saveId does not match its file.")
	}
	if snapshot.PlayerID != request.PlayerID {
		return failedLoad("PLAYER_MISMATCH", "Conversation snapshot belongs to a different player.")
	}
	requestedNPCs := make(map[string]struct{}, len(request.NPCIDs))
	for _, npcID := range request.NPCIDs {
		requestedNPCs[npcID] = struct{}{}
	}
	for _, context := range snapshot.Contexts {
		if _, ok := requestedNPCs[context.NPCID]; !ok {
			return failedLoad("NPC_SET_MISMATCH", "Conversation snapshot contains an NPC that is not present in the loaded world.")
		}
		instanceID, _, ok := s.runtime.Capabilities(context.NPCID)
		if !ok || instanceID != request.InstanceID {
			return failedLoad("NPC_SET_MISMATCH", "Conversation snapshot contains an NPC unavailable on this Unity instance.")
		}
	}

	s.lifecycleMu.Lock()
	defer s.lifecycleMu.Unlock()
	current, err := s.store.ListByOwner(ctx, request.PlayerID, request.InstanceID)
	if err != nil {
		return failedLoad("STORAGE_IO_ERROR", "Unable to enumerate current conversations.")
	}
	locked := make([]*Session, 0, len(current))
	defer func() {
		for i := len(locked) - 1; i >= 0; i-- {
			locked[i].mu.Unlock()
		}
	}()
	for _, session := range current {
		if !session.mu.TryLock() {
			return failedLoad("CONVERSATION_BUSY", "A conversation is still processing.")
		}
		locked = append(locked, session)
	}

	now := time.Now().UTC()
	replacements := make([]*Session, 0, len(snapshot.Contexts))
	contexts := make([]LoadedConversationContext, 0, len(snapshot.Contexts))
	for _, persisted := range snapshot.Contexts {
		profile, profileFound := s.profiles.Get(persisted.NPCID)
		if !profileFound {
			return failedLoad("NPC_PROFILE_NOT_FOUND", fmt.Sprintf("NPC profile is missing: %s", persisted.NPCID))
		}
		session := &Session{ID: newSessionID(), PlayerID: request.PlayerID, NPCID: persisted.NPCID, UnityInstanceID: request.InstanceID, SystemPrompt: BuildSystemPrompt(profile), Model: s.model, CreatedAt: persisted.CreatedAt, LastActiveAt: persisted.LastActiveAt}
		session.Messages = []Message{{Role: "system", Content: session.SystemPrompt}}
		for _, message := range persisted.HistoryMessages {
			session.Messages = append(session.Messages, cloneMessage(message))
		}
		replacements = append(replacements, session)
		contexts = append(contexts, LoadedConversationContext{NPCID: persisted.NPCID, SessionID: session.ID, VisibleMessages: projectVisibleMessages(persisted.HistoryMessages)})
	}
	if err := s.store.ReplaceByOwner(ctx, request.PlayerID, request.InstanceID, replacements); err != nil {
		return failedLoad("STORAGE_IO_ERROR", "Unable to replace current conversations.")
	}
	return ConversationLoadResult{OK: true, SaveID: request.SaveID, Contexts: contexts, LoadedAt: now}
}

func cloneMessage(message Message) Message {
	copy := message
	copy.ToolCalls = append([]ToolCall(nil), message.ToolCalls...)
	for index := range copy.ToolCalls {
		copy.ToolCalls[index].Arguments = append(json.RawMessage(nil), copy.ToolCalls[index].Arguments...)
	}
	return copy
}

func projectVisibleMessages(messages []Message) []VisibleMessage {
	result := make([]VisibleMessage, 0)
	for _, message := range messages {
		role := message.Role
		if role == "assistant" && len(message.ToolCalls) > 0 {
			continue
		}
		if (role == "user" || role == "assistant") && strings.TrimSpace(message.Content) != "" {
			result = append(result, VisibleMessage{Index: len(result), Role: role, Text: message.Content})
		}
	}
	return result
}

func failedSave(code, message string) ConversationSaveResult {
	return ConversationSaveResult{OK: false, ErrorCode: code, Message: message}
}
func failedLoad(code, message string) ConversationLoadResult {
	return ConversationLoadResult{OK: false, ErrorCode: code, Message: message}
}

func ValidateSaveConversationRequest(request ConversationSaveRequest) error {
	if strings.TrimSpace(request.InstanceID) == "" || strings.TrimSpace(request.PlayerID) == "" {
		return fmt.Errorf("instanceId and playerId are required")
	}
	if !IsCanonicalUUID(request.SaveID) || !IsCanonicalUUID(request.OperationID) {
		return fmt.Errorf("saveId and operationId must be canonical lowercase UUIDs")
	}
	if request.Mode != "create" && request.Mode != "overwrite" {
		return fmt.Errorf("mode must be create or overwrite")
	}
	return nil
}

func ValidateLoadConversationRequest(request ConversationLoadRequest) error {
	if strings.TrimSpace(request.InstanceID) == "" || strings.TrimSpace(request.PlayerID) == "" {
		return fmt.Errorf("instanceId and playerId are required")
	}
	if !IsCanonicalUUID(request.SaveID) {
		return fmt.Errorf("saveId must be a canonical lowercase UUID")
	}
	seen := make(map[string]struct{}, len(request.NPCIDs))
	for _, npcID := range request.NPCIDs {
		if strings.TrimSpace(npcID) == "" {
			return fmt.Errorf("npcIds cannot contain blank values")
		}
		if _, exists := seen[npcID]; exists {
			return fmt.Errorf("npcIds cannot contain duplicates")
		}
		seen[npcID] = struct{}{}
	}
	return nil
}
