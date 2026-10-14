package agent

import (
	"bytes"
	"encoding/json"
	"errors"
	"strings"
)

// Frozen UI keys. Values are plain text; progressFormat uses {downloaded}/{total}.
var NPCTextKeys = []string{"remoteTab", "localTab", "download", "spawn", "cancel", "retry", "close", "refresh", "alreadySpawned", "restartRequired", "progressFormat", "downloading", "installed", "remoteEmpty", "localEmpty", "errorFetch", "errorContent", "errorResource", "errorDownload", "errorCancelled", "errorSpawn", "errorBusy"}

type NPCIndexEntry struct {
	Name         string `json:"name"`
	DisplayName  string `json:"displayName"`
	Description  string `json:"description"`
	AvatarPath   string `json:"avatarPath"`
	ManifestPath string `json:"manifestPath"`
}
type NPCIndex struct {
	Texts map[string]string `json:"texts"`
	NPCs  []NPCIndexEntry   `json:"npcs"`
}

func ParseNPCIndex(data []byte) (*NPCIndex, error) {
	if len(data) > MaxNPCContentBytes || rejectDuplicateJSONProperties(data) != nil || !exactNPCObject(data, "texts", "npcs") {
		return nil, errors.New("NPC_INDEX_JSON_INVALID")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var index NPCIndex
	if decoder.Decode(&index) != nil || index.NPCs == nil || len(index.NPCs) > 256 {
		return nil, errors.New("NPC_INDEX_INVALID")
	}
	if len(index.Texts) != len(NPCTextKeys) {
		return nil, errors.New("NPC_INDEX_TEXT_INVALID")
	}
	for _, key := range NPCTextKeys {
		if strings.TrimSpace(index.Texts[key]) == "" || len(index.Texts[key]) > 2048 {
			return nil, errors.New("NPC_INDEX_TEXT_INVALID")
		}
	}
	if !strings.Contains(index.Texts["progressFormat"], "{downloaded}") || !strings.Contains(index.Texts["progressFormat"], "{total}") {
		return nil, errors.New("NPC_INDEX_TEXT_INVALID")
	}
	seen := map[string]bool{}
	for _, entry := range index.NPCs {
		b := NPCContentBinding{entry.Name}
		prefix := "npc/" + entry.Name + "/"
		if b.Validate() != nil || seen[strings.ToLower(entry.Name)] || entry.ManifestPath != prefix+"npc.json" || entry.AvatarPath != prefix+"avatar.png" || strings.TrimSpace(entry.DisplayName) == "" || strings.TrimSpace(entry.Description) == "" || len([]rune(entry.DisplayName)) > 80 || len([]rune(entry.Description)) > 600 {
			return nil, errors.New("NPC_INDEX_ENTRY_INVALID")
		}
		seen[strings.ToLower(entry.Name)] = true
	}
	return &index, nil
}
