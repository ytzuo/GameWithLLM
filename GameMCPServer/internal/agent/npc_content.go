package agent

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

const MaxNPCContentBytes = 256 * 1024

var contentSegment = regexp.MustCompile(`^[A-Za-z][A-Za-z0-9_]{0,63}$`)
var contentHash = regexp.MustCompile(`^[0-9a-f]{64}$`)

type NPCContentBinding struct {
	EntityID string `json:"entityId"`
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
	if !ValidNPCName(b.EntityID) {
		return errors.New("NPC_CONTENT_BINDING_INVALID")
	}
	return nil
}

// ValidNPCName also rejects Windows device names, including case variants.
func ValidNPCName(name string) bool {
	if !contentSegment.MatchString(name) {
		return false
	}
	upper := strings.ToUpper(name)
	switch upper {
	case "CON", "PRN", "AUX", "NUL":
		return false
	}
	return !(len(upper) == 4 && (strings.HasPrefix(upper, "COM") || strings.HasPrefix(upper, "LPT")) && upper[3] >= '1' && upper[3] <= '9')
}

type NPCVisual struct {
	PrefabAddress string `json:"prefabAddress"`
}
type NPCAnimationDriver struct {
	Kind         string `json:"kind"`
	DriverID     string `json:"driverId,omitempty"`
	Address      string `json:"address,omitempty"`
	AssemblyName string `json:"assemblyName,omitempty"`
	EntryType    string `json:"entryType,omitempty"`
	Length       int64  `json:"length,omitempty"`
	SHA256       string `json:"sha256,omitempty"`
}
type NPCPrompt struct {
	Locale   string `json:"locale"`
	Template string `json:"template"`
}

func (p NPCPrompt) catalog() *SystemPromptCatalog {
	return &SystemPromptCatalog{SchemaVersion: 1, ContentVersion: "current", Locale: p.Locale, Template: p.Template}
}
func (p NPCPrompt) Build(profile NPCProfile) string { return p.catalog().Build(profile) }

type NPCContentDefinition struct {
	Name            string             `json:"name"`
	Visual          NPCVisual          `json:"visual"`
	AnimationDriver NPCAnimationDriver `json:"animationDriver"`
	Profile         NPCProfile         `json:"profile"`
	SystemPrompt    NPCPrompt          `json:"systemPrompt"`
}

func exactNPCObject(data []byte, fields ...string) bool {
	var obj map[string]json.RawMessage
	if json.Unmarshal(data, &obj) != nil || len(obj) != len(fields) {
		return false
	}
	for _, name := range fields {
		if _, ok := obj[name]; !ok {
			return false
		}
	}
	return true
}
func ParseNPCContent(data []byte, binding NPCContentBinding) (*NPCContentDefinition, error) {
	if binding.Validate() != nil || len(data) > MaxNPCContentBytes {
		return nil, errors.New("NPC_CONTENT_INVALID")
	}
	if rejectDuplicateJSONProperties(data) != nil || !exactNPCObject(data, "name", "visual", "animationDriver", "profile", "systemPrompt") {
		return nil, errors.New("NPC_CONTENT_JSON_INVALID")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var d NPCContentDefinition
	if decoder.Decode(&d) != nil {
		return nil, errors.New("NPC_CONTENT_JSON_INVALID")
	}
	if d.Name != binding.EntityID || d.Profile.NPCID != d.Name || validateNPCProfile(d.Profile) != nil || d.SystemPrompt.catalog().Validate() != nil {
		return nil, errors.New("NPC_CONTENT_DEFINITION_INVALID")
	}
	var wire map[string]json.RawMessage
	_ = json.Unmarshal(data, &wire)
	if !exactNPCObject(wire["visual"], "prefabAddress") || !exactNPCObject(wire["systemPrompt"], "locale", "template") {
		return nil, errors.New("NPC_CONTENT_JSON_INVALID")
	}
	if d.AnimationDriver.Kind == "builtin" {
		if !exactNPCObject(wire["animationDriver"], "kind", "driverId") {
			return nil, errors.New("NPC_CONTENT_SCRIPT_INVALID")
		}
	} else if !exactNPCObject(wire["animationDriver"], "kind", "address", "assemblyName", "entryType", "length", "sha256") {
		return nil, errors.New("NPC_CONTENT_SCRIPT_INVALID")
	}
	prefix := "npc/" + d.Name + "/"
	if d.Visual.PrefabAddress != prefix+"visual" {
		return nil, errors.New("NPC_CONTENT_ADDRESS_INVALID")
	}
	driver := d.AnimationDriver
	if driver.Kind == "builtin" {
		if driver.DriverID != "standard-locomotion" || driver.Address != "" || driver.AssemblyName != "" || driver.EntryType != "" || driver.Length != 0 || driver.SHA256 != "" {
			return nil, errors.New("NPC_CONTENT_SCRIPT_INVALID")
		}
	} else if driver.Kind != "hotUpdate" || driver.DriverID != "" || driver.Address != prefix+"animation-script" || driver.Length <= 0 || driver.Length > 16*1024*1024 || !contentHash.MatchString(driver.SHA256) || driver.AssemblyName == "" || driver.EntryType == "" {
		return nil, errors.New("NPC_CONTENT_SCRIPT_INVALID")
	}
	return &d, nil
}

// Each new Context resolves current bytes; existing Contexts retain their prompt.
type NPCContentResolver struct {
	base   *url.URL
	client *http.Client
}

func NewNPCContentResolver(base string) (*NPCContentResolver, error) {
	u, err := url.Parse(base)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || u.RawPath != "" {
		return nil, errors.New("NPC_CONTENT_BASE_URL_INVALID")
	}
	u.Path = strings.TrimRight(u.Path, "/") + "/"
	return &NPCContentResolver{base: u, client: &http.Client{Timeout: 10 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}}, nil
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
	u := *r.base
	u.Path += "npc/" + b.EntityID + "/npc.json"
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
	data, err := io.ReadAll(io.LimitReader(response.Body, MaxNPCContentBytes+1))
	if err != nil {
		return nil, errors.New("NPC_CONTENT_FETCH_FAILED")
	}
	d, err := ParseNPCContent(data, b)
	if err != nil {
		return nil, err
	}
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
