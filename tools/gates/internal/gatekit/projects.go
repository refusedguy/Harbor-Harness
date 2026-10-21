package gatekit

import (
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

// ProjectFiles lists the project files in scope, repository-relative.
//
// `git ls-files` rather than a glob, for the reason the python originals
// state: an untracked scratch project must not change what a gate sees, and
// a tracked project must not be able to hide from it. A directory walk is the
// fallback for a throwaway fixture tree, which is not a git repository.
//
// The result is sorted, and the excluded prefixes are dropped.
func ProjectFiles(root string, suffixes []string, excluded []string) []string {
	if tracked, err := trackedFiles(root, suffixes); err == nil && len(tracked) > 0 {
		var kept []string
		for _, path := range tracked {
			if !HasAnyPrefix(path, excluded) {
				kept = append(kept, path)
			}
		}
		sort.Strings(kept)
		return kept
	}
	var found []string
	walkProjects(root, root, suffixes, excluded, &found)
	sort.Strings(found)
	return found
}

// trackedFiles lists the tracked files with the given suffixes.
func trackedFiles(root string, suffixes []string) ([]string, error) {
	cmd := exec.Command("git", "ls-files", "--", "*.csproj", "*.props", "*.targets")
	cmd.Dir = root
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	out, err := cmd.Output()
	if err != nil {
		return nil, err
	}
	var files []string
	for _, line := range strings.Split(string(out), "\n") {
		if line == "" {
			continue
		}
		if HasAnySuffix(line, suffixes) {
			files = append(files, line)
		}
	}
	return files, nil
}

// walkProjects collects the project files of a tree that is not a git
// repository.
func walkProjects(root string, dir string, suffixes []string, excluded []string, found *[]string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	for _, entry := range entries {
		name := entry.Name()
		full := filepath.Join(dir, name)
		if entry.IsDir() {
			if name == "bin" || name == "obj" || name == ".git" {
				continue
			}
			walkProjects(root, full, suffixes, excluded, found)
			continue
		}
		if !HasAnySuffix(name, suffixes) {
			continue
		}
		relative, err := filepath.Rel(root, full)
		if err != nil {
			continue
		}
		relative = filepath.ToSlash(relative)
		if HasAnyPrefix(relative, excluded) {
			continue
		}
		*found = append(*found, relative)
	}
}

// HasAnySuffix is the python str.endswith over a tuple.
func HasAnySuffix(text string, suffixes []string) bool {
	for _, suffix := range suffixes {
		if strings.HasSuffix(text, suffix) {
			return true
		}
	}
	return false
}

// HasAnyPrefix is the python str.startswith over a tuple.
func HasAnyPrefix(text string, prefixes []string) bool {
	for _, prefix := range prefixes {
		if strings.HasPrefix(text, prefix) {
			return true
		}
	}
	return false
}

// MaskComments replaces every XML comment with spaces, keeping the line
// count.
//
// The line numbers a finding carries have to be the line numbers of the
// FILE, and a comment that names a package must not be read as a
// declaration. Blanking the comment's characters (but not its newlines)
// does both at once.
func MaskComments(text string) string {
	var out strings.Builder
	index := 0
	for index < len(text) {
		start := strings.Index(text[index:], "<!--")
		if start < 0 {
			out.WriteString(text[index:])
			break
		}
		start += index
		out.WriteString(text[index:start])
		end := strings.Index(text[start+4:], "-->")
		if end < 0 {
			// An unterminated comment: treat the rest of the file as a
			// comment, so a broken file cannot manufacture a finding.
			out.WriteString(strings.Repeat(" ", len(text)-start))
			break
		}
		end = start + 4 + end
		body := text[start : end+3]
		for _, ch := range body {
			if ch == '\n' {
				out.WriteByte('\n')
				continue
			}
			out.WriteByte(' ')
		}
		index = end + 3
	}
	return out.String()
}
