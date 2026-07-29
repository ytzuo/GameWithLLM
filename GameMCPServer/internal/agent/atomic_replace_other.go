//go:build !windows

package agent

import "os"

func replaceFileAtomically(source, target string) error {
	return os.Rename(source, target)
}
