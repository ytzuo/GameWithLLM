// Package handler 负责注册 Unity JSON-RPC WebSocket 入口和健康检查入口。
package handler

import (
	"log"
	"net/http"
	"time"

	"GameMCPServer/internal/unity"
)

// RegisterRoutes 注册所有 HTTP 路由。
func RegisterRoutes(mux *http.ServeMux) {
	_ = RegisterRoutesWithTimeout(mux, 10*time.Second)
}

// RegisterRoutesWithTimeout 注册所有 HTTP 路由，并允许调用方配置 Unity 工具调用超时。
func RegisterRoutesWithTimeout(mux *http.ServeMux, timeout time.Duration) *unity.JSONRPCServer {
	jsonRPCServer := unity.NewJSONRPCServer(timeout)

	// /unity/ws 是正式入口；/ws 和根路径在迁移期间继续兼容。
	mux.HandleFunc("/unity/ws", jsonRPCServer.HandleWebSocket)
	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		log.Print("deprecated Unity WebSocket endpoint used: /ws; migrate to /unity/ws")
		jsonRPCServer.HandleWebSocket(w, r)
	})
	mux.HandleFunc("/health", handleHealth)
	mux.HandleFunc("/", jsonRPCServer.HandleRoot)
	return jsonRPCServer
}
