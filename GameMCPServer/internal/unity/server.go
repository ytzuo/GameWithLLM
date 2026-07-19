package unity

import (
	"context"
	"log"
	"net/http"
	"sync"
	"sync/atomic"
	"time"

	"GameMCPServer/internal/agent"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

const maxWebSocketMessageSize = 1 << 20

// JSONRPCServer 实现 unity-NPC-agent-client 使用的 JSON-RPC WebSocket 协议。
type JSONRPCServer struct {
	registry      *UnityRegistry
	conversations agent.ConversationService
	connectionsMu sync.Mutex
	connections   map[*websocket.Conn]struct{}
	nextSessionID atomic.Uint64
}

// NewJSONRPCServer 创建 Unity JSON-RPC WebSocket 服务。
func NewJSONRPCServer(timeout time.Duration) *JSONRPCServer {
	return newJSONRPCServer(timeout, nil, "", 0)
}

func NewJSONRPCServerWithAgent(timeout time.Duration, llm agent.LLMClient, model string, maxToolRounds int) *JSONRPCServer {
	return newJSONRPCServer(timeout, llm, model, maxToolRounds)
}

func newJSONRPCServer(timeout time.Duration, llm agent.LLMClient, model string, maxToolRounds int) *JSONRPCServer {
	registry := NewUnityRegistry()
	executor := NewToolExecutor(registry, timeout)
	server := &JSONRPCServer{
		registry:    registry,
		connections: make(map[*websocket.Conn]struct{}),
	}
	if llm != nil {
		server.conversations = agent.NewConversationService(
			llm, agent.NewMemorySessionStore(), newAgentRuntime(registry, executor), model, maxToolRounds,
		)
	}
	return server
}

// HandleRoot 返回普通 HTTP 运行提示；WebSocket 只允许使用 /unity/ws。
func (s *JSONRPCServer) HandleRoot(w http.ResponseWriter, _ *http.Request) {
	_, _ = w.Write([]byte("Game MCP Server is running!"))
}

// HandleWebSocket 完成 WebSocket 升级并启动单连接会话循环。
func (s *JSONRPCServer) HandleWebSocket(w http.ResponseWriter, r *http.Request) {
	sessionID := s.nextSessionID.Add(1)
	remoteAddr := r.RemoteAddr
	log.Printf("event=websocket_upgrade_started session_id=%d remote_addr=%q path=%q", sessionID, remoteAddr, r.URL.Path)
	conn, err := websocket.Accept(w, r, nil)
	if err != nil {
		log.Printf("event=websocket_upgrade_failed session_id=%d remote_addr=%q error=%q", sessionID, remoteAddr, err)
		return
	}
	activeConnections := s.addConnection(conn)
	defer func() {
		remaining := s.removeConnection(conn)
		log.Printf("event=websocket_disconnected session_id=%d remote_addr=%q active_connections=%d", sessionID, remoteAddr, remaining)
	}()
	defer func() {
		// 对端正常关闭时 Reader 已完成 close handshake，再次 Close 可能返回
		// net.ErrClosed；这里保持 best-effort，不把正常断线记录成服务错误。
		_ = conn.Close(websocket.StatusNormalClosure, "")
	}()
	conn.SetReadLimit(maxWebSocketMessageSize)

	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()

	log.Printf("event=websocket_connected session_id=%d remote_addr=%q active_connections=%d", sessionID, remoteAddr, activeConnections)
	session := newJSONRPCSession(ctx, cancel, &websocketJSONRPCConnection{conn: conn}, s.registry, s.conversations)
	session.readLoop()
}

// Shutdown 使用标准 Going Away 关闭码结束当前所有 Unity WebSocket 连接。
func (s *JSONRPCServer) Shutdown(ctx context.Context) error {
	s.connectionsMu.Lock()
	connections := make([]*websocket.Conn, 0, len(s.connections))
	for conn := range s.connections {
		connections = append(connections, conn)
	}
	s.connectionsMu.Unlock()
	log.Printf("event=websocket_shutdown_started active_connections=%d", len(connections))

	done := make(chan struct{})
	go func() {
		var wg sync.WaitGroup
		for _, conn := range connections {
			wg.Add(1)
			go func(conn *websocket.Conn) {
				defer wg.Done()
				if err := conn.Close(websocket.StatusGoingAway, "server shutting down"); err != nil {
					log.Printf("Unity WebSocket shutdown failed: %v", err)
				}
			}(conn)
		}
		wg.Wait()
		close(done)
	}()

	select {
	case <-done:
		log.Print("event=websocket_shutdown_completed")
		return nil
	case <-ctx.Done():
		log.Printf("event=websocket_shutdown_timed_out error=%q", ctx.Err())
		return ctx.Err()
	}
}

func (s *JSONRPCServer) addConnection(conn *websocket.Conn) int {
	s.connectionsMu.Lock()
	defer s.connectionsMu.Unlock()
	s.connections[conn] = struct{}{}
	return len(s.connections)
}

func (s *JSONRPCServer) removeConnection(conn *websocket.Conn) int {
	s.connectionsMu.Lock()
	defer s.connectionsMu.Unlock()
	delete(s.connections, conn)
	return len(s.connections)
}

type websocketJSONRPCConnection struct {
	conn *websocket.Conn
}

func (c *websocketJSONRPCConnection) Read(ctx context.Context, msg *jsonRPCMessage) error {
	return wsjson.Read(ctx, c.conn, msg)
}

func (c *websocketJSONRPCConnection) Write(ctx context.Context, msg jsonRPCMessage) error {
	return wsjson.Write(ctx, c.conn, msg)
}
