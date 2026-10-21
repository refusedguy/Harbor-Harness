// Package checknotestsdk is the compiled port of tools/check-no-test-sdk.py.
//
// The gate fails if `Microsoft.NET.Test.Sdk` is declared anywhere in this
// repository: the VSTest host stops TUnit's test discovery, and nothing else
// in the tree would complain if the package came back.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checknotestsdk

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// forbiddenID is the one forbidden id. NuGet ids are case-insensitive, so the
// match is too.
const forbiddenID = "Microsoft.NET.Test.Sdk"

// elements are the elements that can introduce a package into a project's
// graph. `PackageReference` declares a dependency, `PackageVersion` pins one
// in the central catalogue, `PackageDownload` fetches one without referencing
// it.
var elements = []string{"PackageReference", "PackageVersion", "PackageDownload"}

// scanSuffixes are the project files in scope: every tracked .csproj /
// .props / .targets.
var scanSuffixes = []string{".csproj", ".props", ".targets"}

// excludedPrefixes is the vendored third-party tree. It is a git submodule
// (ADR-019), so its files are not tracked in this repository and the
// exclusion is belt and braces.
var excludedPrefixes = []string{"contrib/external/"}

// elementRe is the element that can declare a package.
var elementRe = regexp.MustCompile(`(?i)<(PackageReference|PackageVersion|PackageDownload)\b`)

// includeRe is the `Include` attribute of such an element.
//
// The closing quote is checked in code: a backreference is not in the regex
// engine of Go, and the python original required the opening and the closing
// quote to be the same character.
var includeRe = regexp.MustCompile(`\bInclude\s*=\s*(["'])([^"']+)`)

