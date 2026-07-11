package unity

import (
	"log"
	"net/http"
	"time"
)

// JSONRPCServer 实现 unity-NPC-agent-client 使用的 JSON-RPC WebSocket 协议。
type JSONRPCServer struct {
	timeout time.Duration
}

// NewJSONRPCServer 创建 Unity JSON-RPC WebSocket 服务。
func NewJSONRPCServer(timeout time.Duration) *JSONRPCServer {
	return &JSONRPCServer{timeout: timeout}
}

// HandleRoot 处理根路径请求，并在 WebSocket 升级时进入协议处理。
func (s *JSONRPCServer) HandleRoot(w http.ResponseWriter, r *http.Request) {
	// Unity 客户端默认连接 ws://127.0.0.1:8080，所以根路径也要接受 WebSocket 升级。
	if isWebSocketUpgrade(r) {
		s.HandleWebSocket(w, r)
		return
	}
	_, _ = w.Write([]byte("Game MCP Server is running!"))
}

// HandleWebSocket 完成 WebSocket 升级并启动单连接会话循环。
func (s *JSONRPCServer) HandleWebSocket(w http.ResponseWriter, r *http.Request) {
	conn, err := upgradeWebSocket(w, r)
	if err != nil {
		log.Printf("JSON-RPC websocket upgrade failed: %v", err)
		return
	}
	defer conn.Close()

	log.Print("Unity JSON-RPC websocket connected")
	session := &jsonRPCSession{
		conn:    conn,
		pending: make(map[string]chan jsonRPCMessage),
		timeout: s.timeout,
	}
	session.readLoop()
	log.Print("Unity JSON-RPC websocket disconnected")
}
