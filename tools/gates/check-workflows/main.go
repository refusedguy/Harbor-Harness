// Package checkworkflows is the gate that reads the workflow files and fails
// when one of them cannot be parsed.
//
// WHY THIS GATE EXISTS
// ====================
// A GitHub workflow file with a DUPLICATE KEY in a mapping is a file GitHub
// refuses to parse, and the refusal is silent from the repository's point of
// view: the workflow run is created, it has ZERO jobs, and it closes as
// "failure" with the text "This run likely failed because of a workflow file
// issue". Nothing else says so. The lane is not red — it is ABSENT — and
// every pull request that waits for it waits forever.
//
// It happened here. On 2026-10-21 the `ci` workflow stopped running at all:
//
//	run 39613662649 (dev@165813bf)  16 jobs  success   <- the last run with jobs
//	run 39627731779 (dev@e7479b9a)   0 jobs  failure
//	run 39627783779 (dev@259d0a5d)   0 jobs  failure
//
// The commit that broke it (e7479b9a) gave three shard-reporting steps an
// arm64 twin and wrote the twin as a second `if:`/`shell:`/`run:` inside the
// first step's mapping. YAML forbids a duplicate key in a mapping, so the
// file GitHub read had three mappings with `if:` twice, `shell:` twice and
// `run:` twice.
//
// WHY A GATE, AND WHY IT LIVES WHERE IT LIVES
// ============================================
// The check has to run in a workflow that is NOT the broken one: a gate that
// lives inside the file it polices disappears together with it. This one
// runs in the documentation lane, which starts on every pull request, with or
// without a .NET build.
//
// The gate is compiled rather than python because the repository's rule is
// that a NEW tool in `tools/` is a Go package under `tools/gates/`, with the
// standard library only. There is no YAML parser in the standard library, so
// this gate reads the block structure of the workflow files itself — it does
// not need to understand YAML, only to know which lines are the KEYS of which
// MAPPING, which is what a duplicate key is about.
//
// The exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package checkworkflows

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// workflowDir is where the workflow files live, relative to the repository
// root.
const workflowDir = ".github/workflows"

// keyRe matches a mapping key at the start of a line: a bare name, a colon,
// and then the value or the end of the line.
//
// The colon must be followed by a space or by the end of the line, because
// that is what YAML requires: `run:echo` is the scalar "run:echo", not the
// key "run" with the value "echo".
//
// The key is a SIMPLE key — a bare name. GitHub's workflow syntax uses bare
// names everywhere, and a quoted key is not something this repository writes.
var keyRe = regexp.MustCompile(`^([A-Za-z_][A-Za-z0-9_.-]*):(?:[ \t]+(.*))?$`)

// blockScalarRe matches the value of a block scalar: `|`, `>`, and the
// indicators that may follow them (`|-`, `|+`, `>2`, and so on).
var blockScalarRe = regexp.MustCompile(`^[|>][0-9]*[-+]?$|^[|>][-+]?[0-9]*$`)

// finding is one duplicate key, with the evidence to act on it.
type finding struct {
	path      string
	line      int
	key       string
	firstLine int
}

// String is the one-line form of a finding.
func (f finding) String() string {
	return fmt.Sprintf("%s:%d  duplicate key %q (first declared at line %d)",
		f.path, f.line, f.key, f.firstLine)
}

// scope is one mapping: the keys it declares, and the line each was
// declared on.
//
// A scope knows the indent of the keys that belong to it. A key line at that
// indent is a sibling of the keys already there, and a key line at a deeper
// indent opens a nested mapping.
type scope struct {
	indent int
	keys   map[string]int
	broken bool
}

