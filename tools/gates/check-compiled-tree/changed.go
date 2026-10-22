// changed.go -- the changed set, and the git the gate asks.
//
// The gate's question is "which files differ from the commit the restored
// outputs were built from", and the answer must come from the CONTENT, not
// from the mtimes: the mtimes are what the normalisation is allowed to
// rewrite, and a gate that asks the mtimes is asking the question under
// dispute.
//
// The comparison is against the WORKING TREE, not against HEAD. The
// working tree is what the compiler read: a lane that patched the tree
// after the checkout compiled something other than the commit, and the gate
// has to see the file that differs from the base, whatever the lane did
// with the commit.

package checkcompiledtree

import (
	"bytes"
	"fmt"
	"os/exec"
	"sort"
	"strings"
)

// changedFiles lists the files whose content differs between the base
// commit and the tree under test.
//
// The base commit is the one the restored outputs were built from. It is
// resolved the way the normalisation resolves it: a commit that is not in
// this clone is fetched, because GitHub serves a commit by SHA and the
// clone is shallow.
func changedFiles(root string, base string) (map[string]bool, error) {
	if _, err := resolveBase(root, base); err != nil {
		return nil, err
	}

	changed := map[string]bool{}

	// The tracked files that differ, compared against the working tree.
	code, out, errOut := git(root, "diff", "--name-only", "-z", "--no-renames", base)
	if code != 0 {
		return nil, fmt.Errorf("git diff %s failed: %s", base, strings.TrimSpace(errOut))
	}
	for _, path := range splitNull(out) {
		changed[path] = true
	}

	// The files that are new and not tracked yet. A file the lane created
	// after the checkout is part of the tree the compiler read, and the
	// compiler reads it: an untracked source file is a compile input.
	code, out, errOut = git(root, "ls-files", "--others", "--exclude-standard", "-z")
	if code != 0 {
		return nil, fmt.Errorf("git ls-files failed: %s", strings.TrimSpace(errOut))
	}
	for _, path := range splitNull(out) {
		changed[path] = true
	}

	return changed, nil
}

// resolveBase makes sure the base commit is in the clone, and returns its
// SHA.
//
// The clone is shallow, and the commit the cache was built from is usually
// not in it. GitHub serves a commit by SHA, so one shallow fetch brings
// back the only thing that is needed: the TREE.
func resolveBase(root string, base string) (string, error) {
	code, out, _ := git(root, "rev-parse", "--verify", "--quiet", base+"^{commit}")
	if code == 0 && strings.TrimSpace(out) != "" {
		return strings.TrimSpace(out), nil
	}
	_, _, fetchErr := git(root, "fetch", "--no-tags", "--depth=1", "origin", base)
	code, out, _ = git(root, "rev-parse", "--verify", "--quiet", base+"^{commit}")
	if code == 0 && strings.TrimSpace(out) != "" {
		return strings.TrimSpace(out), nil
	}
	detail := strings.TrimSpace(fetchErr)
	if detail == "" {
		detail = "fetch failed"
	}
	return "", fmt.Errorf(
		"the base commit %s is not in this clone (%s) — the changed set is unknown, "+
			"so the gate cannot tell a recompiled project from a skipped one",
		base, detail)
}

// git runs a git command in the repository and returns its code and output.
func git(root string, args ...string) (int, string, string) {
	cmd := exec.Command("git", args...)
	cmd.Dir = root
	cmd.Env = append(exec.Command("git", "--version").Env, "GIT_TERMINAL_PROMPT=0")
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	code := 0
	if err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			code = exitErr.ExitCode()
		} else {
			code = 1
		}
	}
	return code, stdout.String(), stderr.String()
}

// splitNull splits the NUL-separated output of git.
func splitNull(text string) []string {
	var paths []string
	for _, path := range strings.Split(text, "\x00") {
		if path == "" {
			continue
		}
		paths = append(paths, path)
	}
	sort.Strings(paths)
	return paths
}
