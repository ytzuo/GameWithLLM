// Package handler wires the A2A conversation plane, MCP tool plane, Runtime
// Gateway, save coordination, and health endpoints.
package handler

import (
	"context"
	"fmt"
	"net/http"

	"GameMCPServer/internal/a2a"
	"GameMCPServer/internal/agent"
	"GameMCPServer/internal/config"
	"GameMCPServer/internal/gateway"
	"GameMCPServer/internal/mcp"
	"GameMCPServer/internal/savecoord"
)

type App struct{ Gateway *gateway.Server }

func (a *App) Shutdown(ctx context.Context) error {
	if a == nil || a.Gateway == nil {
		return nil
	}
	return a.Gateway.Shutdown(ctx)
}

func RegisterRoutesWithConfig(mux *http.ServeMux, cfg config.Config) (*App, error) {
	profiles, err := agent.LoadNPCProfileCatalog(cfg.NPCProfilePath)
	if err != nil {
		return nil, fmt.Errorf("load NPC profiles: %w", err)
	}
	llm := agent.NewOpenAICompatibleClient(cfg.LLMAPIURL, cfg.LLMAPIKey, cfg.LLMModel, cfg.LLMRequestTimeout, cfg.LLMMaxRetries)
	registry := gateway.NewRegistry()
	runtime := mcp.NewAgentRuntime(registry)
	conversations := agent.NewConversationServiceWithArchive(
		llm, agent.NewMemorySessionStore(), runtime, profiles, cfg.LLMModel,
		cfg.LLMMaxToolRounds, agent.NewFileConversationArchive(cfg.ConversationSaveDir), cfg.LLMMaxContextChars)
	a2aServer := a2a.NewServer(conversations, cfg.BaseURL, cfg.A2ABearerToken)
	gatewayServer := gateway.NewServer(registry, cfg.RuntimeGatewayToken, cfg.GatewayServiceToken)
	saveCoordinator := savecoord.New(conversations, cfg.A2ABearerToken)

	// 注册路由
	mux.HandleFunc("/.well-known/agent-card.json", a2aServer.HandleAgentCard) // 返回 agent card
	mux.HandleFunc("/.well-known/agent.json", a2aServer.HandleAgentCard)      // 返回 agent card 别名路由
	mux.HandleFunc("/a2a", a2aServer.Handle)                                  // 通过 a2a 把对话发给 server
	mux.HandleFunc("/runtime/ws", gatewayServer.HandleRuntimeWebSocket)       // 建立 web socket 连接
	mux.HandleFunc("/mcp/runtimes/", gatewayServer.HandleVirtualMCP)          // 暴露成 MCP 服务，可以由 agent 操作
	mux.HandleFunc("/game-saves/", saveCoordinator.Handle)                    // 获取对话历史，保存到游戏数据
	mux.HandleFunc("/health", handleHealth)                                   // 健康检查
	mux.HandleFunc("/{$}", func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("Game Agent Service is running!")) })
	return &App{Gateway: gatewayServer}, nil
}

// RegisterRoutes is retained only as a small test fixture constructor.
func RegisterRoutes(mux *http.ServeMux) *App {
	cfg := config.Load()
	app, _ := RegisterRoutesWithConfig(mux, cfg)
	return app
}
