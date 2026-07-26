package agent

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func testNPCProfile(npcID string) NPCProfile {
	return NPCProfile{
		NPCID: npcID, DisplayName: npcID, Identity: "测试 NPC", SpeakingStyle: "简洁直接",
		Personality: []string{"可靠"}, Responsibilities: []string{"完成玩家任务"},
		WorldKnowledge:  []string{"这是测试场景"},
		ForbiddenTopics: []string{"不得编造结果"},
	}
}

func testProfileCatalog(npcIDs ...string) *NPCProfileCatalog {
	profiles := make([]NPCProfile, 0, len(npcIDs))
	for _, npcID := range npcIDs {
		profiles = append(profiles, testNPCProfile(npcID))
	}
	catalog, err := NewNPCProfileCatalog(profiles)
	if err != nil {
		panic(err)
	}
	return catalog
}

func TestLoadNPCProfileCatalog_StrictValidationAndImmutableReads(t *testing.T) {
	profile := testNPCProfile("Ryan_001")
	content, err := json.Marshal(npcProfileFile{Version: npcProfileVersion, Profiles: []NPCProfile{profile}})
	require.NoError(t, err)
	path := filepath.Join(t.TempDir(), "profiles.json")
	require.NoError(t, os.WriteFile(path, content, 0o600))

	catalog, err := LoadNPCProfileCatalog(path)
	require.NoError(t, err)
	loaded, ok := catalog.Get("Ryan_001")
	require.True(t, ok)
	assert.Equal(t, profile.DisplayName, loaded.DisplayName)

	loaded.Personality[0] = "被外部修改"
	again, ok := catalog.Get("Ryan_001")
	require.True(t, ok)
	assert.Equal(t, profile.Personality[0], again.Personality[0])
}

func TestLoadNPCProfileCatalog_RejectsUnknownFieldsAndDuplicates(t *testing.T) {
	path := filepath.Join(t.TempDir(), "profiles.json")
	require.NoError(t, os.WriteFile(path, []byte(`{"version":1,"unknown":true,"profiles":[]}`), 0o600))
	_, err := LoadNPCProfileCatalog(path)
	assert.ErrorContains(t, err, "unknown field")

	profile := testNPCProfile("Ryan_001")
	_, err = NewNPCProfileCatalog([]NPCProfile{profile, profile})
	assert.ErrorContains(t, err, "duplicate npcId")
}

func TestRepositoryNPCProfilesGiveBothNPCsSharedResponsibilities(t *testing.T) {
	catalog, err := LoadNPCProfileCatalog(filepath.Join("..", "..", "config", "npc_profiles.json"))
	require.NoError(t, err)
	ryan, ok := catalog.Get("Ryan_001")
	require.True(t, ok)
	alice, ok := catalog.Get("Alice_001")
	require.True(t, ok)

	assert.Equal(t, ryan.Responsibilities, alice.Responsibilities)
	assert.NotEqual(t, ryan.Identity, alice.Identity)
	assert.NotEqual(t, ryan.Personality, alice.Personality)
	assert.NotEqual(t, ryan.SpeakingStyle, alice.SpeakingStyle)
	assert.GreaterOrEqual(t, len(ryan.Personality), 5)
	assert.GreaterOrEqual(t, len(alice.Personality), 5)
}

func TestConversationServiceRejectsMissingNPCProfile(t *testing.T) {
	service := NewConversationService(
		&scriptedLLM{}, NewMemorySessionStore(), &fakeRuntime{},
		testProfileCatalog("Ryan_001"), "test-model", 3,
	)
	_, err := service.StartSession(context.Background(), "player", "Alice_001")
	assert.ErrorIs(t, err, ErrNPCProfileNotFound)
}
