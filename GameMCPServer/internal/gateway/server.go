// Package gateway implements the Unity-initiated Runtime Bridge and virtual MCP endpoints.
package gateway

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"

	"GameMCPServer/internal/mcp"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

const maxMessageBytes = 1 << 20
const maxPendingPerRuntime = 32

type Manifest struct {
	InstanceID string     `json:"instanceId"`
	Entities   []string   `json:"entities"`
	Tools      []mcp.Tool `json:"tools"`
	Revision   int64      `json:"revision"`
}
type initializeParams struct {
	Token    string   `json:"token"`
	Manifest Manifest `json:"manifest"`
}
type bridgeMessage struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      string          `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *mcp.RPCError   `json:"error,omitempty"`
}

type Registry struct {
	mu          sync.RWMutex
	runtimes    map[string]*runtimeSession
	generations map[string]uint64
}

// NewRegistry 创建按 Unity instanceId 索引当前活动连接的 Runtime Registry。
func NewRegistry() *Registry {
	return &Registry{runtimes: make(map[string]*runtimeSession), generations: make(map[string]uint64)}
}

// register 用递增 generation 替换同 instanceId 的旧连接，隔离迟到响应。
func (r *Registry) register(session *runtimeSession) uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.generations[session.manifest.InstanceID]++
	generation := r.generations[session.manifest.InstanceID]
	if previous := r.runtimes[session.manifest.InstanceID]; previous != nil {
		previous.close(errors.New("runtime replaced by a newer connection"))
	}
	session.generation = generation
	r.runtimes[session.manifest.InstanceID] = session
	return generation
}
func (r *Registry) unregister(session *runtimeSession) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.runtimes[session.manifest.InstanceID] == session {
		delete(r.runtimes, session.manifest.InstanceID)
	}
}

// ResolveClient 只返回当前仍在线的 Runtime 连接。
func (r *Registry) ResolveClient(instanceID string) (mcp.Client, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	session, ok := r.runtimes[instanceID]
	return session, ok && !session.closed.Load()
}

type Server struct {
	registry      *Registry
	runtimeToken  string
	serviceToken  string
	connectionsMu sync.Mutex
	connections   map[*websocket.Conn]struct{}
}

func NewServer(registry *Registry, runtimeToken, serviceToken string) *Server {
	return &Server{registry: registry, runtimeToken: runtimeToken, serviceToken: serviceToken, connections: make(map[*websocket.Conn]struct{})}
}

// HandleRuntimeWebSocket 接受 Unity 主动建立的连接，并要求首帧完成认证和 Manifest 注册。
func (s *Server) HandleRuntimeWebSocket(w http.ResponseWriter, r *http.Request) {
	conn, err := websocket.Accept(w, r, nil)
	if err != nil {
		return
	}
	conn.SetReadLimit(maxMessageBytes)
	s.connectionsMu.Lock()
	s.connections[conn] = struct{}{}
	s.connectionsMu.Unlock()
	defer func() {
		s.connectionsMu.Lock()
		delete(s.connections, conn)
		s.connectionsMu.Unlock()
		_ = conn.Close(websocket.StatusNormalClosure, "")
	}()
	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()
	var first bridgeMessage
	if err := wsjson.Read(ctx, conn, &first); err != nil || first.Method != "runtime.initialize" || first.ID == "" {
		return
	}
	var params initializeParams
	if json.Unmarshal(first.Params, &params) != nil || validateManifest(params.Manifest) != nil {
		return
	}
	if s.runtimeToken == "" || params.Token != s.runtimeToken {
		_ = wsjson.Write(ctx, conn, bridgeMessage{JSONRPC: "2.0", ID: first.ID, Error: &mcp.RPCError{Code: -32001, Message: "runtime authentication failed"}})
		return
	}
	session := newRuntimeSession(ctx, conn, params.Manifest)
	generation := s.registry.register(session)
	defer func() { s.registry.unregister(session); session.close(errors.New("runtime disconnected")) }()
	if err := session.write(bridgeMessage{JSONRPC: "2.0", ID: first.ID, Result: mustJSON(map[string]any{"accepted": true, "connectionGeneration": generation})}); err != nil {
		return
	}
	log.Printf("event=runtime_connected instance_id=%q connection_generation=%d tool_count=%d entity_count=%d", params.Manifest.InstanceID, generation, len(params.Manifest.Tools), len(params.Manifest.Entities))
	session.readLoop()
}

// HandleVirtualMCP 将可选的外部 MCP 请求路由到指定的在线 Unity Runtime。
func (s *Server) HandleVirtualMCP(w http.ResponseWriter, r *http.Request) {
	// This endpoint is service-to-service. Browser-originated requests are not
	// accepted, which also prevents DNS-rebinding access to a local deployment.
	if r.Header.Get("Origin") != "" {
		http.Error(w, "origin is not allowed", http.StatusForbidden)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if s.serviceToken == "" || r.Header.Get("Authorization") != "Bearer "+s.serviceToken {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	instanceID := strings.TrimPrefix(r.URL.Path, "/mcp/runtimes/")
	if instanceID == "" || strings.Contains(instanceID, "/") {
		http.NotFound(w, r)
		return
	}
	client, ok := s.registry.ResolveClient(instanceID)
	if !ok {
		writeMCPError(w, nil, -32001, "runtime unavailable")
		return
	}
	var request mcp.RPCRequest
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, maxMessageBytes)).Decode(&request) != nil || request.JSONRPC != "2.0" {
		writeMCPError(w, nil, -32600, "invalid MCP request")
		return
	}
	switch request.Method {
	case "initialize":
		writeMCPResult(w, request.ID, map[string]any{"protocolVersion": mcp.ProtocolVersion, "capabilities": map[string]any{"tools": map[string]any{}}, "serverInfo": map[string]string{"name": "game-runtime-gateway", "version": "1.0.0"}})
	case "notifications/initialized", "notifications/cancelled":
		w.WriteHeader(http.StatusAccepted)
	case "tools/list":
		tools, err := client.ListTools(r.Context())
		if err != nil {
			writeMCPError(w, request.ID, -32001, err.Error())
			return
		}
		writeMCPResult(w, request.ID, map[string]any{"tools": tools})
	case "tools/call":
		var params struct {
			Name      string          `json:"name"`
			Arguments json.RawMessage `json:"arguments"`
		}
		encoded, _ := json.Marshal(request.Params)
		if json.Unmarshal(encoded, &params) != nil {
			writeMCPError(w, request.ID, -32602, "invalid tools/call params")
			return
		}
		result, err := client.CallTool(r.Context(), params.Name, params.Arguments)
		if err != nil {
			writeMCPError(w, request.ID, -32002, err.Error())
			return
		}
		writeMCPResult(w, request.ID, result)
	default:
		writeMCPError(w, request.ID, -32601, "method not found")
	}
}

// Shutdown 关闭所有 Runtime WebSocket，使客户端进入各自的重连流程。
func (s *Server) Shutdown(ctx context.Context) error {
	s.connectionsMu.Lock()
	connections := make([]*websocket.Conn, 0, len(s.connections))
	for conn := range s.connections {
		connections = append(connections, conn)
	}
	s.connectionsMu.Unlock()
	for _, conn := range connections {
		_ = conn.Close(websocket.StatusGoingAway, "server shutting down")
	}
	return ctx.Err()
}

type runtimeSession struct {
	ctx        context.Context
	conn       *websocket.Conn
	manifest   Manifest
	manifestMu sync.RWMutex
	generation uint64
	writeMu    sync.Mutex
	pendingMu  sync.Mutex
	pending    map[string]chan bridgeMessage
	nextID     atomic.Uint64
	closed     atomic.Bool
}

func newRuntimeSession(ctx context.Context, conn *websocket.Conn, manifest Manifest) *runtimeSession {
	return &runtimeSession{ctx: ctx, conn: conn, manifest: manifest, pending: make(map[string]chan bridgeMessage)}
}
func (s *runtimeSession) ListTools(context.Context) ([]mcp.Tool, error) {
	if s.closed.Load() {
		return nil, errors.New("runtime disconnected")
	}
	s.manifestMu.RLock()
	defer s.manifestMu.RUnlock()
	result := make([]mcp.Tool, len(s.manifest.Tools))
	copy(result, s.manifest.Tools)
	return result, nil
}

// CallTool 登记 pending 调用并等待对应结果；Context 取消会通知 Unity 停止执行。
func (s *runtimeSession) CallTool(ctx context.Context, name string, arguments json.RawMessage) (mcp.CallToolResult, error) {
	if s.closed.Load() {
		return mcp.CallToolResult{}, errors.New("runtime disconnected")
	}
	id := fmt.Sprintf("invocation-%d-%d", s.generation, s.nextID.Add(1))
	responseChannel := make(chan bridgeMessage, 1)
	s.pendingMu.Lock()
	if len(s.pending) >= maxPendingPerRuntime {
		s.pendingMu.Unlock()
		return mcp.CallToolResult{}, errors.New("runtime concurrent invocation limit exceeded")
	}
	s.pending[id] = responseChannel
	s.pendingMu.Unlock()
	defer func() { s.pendingMu.Lock(); delete(s.pending, id); s.pendingMu.Unlock() }()
	if err := s.write(bridgeMessage{JSONRPC: "2.0", ID: id, Method: "runtime.tools.call", Params: mustJSON(map[string]any{"name": name, "arguments": json.RawMessage(arguments)})}); err != nil {
		return mcp.CallToolResult{}, err
	}
	select {
	case response := <-responseChannel:
		if response.Error != nil {
			return mcp.CallToolResult{}, fmt.Errorf("runtime call failed (%d): %s", response.Error.Code, response.Error.Message)
		}
		var result mcp.CallToolResult
		if json.Unmarshal(response.Result, &result) != nil {
			return mcp.CallToolResult{}, errors.New("runtime returned invalid CallToolResult")
		}
		return result, nil
	case <-ctx.Done():
		_ = s.write(bridgeMessage{JSONRPC: "2.0", Method: "runtime.cancelled", Params: mustJSON(map[string]string{"requestId": id, "reason": "agent request cancelled"})})
		return mcp.CallToolResult{}, ctx.Err()
	case <-s.ctx.Done():
		return mcp.CallToolResult{}, errors.New("runtime disconnected")
	}
}

// validateManifest 拒绝空值、重复项和非对象 Schema，避免污染全局工具目录。
func validateManifest(manifest Manifest) error {
	if strings.TrimSpace(manifest.InstanceID) == "" {
		return errors.New("instanceId is required")
	}
	entities := make(map[string]struct{}, len(manifest.Entities))
	for _, entityID := range manifest.Entities {
		if strings.TrimSpace(entityID) == "" {
			return errors.New("manifest contains a blank entityId")
		}
		if _, exists := entities[entityID]; exists {
			return errors.New("manifest contains duplicate entityId")
		}
		entities[entityID] = struct{}{}
	}
	tools := make(map[string]struct{}, len(manifest.Tools))
	for _, tool := range manifest.Tools {
		if strings.TrimSpace(tool.Name) == "" {
			return errors.New("manifest contains a blank tool name")
		}
		if _, exists := tools[tool.Name]; exists {
			return errors.New("manifest contains duplicate tool name")
		}
		tools[tool.Name] = struct{}{}
		var schema map[string]any
		if json.Unmarshal(tool.InputSchema, &schema) != nil || schema == nil {
			return errors.New("manifest tool inputSchema must be an object")
		}
	}
	return nil
}

// readLoop 应用完整 Manifest 更新，并将工具结果按 request ID 交给对应 pending 调用。
func (s *runtimeSession) readLoop() {
	for {
		var message bridgeMessage
		if wsjson.Read(s.ctx, s.conn, &message) != nil {
			return
		}
		if message.Method == "runtime.manifest.changed" {
			var manifest Manifest
			s.manifestMu.RLock()
			instanceID := s.manifest.InstanceID
			s.manifestMu.RUnlock()
			if json.Unmarshal(message.Params, &manifest) == nil &&
				manifest.InstanceID == instanceID && validateManifest(manifest) == nil {
				s.manifestMu.Lock()
				s.manifest = manifest
				s.manifestMu.Unlock()
			}
			continue
		}
		if message.ID == "" {
			continue
		}
		s.pendingMu.Lock()
		channel := s.pending[message.ID]
		s.pendingMu.Unlock()
		if channel != nil {
			select {
			case channel <- message:
			default:
			}
		}
	}
}

// write 串行化同一 WebSocket 的所有写入。
func (s *runtimeSession) write(message bridgeMessage) error {
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	if s.closed.Load() {
		return errors.New("runtime disconnected")
	}
	return wsjson.Write(s.ctx, s.conn, message)
}

// close 只执行一次，并用断线错误完成该 generation 的全部 pending 调用。
func (s *runtimeSession) close(reason error) {
	if !s.closed.CompareAndSwap(false, true) {
		return
	}
	s.pendingMu.Lock()
	defer s.pendingMu.Unlock()
	for id, channel := range s.pending {
		select {
		case channel <- bridgeMessage{Error: &mcp.RPCError{Code: -32001, Message: reason.Error()}}:
		default:
		}
		delete(s.pending, id)
	}
}

func mustJSON(value any) json.RawMessage { data, _ := json.Marshal(value); return data }
func writeMCPResult(w http.ResponseWriter, id string, result any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"jsonrpc": "2.0", "id": id, "result": result})
}
func writeMCPError(w http.ResponseWriter, id any, code int, message string) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"jsonrpc": "2.0", "id": id, "error": mcp.RPCError{Code: code, Message: message}})
}
