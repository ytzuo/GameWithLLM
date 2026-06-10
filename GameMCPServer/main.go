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

	// 2. 创建 MCP Streamable HTTP 服务器
	httpServer := mcp.NewStreamableHTTPServer(mcpSvr)

	// 3. 创建 Hertz HTTP 服务器
	h := server.Default(server.WithHostPorts(":8080"))

	// 4. 注册路由
	handler.RegisterRoutes(h.Engine, httpServer)

	hlog.Info("Game MCP Server starting on http://localhost:8080")
	hlog.Info("MCP Streamable HTTP endpoint: http://localhost:8080/mcp")

	// 5. 启动服务
	if err := h.Run(); err != nil {
		log.Fatalf("Server failed to start: %v", err)
	}
}
