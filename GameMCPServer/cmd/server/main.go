package main

import (
	"log"
	"net/http"

	"GameMCPServer/internal/config"
	"GameMCPServer/internal/handler"
)

// main 启动 HTTP 服务并暴露 Unity JSON-RPC WebSocket 入口。
func main() {
	cfg := config.Load()
	mux := http.NewServeMux()
	handler.RegisterRoutesWithTimeout(mux, cfg.UnityToolTimeout)

	log.Printf("Game MCP Server starting on %s", cfg.BaseURL)
	log.Printf("Unity JSON-RPC WebSocket endpoint: %s", cfg.UnityJSONRPCWSURL)
	log.Printf("Unity tool timeout: %ds", cfg.UnityToolTimeoutSecond)

	if err := http.ListenAndServe(cfg.ServerAddr, mux); err != nil {
		log.Fatalf("Server failed to start: %v", err)
	}
}
