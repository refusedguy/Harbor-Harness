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
// WHAT THE GATE READS
// ===================
// Three shapes make a file that GitHub will not read, and all three are
// silent in the same way:
//
//  1. a MAPPING that declares the same key twice — in the block form
//     (`if:` twice in one step) and in the flow form (`with: {a: 1, a: 2}`);
//  2. a QUOTED key (`"if":` twice) — the same defect with a different
//     spelling of the key;
//  3. a BLOCK SEQUENCE that holds a line which is not a list item — the arm64
//     twin written at the indent of the list instead of at the indent of the
//     step, which YAML reads as a mapping where a list item must be.
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

// The kinds of finding the gate reports.
const (
	// kindDuplicate is a mapping that declares the same key twice.
	kindDuplicate = "duplicate"
	// kindStructure is a line that stands where a list item must stand.
	kindStructure = "structure"
)

// The three spellings of a mapping key.
//
// A key is a plain name, a double-quoted name, or a single-quoted name. The
// colon must be followed by a space or by the end of the line, because that
// is what YAML requires: `run:echo` is the scalar "run:echo", not the key
// "run" with the value "echo".
//
// The plain name may not start with a YAML indicator, and it may not contain
// a colon, so the FIRST colon of the line is the separator.
var (
	dquotedKeyRe = regexp.MustCompile(`^"([^"\n]*)":([ \t]+(.*))?$`)
	squotedKeyRe = regexp.MustCompile(`^'([^'\n]*)':([ \t]+(.*))?$`)
	plainKeyRe   = regexp.MustCompile(`^([A-Za-z0-9_][A-Za-z0-9_.\-+/]*):([ \t]+(.*))?$`)
)

// blockScalarRe matches the value of a block scalar: `|`, `>`, and the
// indicators that may follow them (`|-`, `|+`, `>2`, and so on).
var blockScalarRe = regexp.MustCompile(`^[|>][0-9]*[-+]?$|^[|>][-+]?[0-9]*$`)

// finding is one defect, with the evidence to act on it.
type finding struct {
	path      string
	line      int
	kind      string
	key       string
	firstLine int
	note      string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	switch f.kind {
	case kindDuplicate:
		if f.note != "" {
			return fmt.Sprintf("%s:%d  duplicate key %q %s", f.path, f.line, f.key, f.note)
		}
		return fmt.Sprintf("%s:%d  duplicate key %q (first declared at line %d)",
			f.path, f.line, f.key, f.firstLine)
	default:
		return fmt.Sprintf("%s:%d  %s: %s", f.path, f.line, f.kind, f.note)
	}
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
}

// sequence is one open block sequence: the indent of its "-" markers, and the
// indent of the mapping that owns it.
//
// The two indents are what tell an INDENTED sequence from a COMPACT one. A
// compact sequence sits at the same indent as the key that owns it:
//
//	steps:            <- the key, at indent 0
//	- name: Build     <- the sequence, at indent 0
//
// and a key at that indent afterwards is a SIBLING of `steps:`, which is
// legal YAML. An indented sequence sits deeper than the key that owns it:
//
//	steps:            <- the key, at indent 0
//	  - name: Build   <- the sequence, at indent 2
//
// and a key at indent 2 afterwards is NOT a list item — it is a mapping
// where the file says there must be a list, and YAML will not read the file.
type sequence struct {
	indent int
	parent int
	line   int
}

