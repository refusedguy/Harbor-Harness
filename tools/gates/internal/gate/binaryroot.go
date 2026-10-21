package gate

// binaryroot.go -- the repository root, derived from the binary's own path.
//
// The python originals that reach for files OUTSIDE the checkout's working
// directory derived their root from `__file__`: tools/changelog-assemble.py
// resolved its repository from its own path, so a copy of the script in a
// throwaway tree worked on that tree.
//
// The compiled gate has the same property, and the same reason to keep it:
// the binary is committed at tools/bin/harbor-gates, so the repository is
// two directories above it. Deriving the root from the executable rather
// than from the working directory is what lets the gate be exercised against
// a fixture tree at all.
//
// The fallback is the working directory's root, for a binary that was copied
// somewhere else.

import (
	"fmt"
	"os"
	"path/filepath"
)

// BinaryRoot is the repository the running binary belongs to.
func BinaryRoot() (string, error) {
	executable, err := os.Executable()
	if err != nil {
		return "", err
	}
	resolved, err := filepath.EvalSymlinks(executable)
	if err != nil {
		resolved = executable
	}
	dir := filepath.Dir(resolved)
	for {
		if isRepoRoot(dir) {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			// The binary is not in a checkout: fall back to the working
			// directory's root.
			return RepoRoot()
		}
		dir = parent
	}
}

// RepoRootOrBroken is RepoRoot with the error already phrased for a gate.
func RepoRootOrBroken() (string, error) {
	root, err := RepoRoot()
	if err != nil {
		return "", fmt.Errorf("cannot determine the repository root: %w", err)
	}
	return root, nil
}
