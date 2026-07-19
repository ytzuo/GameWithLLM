package agent

import (
	"context"
	"errors"
	"sync"
)

var ErrSessionNotFound = errors.New("conversation session not found")

type SessionStore interface {
	Load(ctx context.Context, sessionID string) (*Session, error)
	Save(ctx context.Context, session *Session) error
	Delete(ctx context.Context, sessionID string) error
}

type MemorySessionStore struct {
	mu       sync.RWMutex
	sessions map[string]*Session
}

func NewMemorySessionStore() *MemorySessionStore {
	return &MemorySessionStore{sessions: make(map[string]*Session)}
}

func (s *MemorySessionStore) Load(_ context.Context, sessionID string) (*Session, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	session := s.sessions[sessionID]
	if session == nil {
		return nil, ErrSessionNotFound
	}
	return session, nil
}

func (s *MemorySessionStore) Save(_ context.Context, session *Session) error {
	if session == nil || session.ID == "" {
		return errors.New("session with id is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sessions[session.ID] = session
	return nil
}

func (s *MemorySessionStore) Delete(_ context.Context, sessionID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.sessions[sessionID]; !ok {
		return ErrSessionNotFound
	}
	delete(s.sessions, sessionID)
	return nil
}
