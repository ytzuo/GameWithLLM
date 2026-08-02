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

// RegisterRoutesWithConfig 装配单进程内的 A2A、Runtime Gateway、MCP 和存档端点。
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

	mux.HandleFunc("/.well-known/agent-card.json", a2aServer.HandleAgentCard)
	mux.HandleFunc("/.well-known/agent.json", a2aServer.HandleAgentCard)
	mux.HandleFunc("/a2a", a2aServer.Handle)
	mux.HandleFunc("/runtime/ws", gatewayServer.HandleRuntimeWebSocket)
	mux.HandleFunc("/mcp/runtimes/", gatewayServer.HandleVirtualMCP)
	mux.HandleFunc("/game-saves/", saveCoordinator.Handle)
	mux.HandleFunc("/health", handleHealth)
	mux.HandleFunc("/{$}", func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("Game Agent Service is running!")) })
	return &App{Gateway: gatewayServer}, nil
}

// RegisterRoutes is retained only as a small test fixture constructor.
func RegisterRoutes(mux *http.ServeMux) *App {
	cfg := config.Load()
	app, _ := RegisterRoutesWithConfig(mux, cfg)
	return app
}
