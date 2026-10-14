package agent

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

type testBindings map[string]NPCContentBinding

func (b testBindings) NPCContentBinding(_ context.Context, _, id string) (*NPCContentBinding, error) {
	v, ok := b[id]
	if !ok {
		return nil, nil
	}
	return &v, nil
}
func contentFixture(id string) ([]byte, NPCContentBinding) {
	prefix := "npc/" + id + "/"
	prompt := DefaultSystemPromptCatalog()
	d := NPCContentDefinition{Name: id, Profile: testNPCProfile(id), SystemPrompt: NPCPrompt{Locale: prompt.Locale, Template: id + " identity\n" + prompt.Template}, Visual: NPCVisual{PrefabAddress: prefix + "visual"}, AnimationDriver: NPCAnimationDriver{Kind: "builtin", DriverID: "standard-locomotion"}}
	data, _ := json.Marshal(d)
	return data, bindingFor(id, data)
}
func bindingFor(id string, _ []byte) NPCContentBinding { return NPCContentBinding{id} }

func TestNPCResolverAndContextIsolation(t *testing.T) {
	a, ba := contentFixture("Merchant")
	b, bb := contentFixture("Guide")
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		switch r.URL.Path {
		case "/npc/Merchant/npc.json":
			w.Write(a)
		case "/npc/Guide/npc.json":
			w.Write(b)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	resolver, err := NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	unknown := ba
	unknown.EntityID = "Unknown"
	_, err = resolver.Resolve(context.Background(), unknown)
	require.EqualError(t, err, "NPC_CONTENT_FETCH_FAILED")
	requests.Store(0)
	llm := &scriptedLLM{results: []*CompletionResult{{Content: "merchant"}, {Content: "guide"}}}
	service := NewConversationServiceWithArchive(llm, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("legacy"), "model", 3, NewFileConversationArchive(t.TempDir()))
	bindings := testBindings{ba.EntityID: ba, bb.EntityID: bb}
	service.ConfigureNPCContent(resolver, bindings)
	ctx := context.Background()
	first, err := service.StartSession(ctx, "player", ba.EntityID)
	require.NoError(t, err)
	second, err := service.StartSession(ctx, "player", bb.EntityID)
	require.NoError(t, err)
	_, err = service.SubmitMessage(ctx, first.ID, "hello")
	require.NoError(t, err)
	_, err = service.SubmitMessage(ctx, second.ID, "hello")
	require.NoError(t, err)
	assert.Contains(t, llm.requests[0].Messages[0].Content, "Merchant identity")
	assert.Contains(t, llm.requests[1].Messages[0].Content, "Guide identity")
	legacy, err := service.StartSession(ctx, "player", "legacy")
	require.NoError(t, err)
	assert.Nil(t, legacy.NPCContent)
	original := first.SystemPrompt
	catalog := DefaultSystemPromptCatalog()
	catalog.Template = "new global\n" + catalog.Template
	require.NoError(t, service.ActivateSystemPromptCatalog(catalog))
	assert.Equal(t, original, first.SystemPrompt)
	require.True(t, service.SaveConversations(ctx, ConversationSaveRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, OperationID: testOpID1, Mode: "create"}).OK)
	loaded := service.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, NPCIDs: []string{ba.EntityID, bb.EntityID, "legacy"}})
	require.True(t, loaded.OK, loaded.Message)
	assert.EqualValues(t, 4, requests.Load())
	// Removing the identity binding rejects restore without replacing live Contexts.
	delete(bindings, ba.EntityID)
	loaded = service.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, NPCIDs: []string{ba.EntityID, bb.EntityID, "legacy"}})
	assert.Equal(t, "NPC_CONTENT_BINDING_CHANGED", loaded.ErrorCode)

}

func TestNPCContentFetchDoesNotHoldLifecycleLock(t *testing.T) {
	data, binding := contentFixture("Merchant")
	started := make(chan struct{})
	release := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		close(started)
		<-release
		w.Write(data)
	}))
	defer server.Close()
	resolver, err := NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	service := NewConversationService(&scriptedLLM{}, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("legacy"), "model", 3)
	service.ConfigureNPCContent(resolver, testBindings{binding.EntityID: binding})
	finished := make(chan error, 1)
	go func() {
		_, err := service.StartSession(context.Background(), "player", binding.EntityID)
		finished <- err
	}()
	<-started
	activated := make(chan error, 1)
	go func() { activated <- service.ActivateSystemPromptCatalog(DefaultSystemPromptCatalog()) }()
	unlocked := false
	select {
	case err := <-activated:
		require.NoError(t, err)
		unlocked = true
	case <-time.After(time.Second):
	}
	close(release)
	require.NoError(t, <-finished)
	require.True(t, unlocked, "HTTP resolution held the Service lifecycle lock")
}

func TestNPCResolverRejectsUntrustedContentAndRetries(t *testing.T) {
	valid, binding := contentFixture("Merchant")
	var body atomic.Value
	body.Store([]byte(`{"schemaVersion":1}`))
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.Write(body.Load().([]byte)) }))
	defer server.Close()
	resolver, err := NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	_, err = resolver.Resolve(context.Background(), binding)
	assert.EqualError(t, err, "NPC_CONTENT_JSON_INVALID")
	body.Store(valid)
	_, err = resolver.Resolve(context.Background(), binding)
	require.NoError(t, err)
	d, err := resolver.Resolve(context.Background(), binding)
	require.NoError(t, err)
	d.Profile.DisplayName = "mutated"
	d, err = resolver.Resolve(context.Background(), binding)
	require.NoError(t, err)
	assert.NotEqual(t, "mutated", d.Profile.DisplayName)
	for _, id := range []string{"../x", "a/b", "a%2fb", "a\\b", ".", ""} {
		_, err = resolver.Resolve(context.Background(), NPCContentBinding{id})
		require.Error(t, err)
	}
	bads := [][]byte{[]byte(`{"x":1,"x":2}`), []byte(`null`), append(valid, []byte(`{}`)...), []byte(strings.Replace(string(valid), `"name":`, `"schemaVersion":1,"name":`, 1)), []byte(strings.Replace(string(valid), `"npcId":"Merchant"`, `"npcId":"wrong"`, 1)), []byte(strings.Replace(string(valid), `"visual"`, `"unknown"`, 1)), []byte(strings.Replace(string(valid), "{{npcId}}", "{{unknown}}", 1)), make([]byte, MaxNPCContentBytes+1)}
	for _, bad := range bads {
		_, err = ParseNPCContent(bad, bindingFor(binding.EntityID, bad))
		require.Error(t, err)
	}
}

