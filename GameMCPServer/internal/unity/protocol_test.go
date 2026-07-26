package unity

import (
	"encoding/json"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestUnityRegistrationValidation_BitsUT(t *testing.T) {
	valid := UnityRegistration{
		ProtocolVersion: unityProtocolVersion,
		InstanceID:      "local-game-1",
		NPCs:            []string{"Ryan_001"},
		NPCTools:        map[string][]string{"Ryan_001": {"game_npc_move"}},
		Tools: []ToolDefinition{{
			Name:        "game_npc_move",
			InputSchema: json.RawMessage(`{"type":"object"}`),
		}},
	}
	require.NoError(t, valid.Validate())

	tests := []struct {
		name         string
		registration UnityRegistration
		contains     string
	}{
		{"wrong version", UnityRegistration{ProtocolVersion: 1, InstanceID: "game"}, "protocolVersion"},
		{"missing instance", UnityRegistration{ProtocolVersion: 2}, "instanceId"},
		{"empty npc", UnityRegistration{ProtocolVersion: 2, InstanceID: "game", NPCs: []string{""}}, "npcId"},
		{"duplicate npc", UnityRegistration{
			ProtocolVersion: 2, InstanceID: "game", NPCs: []string{"npc-1", "npc-1"},
			NPCTools: map[string][]string{"npc-1": {}},
		}, "duplicate npcId"},
		{"invalid schema", UnityRegistration{
			ProtocolVersion: 2, InstanceID: "game",
			Tools:    []ToolDefinition{{Name: "move", InputSchema: json.RawMessage(`[]`)}},
			NPCTools: map[string][]string{},
		}, "inputSchema"},
		{"duplicate tool", UnityRegistration{
			ProtocolVersion: 2, InstanceID: "game",
			Tools: []ToolDefinition{
				{Name: "move", InputSchema: json.RawMessage(`{"type":"object"}`)},
				{Name: "move", InputSchema: json.RawMessage(`{"type":"object"}`)},
			},
			NPCTools: map[string][]string{},
		}, "duplicate tool"},
		{"missing npc tools", UnityRegistration{ProtocolVersion: 2, InstanceID: "game", NPCs: []string{"npc-1"}}, "npcTools"},
		{"unknown mapped tool", UnityRegistration{
			ProtocolVersion: 2, InstanceID: "game", NPCs: []string{"npc-1"},
			NPCTools: map[string][]string{"npc-1": {"missing"}},
		}, "unknown tool"},
		{"duplicate mapped tool", UnityRegistration{
			ProtocolVersion: 2, InstanceID: "game", NPCs: []string{"npc-1"},
			Tools: []ToolDefinition{{
				Name: "move", InputSchema: json.RawMessage(`{"type":"object"}`),
			}},
			NPCTools: map[string][]string{"npc-1": {"move", "move"}},
		}, "duplicate tool"},
		{"unknown mapped npc", UnityRegistration{
			ProtocolVersion: 2, InstanceID: "game", NPCTools: map[string][]string{"npc-1": {}},
		}, "unknown npcId"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := tt.registration.Validate()
			require.Error(t, err)
			assert.Contains(t, err.Error(), tt.contains)
		})
	}
}

func TestUnityToolsChangedValidation_BitsUT(t *testing.T) {
	valid := UnityToolsChangedParams{
		InstanceID: "game",
		Tools: []ToolDefinition{{
			Name: "move", InputSchema: json.RawMessage(`{"type":"object"}`),
		}},
		NPCTools: map[string][]string{"npc-1": {"move"}},
	}
	require.NoError(t, valid.Validate())

	invalid := valid
	invalid.NPCTools = map[string][]string{"npc-1": {"missing"}}
	assert.ErrorContains(t, invalid.Validate(), "unknown tool")
}

func TestUnityToolExecuteParamsRequireObjectArguments_BitsUT(t *testing.T) {
	valid := UnityToolExecuteParams{
		NPCID:     "Ryan_001",
		Tool:      "game_npc_move",
		Arguments: json.RawMessage(`{"targetId":"landmark:warehouse"}`),
	}
	require.NoError(t, valid.Validate())

	invalid := valid
	invalid.Arguments = json.RawMessage(`"{\"targetId\":\"landmark:warehouse\"}"`)
	err := invalid.Validate()
	require.Error(t, err)
	assert.Contains(t, err.Error(), "JSON object")
}

func TestToolResultJSONShape_BitsUT(t *testing.T) {
	payload, err := json.Marshal(ToolResult{OK: false, ErrorCode: "LANDMARK_NOT_FOUND", Message: "目标地标不存在"})
	require.NoError(t, err)
	assert.JSONEq(t, `{"ok":false,"errorCode":"LANDMARK_NOT_FOUND","message":"目标地标不存在"}`, string(payload))
}
func TestToolResultStructuredDataJSONShape_BitsUT(t *testing.T) {
	payload, err := json.Marshal(ToolResult{
		OK: true, Data: json.RawMessage(`{"items":[{"itemId":"apple","quantity":2}]}`),
	})
	require.NoError(t, err)
	assert.JSONEq(t, `{"ok":true,"data":{"items":[{"itemId":"apple","quantity":2}]}}`, string(payload))
}

func TestToolResultErrorIncludesStructuredData_BitsUT(t *testing.T) {
	payload, err := json.Marshal(ToolResult{
		OK: false, ErrorCode: "CONTAINER_TOO_FAR", Message: "容器距离过远",
		Data: json.RawMessage(`{"containerId":"player:local-player-1.inventory","distance":4.5,"interactionRange":3}`),
	})
	require.NoError(t, err)
	assert.JSONEq(t, `{
		"ok":false,
		"errorCode":"CONTAINER_TOO_FAR",
		"message":"容器距离过远",
		"data":{"containerId":"player:local-player-1.inventory","distance":4.5,"interactionRange":3}
	}`, string(payload))
}

func TestAssistantDeltaResetJSONShape_BitsUT(t *testing.T) {
	payload, err := json.Marshal(AssistantDeltaParams{
		Type: "assistant.delta", SessionID: "session-1", Reset: true,
	})
	require.NoError(t, err)
	assert.JSONEq(t, `{"type":"assistant.delta","sessionId":"session-1","reset":true}`, string(payload))
}
