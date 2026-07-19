package unity

const gameNPCMoveToolName = "game_npc_move"

// unityClientTools 返回 Unity 客户端当前支持的工具声明。
func unityClientTools() []map[string]any {
	// 这个 schema 需要和 unity-NPC-agent-client/ToolsRegistry.GetToolsForHost 保持一致。
	return []map[string]any{
		{
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
		},
	}
}

func unityClientToolExists(name string) bool {
	for _, tool := range unityClientTools() {
		if tool["name"] == name {
			return true
		}
	}
	return false
}
