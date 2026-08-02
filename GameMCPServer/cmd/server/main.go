package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"GameMCPServer/internal/config"
	"GameMCPServer/internal/handler"
)

// main 启动 Agent Service 的 A2A、MCP Gateway 与存档协调端点。
func main() {
	cfg := config.Load()
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if err := run(ctx, cfg); err != nil {
		log.Fatalf("Game Agent Service stopped with error: %v", err)
	}
}

func run(ctx context.Context, cfg config.Config) error {
	mux := http.NewServeMux()
	app, err := handler.RegisterRoutesWithConfig(mux, cfg)
	if err != nil {
		return err
	}
	httpServer := &http.Server{
		Addr:              cfg.ServerAddr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	log.Printf(
		"event=server_starting\n"+
			"  base_url=%q\n"+
			"  llm_model=%q\n"+
			"  llm_timeout_seconds=%d\n"+
			"  llm_max_retries=%d\n"+
			"  llm_max_tool_rounds=%d\n"+
			"  llm_max_context_chars=%d\n"+
			"  conversation_save_dir=%q\n"+
			"  gateway_auth_configured=%t\n"+
			"  llm_api_key_configured=%t",
		cfg.BaseURL,
		cfg.LLMModel,
		cfg.LLMRequestTimeoutSecond,
		cfg.LLMMaxRetries,
		cfg.LLMMaxToolRounds,
		cfg.LLMMaxContextChars,
		cfg.ConversationSaveDir,
		cfg.RuntimeGatewayToken != "",
		cfg.LLMAPIKey != "",
	)

	serveErr := make(chan error, 1)
	go func() {
		serveErr <- httpServer.ListenAndServe()
	}()

	select {
	case err := <-serveErr:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
		log.Printf("event=server_shutdown_started reason=%q", ctx.Err())
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()

		httpErr := httpServer.Shutdown(shutdownCtx)
		webSocketErr := app.Shutdown(shutdownCtx)
		serverErr := <-serveErr
		if errors.Is(serverErr, http.ErrServerClosed) {
			serverErr = nil
		}
		return errors.Join(
			wrapError("HTTP shutdown", httpErr),
			wrapError("WebSocket shutdown", webSocketErr),
			wrapError("HTTP server", serverErr),
		)
	}
}

func wrapError(operation string, err error) error {
	if err == nil {
		return nil
	}
	return fmt.Errorf("%s: %w", operation, err)
}
