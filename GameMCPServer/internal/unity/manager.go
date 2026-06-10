package unity

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"sync"
	"sync/atomic"
	"time"

	"github.com/cloudwego/hertz/pkg/common/hlog"
	"github.com/gorilla/websocket"
)

var (
	ErrNoClient = errors.New("unity client is not connected")
	ErrTimeout  = errors.New("unity command timed out")
)

type Manager struct {
	mu       sync.RWMutex
	conn     *websocket.Conn
	clientID string

	writeMu sync.Mutex
	pending map[string]chan Result
	nextID  atomic.Uint64
	timeout time.Duration
}

func NewManager(timeout time.Duration) *Manager {
	return &Manager{
		pending: make(map[string]chan Result),
		timeout: timeout,
	}
}

func (m *Manager) Connected() bool {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.conn != nil
}

func (m *Manager) ClientID() string {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.clientID
}

func (m *Manager) SendCommand(ctx context.Context, toolName, npcID string, args map[string]any) (*Result, error) {
	conn := m.currentConn()
	if conn == nil {
		return nil, ErrNoClient
	}

	commandID := fmt.Sprintf("%d-%d", time.Now().UnixNano(), m.nextID.Add(1))
	ch := make(chan Result, 1)

	m.mu.Lock()
	m.pending[commandID] = ch
	m.mu.Unlock()
	defer m.removePending(commandID)

	command := Command{
		Type:      MessageTypeCommand,
		CommandID: commandID,
		ToolName:  toolName,
		NPCID:     npcID,
		Arguments: args,
	}

	m.writeMu.Lock()
	err := conn.WriteJSON(command)
	m.writeMu.Unlock()
	if err != nil {
		return nil, fmt.Errorf("send unity command: %w", err)
	}

	waitCtx, cancel := context.WithTimeout(ctx, m.timeout)
	defer cancel()

	select {
	case result := <-ch:
		return &result, nil
	case <-waitCtx.Done():
		if errors.Is(waitCtx.Err(), context.DeadlineExceeded) {
			return nil, ErrTimeout
		}
		return nil, waitCtx.Err()
	}
}

func (m *Manager) HandleWebSocket(w http.ResponseWriter, r *http.Request) {
	upgrader := websocket.Upgrader{
		CheckOrigin: func(*http.Request) bool { return true },
	}

	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		hlog.Errorf("Unity websocket upgrade failed: %v", err)
		return
	}

	m.replaceConnection(conn)
	hlog.Info("Unity websocket connected")

	defer func() {
		m.clearConnection(conn)
		_ = conn.Close()
		hlog.Info("Unity websocket disconnected")
	}()

	for {
		_, payload, err := conn.ReadMessage()
		if err != nil {
			hlog.Errorf("Unity websocket read failed: %v", err)
			return
		}
		if err := m.handleMessage(conn, payload); err != nil {
			hlog.Errorf("Unity websocket message ignored: %v", err)
		}
	}
}

func (m *Manager) currentConn() *websocket.Conn {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.conn
}

func (m *Manager) replaceConnection(conn *websocket.Conn) {
	m.mu.Lock()
	old := m.conn
	m.conn = conn
	m.clientID = ""
	m.failPendingLocked("unity connection replaced")
	m.mu.Unlock()

	if old != nil {
		_ = old.Close()
	}
}

func (m *Manager) clearConnection(conn *websocket.Conn) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.conn != conn {
		return
	}
	m.conn = nil
	m.clientID = ""
	m.failPendingLocked("unity connection closed")
}

func (m *Manager) handleMessage(conn *websocket.Conn, payload []byte) error {
	var env envelope
	if err := json.Unmarshal(payload, &env); err != nil {
		return err
	}
	env.Raw = payload

	switch env.Type {
	case MessageTypeHello:
		var hello HelloMessage
		if err := json.Unmarshal(payload, &hello); err != nil {
			return err
		}
		m.mu.Lock()
		if m.conn == conn {
			m.clientID = hello.ClientID
		}
		m.mu.Unlock()
		hlog.Infof("Unity hello received: client_id=%s capabilities=%v", hello.ClientID, hello.Capabilities)
	case MessageTypeResult:
		var result Result
		if err := json.Unmarshal(payload, &result); err != nil {
			return err
		}
		m.complete(result)
	case MessageTypePing:
		m.writeMu.Lock()
		err := conn.WriteJSON(map[string]any{"type": MessageTypePong})
		m.writeMu.Unlock()
		return err
	default:
		return fmt.Errorf("unknown message type %q", env.Type)
	}
	return nil
}

func (m *Manager) complete(result Result) {
	m.mu.RLock()
	ch := m.pending[result.CommandID]
	m.mu.RUnlock()
	if ch == nil {
		hlog.Warnf("Unity result has no pending command: command_id=%s", result.CommandID)
		return
	}
	ch <- result
}

func (m *Manager) removePending(commandID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.pending, commandID)
}

func (m *Manager) failPendingLocked(message string) {
	for commandID, ch := range m.pending {
		ch <- Result{
			Type:      MessageTypeResult,
			CommandID: commandID,
			OK:        false,
			ErrorCode: "unity_disconnected",
			Message:   message,
		}
		delete(m.pending, commandID)
	}
}
