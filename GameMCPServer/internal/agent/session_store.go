package agent

import (
	"context"
	"errors"
	"sync"
)

// ErrSessionNotFound 表示指定对话 Session 不存在或已被结束。
var ErrSessionNotFound = errors.New("conversation session not found")

// SessionStore 定义当前进程内的会话读写边界，不包含持久化或恢复语义。
type SessionStore interface {
	Load(ctx context.Context, sessionID string) (*Session, error)
	Save(ctx context.Context, session *Session) error
	Delete(ctx context.Context, sessionID string) error
}

// MemorySessionStore 使用并发安全的进程内 map 保存 Session。
type MemorySessionStore struct {
	mu       sync.RWMutex
	sessions map[string]*Session
}

// NewMemorySessionStore 创建空的内存会话存储。
func NewMemorySessionStore() *MemorySessionStore {
	return &MemorySessionStore{sessions: make(map[string]*Session)}
}

// Load 返回 Session 的共享指针；调用方必须使用 Session 自身的锁保护可变状态。
func (s *MemorySessionStore) Load(_ context.Context, sessionID string) (*Session, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	session := s.sessions[sessionID]
	if session == nil {
		return nil, ErrSessionNotFound
	}
	return session, nil
}

// Save 按 Session ID 新增或替换内存中的会话引用。
func (s *MemorySessionStore) Save(_ context.Context, session *Session) error {
	if session == nil || session.ID == "" {
		return errors.New("session with id is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sessions[session.ID] = session
	return nil
}

// Delete 从内存中移除指定 Session；不存在时返回 ErrSessionNotFound。
func (s *MemorySessionStore) Delete(_ context.Context, sessionID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.sessions[sessionID]; !ok {
		return ErrSessionNotFound
	}
	delete(s.sessions, sessionID)
	return nil
}