func TestNPCResolverNoRedirectAndCancellation(t *testing.T) {
	var hits atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { hits.Add(1) }))
	defer target.Close()
	redirect := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { http.Redirect(w, r, target.URL, 302) }))
	defer redirect.Close()
	_, b := contentFixture("Merchant")
	resolver, err := NewNPCContentResolver(redirect.URL)
	require.NoError(t, err)
	_, err = resolver.Resolve(context.Background(), b)
	require.Error(t, err)
	assert.Zero(t, hits.Load())
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { <-r.Context().Done() }))
	defer server.Close()
	resolver, err = NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	_, err = resolver.Resolve(ctx, b)
	require.Error(t, err)
}

func TestRepositoryNPCStaticContract(t *testing.T) {
	root := filepath.Join("..", "..", "..", "NpcContent")
	raw, err := os.ReadFile(filepath.Join(root, "npc", "index.json"))
	require.NoError(t, err)
	index, err := ParseNPCIndex(raw)
	require.NoError(t, err)
	require.NotEmpty(t, index.NPCs)
	for _, entry := range index.NPCs {
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(entry.ManifestPath)))
		require.NoError(t, err)
		definition, err := ParseNPCContent(data, NPCContentBinding{entry.Name})
		require.NoError(t, err)
		assert.Equal(t, "builtin", definition.AnimationDriver.Kind)
		assert.Equal(t, "standard-locomotion", definition.AnimationDriver.DriverID)
	}
}
func TestNewContextReadsCurrentContent(t *testing.T) {
	original, binding := contentFixture("Merchant")
	var body atomic.Value
	body.Store(original)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.Write(body.Load().([]byte)) }))
	defer server.Close()
	resolver, err := NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	service := NewConversationServiceWithArchive(&scriptedLLM{}, NewMemorySessionStore(), &fakeRuntime{}, testProfileCatalog("legacy"), "model", 3, NewFileConversationArchive(t.TempDir()))
	service.ConfigureNPCContent(resolver, testBindings{binding.EntityID: binding})
	first, err := service.StartSession(context.Background(), "player", binding.EntityID)
	require.NoError(t, err)
	body.Store([]byte(strings.Replace(string(original), "Merchant identity", "changed identity", 1)))
	second, err := service.StartSession(context.Background(), "another-player", binding.EntityID)
	require.NoError(t, err)
	assert.Contains(t, first.SystemPrompt, "Merchant identity")
	assert.Contains(t, second.SystemPrompt, "changed identity")
	ctx := context.Background()
	saved := service.SaveConversations(ctx, ConversationSaveRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, OperationID: testOpID1, Mode: "create"})
	require.True(t, saved.OK, saved.Message)
	restored := service.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, NPCIDs: []string{binding.EntityID}})
	require.True(t, restored.OK, restored.Message)
	current, err := service.store.Load(ctx, restored.Contexts[0].SessionID)
	require.NoError(t, err)
	assert.Contains(t, current.SystemPrompt, "changed identity")
	body.Store([]byte(`{"schemaVersion":1}`))
	failed := service.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, NPCIDs: []string{binding.EntityID}})
	assert.False(t, failed.OK)
	retained, err := service.store.Load(ctx, current.ID)
	require.NoError(t, err)
	assert.Equal(t, current.SystemPrompt, retained.SystemPrompt)

}

func TestCurrentNPCNamesAndOldFormats(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join("..", "..", "..", "NpcContent", "npc", "index.json"))
	require.NoError(t, err)
	var wire map[string]any
	require.NoError(t, json.Unmarshal(raw, &wire))
	entries := wire["npcs"].([]any)
	entry := entries[0].(map[string]any)
	duplicate := map[string]any{}
	for key, value := range entry {
		duplicate[key] = value
	}
	duplicate["name"] = strings.ToLower(entry["name"].(string))
	wire["npcs"] = append(entries, duplicate)
	bad, _ := json.Marshal(wire)
	_, err = ParseNPCIndex(bad)
	require.Error(t, err)
	wire["npcs"] = entries
	wire["schemaVersion"] = 1
	bad, _ = json.Marshal(wire)
	_, err = ParseNPCIndex(bad)
	require.Error(t, err)
	for _, name := range []string{"CON", "lpt1", "1Merchant", "Merchant-1", "../x"} {
		assert.False(t, ValidNPCName(name))
	}
	var binding NPCContentBinding
	assert.Error(t, json.Unmarshal([]byte(`{"entityId":"Merchant","contentVersion":"3","manifestSha256":"ignored"}`), &binding))
	assert.NoError(t, json.Unmarshal([]byte(`{"entityId":"Merchant"}`), &binding))
	assert.EqualError(t, validateConversationSnapshot(conversationSnapshot{SnapshotVersion: 1}), "unsupported conversation snapshot version")
}
