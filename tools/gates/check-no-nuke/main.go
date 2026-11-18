// Package checknonuke is the gate that keeps the retired NUKE build system
// from coming back (issue #2184, step 5 of the fallout migration).
//
// # WHY THIS IS A FILE GATE (the "why python" rule of docs/CI_GATES.md)
//
// The rule asks a question about FILES: does any tracked file still carry a
// NuGet reference to Nuke.*, a `using Nuke.*`, or one of the four strings
// that boot the old entry point? It needs no SDK, no restore, no build of
// Harbor and no network, so it can run on the pull request that breaks the
// build and on the documentation-only pull request that never compiles
// anything. That is the whole reason gates in this repository are text
// readers. It is COMPILED (Go standard library only, no module, no go.sum)
// because the binary is committed to the tree, the same way the other gates
// are: one artifact that runs where even python may be absent.
//
// # THE TWO RULES
//
//	NUKE-NUGET-REFERENCE  a Nuke.* NuGet reference or `using Nuke.*` remains
//	NUKE-LEGACY-ENTRYPOINT  a workflow or bootstrap script still calls the
//	                        old build entry point (dotnet nuke,
//	                        NUKE_TELEMETRY_OPTOUT, .nuke/temp,
//	                        NukeTelemetryVersion)
//
// Both are BLOCKING. There is no --warn-only flag: a gate that can be
// switched off is not a gate. The positive control is --self-test, which
// plants Nuke.Common in a fixture and requires exit 1 naming the file:line.
//
// # THE EXCLUSIONS ARE HERE, NOT IN A CONFIG FILE
//
// The four historical documents that quote the old build on purpose are
// named below, in code, with the reason. The gate's own source is excluded
// too: a detector that flagged the strings naming what it forbids would be
// turned off within a week. Compiled binaries under tools/bin/ are excluded
// because they are artifacts, not sources.
package checknonuke

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The rule codes printed with every finding.
const (
	ruleNuGet     = "NUKE-NUGET-REFERENCE"
	ruleLegacy    = "NUKE-LEGACY-ENTRYPOINT"
	ruleScanBlind = "SCAN-BLIND"
)

// The patterns. NuGet ids and the old entry-point strings are matched
// case-insensitively: a reintroduction in a different casing is still a
// reintroduction.
var (
	nugetRe   = regexp.MustCompile(`(?i)<(PackageReference|PackageVersion|PackageDownload)\b[^>]*\bInclude\s*=\s*["'][^"']*Nuke\.`)
	usingRe   = regexp.MustCompile(`(?i)\busing\s+Nuke\.`)
	dotnetRe  = regexp.MustCompile(`(?i)\bdotnet\s+nuke\b`)
	optoutRe  = regexp.MustCompile(`(?i)NUKE_TELEMETRY_OPTOUT`)
	tempRe    = regexp.MustCompile(`(?i)\.nuke/temp`)
	versionRe = regexp.MustCompile(`(?i)NukeTelemetryVersion`)
)

// excludedPrefixes is the exclusion list, in the gate and not in a config
// file. The first four are the historical documents that mention NUKE on
// purpose (the issue names them); then this gate's own rule text, the
// compiled artifacts, and the vendored submodule, none of which can be a
// source of a reintroduction.
var excludedPrefixes = []string{
	"docs/.kilo-docs/",
	"docs/audit-archive/",
	"docs/BUILDSYSTEM_STUDY.md",
	"docs/adr/ADR-023-",
	"tools/bin/",
	"tools/gates/check-no-nuke/",
	// A gitlink: git ls-files lists it even though the working tree holds
	// a directory, and the vendored tree is not ours to police (ADR-019).
	"contrib/external/",
}

// finding is one forbidden occurrence, with the file:line to act on it.
type finding struct {
	rule    string
	path    string
	line    int
	snippet string
}

// String is the one-line form, and it always carries a file:line.
func (f finding) String() string {
	return fmt.Sprintf("%s  %s:%d  %s", f.rule, f.path, f.line, f.snippet)
}

