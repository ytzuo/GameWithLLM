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

// main 启动 HTTP 服务并暴露 Unity JSON-RPC WebSocket 入口。
func main() {
	cfg := config.Load()
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if err := run(ctx, cfg); err != nil {
		log.Fatalf("Game Agent Host stopped with error: %v", err)
	}
}

func run(ctx context.Context, cfg config.Config) error {
	mux := http.NewServeMux()
	jsonRPCServer := handler.RegisterRoutesWithConfig(mux, cfg)
	httpServer := &http.Server{
		Addr:              cfg.ServerAddr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	log.Printf("event=server_starting base_url=%q websocket_url=%q tool_timeout_seconds=%d llm_model=%q llm_timeout_seconds=%d llm_max_retries=%d llm_max_tool_rounds=%d llm_max_context_chars=%d conversation_save_dir=%q llm_api_key_configured=%t", cfg.BaseURL, cfg.UnityJSONRPCWSURL, cfg.UnityToolTimeoutSecond, cfg.LLMModel, cfg.LLMRequestTimeoutSecond, cfg.LLMMaxRetries, cfg.LLMMaxToolRounds, cfg.LLMMaxContextChars, cfg.ConversationSaveDir, cfg.LLMAPIKey != "")

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
		webSocketErr := jsonRPCServer.Shutdown(shutdownCtx)
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
