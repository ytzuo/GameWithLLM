package agent

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"sync"
	"time"
)

const MaxNPCContentBytes = 256 * 1024

var contentSegment = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`)
var contentHash = regexp.MustCompile(`^[0-9a-f]{64}$`)

type NPCContentBinding struct {
	EntityID       string `json:"entityId"`
	ContentVersion string `json:"contentVersion"`
	ManifestSHA256 string `json:"manifestSha256"`
}

func (b *NPCContentBinding) UnmarshalJSON(data []byte) error {
	type binding NPCContentBinding
	if rejectDuplicateJSONProperties(data) != nil {
		return errors.New("NPC_CONTENT_BINDING_INVALID")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var value binding
	if decoder.Decode(&value) != nil {
		return errors.New("NPC_CONTENT_BINDING_INVALID")
	}
	*b = NPCContentBinding(value)
	return b.Validate()
}

func (b NPCContentBinding) Validate() error {
	if !contentSegment.MatchString(b.EntityID) || !contentSegment.MatchString(b.ContentVersion) || !contentHash.MatchString(b.ManifestSHA256) {
		return errors.New("NPC_CONTENT_BINDING_INVALID")
	}
	return nil
}

type NPCVisual struct {
	PrefabAddress             string   `json:"prefabAddress"`
	AnimatorControllerAddress string   `json:"animatorControllerAddress"`
	AnimationAddresses        []string `json:"animationAddresses"`
	MaterialAddresses         []string `json:"materialAddresses"`
	TextureAddresses          []string `json:"textureAddresses"`
}
type NPCAnimationScript struct {
	Address      string `json:"address"`
	AssemblyName string `json:"assemblyName"`
	EntryType    string `json:"entryType"`
	Length       int64  `json:"length"`
	SHA256       string `json:"sha256"`
}
type NPCContentDefinition struct {
	SchemaVersion   int                 `json:"schemaVersion"`
	NPCID           string              `json:"npcId"`
	ContentVersion  string              `json:"contentVersion"`
	PlayerBuildID   string              `json:"playerBuildId"`
	Visual          NPCVisual           `json:"visual"`
	AnimationScript NPCAnimationScript  `json:"animationScript"`
	Profile         NPCProfile          `json:"profile"`
	SystemPrompt    SystemPromptCatalog `json:"systemPrompt"`
}

func ParseNPCContent(data []byte, binding NPCContentBinding) (*NPCContentDefinition, error) {
	if binding.Validate() != nil || len(data) > MaxNPCContentBytes {
		return nil, errors.New("NPC_CONTENT_INVALID")
	}
	hash := sha256.Sum256(data)
	if hex.EncodeToString(hash[:]) != binding.ManifestSHA256 {
		return nil, errors.New("NPC_CONTENT_HASH_MISMATCH")
	}
	if rejectDuplicateJSONProperties(data) != nil {
		return nil, errors.New("NPC_CONTENT_JSON_INVALID")
	}
	var d NPCContentDefinition
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&d) != nil {
		return nil, errors.New("NPC_CONTENT_JSON_INVALID")
	}
	if d.SchemaVersion != 1 || d.NPCID != binding.EntityID || d.ContentVersion != binding.ContentVersion || d.Profile.NPCID != d.NPCID || d.SystemPrompt.ContentVersion != d.ContentVersion || d.PlayerBuildID == "" || validateNPCProfile(d.Profile) != nil || d.SystemPrompt.Validate() != nil {
		return nil, errors.New("NPC_CONTENT_DEFINITION_INVALID")
	}
	prefix := "npc/" + d.NPCID + "/" + d.ContentVersion + "/"
	addresses := append([]string{d.Visual.PrefabAddress, d.Visual.AnimatorControllerAddress, d.AnimationScript.Address}, d.Visual.AnimationAddresses...)
	addresses = append(addresses, d.Visual.MaterialAddresses...)
	addresses = append(addresses, d.Visual.TextureAddresses...)
	for _, address := range addresses {
		if !strings.HasPrefix(address, prefix) || strings.Contains(address, "..") || strings.ContainsAny(address, "\\:%?#") {
			return nil, errors.New("NPC_CONTENT_ADDRESS_INVALID")
		}
	}
	if d.AnimationScript.Length <= 0 || d.AnimationScript.Length > 16*1024*1024 || !contentHash.MatchString(d.AnimationScript.SHA256) || d.AnimationScript.AssemblyName == "" || d.AnimationScript.EntryType == "" {
		return nil, errors.New("NPC_CONTENT_SCRIPT_INVALID")
	}
	return &d, nil
}

// Successful immutable bytes are cached; each caller receives an independent definition.
type NPCContentResolver struct {
	base   *url.URL
	client *http.Client
	mu     sync.Mutex
	cache  map[NPCContentBinding][]byte
}

func NewNPCContentResolver(base string) (*NPCContentResolver, error) {
	u, err := url.Parse(base)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || u.RawPath != "" {
		return nil, errors.New("NPC_CONTENT_BASE_URL_INVALID")
	}
	u.Path = strings.TrimRight(u.Path, "/") + "/"
	return &NPCContentResolver{base: u, client: &http.Client{Timeout: 10 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}, cache: make(map[NPCContentBinding][]byte)}, nil
}
func (r *NPCContentResolver) Resolve(ctx context.Context, b NPCContentBinding) (*NPCContentDefinition, error) {
	if ctx.Err() != nil {
		return nil, errors.New("NPC_CONTENT_FETCH_FAILED")
	}
	if b.Validate() != nil {
		return nil, errors.New("NPC_CONTENT_BINDING_INVALID")
	}
	if r == nil {
		return nil, errors.New("NPC_CONTENT_NOT_CONFIGURED")
	}
	r.mu.Lock()
	data, ok := r.cache[b]
	r.mu.Unlock()
	if ok {
		return ParseNPCContent(data, b)
	}
	u := *r.base
	u.Path += "npc/" + b.EntityID + "/" + b.ContentVersion + "/npc.json"
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return nil, errors.New("NPC_CONTENT_FETCH_FAILED")
	}
	response, err := r.client.Do(request)
	if err != nil {
		return nil, errors.New("NPC_CONTENT_FETCH_FAILED")
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return nil, errors.New("NPC_CONTENT_FETCH_FAILED")
	}
	data, err = io.ReadAll(io.LimitReader(response.Body, MaxNPCContentBytes+1))
	if err != nil {
		return nil, errors.New("NPC_CONTENT_FETCH_FAILED")
	}
	d, err := ParseNPCContent(data, b)
	if err != nil {
		return nil, err
	}
	r.mu.Lock()
	r.cache[b] = data
	r.mu.Unlock()
	return d, nil
}

type NPCContentBindingSource interface {
	NPCContentBinding(context.Context, string, string) (*NPCContentBinding, error)
}

// ConfigureNPCContent is called once during service construction, before requests are admitted.
func (s *Service) ConfigureNPCContent(resolver *NPCContentResolver, source NPCContentBindingSource) {
	s.contentResolver = resolver
	s.contentBindings = source
}
func (s *Service) resolvePrompt(ctx context.Context, instanceID, npcID string) (string, *NPCContentBinding, error) {
	if s.contentBindings != nil {
		b, err := s.contentBindings.NPCContentBinding(ctx, instanceID, npcID)
		if err != nil {
			return "", nil, err
		}
		if b != nil {
			d, err := s.contentResolver.Resolve(ctx, *b)
			if err != nil {
				return "", nil, err
			}
			current, err := s.contentBindings.NPCContentBinding(ctx, instanceID, npcID)
			if err != nil || !sameNPCBinding(current, b) {
				return "", nil, errors.New("NPC_CONTENT_BINDING_CHANGED")
			}
			return d.SystemPrompt.Build(d.Profile), b, nil
		}
	}
	profile, found := s.profiles.Get(npcID)
	if !found {
		return "", nil, ErrNPCProfileNotFound
	}
	s.lifecycleMu.Lock()
	catalog := s.promptCatalog
	s.lifecycleMu.Unlock()
	return catalog.Build(profile), nil, nil
}

func sameNPCBinding(a, b *NPCContentBinding) bool {
	return (a == nil && b == nil) || (a != nil && b != nil && *a == *b)
}
