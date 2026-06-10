// Package handler 负责 Hertz HTTP 路由注册和 MCP 请求适配
package handler

import (
	"context"

	"github.com/cloudwego/hertz/pkg/app"
	"github.com/cloudwego/hertz/pkg/route"
	mcpserver "github.com/mark3labs/mcp-go/server"
)

// RegisterRoutes 注册所有 HTTP 路由。
func RegisterRoutes(h *route.Engine, mcpHTTPServer *mcpserver.StreamableHTTPServer) {
	h.POST("/mcp", func(ctx context.Context, c *app.RequestContext) {
		handleMCP(ctx, c, mcpHTTPServer)
	})

	h.GET("/mcp", func(ctx context.Context, c *app.RequestContext) {
		handleMCP(ctx, c, mcpHTTPServer)
	})

	h.DELETE("/mcp", func(ctx context.Context, c *app.RequestContext) {
		handleMCP(ctx, c, mcpHTTPServer)
	})

	h.GET("/health", handleHealth)
	h.GET("/", handleRoot)
}
