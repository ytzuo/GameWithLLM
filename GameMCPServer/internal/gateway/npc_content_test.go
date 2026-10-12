package gateway

import (
	"GameMCPServer/internal/agent"
	"context"
	"github.com/stretchr/testify/require"
	"strings"
	"testing"
)

func TestManifestNPCBindingAndRegistrySnapshot(t *testing.T) {
	binding := agent.NPCContentBinding{EntityID: "merchant_001", ContentVersion: "1", ManifestSHA256: strings.Repeat("a", 64)}
	manifest := Manifest{InstanceID: "game", Entities: []string{"merchant_001", "legacy"}, NPCContents: []agent.NPCContentBinding{binding}}
	require.NoError(t, validateManifest(manifest))
	registry := NewRegistry()
	session := newRuntimeSession(context.Background(), nil, manifest)
	registry.register(session)
	resolved, err := registry.NPCContentBinding(context.Background(), "game", "merchant_001")
	require.NoError(t, err)
	require.Equal(t, binding, *resolved)
	resolved.ContentVersion = "mutated"
	resolved, err = registry.NPCContentBinding(context.Background(), "game", "merchant_001")
	require.NoError(t, err)
	require.Equal(t, "1", resolved.ContentVersion)
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
	changed.ContentVersion = "2"
	bad.NPCContents = []agent.NPCContentBinding{changed}
	require.False(t, unchangedNPCBindings(manifest, bad))
	require.True(t, unchangedNPCBindings(manifest, manifest))
}
