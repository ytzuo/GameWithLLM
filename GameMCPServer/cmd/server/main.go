package main

import (
	"log"
	"net/http"

	"GameMCPServer/internal/handler"
)

// main 启动 HTTP 服务并暴露 Unity JSON-RPC WebSocket 入口。
func main() {
	mux := http.NewServeMux()
	handler.RegisterRoutes(mux)

	log.Println("Game MCP Server starting on http://localhost:8080")
	log.Println("Unity JSON-RPC WebSocket endpoint: ws://localhost:8080")
	log.Println("Unity JSON-RPC WebSocket endpoint: ws://localhost:8080/ws")

	if err := http.ListenAndServe(":8080", mux); err != nil {
		log.Fatalf("Server failed to start: %v", err)
	}
}
