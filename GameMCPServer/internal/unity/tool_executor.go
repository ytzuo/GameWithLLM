package unity

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"
)

var (
	ErrUnityInstanceOffline = errors.New("Unity instance is offline")
	ErrNPCOffline           = errors.New("NPC is not registered or offline")
	ErrToolUnavailable      = errors.New("tool is not registered for Unity instance")
)

type ToolExecutor interface {
	Execute(ctx context.Context, instanceID, npcID, tool string, arguments json.RawMessage) (*ToolResult, error)
}

type registryToolExecutor struct {
	registry *UnityRegistry
	timeout  time.Duration
}

func NewToolExecutor(registry *UnityRegistry, timeout time.Duration) ToolExecutor {
	return &registryToolExecutor{registry: registry, timeout: timeout}
}

func (e *registryToolExecutor) Execute(ctx context.Context, instanceID, npcID, tool string, arguments json.RawMessage) (*ToolResult, error) {
	params := UnityToolExecuteParams{NPCID: npcID, Tool: tool, Arguments: arguments}
	if err := params.Validate(); err != nil {
		return nil, err
	}

	resolvedInstanceID, session, ok := e.registry.ResolveNPC(npcID)
	if !ok {
		return nil, fmt.Errorf("%w: %s", ErrNPCOffline, npcID)
	}
	if instanceID != "" && instanceID != resolvedInstanceID {
		return nil, fmt.Errorf("%w: requested=%s actual=%s", ErrUnityInstanceOffline, instanceID, resolvedInstanceID)
	}
	if !e.registry.HasTool(resolvedInstanceID, tool) {
		return nil, fmt.Errorf("%w: %s", ErrToolUnavailable, tool)
	}

	execCtx := ctx
	cancel := func() {}
	if e.timeout > 0 {
		execCtx, cancel = context.WithTimeout(ctx, e.timeout)
	}
	defer cancel()
	return session.executeUnityTool(execCtx, params)
}
