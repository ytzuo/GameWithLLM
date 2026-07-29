package agent

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
)

const npcProfileVersion = 1

var ErrNPCProfileNotFound = errors.New("NPC profile not found")

type NPCProfile struct {
	NPCID            string   `json:"npcId"`
	DisplayName      string   `json:"displayName"`
	Personality      []string `json:"personality"`
	SpeakingStyle    string   `json:"speakingStyle"`
	Identity         string   `json:"identity"`
	Responsibilities []string `json:"responsibilities"`
	WorldKnowledge   []string `json:"worldKnowledge"`
	ForbiddenTopics  []string `json:"forbiddenTopics"`
}

type npcProfileFile struct {
	Version  int          `json:"version"`
	Profiles []NPCProfile `json:"profiles"`
}

type NPCProfileCatalog struct {
	profiles map[string]NPCProfile
}

func LoadNPCProfileCatalog(path string) (*NPCProfileCatalog, error) {
	content, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read NPC profiles: %w", err)
	}
	var file npcProfileFile
	decoder := json.NewDecoder(bytes.NewReader(content))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&file); err != nil {
		return nil, fmt.Errorf("decode NPC profiles: %w", err)
	}
	var trailing any
	if err := decoder.Decode(&trailing); err != io.EOF {
		if err == nil {
			return nil, fmt.Errorf("decode NPC profiles: multiple JSON values are not allowed")
		}
		return nil, fmt.Errorf("decode NPC profiles: %w", err)
	}
	if file.Version != npcProfileVersion {
		return nil, fmt.Errorf("unsupported NPC profile version: %d", file.Version)
	}
	return NewNPCProfileCatalog(file.Profiles)
}

func NewNPCProfileCatalog(profiles []NPCProfile) (*NPCProfileCatalog, error) {
	if len(profiles) == 0 {
		return nil, errors.New("at least one NPC profile is required")
	}
	catalog := &NPCProfileCatalog{profiles: make(map[string]NPCProfile, len(profiles))}
	for index, raw := range profiles {
		profile := cloneNPCProfile(raw)
		normalizeNPCProfile(&profile)
		if err := validateNPCProfile(profile); err != nil {
			return nil, fmt.Errorf("profile[%d]: %w", index, err)
		}
		if _, exists := catalog.profiles[profile.NPCID]; exists {
			return nil, fmt.Errorf("duplicate npcId %q", profile.NPCID)
		}
		catalog.profiles[profile.NPCID] = profile
	}
	return catalog, nil
}

func (c *NPCProfileCatalog) Get(npcID string) (NPCProfile, bool) {
	if c == nil {
		return NPCProfile{}, false
	}
	profile, ok := c.profiles[npcID]
	if !ok {
		return NPCProfile{}, false
	}
	return cloneNPCProfile(profile), true
}

func normalizeNPCProfile(profile *NPCProfile) {
	profile.NPCID = strings.TrimSpace(profile.NPCID)
	profile.DisplayName = strings.TrimSpace(profile.DisplayName)
	profile.SpeakingStyle = strings.TrimSpace(profile.SpeakingStyle)
	profile.Identity = strings.TrimSpace(profile.Identity)
	trimStrings(profile.Personality)
	trimStrings(profile.Responsibilities)
	trimStrings(profile.WorldKnowledge)
	trimStrings(profile.ForbiddenTopics)
}

func trimStrings(values []string) {
	for index := range values {
		values[index] = strings.TrimSpace(values[index])
	}
}

func validateNPCProfile(profile NPCProfile) error {
	if err := validateProfileText("npcId", profile.NPCID, 100); err != nil {
		return err
	}
	if err := validateProfileText("displayName", profile.DisplayName, 80); err != nil {
		return err
	}
	if err := validateProfileText("identity", profile.Identity, 300); err != nil {
		return err
	}
	if err := validateProfileText("speakingStyle", profile.SpeakingStyle, 500); err != nil {
		return err
	}
	for name, values := range map[string][]string{
		"personality": profile.Personality, "responsibilities": profile.Responsibilities,
		"worldKnowledge":  profile.WorldKnowledge,
		"forbiddenTopics": profile.ForbiddenTopics,
	} {
		if len(values) == 0 || len(values) > 20 {
			return fmt.Errorf("%s must contain between 1 and 20 items", name)
		}
		seen := make(map[string]struct{}, len(values))
		for _, value := range values {
			if err := validateProfileText(name+" item", value, 300); err != nil {
				return err
			}
			if _, exists := seen[value]; exists {
				return fmt.Errorf("%s contains duplicate item %q", name, value)
			}
			seen[value] = struct{}{}
		}
	}
	return nil
}

func validateProfileText(name, value string, maximum int) error {
	if value == "" {
		return fmt.Errorf("%s is required", name)
	}
	if len([]rune(value)) > maximum {
		return fmt.Errorf("%s exceeds %d characters", name, maximum)
	}
	return nil
}

func cloneNPCProfile(profile NPCProfile) NPCProfile {
	profile.Personality = append([]string(nil), profile.Personality...)
	profile.Responsibilities = append([]string(nil), profile.Responsibilities...)
	profile.WorldKnowledge = append([]string(nil), profile.WorldKnowledge...)
	profile.ForbiddenTopics = append([]string(nil), profile.ForbiddenTopics...)
	return profile
}
