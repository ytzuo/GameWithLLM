package unity

import (
	"context"
	"encoding/json"

	"GameMCPServer/internal/agent"
	gametools "GameMCPServer/internal/tools"
)

type agentRuntime struct {
	registry *UnityRegistry
	executor ToolExecutor
}

func newAgentRuntime(registry *UnityRegistry, executor ToolExecutor) agent.Runtime {
	return &agentRuntime{registry: registry, executor: executor}
}

// Capabilities 将 Unity 注册的协议 DTO 复制为 Agent 可见的工具定义。
func (r *agentRuntime) Capabilities(npcID string) (string, []gametools.Definition, bool) {
	instanceID, definitions, ok := r.registry.CapabilitiesForNPC(npcID)
	if !ok {
		return "", nil, false
	}
	result := make([]gametools.Definition, 0, len(definitions))
	for _, definition := range definitions {
		result = append(result, gametools.Definition{
			Name: definition.Name, Description: definition.Description,
			InputSchema: append(json.RawMessage(nil), definition.InputSchema...),
		})
	}
	return instanceID, result, true
}

// Execute 调用 Unity 工具并复制结构化 Data，避免跨层共享可变 JSON 字节。
func (r *agentRuntime) Execute(ctx context.Context, instanceID, npcID, tool string, arguments json.RawMessage) (agent.ToolExecutionResult, error) {
	result, err := r.executor.Execute(ctx, instanceID, npcID, tool, arguments)
	if err != nil {
		return agent.ToolExecutionResult{}, err
	}
	return agent.ToolExecutionResult{
		OK: result.OK, ErrorCode: result.ErrorCode, Message: result.Message,
		Data: append(json.RawMessage(nil), result.Data...),
	}, nil
}
