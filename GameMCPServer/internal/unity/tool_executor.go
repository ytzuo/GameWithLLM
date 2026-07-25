package unity

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"
)

// 下列错误区分离线、路由漂移和能力缺失，供上层保留 errors.Is 语义。
var (
	ErrUnityInstanceOffline = errors.New("Unity instance is offline")
	ErrNPCOffline           = errors.New("NPC is not registered or offline")
	ErrToolUnavailable      = errors.New("tool is not registered for Unity instance")
)

// ToolExecutor 校验运行时路由后，将工具请求调度到拥有 NPC 的 Unity 连接。
type ToolExecutor interface {
	Execute(ctx context.Context, instanceID, npcID, tool string, arguments json.RawMessage) (*ToolResult, error)
}

type registryToolExecutor struct {
	registry *UnityRegistry
	timeout  time.Duration
}

// NewToolExecutor 创建带统一执行超时的 Unity 工具调度器。
func NewToolExecutor(registry *UnityRegistry, timeout time.Duration) ToolExecutor {
	return &registryToolExecutor{registry: registry, timeout: timeout}
}

// Execute 验证 NPC 归属和工具能力，再等待 Unity 返回业务结果。
func (e *registryToolExecutor) Execute(ctx context.Context, instanceID, npcID, tool string, arguments json.RawMessage) (*ToolResult, error) {
	params := UnityToolExecuteParams{NPCID: npcID, Tool: tool, Arguments: arguments}
	if err := params.Validate(); err != nil {
		return nil, err
	}

	resolvedInstanceID, session, ok := e.registry.ResolveNPC(npcID)
	if !ok {
		return nil, fmt.Errorf("%w: %s", ErrNPCOffline, npcID)
	}
	// Session 绑定的实例必须与当前路由一致，防止重连后把命令发到新世界状态。
	if instanceID != "" && instanceID != resolvedInstanceID {
		return nil, fmt.Errorf("%w: requested=%s actual=%s", ErrUnityInstanceOffline, instanceID, resolvedInstanceID)
	}
	if !e.registry.HasTool(resolvedInstanceID, npcID, tool) {
		return nil, fmt.Errorf("%w: npc=%s tool=%s", ErrToolUnavailable, npcID, tool)
	}

	execCtx := ctx
	cancel := func() {}
	if e.timeout > 0 {
		execCtx, cancel = context.WithTimeout(ctx, e.timeout)
	}
	defer cancel()
	return session.executeUnityTool(execCtx, params)
}
