package main

import (
	"os"
	"path/filepath"
	"testing"
)

func copyCurrent(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	source := filepath.Join("..", "..", "..", "NpcContent")
	err := filepath.WalkDir(filepath.Join(source, "npc"), func(path string, item os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		dest := filepath.Join(root, rel)
		if item.IsDir() {
			return os.MkdirAll(dest, 0755)
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		return os.WriteFile(dest, data, 0644)
	})
	if err != nil {
		t.Fatal(err)
	}
	return root
}

func TestCurrentTree(t *testing.T) {
	if err := validateRoot(copyCurrent(t)); err != nil {
		t.Fatal(err)
	}
}

func TestRejectGeneratedHistoryAndCorruptAvatar(t *testing.T) {
	for _, name := range []string{"deleted-npc", "history-file", "truncated-png"} {
		t.Run(name, func(t *testing.T) {
			root := copyCurrent(t)
			var err error
			switch name {
			case "deleted-npc":
				err = os.Mkdir(filepath.Join(root, "npc", "DeletedNpc"), 0755)
			case "history-file":
				err = os.WriteFile(filepath.Join(root, "npc", "Merchant", "old.json"), []byte("{}"), 0644)
			case "truncated-png":
				err = os.WriteFile(filepath.Join(root, "npc", "Merchant", "avatar.png"), []byte("\x89PNG\r\n\x1a\n"), 0644)
			}
			if err != nil {
				t.Fatal(err)
			}
			if validateRoot(root) == nil {
				t.Fatal("invalid generated content accepted")
			}
		})
	}
}
