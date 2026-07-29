package agent

import (
	"context"
	"sync"
	"time"
)

// Session 保存单个玩家与 NPC 的内存对话状态；并发字段不参与网络序列化。
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
	closed   bool
}
