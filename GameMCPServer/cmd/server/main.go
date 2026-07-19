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
		log.Fatalf("Game MCP Server stopped with error: %v", err)
	}
}

func run(ctx context.Context, cfg config.Config) error {
	mux := http.NewServeMux()
	jsonRPCServer := handler.RegisterRoutesWithTimeout(mux, cfg.UnityToolTimeout)
	httpServer := &http.Server{
		Addr:              cfg.ServerAddr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	log.Printf("Game MCP Server starting on %s", cfg.BaseURL)
	log.Printf("Unity JSON-RPC WebSocket endpoint: %s", cfg.UnityJSONRPCWSURL)
	log.Printf("Unity tool timeout: %ds", cfg.UnityToolTimeoutSecond)

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
		log.Print("Game MCP Server shutting down")
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
