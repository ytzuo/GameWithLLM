package agent

import (
	"context"
	"sync"
	"time"
)

type Session struct {
	ID                string
	PlayerID          string
	NPCID             string
	UnityInstanceID   string
	SystemPrompt      string
	Messages          []Message
	Model             string
	CurrentToolCallID string
	CreatedAt         time.Time
	LastActiveAt      time.Time

	mu       sync.Mutex
	cancelMu sync.Mutex
	cancel   context.CancelFunc
}
