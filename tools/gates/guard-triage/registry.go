package guardtriage

// registry.go -- the human decisions: which class a rule's firings belong to.
//
// The python original loaded tools/guard-triage-classes.json. The schema and
// the four classes are checked the same way, so a registry edited into an
// unreadable state is a broken input (exit 1), not a quiet "everything is
// unknown" report.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const registrySchema = "harbor.guard-triage/registry/1"

// classes are the four classes, in the order the report prints them.
var classes = []string{"defect", "style", "noise", "unknown"}

func isClass(value string) bool {
	for _, name := range classes {
		if value == name {
			return true
		}
	}
	return false
}

type ruleEntry struct {
	rule           string
	class          string
	reason         string
	ratchet        string
	allowanceFiles []string
}

type firingEntry struct {
	rule             string
	evidenceContains string
	class            string
	reason           string
	ratchet          string
}

type registry struct {
	rules   map[string]*ruleEntry
	firings []*firingEntry
	path    string
}

type registryFile struct {
	Schema string `json:"schema"`
	Rules  []struct {
		Rule           string   `json:"rule"`
		Class          string   `json:"class"`
		Reason         string   `json:"reason"`
		Ratchet        string   `json:"ratchet"`
		AllowanceFiles []string `json:"allowance_files"`
	} `json:"rules"`
	Firings []struct {
		Rule             string `json:"rule"`
		EvidenceContains string `json:"evidence_contains"`
		Class            string `json:"class"`
		Reason           string `json:"reason"`
		Ratchet          string `json:"ratchet"`
	} `json:"firings"`
}

// loadRegistry reads and validates the classification registry.
func loadRegistry(path string) (*registry, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var raw registryFile
	if err := json.Unmarshal(data, &raw); err != nil {
		return nil, err
	}
	if raw.Schema != registrySchema {
		return nil, fmt.Errorf("%s: unknown schema '%s', expected '%s'", path, raw.Schema, registrySchema)
	}
	out := &registry{rules: map[string]*ruleEntry{}, path: path}
	for _, entry := range raw.Rules {
		if entry.Rule == "" {
			return nil, fmt.Errorf("%s: a rule entry has no 'rule' key", path)
		}
		if !isClass(entry.Class) {
			return nil, fmt.Errorf("%s: rule %s has class '%s', expected one of (%s)",
				path, entry.Rule, entry.Class, pyTuple(classes))
		}
		out.rules[entry.Rule] = &ruleEntry{
			rule:           entry.Rule,
			class:          entry.Class,
			reason:         entry.Reason,
			ratchet:        entry.Ratchet,
			allowanceFiles: entry.AllowanceFiles,
		}
	}
	for _, entry := range raw.Firings {
		out.firings = append(out.firings, &firingEntry{
			rule:             entry.Rule,
			evidenceContains: entry.EvidenceContains,
			class:            entry.Class,
			reason:           entry.Reason,
			ratchet:          entry.Ratchet,
		})
	}
	return out, nil
}

func pyTuple(items []string) string {
	quoted := make([]string, len(items))
	for i, item := range items {
		quoted[i] = "'" + item + "'"
	}
	return strings.Join(quoted, ", ")
}

// allowance is one allowance file and its content, in registry order.
type allowance struct {
	path string
	text string
}

// allowanceText reads the allowance files the rule declares. The pairs keep
// the order of the patterns and of the sorted matches, the way python's dict
// did, so a reason names the same file on every run.
func (r *registry) allowanceText(repoRoot string, rule string) []allowance {
	entry := r.rules[rule]
	if entry == nil {
		return nil
	}
	var out []allowance
	for _, pattern := range entry.allowanceFiles {
		for _, path := range globUnder(repoRoot, pattern) {
			full := filepath.Join(repoRoot, filepath.FromSlash(path))
			data, err := os.ReadFile(full)
			if err != nil {
				continue
			}
			out = append(out, allowance{path: path, text: string(data)})
		}
	}
	return out
}

// globUnder expands a python-style glob relative to repoRoot and returns the
// matching file paths, sorted and relative to the root with forward slashes.
// Path.glob's `**` is recursive; filepath.Glob's is not, so it is handled by
// walking the tree and matching the remaining segments.
func globUnder(repoRoot string, pattern string) []string {
	pattern = filepath.ToSlash(pattern)
	if strings.Contains(pattern, "**") {
		return globStar(repoRoot, pattern)
	}
	matches, err := filepath.Glob(filepath.Join(repoRoot, filepath.FromSlash(pattern)))
	if err != nil {
		return nil
	}
	var out []string
	for _, match := range matches {
		info, err := os.Stat(match)
		if err == nil && !info.IsDir() {
			rel, err := filepath.Rel(repoRoot, match)
			if err == nil {
				out = append(out, filepath.ToSlash(rel))
			}
		}
	}
	sort.Strings(out)
	return out
}

func globStar(repoRoot string, pattern string) []string {
	segments := strings.Split(pattern, "/")
	var walk func(dir string, index int) []string
	walk = func(dir string, index int) []string {
		if index == len(segments) {
			return nil
		}
		segment := segments[index]
		if segment == "**" {
			var out []string
			out = append(out, walk(dir, index+1)...)
			entries, err := os.ReadDir(dir)
			if err == nil {
				for _, entry := range entries {
					if entry.IsDir() {
						out = append(out, walk(filepath.Join(dir, entry.Name()), index)...)
					}
				}
			}
			return out
		}
		if index == len(segments)-1 {
			matches, err := filepath.Glob(filepath.Join(dir, segment))
			if err != nil {
				return nil
			}
			var out []string
			for _, match := range matches {
				info, err := os.Stat(match)
				if err == nil && !info.IsDir() {
					rel, err := filepath.Rel(repoRoot, match)
					if err == nil {
						out = append(out, filepath.ToSlash(rel))
					}
				}
			}
			return out
		}
		matches, err := filepath.Glob(filepath.Join(dir, segment))
		if err != nil {
			return nil
		}
		var out []string
		for _, match := range matches {
			info, err := os.Stat(match)
			if err == nil && info.IsDir() {
				out = append(out, walk(match, index+1)...)
			}
		}
		return out
	}
	out := walk(repoRoot, 0)
	sort.Strings(out)
	return out
}
