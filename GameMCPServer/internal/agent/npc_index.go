package agent

import (
	"bytes"
	"encoding/json"
	"errors"
	"strings"
)

// Frozen UI keys. Values are plain text; progressFormat uses {downloaded}/{total}.
var NPCTextKeys = []string{"remoteTab", "localTab", "download", "spawn", "cancel", "retry", "close", "refresh", "alreadySpawned", "restartRequired", "progressFormat", "downloading", "installed", "remoteEmpty", "localEmpty", "errorFetch", "errorContent", "errorCompatibility", "errorDownload", "errorCancelled", "errorSpawn", "errorBusy"}

type NPCIndexEntry struct {
	NPCID            string `json:"npcId"`
	ContentVersion   string `json:"contentVersion"`
	DisplayName      string `json:"displayName"`
	Description      string `json:"description"`
	AvatarPath       string `json:"avatarPath"`
	ManifestPath     string `json:"manifestPath"`
	ManifestSHA256   string `json:"manifestSha256"`
	MinPlayerVersion string `json:"minPlayerVersion"`
	MaxPlayerVersion string `json:"maxPlayerVersion"`
}
type NPCIndex struct {
	SchemaVersion         int               `json:"schemaVersion"`
	CatalogContentVersion string            `json:"catalogContentVersion"`
	Texts                 map[string]string `json:"texts"`
	NPCs                  []NPCIndexEntry   `json:"npcs"`
}

func ParseNPCIndex(data []byte) (*NPCIndex, error) {
	if len(data) > MaxNPCContentBytes || rejectDuplicateJSONProperties(data) != nil {
		return nil, errors.New("NPC_INDEX_JSON_INVALID")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var index NPCIndex
	if decoder.Decode(&index) != nil || index.SchemaVersion != 1 || strings.TrimSpace(index.CatalogContentVersion) == "" || index.NPCs == nil {
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
		b := NPCContentBinding{entry.NPCID, entry.ContentVersion, entry.ManifestSHA256}
		prefix := "npc/" + entry.NPCID + "/" + entry.ContentVersion + "/"
		if b.Validate() != nil || seen[entry.NPCID] || entry.ManifestPath != prefix+"npc.json" || entry.AvatarPath != prefix+"avatar.png" || strings.TrimSpace(entry.DisplayName) == "" || strings.TrimSpace(entry.Description) == "" || entry.MinPlayerVersion == "" || entry.MaxPlayerVersion == "" {
			return nil, errors.New("NPC_INDEX_ENTRY_INVALID")
		}
		seen[entry.NPCID] = true
	}
	return &index, nil
}