// runScan is the verdict, shared by Run and --self-test so the proof
// exercises the same path CI does.
func runScan(root string) (int, []finding) {
	_, findings := analyse(root)
	if len(findings) > 0 {
		return gate.Red, findings
	}
	return gate.Green, findings
}

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	report := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--root needs a path")
				return gate.Broken
			}
			i++
			root = args[i]
		case strings.HasPrefix(arg, "--root="):
			root = strings.TrimPrefix(arg, "--root=")
		case arg == "--report":
			report = true
		case arg == "--self-test" || arg == "--selftest":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-no-nuke [--root DIR] [--report] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if root == "" {
		found, err := gate.RepoRoot()
		if err != nil {
			fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
			return gate.Broken
		}
		root = found
	}
	scanned, findings := analyse(root)

	if report {
		fmt.Printf("tracked files scanned: %d\n", scanned)
		fmt.Printf("excluded prefixes: %s\n", strings.Join(excludedPrefixes, ", "))
		if len(findings) > 0 {
			fmt.Println("\nfindings:")
			for _, item := range findings {
				fmt.Printf("  %s\n", item)
			}
		}
		return gate.Green
	}

	code, _ := runScan(root)
	if code == gate.Green {
		fmt.Printf("OK: %d tracked file(s) scanned, no NUKE build-system reference found\n", scanned)
		return gate.Green
	}

	fmt.Println("No-NUKE gate findings:")
	fmt.Println()
	for _, item := range findings {
		fmt.Printf("  %s\n", item)
		fmt.Printf("      %s\n", item.snippet)
	}
	fmt.Println()
	fmt.Println("  The NUKE build path was retired in #2183 (see docs/adr/ADR-023).")
	fmt.Println("  Remove the reference. Do NOT add an exception to make the gate green:")
	fmt.Println("  a gate that can be switched off protects nothing.")
	return gate.Red
}

// analyse is the file scan: (files scanned, findings).
func analyse(root string) (int, []finding) {
	files := scanFiles(root)
	var findings []finding
	for _, relative := range files {
		path := filepath.Join(root, filepath.FromSlash(relative))
		if info, err := os.Stat(path); err == nil && info.IsDir() {
			// A submodule gitlink, listed by git but not a file.
			continue
		}
		fh, err := os.Open(path)
		if err != nil {
			findings = append(findings, finding{
				rule:    ruleScanBlind,
				path:    relative,
				line:    0,
				snippet: fmt.Sprintf("file could not be read (%v)", err),
			})
			continue
		}
		lineNo := 0
		scanner := bufio.NewScanner(fh)
		scanner.Buffer(make([]byte, 0, 64*1024), 1024*1024)
		for scanner.Scan() {
			lineNo++
			line := scanner.Text()
			addPatterns(&findings, relative, lineNo, line,
				ruleNuGet, nugetRe, usingRe)
			addPatterns(&findings, relative, lineNo, line,
				ruleLegacy, dotnetRe, optoutRe, tempRe, versionRe)
		}
		if err := scanner.Err(); err != nil {
			findings = append(findings, finding{
				rule:    ruleScanBlind,
				path:    relative,
				line:    lineNo + 1,
				snippet: fmt.Sprintf("file could not be read (%v)", err),
			})
		}
		fh.Close()
	}
	return len(files), findings
}

// addPatterns adds one finding per pattern that matches a line.
func addPatterns(out *[]finding, path string, lineNo int, line, rule string, patterns ...*regexp.Regexp) {
	for _, pattern := range patterns {
		match := pattern.FindString(line)
		if match == "" {
			continue
		}
		*out = append(*out, finding{
			rule:    rule,
			path:    path,
			line:    lineNo,
			snippet: strings.TrimSpace(match),
		})
	}
}

// scanFiles is the tracked-file list, minus the exclusions. `git ls-files`
// rather than a glob: an untracked scratch file must not change the verdict,
// and a tracked file must not be able to hide.
func scanFiles(root string) []string {
	tracked, err := gate.TrackedFiles(root)
	if err == nil && len(tracked) > 0 {
		var kept []string
		for _, path := range tracked {
			if !hasExcludedPrefix(path) {
				kept = append(kept, path)
			}
		}
		sort.Strings(kept)
		return kept
	}

	var found []string
	walk(root, root, &found)
	sort.Strings(found)
	return found
}

// walk is the fallback for a tree with no git (the self-test fixtures).
func walk(root string, dir string, found *[]string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() {
			if name == ".git" {
				continue
			}
			walk(root, filepath.Join(dir, name), found)
			continue
		}
		relative, err := filepath.Rel(root, filepath.Join(dir, name))
		if err != nil {
			continue
		}
		relative = filepath.ToSlash(relative)
		if hasExcludedPrefix(relative) {
			continue
		}
		*found = append(*found, relative)
	}
}

// hasExcludedPrefix is whether a path is in the exclusion list.
func hasExcludedPrefix(path string) bool {
	for _, prefix := range excludedPrefixes {
		if strings.HasPrefix(path, prefix) {
			return true
		}
	}
	return false
}
