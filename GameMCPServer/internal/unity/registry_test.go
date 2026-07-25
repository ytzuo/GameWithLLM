package unity

import (
	"encoding/json"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func testRegistration(instanceID string, npcs ...string) UnityRegistration {
	npcTools := make(map[string][]string, len(npcs))
	for _, npcID := range npcs {
		npcTools[npcID] = []string{"game_npc_move"}
	}
	return UnityRegistration{
		ProtocolVersion: unityProtocolVersion,
		InstanceID:      instanceID,
		NPCs:            npcs,
		NPCTools:        npcTools,
		Tools: []ToolDefinition{{
			Name:        "game_npc_move",
			Description: "move",
			InputSchema: json.RawMessage(`{"type":"object"}`),
		}},
	}
}

func TestUnityRegistryRegisterResolveAndDisconnect_BitsUT(t *testing.T) {
	registry := NewUnityRegistry()
	session, _ := newTestSession(time.Second)

	replaced, err := registry.Register(session, testRegistration("game-1", "Ryan_001"))
	require.NoError(t, err)
	assert.False(t, replaced)
	instanceID, resolved, ok := registry.ResolveNPC("Ryan_001")
	assert.True(t, ok)
	assert.Equal(t, "game-1", instanceID)
	assert.Same(t, session, resolved)
	assert.True(t, registry.HasTool("game-1", "Ryan_001", "game_npc_move"))

	registry.UnregisterSession(session)
	_, _, ok = registry.ResolveNPC("Ryan_001")
	assert.False(t, ok)
}

func TestUnityRegistryNewConnectionReplacesOldWithoutStaleCleanup_BitsUT(t *testing.T) {
	registry := NewUnityRegistry()
	oldSession, _ := newTestSession(time.Second)
	newSession, _ := newTestSession(time.Second)
	require.NoError(t, registerForTest(registry, oldSession, testRegistration("game-1", "Ryan_001")))

	replaced, err := registry.Register(newSession, testRegistration("game-1", "Ryan_001", "Mia_002"))
	require.NoError(t, err)
	assert.True(t, replaced)
	registry.UnregisterSession(oldSession)

	_, resolved, ok := registry.ResolveNPC("Ryan_001")
	assert.True(t, ok)
	assert.Same(t, newSession, resolved)
	instances, npcs := registry.Counts()
	assert.Equal(t, 1, instances)
	assert.Equal(t, 2, npcs)
}

func TestUnityRegistryChangesRequireOwningSession_BitsUT(t *testing.T) {
	registry := NewUnityRegistry()
	owner, _ := newTestSession(time.Second)
	other, _ := newTestSession(time.Second)
	require.NoError(t, registerForTest(registry, owner, testRegistration("game-1", "Ryan_001")))

	err := registry.UpdateNPC(other, UnityNPCChangedParams{InstanceID: "game-1", NPCID: "Mia_002", Online: true})
	require.Error(t, err)
	assert.Contains(t, err.Error(), "does not own")
}

func TestUnityRegistryUpdatesNPCAndTools_BitsUT(t *testing.T) {
	registry := NewUnityRegistry()
	session, _ := newTestSession(time.Second)
	require.NoError(t, registerForTest(registry, session, testRegistration("game-1", "Ryan_001")))

	require.NoError(t, registry.UpdateNPC(session, UnityNPCChangedParams{InstanceID: "game-1", NPCID: "Mia_002", Online: true}))
	_, _, ok := registry.ResolveNPC("Mia_002")
	assert.True(t, ok)
	_, miaToolsBeforeSnapshot, ok := registry.CapabilitiesForNPC("Mia_002")
	require.True(t, ok)
	assert.Empty(t, miaToolsBeforeSnapshot)

	require.NoError(t, registry.UpdateTools(session, UnityToolsChangedParams{
		InstanceID: "game-1",
		Tools:      []ToolDefinition{{Name: "inspect", InputSchema: json.RawMessage(`{"type":"object"}`)}},
		NPCTools: map[string][]string{
			"Ryan_001": {"inspect"},
			"Mia_002":  {},
		},
	}))
	assert.False(t, registry.HasTool("game-1", "Ryan_001", "game_npc_move"))
	assert.True(t, registry.HasTool("game-1", "Ryan_001", "inspect"))
	assert.False(t, registry.HasTool("game-1", "Mia_002", "inspect"))

	require.NoError(t, registry.UpdateNPC(session, UnityNPCChangedParams{
		InstanceID: "game-1",
		NPCID:      "Mia_002",
		Online:     false,
	}))
	_, _, ok = registry.ResolveNPC("Mia_002")
	assert.False(t, ok)
	assert.False(t, registry.HasTool("game-1", "Mia_002", "inspect"))
}

func TestUnityRegistryIsolatesCapabilitiesByNPC_BitsUT(t *testing.T) {
	registry := NewUnityRegistry()
	session, _ := newTestSession(time.Second)
	registration := testRegistration("game-1", "Ryan_001", "Mia_002")
	registration.NPCTools["Mia_002"] = []string{}
	require.NoError(t, registerForTest(registry, session, registration))

	_, ryanTools, ok := registry.CapabilitiesForNPC("Ryan_001")
	require.True(t, ok)
	require.Len(t, ryanTools, 1)
	assert.Equal(t, "game_npc_move", ryanTools[0].Name)

	_, miaTools, ok := registry.CapabilitiesForNPC("Mia_002")
	require.True(t, ok)
	assert.Empty(t, miaTools)
	assert.False(t, registry.HasTool("game-1", "Mia_002", "game_npc_move"))
}

func TestUnityRegistryRejectsCapabilitySnapshotWithStaleNPC_BitsUT(t *testing.T) {
	registry := NewUnityRegistry()
	session, _ := newTestSession(time.Second)
	require.NoError(t, registerForTest(registry, session, testRegistration("game-1", "Ryan_001")))

	err := registry.UpdateTools(session, UnityToolsChangedParams{
		InstanceID: "game-1",
		Tools: []ToolDefinition{{
			Name: "game_npc_move", InputSchema: json.RawMessage(`{"type":"object"}`),
		}},
		NPCTools: map[string][]string{"Mia_002": {"game_npc_move"}},
	})
	require.Error(t, err)
	assert.Contains(t, err.Error(), "online npcId")
	assert.True(t, registry.HasTool("game-1", "Ryan_001", "game_npc_move"))
}

func registerForTest(registry *UnityRegistry, session *jsonRPCSession, registration UnityRegistration) error {
	_, err := registry.Register(session, registration)
	return err
}
