package gateway

import (
	"GameMCPServer/internal/agent"
	"context"
	"github.com/stretchr/testify/require"
	"testing"
)

func TestManifestNPCBindingAndRegistrySnapshot(t *testing.T) {
	binding := agent.NPCContentBinding{EntityID: "Merchant"}
	manifest := Manifest{InstanceID: "game", Entities: []string{"Merchant", "legacy"}, NPCContents: []agent.NPCContentBinding{binding}}
	require.NoError(t, validateManifest(manifest))
	registry := NewRegistry()
	session := newRuntimeSession(context.Background(), nil, manifest)
	registry.register(session)
	resolved, err := registry.NPCContentBinding(context.Background(), "game", "Merchant")
	require.NoError(t, err)
	require.Equal(t, binding, *resolved)
	resolved.EntityID = "mutated"
	resolved, err = registry.NPCContentBinding(context.Background(), "game", "Merchant")
	require.NoError(t, err)
	require.Equal(t, "Merchant", resolved.EntityID)
	resolved, err = registry.NPCContentBinding(context.Background(), "game", "legacy")
	require.NoError(t, err)
	require.Nil(t, resolved)
	_, err = registry.NPCContentBinding(context.Background(), "game", "wrong")
	require.Error(t, err)
	bad := manifest
	bad.Entities = []string{"wrong"}
	require.Error(t, validateManifest(bad))
	bad = manifest
	bad.NPCContents = append([]agent.NPCContentBinding{binding}, binding)
	require.Error(t, validateManifest(bad))
	bad = manifest
	changed := binding
	changed.EntityID = "Guide"
	bad.NPCContents = []agent.NPCContentBinding{changed}
	require.False(t, unchangedNPCBindings(manifest, bad))
	require.True(t, unchangedNPCBindings(manifest, manifest))
}
