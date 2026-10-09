// Validates staged static content with the same strict parser used by the service.
package main

import (
	"GameMCPServer/internal/agent"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run() error {
	if len(os.Args) != 2 {
		return fmt.Errorf("usage: npc-content-validate <content-root>")
	}
	root := os.Args[1]
	data, err := os.ReadFile(filepath.Join(root, "npc", "index.json"))
	if err != nil {
		return err
	}
	index, err := agent.ParseNPCIndex(data)
	if err != nil {
		return err
	}
	for _, entry := range index.NPCs {
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(entry.ManifestPath)))
		if err != nil {
			return err
		}
		d, err := agent.ParseNPCContent(data, agent.NPCContentBinding{EntityID: entry.NPCID, ContentVersion: entry.ContentVersion, ManifestSHA256: entry.ManifestSHA256})
		if err != nil {
			return err
		}
		directory := filepath.Dir(filepath.Join(root, filepath.FromSlash(entry.ManifestPath)))
		dll, err := os.ReadFile(filepath.Join(directory, "animation.dll.bytes"))
		if err != nil {
			return err
		}
		sum := sha256.Sum256(dll)
		if int64(len(dll)) != d.AnimationScript.Length || hex.EncodeToString(sum[:]) != d.AnimationScript.SHA256 {
			return fmt.Errorf("NPC animation hash/length mismatch")
		}
		avatar, err := os.ReadFile(filepath.Join(directory, "avatar.png"))
		if err != nil {
			return err
		}
		if len(avatar) > 256*1024 || len(avatar) < 8 || string(avatar[:8]) != "\x89PNG\r\n\x1a\n" {
			return fmt.Errorf("NPC avatar invalid")
		}
	}
	fmt.Printf("NPC_CONTENT_VALIDATED npc_count=%d\n", len(index.NPCs))
	return nil
}
