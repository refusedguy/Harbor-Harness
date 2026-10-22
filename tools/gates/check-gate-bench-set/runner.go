package benchset

// runner.go -- the tree runner's list, and what a workflow that calls it
// really runs.
//
// Most of the tree gates of this repository run through ONE command:
// `harbor-gates all`. A workflow that calls the runner RUNS the gates of
// the runner's list, so a reader that only looks for a gate's name on the
// command line sees a workflow that runs nothing but the runner, and every
// gate the runner starts looks like a gate nobody runs.
//
// This file reads the list the runner reads -- the GO SOURCE of the
// runner's package -- and turns it into the invocations the workflows make.
//
// The list is read out of the SOURCE and not out of `harbor-gates all
// --list`, for the reason the policy checker gives: the committed binaries
// are LINUX binaries, and a reader has to work on any machine; and
// `--list` prints a HUMAN table, and recovering machine facts out of a
// human table couples the reader to a presentation format.
//
// And the parse is LOUD: a list that cannot be read is a FINDING, never an
// empty list. A reader that cannot tell which gates the runner runs must
// say so, not shrug -- an empty list is a lie that lets the set drift.

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

const (
	// runnerGate is the subcommand that starts the tree gates.
	runnerGate = "all"
	// runnerListFile is the GO SOURCE of the runner's list.
	runnerListFile = "tools/gates/all/gates.go"
	// runnerPackageDir is the runner's package.
	runnerPackageDir = "tools/gates/all"
)

var (
	// The declaration of the list, and the fields of one entry.
	runnerListDeclRe = regexp.MustCompile(`^var\s+treeGates\s*=\s*\[\]treeGate\s*\{`)
	runnerNameRe     = regexp.MustCompile(`name:\s*"([^"]+)"`)
	runnerArgsRe     = regexp.MustCompile(`args:\s*\[\]string\{([^}]*)\}`)
	runnerWriterRe   = regexp.MustCompile(`writer:\s*true`)
)

// runnerEntry is one gate of the runner's list.
//
// The fields are what the reader needs and nothing more: the name (the
// gate), the arguments (the floors the runner passes it) and the writer
// flag (a gate that REWRITES the tree is not run by a checker).
type runnerEntry struct {
	name   string
	args   []string
	writer bool
}

// readRunnerEntries reads the list of the tree runner.
//
// The second return value is the reason the list could not be read, and it
// is empty when the list was read. An empty list AND no reason means the
// tree has no tree runner at all: there is nothing to understand, and no
// rule to enforce.
func readRunnerEntries(root string) ([]runnerEntry, string) {
	if info, err := os.Stat(filepath.Join(root, runnerPackageDir)); err != nil || !info.IsDir() {
		return nil, ""
	}
	data, err := os.ReadFile(filepath.Join(root, runnerListFile))
	if err != nil {
		return nil, err.Error()
	}
	return parseRunnerList(string(data))
}

// parseRunnerList reads the entries out of the Go composite literal that
// holds the list.
//
// The braces are counted, and not the lines: an entry whose `args` span
// several lines has a closing brace of its own before the closing brace of
// the entry.
func parseRunnerList(text string) ([]runnerEntry, string) {
	lines := strings.Split(text, "\n")
	start := -1
	for index, line := range lines {
		if runnerListDeclRe.MatchString(strings.TrimSpace(line)) {
			start = index
			break
		}
	}
	if start < 0 {
		return nil, fmt.Sprintf("%s has no `var treeGates = []treeGate{` block", runnerListFile)
	}

	var entries []runnerEntry
	var body []string
	inside := false
	depth := 0
	for _, line := range lines[start+1:] {
		stripped := strings.TrimSpace(line)
		if !inside {
			if stripped == "{" {
				inside = true
				depth = 1
				body = nil
			} else if stripped == "}" {
				break
			}
			continue
		}
		depth += strings.Count(line, "{") - strings.Count(line, "}")
		if depth <= 0 {
			entry, ok := parseRunnerEntry(strings.Join(body, "\n"))
			if !ok {
				return nil, fmt.Sprintf("%s has an entry of the list with no name", runnerListFile)
			}
			entries = append(entries, entry)
			inside = false
			body = nil
			continue
		}
		body = append(body, line)
	}
	if inside {
		return nil, fmt.Sprintf("%s has an unterminated entry of the list", runnerListFile)
	}
	if len(entries) == 0 {
		return nil, fmt.Sprintf("%s has an empty list of tree gates", runnerListFile)
	}
	return entries, ""
}

// parseRunnerEntry reads one entry of the list.
func parseRunnerEntry(text string) (runnerEntry, bool) {
	name := runnerNameRe.FindStringSubmatch(text)
	if name == nil {
		return runnerEntry{}, false
	}
	entry := runnerEntry{name: name[1]}
	if args := runnerArgsRe.FindStringSubmatch(text); args != nil {
		entry.args = goTokens(args[1])
	}
	entry.writer = runnerWriterRe.MatchString(text)
	return entry, true
}

// goTokens reads the elements of a `[]string{...}` literal.
//
// A string literal loses its quotes; an identifier (a placeholder such as
// `placeholderBase`) keeps its name, because the reader has no business
// resolving it and no floor is one.
func goTokens(text string) []string {
	var out []string
	for _, piece := range strings.Split(text, ",") {
		token := strings.TrimSpace(piece)
		if token == "" {
			continue
		}
		if len(token) >= 2 && strings.HasPrefix(token, "\"") && strings.HasSuffix(token, "\"") {
			out = append(out, strings.ReplaceAll(token[1:len(token)-1], `\"`, `"`))
			continue
		}
		out = append(out, token)
	}
	return out
}

// runnerInvocations returns the invocations the runner makes for one
// workflow that calls it.
//
// Two of the list's gates are not run by the runner: a gate that is still a
// PYTHON script (the binary must not need python -- the local runner runs
// those with python3) and a gate that REWRITES the tree (a checker must
// not change what it checks). Both keep a job of their own in CI, so the
// workflows name them directly.
func runnerInvocations(root string, entries []runnerEntry) []invocation {
	var out []invocation
	for _, entry := range entries {
		if entry.writer {
			continue
		}
		if info, err := os.Stat(filepath.Join(root, "tools", "gates", entry.name)); err != nil || !info.IsDir() {
			continue
		}
		out = append(out, invocation{
			name: entry.name,
			kind: "compiled",
			args: strings.Join(entry.args, " "),
		})
	}
	return out
}
