package main

import (
	"log"
	"time"

	"github.com/cloudwego/hertz/pkg/app/server"
	"github.com/cloudwego/hertz/pkg/common/hlog"

	"GameMCPServer/internal/handler"
	"GameMCPServer/internal/mcp"
	"GameMCPServer/internal/unity"
)

func main() {
	unityManager := unity.NewManager(10 * time.Second)

	// 1. 创建 MCP 服务器
	mcpSvr := mcp.NewServer(unityManager)

	// 2. 创建 MCP Streamable HTTP 服务器
	httpServer := mcp.NewStreamableHTTPServer(mcpSvr)

	// 3. 创建 Hertz HTTP 服务器
	h := server.Default(server.WithHostPorts(":8080"))

	// 4. 注册路由
	handler.RegisterRoutes(h.Engine, httpServer, unityManager)

	hlog.Info("Game MCP Server starting on http://localhost:8080")
	hlog.Info("MCP Streamable HTTP endpoint: http://localhost:8080/mcp")
	hlog.Info("Unity WebSocket endpoint: ws://localhost:8080/unity/ws")

	// 5. 启动服务
	if err := h.Run(); err != nil {
		log.Fatalf("Server failed to start: %v", err)
	}
}
