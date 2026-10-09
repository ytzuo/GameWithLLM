package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
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
	prefix := "npc/" + id + "/1/"
	prompt := *DefaultSystemPromptCatalog()
	prompt.ContentVersion = "1"
	prompt.Template = id + " identity\n" + prompt.Template
	d := NPCContentDefinition{SchemaVersion: 1, NPCID: id, ContentVersion: "1", PlayerBuildID: "windows-x64-0.1.0", Profile: testNPCProfile(id), SystemPrompt: prompt, Visual: NPCVisual{PrefabAddress: prefix + "visual", AnimatorControllerAddress: prefix + "controller"}, AnimationScript: NPCAnimationScript{Address: prefix + "script", AssemblyName: "Sample." + id + ".V1", EntryType: "Sample.Driver", Length: 100, SHA256: strings.Repeat("a", 64)}}
	data, _ := json.Marshal(d)
	return data, bindingFor(id, data)
}
func bindingFor(id string, data []byte) NPCContentBinding {
	sum := sha256.Sum256(data)
	return NPCContentBinding{id, "1", hex.EncodeToString(sum[:])}
}

func TestNPCResolverAndContextIsolation(t *testing.T) {
	a, ba := contentFixture("merchant_001")
	b, bb := contentFixture("guide_001")
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		switch r.URL.Path {
		case "/npc/merchant_001/1/npc.json":
			w.Write(a)
		case "/npc/guide_001/1/npc.json":
			w.Write(b)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	resolver, err := NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	unknown := ba
	unknown.ContentVersion = "unknown"
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
	assert.Contains(t, llm.requests[0].Messages[0].Content, "merchant_001 identity")
	assert.Contains(t, llm.requests[1].Messages[0].Content, "guide_001 identity")
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
	assert.EqualValues(t, 2, requests.Load())
	// Same ID/version, different hash is a conflict as well.
	changed := ba
	changed.ManifestSHA256 = strings.Repeat("b", 64)
	bindings[ba.EntityID] = changed
	loaded = service.LoadConversations(ctx, ConversationLoadRequest{InstanceID: "game-1", PlayerID: "player", SaveID: testSaveID, NPCIDs: []string{ba.EntityID, bb.EntityID, "legacy"}})
	assert.Equal(t, "NPC_CONTENT_VERSION_MISMATCH", loaded.ErrorCode)
}

func TestNPCContentFetchDoesNotHoldLifecycleLock(t *testing.T) {
	data, binding := contentFixture("merchant_001")
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
	valid, binding := contentFixture("merchant_001")
	var body atomic.Value
	body.Store([]byte(`{"schemaVersion":1}`))
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.Write(body.Load().([]byte)) }))
	defer server.Close()
	resolver, err := NewNPCContentResolver(server.URL)
	require.NoError(t, err)
	_, err = resolver.Resolve(context.Background(), binding)
	assert.EqualError(t, err, "NPC_CONTENT_HASH_MISMATCH")
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
		_, err = resolver.Resolve(context.Background(), NPCContentBinding{id, "1", binding.ManifestSHA256})
		require.Error(t, err)
	}
	bads := [][]byte{[]byte(`{"x":1,"x":2}`), []byte(`null`), append(valid, []byte(`{}`)...), []byte(strings.Replace(string(valid), `"schemaVersion":1`, `"schemaVersion":2`, 1)), []byte(strings.Replace(string(valid), `"npcId":"merchant_001"`, `"npcId":"wrong"`, 1)), []byte(strings.Replace(string(valid), `"playerBuildId"`, `"unknown"`, 1)), []byte(strings.Replace(string(valid), "{{npcId}}", "{{unknown}}", 1)), make([]byte, MaxNPCContentBytes+1)}
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
	_, b := contentFixture("merchant_001")
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
	require.Len(t, index.NPCs, 2)
	for _, entry := range index.NPCs {
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(entry.ManifestPath)))
		require.NoError(t, err)
		_, err = ParseNPCContent(data, NPCContentBinding{entry.NPCID, entry.ContentVersion, entry.ManifestSHA256})
		require.NoError(t, err)
	}
}
