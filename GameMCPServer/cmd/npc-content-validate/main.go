// Validates staged static content with the same strict parser used by the service.
package main

import (
	"GameMCPServer/internal/agent"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"image/png"
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
	return validateRoot(root)
}

func validateRoot(root string) error {
	data, err := os.ReadFile(filepath.Join(root, "npc", "index.json"))
	if err != nil {
		return err
	}
	index, err := agent.ParseNPCIndex(data)
	if err != nil {
		return err
	}
	expected := map[string]bool{"index.json": true}
	for _, entry := range index.NPCs {
		expected[entry.Name] = true
		expected[entry.Name+"/npc.json"] = true
		expected[entry.Name+"/avatar.png"] = true
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(entry.ManifestPath)))
		if err != nil {
			return err
		}
		d, err := agent.ParseNPCContent(data, agent.NPCContentBinding{EntityID: entry.Name})
		if err != nil {
			return err
		}
		directory := filepath.Dir(filepath.Join(root, filepath.FromSlash(entry.ManifestPath)))
		if d.AnimationDriver.Kind == "hotUpdate" {
			expected[entry.Name+"/animation.dll.bytes"] = true
			dll, err := os.ReadFile(filepath.Join(directory, "animation.dll.bytes"))
			if err != nil {
				return err
			}
			sum := sha256.Sum256(dll)
			if int64(len(dll)) != d.AnimationDriver.Length || hex.EncodeToString(sum[:]) != d.AnimationDriver.SHA256 {
				return fmt.Errorf("NPC animation hash/length mismatch")
			}
		}
		avatar, err := os.ReadFile(filepath.Join(directory, "avatar.png"))
		if err != nil {
			return err
		}
		if len(avatar) > 256*1024 || len(avatar) < 8 || string(avatar[:8]) != "\x89PNG\r\n\x1a\n" {
			return fmt.Errorf("NPC avatar invalid")
		}
		config, err := png.DecodeConfig(bytes.NewReader(avatar))
		if err != nil || config.Width < 1 || config.Height < 1 || config.Width > 2048 || config.Height > 2048 {
			return fmt.Errorf("NPC avatar dimensions invalid")
		}
		if _, err := png.Decode(bytes.NewReader(avatar)); err != nil {
			return fmt.Errorf("NPC avatar invalid: %w", err)
		}
	}
	// This is a dedicated generated tree: orphan directories, history and symlinks
	// must not silently enter a candidate alongside the current index.
	npcRoot := filepath.Join(root, "npc")
	if err := filepath.WalkDir(npcRoot, func(path string, item os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if path == npcRoot {
			return nil
		}
		rel, err := filepath.Rel(npcRoot, path)
		if err != nil {
			return err
		}
		if item.Type()&os.ModeSymlink != 0 || !expected[filepath.ToSlash(rel)] {
			return fmt.Errorf("unexpected NPC generated artifact: %s", filepath.ToSlash(rel))
		}
		return nil
	}); err != nil {
		return err
	}
	fmt.Printf("NPC_CONTENT_VALIDATED npc_count=%d\n", len(index.NPCs))
	return nil
}
