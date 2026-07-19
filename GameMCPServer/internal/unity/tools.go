package unity

import "sync"

const gameNPCMoveToolName = "game_npc_move"

// ToolRegistry 动态存储和管理 Unity 客户端工具声明，支持运行时注册和查询。
type ToolRegistry struct {
	mu    sync.RWMutex
	tools []map[string]any
}

// NewToolRegistry 创建工具注册中心并注册默认的 game_npc_move 工具。
func NewToolRegistry() *ToolRegistry {
	r := &ToolRegistry{}
	r.Register(gameNPCMoveTool())
	return r
}

// Register 注册一个工具声明。方法内部会加写锁，并发安全。
func (r *ToolRegistry) Register(tool map[string]any) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.tools = append(r.tools, tool)
}

// List 返回当前所有已注册工具的副本，调用方可安全修改返回值。
func (r *ToolRegistry) List() []map[string]any {
	r.mu.RLock()
	defer r.mu.RUnlock()
	result := make([]map[string]any, len(r.tools))
	copy(result, r.tools)
	return result
}

// ReplaceAll 清空已有工具并替换为给定的工具列表，并发安全。
func (r *ToolRegistry) ReplaceAll(tools []map[string]any) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.tools = make([]map[string]any, len(tools))
	copy(r.tools, tools)
}

// Exists 检查指定名称的工具是否已注册，并发安全。
func (r *ToolRegistry) Exists(name string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	for _, t := range r.tools {
		if t["name"] == name {
			return true
		}
	}
	return false
}

// gameNPCMoveTool 返回 game_npc_move 工具的声明。
// 这个 schema 需要和 unity-NPC-agent-client/ToolsRegistry.GetToolsForHost 保持一致。
func gameNPCMoveTool() map[string]any {
	return map[string]any{
		"name":        gameNPCMoveToolName,
		"description": "使 NPC 前往指定地标 (warehouse|gate)",
		"inputSchema": map[string]any{
			"type": "object",
			"properties": map[string]any{
				"targetLandmark": map[string]any{
					"type":        "string",
					"enum":        []string{"warehouse", "gate"},
					"description": "目标地标名称",
				},
			},
			"required": []string{"targetLandmark"},
		},
	}
}
