// Package mcp 负责 MCP 服务器的初始化、工具注册和 SSE 服务器创建
package mcp

import (
	"time"

	"github.com/mark3labs/mcp-go/mcp"
	mcpserver "github.com/mark3labs/mcp-go/server"

	"GameMCPServer/internal/tool"
	"GameMCPServer/internal/unity"
)

// NewServer 创建并配置 MCP 服务器，注册所有游戏工具
func NewServer(unityManager *unity.Manager) *mcpserver.MCPServer {
	s := mcpserver.NewMCPServer(
		"GameMCPServer", // 服务器名称
		"1.0.0",         // 版本号
	)

	registerTools(s, unityManager)
	return s
}

// registerTools 注册所有 MCP 工具
func registerTools(s *mcpserver.MCPServer, unityManager *unity.Manager) {
	handlers := tool.NewNPCHandlers(unityManager)

	// 查询类工具
	s.AddTool(mcp.NewTool("get_npc_status",
		mcp.WithDescription("获取指定 NPC 的当前状态信息"),
		mcp.WithString("npc_id",
			mcp.Required(),
			mcp.Description("NPC 的唯一标识符"),
		),
	), handlers.HandleGetNPCStatus)

	s.AddTool(mcp.NewTool("get_npc_position",
		mcp.WithDescription("获取指定 NPC 的当前位置坐标"),
		mcp.WithString("npc_id",
			mcp.Required(),
			mcp.Description("NPC 的唯一标识符"),
		),
	), handlers.HandleGetNPCPosition)

	// 行为类工具
	s.AddTool(mcp.NewTool("move_to",
		mcp.WithDescription("让指定 NPC 移动到目标位置"),
		mcp.WithString("npc_id",
			mcp.Required(),
			mcp.Description("NPC 的唯一标识符"),
		),
		mcp.WithString("target",
			mcp.Required(),
			mcp.Description("目标位置或地标名称"),
		),
	), handlers.HandleMoveTo)

	s.AddTool(mcp.NewTool("say",
		mcp.WithDescription("让指定 NPC 说一句话"),
		mcp.WithString("npc_id",
			mcp.Required(),
			mcp.Description("NPC 的唯一标识符"),
		),
		mcp.WithString("content",
			mcp.Required(),
			mcp.Description("NPC 要说的内容"),
		),
	), handlers.HandleSay)
}

// NewStreamableHTTPServer 基于已创建的 MCP 服务器创建 Streamable HTTP 服务器。
func NewStreamableHTTPServer(mcpSvr *mcpserver.MCPServer) *mcpserver.StreamableHTTPServer {
	return mcpserver.NewStreamableHTTPServer(
		mcpSvr,
		mcpserver.WithEndpointPath("/mcp"),
		mcpserver.WithHeartbeatInterval(time.Second),
	)
}
