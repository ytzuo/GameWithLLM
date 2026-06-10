// Package handler 负责 Hertz HTTP 路由注册和 MCP 请求适配
package handler

import (
	"context"
	"net/http"

	"github.com/cloudwego/hertz/pkg/app"
	"github.com/cloudwego/hertz/pkg/common/adaptor"
	"github.com/cloudwego/hertz/pkg/protocol/consts"
	"github.com/cloudwego/hertz/pkg/route"
	mcpserver "github.com/mark3labs/mcp-go/server"

	"GameMCPServer/internal/unity"
)

// RegisterRoutes 注册所有 HTTP 路由。
func RegisterRoutes(h *route.Engine, mcpHTTPServer *mcpserver.StreamableHTTPServer, unityManager *unity.Manager) {
	h.POST("/mcp", func(ctx context.Context, c *app.RequestContext) {
		handleMCP(ctx, c, mcpHTTPServer)
	})

	h.GET("/mcp", func(ctx context.Context, c *app.RequestContext) {
		handleMCP(ctx, c, mcpHTTPServer)
	})

	h.DELETE("/mcp", func(ctx context.Context, c *app.RequestContext) {
		handleMCP(ctx, c, mcpHTTPServer)
	})

	h.GET("/unity/ws", adaptor.HertzHandler(http.HandlerFunc(unityManager.HandleWebSocket)))
	h.GET("/unity/status", func(ctx context.Context, c *app.RequestContext) {
		c.JSON(consts.StatusOK, map[string]any{
			"connected": unityManager.Connected(),
			"client_id": unityManager.ClientID(),
		})
	})

	h.GET("/health", handleHealth)
	h.GET("/", handleRoot)
}