// scanWorkflow reads one workflow file and reports the shapes that make it a
// file GitHub will not read.
//
// The scanner walks the block structure of the file by indentation, which is
// what YAML block mappings and block sequences are:
//
//   - a line whose trimmed text starts with "-" is a SEQUENCE ITEM, and the
//     mapping it opens (if any) starts at the column of the text after the
//     dash;
//   - a line that is `name:` is a KEY of the mapping that owns its indent;
//   - the lines of a BLOCK SCALAR (the `run: |` body) are content, not
//     structure, and are skipped: a line of shell script that happens to look
//     like `foo: bar` is not a mapping key.
//
// The scanner does not read YAML. It reads the SHAPE a YAML reader reads,
// and the shape is enough to answer the questions this gate asks.
func scanWorkflow(path string, lines []string) []finding {
	var findings []finding
	var scopes []scope
	var sequences []sequence
	skipIndent := -1

	// popScopes removes the mappings that are deeper than the given indent:
	// they ended, and the next line belongs to an outer mapping.
	popScopes := func(indent int) {
		for len(scopes) > 0 && scopes[len(scopes)-1].indent > indent {
			scopes = scopes[:len(scopes)-1]
		}
	}

	// popScopesTo removes the mappings at or deeper than the given indent. A
	// sequence item closes the mapping of the item before it.
	popScopesTo := func(indent int) {
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
			// A SEQUENCE ITEM. The mapping it opens starts at the column of
			// the text after the dash.
			after := trimmed[1:]
			content := strings.TrimLeft(after, " ")
			column := indent + 1 + (len(after) - len(content))
			popScopesTo(column)

			// The sequences deeper than this one ended: this item belongs to
			// an outer list.
			for len(sequences) > 0 && sequences[len(sequences)-1].indent > indent {
				sequences = sequences[:len(sequences)-1]
			}
			if len(sequences) == 0 || sequences[len(sequences)-1].indent != indent {
				// A NEW sequence. Its owner is the mapping the key that
				// opened it belongs to.
				parent := -1
				if len(scopes) > 0 {
					parent = scopes[len(scopes)-1].indent
				}
				sequences = append(sequences, sequence{indent: indent, parent: parent, line: lineNo})
			}

			current := scope{indent: column, keys: map[string]int{}}
			if key, value, ok := matchKey(content); ok {
				current.keys[key] = lineNo
				if blockScalarRe.MatchString(value) {
					skipIndent = column
				}
			}
			scopes = append(scopes, current)
			continue
		}

		key, value, isKey := matchKey(trimmed)
		if !isKey {
			// Not a key and not a list item: a plain scalar, the closing
			// brace of a flow mapping, or a line the scanner has no use for.
			// A YAML reader would have its own opinion about some of these;
			// the gate does not guess.
			continue
		}

		// The sequences deeper than this indent ended.
		for len(sequences) > 0 && sequences[len(sequences)-1].indent > indent {
			sequences = sequences[:len(sequences)-1]
		}
		popScopes(indent)

		if len(scopes) > 0 && scopes[len(scopes)-1].indent == indent {
			// A key of the mapping that owns this indent.
			current := &scopes[len(scopes)-1]
			if first, seen := current.keys[key]; seen {
				findings = append(findings, finding{
					path:      path,
					line:      lineNo,
					kind:      kindDuplicate,
					key:       key,
					firstLine: first,
				})
			} else {
				current.keys[key] = lineNo
			}
		} else {
			// This line opens a mapping of its own. If it stands at the
			// indent of an open INDENTED sequence, it is not a list item,
			// and the file is not a file YAML will read.
			for _, seq := range sequences {
				if seq.indent == indent && seq.indent > seq.parent {
					findings = append(findings, finding{
						path: path,
						line: lineNo,
						kind: kindStructure,
						note: fmt.Sprintf("a block sequence starts at line %d and its entries must start with \"-\": this line is not one",
							seq.line),
					})
					break
				}
			}
			scopes = append(scopes, scope{
				indent: indent,
				keys:   map[string]int{key: lineNo},
			})
		}

		// The value of a key may be a flow collection, and a flow mapping
		// may declare the same key twice.
		findings = append(findings, flowFindings(value, path, lineNo)...)

		if blockScalarRe.MatchString(value) {
			skipIndent = indent
		}
	}

	return findings
}

// matchKey reads a mapping key from the start of a line.
//
// It returns the key's text, the value's text, and whether the line is a key
// at all.
func matchKey(text string) (string, string, bool) {
	if match := dquotedKeyRe.FindStringSubmatch(text); match != nil {
		return match[1], match[3], true
	}
	if match := squotedKeyRe.FindStringSubmatch(text); match != nil {
		return match[1], match[3], true
	}
	if match := plainKeyRe.FindStringSubmatch(text); match != nil {
		return match[1], match[3], true
	}
	return "", "", false
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
		fmt.Println("  A block sequence's entries must start with \"-\". A line that stands")
		fmt.Println("  where an entry must stand is a file GitHub will not read either.")
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
