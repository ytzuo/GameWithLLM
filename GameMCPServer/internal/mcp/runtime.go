package mcp

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"

	"GameMCPServer/internal/agent"
	gametools "GameMCPServer/internal/tools"
)

type ClientResolver interface {
	ResolveClient(instanceID string) (Client, bool)
}

type StaticResolver struct {
	Client Client
}

func (r StaticResolver) ResolveClient(_ string) (Client, bool) { return r.Client, r.Client != nil }

type AgentRuntime struct {
	resolver ClientResolver
}

func NewAgentRuntime(resolver ClientResolver) *AgentRuntime { return &AgentRuntime{resolver: resolver} }

func (r *AgentRuntime) Capabilities(ctx context.Context, instanceID, entityID string) ([]gametools.Definition, error) {
	client, ok := r.resolver.ResolveClient(instanceID)
	if !ok {
		return nil, fmt.Errorf("runtime %q is unavailable", instanceID)
	}
	tools, err := client.ListTools(ctx)
	if err != nil {
		return nil, err
	}
	definitions := make([]gametools.Definition, 0, len(tools))
	for _, tool := range tools {
		modelSchema := modelVisibleSchema(tool.InputSchema)
		definitions = append(definitions, gametools.Definition{
			Name: tool.Name, Description: tool.Description,
			InputSchema: modelSchema,
		})
	}
	return definitions, nil
}

// modelVisibleSchema hides the runtime routing field from the LLM. The Agent
// Service binds the authenticated A2A entity before sending tools/call.
func modelVisibleSchema(schema json.RawMessage) json.RawMessage {
	var object map[string]any
	if json.Unmarshal(schema, &object) != nil {
		return append(json.RawMessage(nil), schema...)
	}
	if properties, ok := object["properties"].(map[string]any); ok {
		delete(properties, "entityId")
	}
	if required, ok := object["required"].([]any); ok {
		filtered := required[:0]
		for _, item := range required {
			if item != "entityId" {
				filtered = append(filtered, item)
			}
		}
		if len(filtered) == 0 {
			delete(object, "required")
		} else {
			object["required"] = filtered
		}
	}
	encoded, err := json.Marshal(object)
	if err != nil {
		return append(json.RawMessage(nil), schema...)
	}
	return encoded
}

func (r *AgentRuntime) Execute(ctx context.Context, instanceID, entityID, tool string, arguments json.RawMessage) (agent.ToolExecutionResult, error) {
	client, ok := r.resolver.ResolveClient(instanceID)
	if !ok {
		return agent.ToolExecutionResult{}, fmt.Errorf("runtime %q is unavailable", instanceID)
	}
	bound, err := BindEntityID(arguments, entityID)
	if err != nil {
		return agent.ToolExecutionResult{}, err
	}
	result, err := client.CallTool(ctx, tool, bound)
	if err != nil {
		return agent.ToolExecutionResult{}, err
	}
	converted := agent.ToolExecutionResult{OK: !result.IsError, Data: append(json.RawMessage(nil), result.StructuredContent...)}
	if len(result.Content) > 0 {
		converted.Message = result.Content[0].Text
	}
	if result.IsError {
		converted.ErrorCode = errorCode(result.StructuredContent)
	}
	return converted, nil
}

func BindEntityID(arguments json.RawMessage, entityID string) (json.RawMessage, error) {
	if strings.TrimSpace(entityID) == "" {
		return nil, errors.New("entityId is required")
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(arguments, &object); err != nil || object == nil {
		return nil, errors.New("tool arguments must be a JSON object")
	}
	if raw, exists := object["entityId"]; exists {
		var supplied string
		if json.Unmarshal(raw, &supplied) != nil || supplied != entityID {
			return nil, errors.New("tool entityId does not match the active A2A agent")
		}
	} else {
		object["entityId"], _ = json.Marshal(entityID)
	}
	return json.Marshal(object)
}

func errorCode(data json.RawMessage) string {
	var value struct {
		ErrorCode string `json:"errorCode"`
	}
	if json.Unmarshal(data, &value) == nil {
		return value.ErrorCode
	}
	return "TOOL_ERROR"
}
