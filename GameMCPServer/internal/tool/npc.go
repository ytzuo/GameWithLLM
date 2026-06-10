// Package tool 包含所有 MCP 工具的业务处理逻辑
package tool

import (
	"context"
	"fmt"

	"github.com/cloudwego/hertz/pkg/common/hlog"
	"github.com/mark3labs/mcp-go/mcp"

	"GameMCPServer/internal/unity"
)

type NPCHandlers struct {
	unityManager *unity.Manager
}

func NewNPCHandlers(unityManager *unity.Manager) *NPCHandlers {
	return &NPCHandlers{unityManager: unityManager}
}

// HandleGetNPCStatus 处理查询 NPC 状态请求
func (h *NPCHandlers) HandleGetNPCStatus(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	hlog.Infof("Getting status for NPC: %s", npcID)
	return h.callUnity(ctx, "get_npc_status", npcID, nil)
}

// HandleGetNPCPosition 处理查询 NPC 位置请求
func (h *NPCHandlers) HandleGetNPCPosition(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	hlog.Infof("Getting position for NPC: %s", npcID)
	return h.callUnity(ctx, "get_npc_position", npcID, nil)
}

// HandleMoveTo 处理 NPC 移动请求
func (h *NPCHandlers) HandleMoveTo(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	target, err := request.RequireString("target")
	if err != nil {
		return nil, fmt.Errorf("target is required")
	}

	hlog.Infof("Moving NPC %s to %s", npcID, target)
	return h.callUnity(ctx, "move_to", npcID, map[string]any{"target": target})
}

// HandleSay 处理 NPC 说话请求
func (h *NPCHandlers) HandleSay(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	npcID, err := request.RequireString("npc_id")
	if err != nil {
		return nil, fmt.Errorf("npc_id is required")
	}

	content, err := request.RequireString("content")
	if err != nil {
		return nil, fmt.Errorf("content is required")
	}

	hlog.Infof("NPC %s says: %s", npcID, content)
	return h.callUnity(ctx, "say", npcID, map[string]any{"content": content})
}

func (h *NPCHandlers) callUnity(ctx context.Context, toolName, npcID string, args map[string]any) (*mcp.CallToolResult, error) {
	if h.unityManager == nil {
		return mcp.NewToolResultError("Unity 客户端未配置"), nil
	}

	result, err := h.unityManager.SendCommand(ctx, toolName, npcID, args)
	if err != nil {
		return mcp.NewToolResultError(fmt.Sprintf("Unity 执行失败: %v", err)), nil
	}
	if !result.OK {
		if result.ErrorCode != "" {
			return mcp.NewToolResultError(fmt.Sprintf("Unity 执行失败[%s]: %s", result.ErrorCode, result.Message)), nil
		}
		return mcp.NewToolResultError(fmt.Sprintf("Unity 执行失败: %s", result.Message)), nil
	}
	return mcp.NewToolResultText(result.Message), nil
}
