// Package handler 负责注册 Unity JSON-RPC WebSocket 入口和健康检查入口。
package handler

import (
	"net/http"
	"time"

	"GameMCPServer/internal/agent"
	"GameMCPServer/internal/config"

	"GameMCPServer/internal/unity"
)

// RegisterRoutes 注册所有 HTTP 路由。
func RegisterRoutes(mux *http.ServeMux) {
	_ = RegisterRoutesWithTimeout(mux, 10*time.Second)
}

// RegisterRoutesWithTimeout 注册不启用 Go Agent Host 的测试路由。
func RegisterRoutesWithTimeout(mux *http.ServeMux, timeout time.Duration) *unity.JSONRPCServer {
	return registerRoutes(mux, unity.NewJSONRPCServer(timeout))
}

// RegisterRoutesWithConfig 注册生产路由，并把 LLM 与 ConversationService 装配到 Go Agent Host。
func RegisterRoutesWithConfig(mux *http.ServeMux, cfg config.Config) *unity.JSONRPCServer {
	llm := agent.NewOpenAICompatibleClient(cfg.LLMAPIURL, cfg.LLMAPIKey, cfg.LLMModel, cfg.LLMRequestTimeout)
	return registerRoutes(mux, unity.NewJSONRPCServerWithAgent(
		cfg.UnityToolTimeout, llm, cfg.LLMModel, cfg.LLMMaxToolRounds,
	))
}

func registerRoutes(mux *http.ServeMux, jsonRPCServer *unity.JSONRPCServer) *unity.JSONRPCServer {
	mux.HandleFunc("/unity/ws", jsonRPCServer.HandleWebSocket)
	mux.HandleFunc("/health", handleHealth)
	mux.HandleFunc("/", jsonRPCServer.HandleRoot)
	return jsonRPCServer
}
