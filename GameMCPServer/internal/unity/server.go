package unity

import (
	"context"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

const maxWebSocketMessageSize = 1 << 20

// JSONRPCServer 实现 unity-NPC-agent-client 使用的 JSON-RPC WebSocket 协议。
type JSONRPCServer struct {
	timeout       time.Duration
	tools         *ToolRegistry
	connectionsMu sync.Mutex
	connections   map[*websocket.Conn]struct{}
}

// NewJSONRPCServer 创建 Unity JSON-RPC WebSocket 服务。
func NewJSONRPCServer(timeout time.Duration) *JSONRPCServer {
	return &JSONRPCServer{
		timeout:     timeout,
		tools:       NewToolRegistry(),
		connections: make(map[*websocket.Conn]struct{}),
	}
}

// HandleRoot 处理根路径请求，并在 WebSocket 升级时进入协议处理。
func (s *JSONRPCServer) HandleRoot(w http.ResponseWriter, r *http.Request) {
	// 迁移期间保留旧根路径，默认配置已经切换到 /unity/ws。
	if isWebSocketUpgrade(r) {
		log.Print("deprecated Unity WebSocket endpoint used: /; migrate to /unity/ws")
		s.HandleWebSocket(w, r)
		return
	}
	_, _ = w.Write([]byte("Game MCP Server is running!"))
}

// HandleWebSocket 完成 WebSocket 升级并启动单连接会话循环。
func (s *JSONRPCServer) HandleWebSocket(w http.ResponseWriter, r *http.Request) {
	conn, err := websocket.Accept(w, r, nil)
	if err != nil {
		log.Printf("JSON-RPC websocket upgrade failed: %v", err)
		return
	}
	s.addConnection(conn)
	defer s.removeConnection(conn)
	defer func() {
		// 对端正常关闭时 Reader 已完成 close handshake，再次 Close 可能返回
		// net.ErrClosed；这里保持 best-effort，不把正常断线记录成服务错误。
		_ = conn.Close(websocket.StatusNormalClosure, "")
	}()
	conn.SetReadLimit(maxWebSocketMessageSize)

	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()

	log.Print("Unity JSON-RPC websocket connected")
	session := newJSONRPCSession(ctx, cancel, &websocketJSONRPCConnection{conn: conn}, s.tools, s.timeout)
	session.readLoop()
	log.Print("Unity JSON-RPC websocket disconnected")
}

// Shutdown 使用标准 Going Away 关闭码结束当前所有 Unity WebSocket 连接。
func (s *JSONRPCServer) Shutdown(ctx context.Context) error {
	s.connectionsMu.Lock()
	connections := make([]*websocket.Conn, 0, len(s.connections))
	for conn := range s.connections {
		connections = append(connections, conn)
	}
	s.connectionsMu.Unlock()

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
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (s *JSONRPCServer) addConnection(conn *websocket.Conn) {
	s.connectionsMu.Lock()
	defer s.connectionsMu.Unlock()
	s.connections[conn] = struct{}{}
}

func (s *JSONRPCServer) removeConnection(conn *websocket.Conn) {
	s.connectionsMu.Lock()
	defer s.connectionsMu.Unlock()
	delete(s.connections, conn)
}

// isWebSocketUpgrade 仅用于兼容根路径入口；握手和协议校验由 websocket 库完成。
func isWebSocketUpgrade(r *http.Request) bool {
	return strings.EqualFold(r.Header.Get("Upgrade"), "websocket") &&
		strings.Contains(strings.ToLower(r.Header.Get("Connection")), "upgrade")
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
