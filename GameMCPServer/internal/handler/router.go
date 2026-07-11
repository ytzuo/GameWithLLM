// Package handler 负责注册 Unity JSON-RPC WebSocket 入口和健康检查入口。
package handler

import (
	"net/http"
	"time"

	"GameMCPServer/internal/unity"
)

// RegisterRoutes 注册所有 HTTP 路由。
func RegisterRoutes(mux *http.ServeMux) {
	RegisterRoutesWithTimeout(mux, 10*time.Second)
}

// RegisterRoutesWithTimeout 注册所有 HTTP 路由，并允许调用方配置 Unity 工具调用超时。
func RegisterRoutesWithTimeout(mux *http.ServeMux, timeout time.Duration) {
	jsonRPCServer := unity.NewJSONRPCServer(timeout)

	// /ws 是显式入口；/ 也接受 WebSocket 升级，因为 Unity 客户端默认连接 ws://127.0.0.1:8080。
	mux.HandleFunc("/ws", jsonRPCServer.HandleWebSocket)
	mux.HandleFunc("/health", handleHealth)
	mux.HandleFunc("/", jsonRPCServer.HandleRoot)
}