// finding is one forbidden declaration, with the evidence to act on it.
type finding struct {
	rule    string
	path    string
	line    int
	element string
	snippet string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	return fmt.Sprintf("%s  %s:%d  <%s>", f.rule, f.path, f.line, f.element)
}

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	report := false
	minFiles := 0
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--root":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--root needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			root = value
		case "--report":
			report = true
		case "--min-files":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-files needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = parsed
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-no-test-sdk [--root DIR] [--report] [--min-files N] [--self-test]")
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
	census, findings := analyse(root)

	if report {
		fmt.Printf("project files scanned: %d\n", census.files)
		for _, suffix := range scanSuffixes {
			fmt.Printf("  %s: %d\n", suffix, census.bySuffix[suffix])
		}
		fmt.Printf("excluded prefixes: %s\n", orDash(strings.Join(excludedPrefixes, ", ")))
		if len(findings) > 0 {
			fmt.Println("\nfindings:")
			for _, item := range findings {
				fmt.Printf("  %s\n", item)
			}
		}
		return gate.Green
	}

	breached := false
	if minFiles != 0 && census.files < minFiles {
		fmt.Printf("  SCAN-BLIND  scan floor: project files compared %d, floor is %d. The gate saw less than the tree holds, so it cannot report that the tree is clean.\n",
			census.files, minFiles)
		breached = true
	}

	if len(findings) > 0 {
		fmt.Printf("No-VSTest-SDK gate — findings:\n\n")
		for _, item := range findings {
			fmt.Printf("  %s\n", item)
			fmt.Printf("      %s\n", item.snippet)
		}
		fmt.Println()
		fmt.Printf("  %s must not be declared in this repository. TUnit's\n", forbiddenID)
		fmt.Println("  installation docs call it out by name:")
		fmt.Println("    \"If you're used to other testing frameworks, you're probably")
		fmt.Println("     used to the package Microsoft.NET.Test.Sdk. This should NOT be")
		fmt.Println("     used with TUnit. It'll stop test discovery from working")
		fmt.Println("     properly.\" — https://tunit.dev/docs/getting-started/installation")
		fmt.Println()
		fmt.Println("  Fix: delete the element. Do NOT replace it with another VSTest")
		fmt.Println("  host package. This repository runs on Microsoft.Testing.Platform,")
		fmt.Println("  and the pieces it needs — Microsoft.Testing.Platform.MSBuild,")
		fmt.Println("  Microsoft.Testing.Extensions.CodeCoverage,")
		fmt.Println("  Microsoft.Testing.Extensions.TrxReport — arrive transitively")
		fmt.Println("  through TUnit, so there is nothing to add.")
		fmt.Println()
	}

	if breached {
		fmt.Println()
	}

	if len(findings) > 0 || breached {
		return gate.Red
	}

	fmt.Printf("OK: %d project file(s) scanned, %s is declared in none of them\n",
		census.files, forbiddenID)
	return gate.Green
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// parseInt reads a decimal integer.
func parseInt(text string) (int, bool) {
	if text == "" {
		return 0, false
	}
	negative := false
	body := text
	if body[0] == '+' || body[0] == '-' {
		negative = body[0] == '-'
		body = body[1:]
	}
	if body == "" {
		return 0, false
	}
	value := 0
	for _, r := range body {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	if negative {
		value = -value
	}
	return value, true
}

// orDash is the python `or "-"`.
func orDash(text string) string {
	if text == "" {
		return "-"
	}
	return text
}

// census is what the scan saw.
type census struct {
	files    int
	bySuffix map[string]int
}

// scanFiles is every project file in scope, repository-relative.
//
// `git ls-files` rather than a glob, for the reason the module docstring
// states: an untracked scratch project must not change what the gate sees,
// and a tracked project must not be able to hide from it.
func scanFiles(root string) []string {
	tracked, err := trackedProjectFiles(root)
	if err == nil && len(tracked) > 0 {
		var kept []string
		for _, path := range tracked {
			if !hasPrefix(path, excludedPrefixes) {
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

// trackedProjectFiles lists the tracked project files.
func trackedProjectFiles(root string) ([]string, error) {
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
		if hasSuffix(line, scanSuffixes) {
			files = append(files, line)
		}
	}
	return files, nil
}

// walk is the fallback for a tree that is not a git repository.
func walk(root string, dir string, found *[]string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() {
			if name == "bin" || name == "obj" || name == ".git" {
				continue
			}
			walk(root, filepath.Join(dir, name), found)
			continue
		}
		if !hasSuffix(name, scanSuffixes) {
			continue
		}
		relative, err := filepath.Rel(root, filepath.Join(dir, name))
		if err != nil {
			continue
		}
		relative = filepath.ToSlash(relative)
		if hasPrefix(relative, excludedPrefixes) {
			continue
		}
		*found = append(*found, relative)
	}
}

// hasSuffix is the python str.endswith over a tuple.
func hasSuffix(text string, suffixes []string) bool {
	for _, suffix := range suffixes {
		if strings.HasSuffix(text, suffix) {
			return true
		}
	}
	return false
}

// hasPrefix is the python str.startswith over a tuple.
func hasPrefix(text string, prefixes []string) bool {
	for _, prefix := range prefixes {
		if strings.HasPrefix(text, prefix) {
			return true
		}
	}
	return false
}

// maskComments replaces every XML comment with spaces, keeping the line count.
//
// The line numbers a finding carries have to be the line numbers of the
// FILE, and a comment that mentions the forbidden id must not be read as
// a declaration. Blanking the comment's characters (but not its newlines)
// does both at once.
func maskComments(text string) string {
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

// findDeclarations is every forbidden element in one file.
func findDeclarations(relative string, text string) []finding {
	masked := maskComments(text)
	var findings []finding
	for _, match := range elementRe.FindAllStringSubmatchIndex(masked, -1) {
		element := masked[match[2]:match[3]]
		nameEnd := match[1]
		// The search space of the attribute is bounded by the first ">":
		// the python original's `[^>]*?` could not cross one.
		limit := strings.IndexByte(masked[nameEnd:], '>')
		if limit < 0 {
			limit = len(masked)
		} else {
			limit += nameEnd
		}
		space := masked[nameEnd:limit]
		for _, attribute := range includeRe.FindAllStringSubmatchIndex(space, -1) {
			quote := space[attribute[2]:attribute[3]]
			id := space[attribute[4]:attribute[5]]
			// The closing quote must be the opening one: the python
			// original used a backreference.
			if attribute[5] >= len(space) || string(space[attribute[5]]) != quote {
				continue
			}
			if !strings.EqualFold(strings.TrimSpace(id), forbiddenID) {
				break
			}
			rule := "VSTEST-SDK-REFERENCE"
			if strings.EqualFold(element, "packageversion") {
				rule = "VSTEST-SDK-PIN"
			}
			line := strings.Count(masked[:match[0]], "\n") + 1
			snippet := strings.Join(strings.Fields("<"+element+space[:attribute[5]+1]), " ")
			findings = append(findings, finding{
				rule:    rule,
				path:    relative,
				line:    line,
				element: element,
				snippet: snippet,
			})
			break
		}
	}
	return findings
}

// analyse is (census, findings). Both are derived from the tree; neither is
// declared.
func analyse(root string) (census, []finding) {
	files := scanFiles(root)
	var findings []finding
	for _, relative := range files {
		path := filepath.Join(root, relative)
		data, err := os.ReadFile(path)
		if err != nil {
			findings = append(findings, finding{
				rule:    "SCAN-BLIND",
				path:    relative,
				line:    0,
				element: "Project",
				snippet: fmt.Sprintf("the file could not be read (%v)", err),
			})
			continue
		}
		findings = append(findings, findDeclarations(relative, string(data))...)
	}

	result := census{files: len(files), bySuffix: map[string]int{}}
	for _, suffix := range scanSuffixes {
		count := 0
		for _, file := range files {
			if strings.HasSuffix(file, suffix) {
				count++
			}
		}
		result.bySuffix[suffix] = count
	}
	return result, findings
}
