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
		{"wrong version", UnityRegistration{ProtocolVersion: 2, InstanceID: "game"}, "protocolVersion"},
		{"missing instance", UnityRegistration{ProtocolVersion: 1}, "instanceId"},
		{"empty npc", UnityRegistration{ProtocolVersion: 1, InstanceID: "game", NPCs: []string{""}}, "npcId"},
		{"invalid schema", UnityRegistration{ProtocolVersion: 1, InstanceID: "game", Tools: []ToolDefinition{{Name: "move", InputSchema: json.RawMessage(`[]`)}}}, "inputSchema"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := tt.registration.Validate()
			require.Error(t, err)
			assert.Contains(t, err.Error(), tt.contains)
		})
	}
}

func TestUnityToolExecuteParamsRequireObjectArguments_BitsUT(t *testing.T) {
	valid := UnityToolExecuteParams{
		NPCID:     "Ryan_001",
		Tool:      "game_npc_move",
		Arguments: json.RawMessage(`{"targetLandmark":"warehouse"}`),
	}
	require.NoError(t, valid.Validate())

	invalid := valid
	invalid.Arguments = json.RawMessage(`"{\"targetLandmark\":\"warehouse\"}"`)
	err := invalid.Validate()
	require.Error(t, err)
	assert.Contains(t, err.Error(), "JSON object")
}

func TestToolResultJSONShape_BitsUT(t *testing.T) {
	payload, err := json.Marshal(ToolResult{OK: false, ErrorCode: "LANDMARK_NOT_FOUND", Message: "目标地标不存在"})
	require.NoError(t, err)
	assert.JSONEq(t, `{"ok":false,"errorCode":"LANDMARK_NOT_FOUND","message":"目标地标不存在"}`, string(payload))
}
func TestAssistantDeltaResetJSONShape_BitsUT(t *testing.T) {
	payload, err := json.Marshal(AssistantDeltaParams{
		Type: "assistant.delta", SessionID: "session-1", Reset: true,
	})
	require.NoError(t, err)
	assert.JSONEq(t, `{"type":"assistant.delta","sessionId":"session-1","reset":true}`, string(payload))
}
