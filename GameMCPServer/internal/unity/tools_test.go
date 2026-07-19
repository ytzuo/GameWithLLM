package unity

import (
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestUnityClientTools_BitsUT(t *testing.T) {
	tools := unityClientTools()
	require.Len(t, tools, 1)
	assert.Equal(t, "game_npc_move", tools[0]["name"])

	schema, ok := tools[0]["inputSchema"].(map[string]any)
	require.True(t, ok)
	assert.Equal(t, "object", schema["type"])
	assert.Equal(t, []string{"targetLandmark"}, schema["required"])

	properties, ok := schema["properties"].(map[string]any)
	require.True(t, ok)
	target, ok := properties["targetLandmark"].(map[string]any)
	require.True(t, ok)
	assert.Equal(t, []string{"warehouse", "gate"}, target["enum"])
	assert.True(t, unityClientToolExists(gameNPCMoveToolName))
	assert.False(t, unityClientToolExists("game_does_not_exist"))
}