// scanWorkflow reads one workflow file and reports the mappings that declare
// the same key twice.
//
// The scanner walks the block structure of the file by indentation, which is
// what YAML block mappings and block sequences are:
//
//   * a line whose trimmed text starts with "-" is a SEQUENCE ITEM, and the
//     mapping it opens (if any) starts at the column of the text after the
//     dash;
//   * a line that is `name:` is a KEY of the mapping that owns its indent;
//   * the lines of a BLOCK SCALAR (the `run: |` body) are content, not
//     structure, and are skipped: a line of shell script that happens to look
//     like `foo: bar` is not a mapping key.
//
// The scanner does not read YAML. It reads the SHAPE a YAML reader reads,
// and the shape is enough to answer the one question this gate asks: does
// any mapping declare the same key twice?
func scanWorkflow(path string, lines []string) []finding {
	var findings []finding
	var scopes []scope
	skipIndent := -1

	// pop removes the scopes that are deeper than the given indent: they
	// ended, and the next line belongs to an outer mapping.
	pop := func(indent int) {
		for len(scopes) > 0 && scopes[len(scopes)-1].indent > indent {
			scopes = scopes[:len(scopes)-1]
		}
	}

	// popTo removes the scopes at or deeper than the given indent. A
	// sequence item closes the mapping of the item before it.
	popTo := func(indent int) {
		for len(scopes) > 0 && scopes[len(scopes)-1].indent >= indent {
			scopes = scopes[:len(scopes)-1]
		}
	}

	for index, raw := range lines {
		lineNo := index + 1
		if strings.TrimSpace(raw) == "" {
			continue
		}
		indent := len(raw) - len(strings.TrimLeft(raw, " "))
		if skipIndent >= 0 {
			if indent > skipIndent {
				continue
			}
			skipIndent = -1
		}
		trimmed := strings.TrimSpace(raw)
		if strings.HasPrefix(trimmed, "#") {
			continue
		}

		if trimmed == "-" || strings.HasPrefix(trimmed, "- ") {
			after := trimmed[1:]
			rest := strings.TrimLeft(after, " ")
			column := indent + 1 + (len(after) - len(rest))
			popTo(column)
			current := scope{indent: column, keys: map[string]int{}}
			if match := keyRe.FindStringSubmatch(rest); match != nil {
				current.keys[match[1]] = lineNo
				if blockScalarRe.MatchString(match[2]) {
					skipIndent = column
				}
			}
			scopes = append(scopes, current)
			continue
		}

		match := keyRe.FindStringSubmatch(trimmed)
		if match == nil {
			continue
		}
		key, value := match[1], match[2]
		pop(indent)
		if len(scopes) > 0 && scopes[len(scopes)-1].indent == indent {
			current := &scopes[len(scopes)-1]
			if first, seen := current.keys[key]; seen {
				if !current.broken {
					current.broken = true
					findings = append(findings, finding{
						path:      path,
						line:      lineNo,
						key:       key,
						firstLine: first,
					})
				}
			} else {
				current.keys[key] = lineNo
			}
		} else {
			scopes = append(scopes, scope{
				indent: indent,
				keys:   map[string]int{key: lineNo},
			})
		}
		if blockScalarRe.MatchString(value) {
			skipIndent = indent
		}
	}

	return findings
}

// analyse reads every tracked workflow file and reports the findings.
func analyse(root string) (int, []finding) {
	paths, err := gate.TrackedFiles(root, ".yml")
	if err != nil {
		return 0, nil
	}
	var findings []finding
	scanned := 0
	for _, path := range paths {
		if !strings.HasPrefix(path, workflowDir+"/") {
			continue
		}
		lines, err := gate.ReadLines(filepath.Join(root, path))
		if err != nil {
			continue
		}
		scanned++
		findings = append(findings, scanWorkflow(path, lines)...)
	}
	sort.Slice(findings, func(i, j int) bool {
		if findings[i].path != findings[j].path {
			return findings[i].path < findings[j].path
		}
		return findings[i].line < findings[j].line
	})
	return scanned, findings
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
			fmt.Println("usage: harbor-gates check-workflows [--root DIR] [--report] [--min-files N] [--self-test]")
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
		fmt.Printf("workflow files scanned: %d\n", scanned)
		if len(findings) > 0 {
			fmt.Println("\nfindings:")
			for _, item := range findings {
				fmt.Printf("  %s\n", item)
			}
		}
		return gate.Green
	}

	breached := false
	if minFiles != 0 && scanned < minFiles {
		fmt.Printf("  SCAN-BLIND  scan floor: workflow files read %d, floor is %d. The gate saw less than the tree holds, so it cannot report that the tree is clean.\n",
			scanned, minFiles)
		breached = true
	}

	if len(findings) > 0 {
		fmt.Printf("Workflow gate — findings:\n\n")
		for _, item := range findings {
			fmt.Printf("  %s\n", item)
		}
		fmt.Println()
		fmt.Println("  A mapping may not declare the same key twice: YAML forbids it, and")
		fmt.Println("  GitHub refuses to parse a workflow file that does it. The refusal")
		fmt.Println("  is SILENT: the run is created with ZERO jobs and closes as a")
		fmt.Println("  failure, so the lane is not red — it is absent — and every pull")
		fmt.Println("  request that waits for it waits forever.")
		fmt.Println()
		fmt.Println("  Fix: make the two declarations two STEPS, not two sets of keys in")
		fmt.Println("  one step. Two steps with the same name are two steps; two `if:`")
		fmt.Println("  keys in one step are a file GitHub will not read.")
		fmt.Println()
	}

	if breached {
		fmt.Println()
	}

	if len(findings) > 0 || breached {
		return gate.Red
	}

	fmt.Printf("OK: %d workflow file(s) read, no mapping declares a key twice\n", scanned)
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
	value := 0
	for _, r := range text {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	return value, true
}
