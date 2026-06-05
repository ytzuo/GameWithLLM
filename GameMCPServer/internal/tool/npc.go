// Package tool 包含所有 MCP 工具的业务处理逻辑
package tool

import (
	"context"
	"fmt"

	"github.com/cloudwego/hertz/pkg/common/hlog"
	"github.com/mark3labs/mcp-go/mcp"
)

// HandleGetNPCStatus 处理查询 NPC 状态请求
func HandleGetNPCStatus(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	hlog.Infof("Getting status for NPC: %s", npcID)

	// TODO: 实现实际的 NPC 状态查询逻辑（转发给 Unity 或查询本地缓存）
	return mcp.NewToolResultText(fmt.Sprintf("NPC %s 状态: 正常, 生命值: 100, 能量: 80", npcID)), nil
}

// HandleGetNPCPosition 处理查询 NPC 位置请求
func HandleGetNPCPosition(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	hlog.Infof("Getting position for NPC: %s", npcID)

	// TODO: 实现实际的 NPC 位置查询逻辑
	return mcp.NewToolResultText(fmt.Sprintf("NPC %s 位置: (100.5, 0.0, 200.3)", npcID)), nil
}

// HandleMoveTo 处理 NPC 移动请求
func HandleMoveTo(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	target, err := request.RequireString("target")
	if err != nil {
		return nil, fmt.Errorf("target is required")
	}

	hlog.Infof("Moving NPC %s to %s", npcID, target)

	// TODO: 实现实际的 NPC 移动逻辑（转发给 Unity）
	return mcp.NewToolResultText(fmt.Sprintf("NPC %s 正在移动到 %s", npcID, target)), nil
}

// HandleSay 处理 NPC 说话请求
func HandleSay(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	content, err := request.RequireString("content")
	if err != nil {
		return nil, fmt.Errorf("content is required")
	}

	hlog.Infof("NPC %s says: %s", npcID, content)

	// TODO: 实现实际的 NPC 说话逻辑（转发给 Unity）
	return mcp.NewToolResultText(fmt.Sprintf("NPC %s 说: %s", npcID, content)), nil
}
