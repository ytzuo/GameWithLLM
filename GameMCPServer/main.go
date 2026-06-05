package main

import (
	"log"

	"github.com/cloudwego/hertz/pkg/app/server"
	"github.com/cloudwego/hertz/pkg/common/hlog"

	"GameMCPServer/internal/handler"
	"GameMCPServer/internal/mcp"
)

func main() {
	// 1. 创建 MCP 服务器
	mcpSvr := mcp.NewServer()

	// 2. 创建 MCP SSE 服务器
	sseServer := mcp.NewSSEServer(mcpSvr)

	// 3. 创建 Hertz HTTP 服务器
	h := server.Default(server.WithHostPorts(":8888"))

	// 4. 注册路由
	handler.RegisterRoutes(h.Engine, sseServer)

	hlog.Info("Game MCP Server starting on http://localhost:8888")
	hlog.Info("MCP SSE endpoint: http://localhost:8888/sse")

	// 5. 启动服务
	if err := h.Run(); err != nil {
		log.Fatalf("Server failed to start: %v", err)
	}
}
