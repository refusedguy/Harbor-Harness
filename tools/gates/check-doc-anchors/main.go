// Package docanchors is the compiled gate that asks one question about the
// citations a NORMATIVE document makes:
//
//	does the `file:line` still point at the CODE the document says it points at?
//
// WHY THIS GATE EXISTS (issue #2119)
//
// `docs/EVENT_TOPOLOGY.md` is the repository's event map, and it is marked
// `Status: normative`. Its producer table names the lines on which each
// producer publishes an event — and those numbers had drifted: the file
// moved, the numbers did not. Four of the five producer rows named lines
// that no longer held a publish, and eleven more citations in the same
// document had slid onto a comment or a blank line.
//
// WHY THE EXISTING CITATION GATE DOES NOT SEE IT
//
// `check-doc-cites` asks whether the citation RESOLVES: the file is in the
// tree, the line is inside the file, the line is not empty. A drifted
// citation resolves perfectly — the file is there, the line is there, and
// the line holds something. What is wrong is WHICH thing the line holds,
// and that is not a question a line number can answer on its own.
//
// So this gate asks the question the line number cannot: the cited line must
// carry CODE (not a comment, not a blank), and when the document names the
// SYMBOL it expects to find there, the symbol must be on that line.
//
// THE TWO RULES
//
//	DOC-ANCHOR-NO-CODE   the cited line (or one line of the cited range)
//	                     carries no code: it is blank, or it is a comment.
//	DOC-ANCHOR-DRIFT     the citation names an anchor — `path/File.cs:123#Symbol`
//	                     — and the symbol is not on the cited line.
//
// The anchor is OPT-IN: a citation without one is not this gate's business,
// and the rule that owns it (`check-doc-cites`) still owns it. A citation
// that names its anchor is a citation that says what it expects to find, and
// this gate is what makes that promise checkable.
//
// THE SCOPE
//
// The documents carrying the `Status: normative` banner, and only those: a
// dated record of what a run once measured is allowed to name a line that
// has moved, because it is a record and not a claim about today.
package docanchors

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// normative is the banner that selects the population. The same selector
// `check-doc-cites` uses, so the two gates can never read different
// populations and call the same citation complete in one and broken in the
// other.
var normative = regexp.MustCompile(`(?m)^[ \t]*>[ \t]*Status: normative\b`)

// citation is the citation grammar: a path, a line or a set of lines or
// ranges, and an OPTIONAL anchor.
//
// The extension list is the one `check-doc-cites` fences with, so the two
// gates agree on what a citation is. The anchor is a `#` and a symbol: the
// document says which symbol it expects to find on the line it names.
//
// Go's regexp has no lookbehind, so the "the path is not the tail of a
// longer path" rule is checked by hand below.
var citation = regexp.MustCompile(
	`([A-Za-z0-9_][A-Za-z0-9_./-]*\.(?:cs|json|yml|yaml|csproj|slnx|props|targets|py|axaml|xaml|editorconfig))` +
		`:(\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)(?:#([A-Za-z_][A-Za-z0-9_]*))?`)

// finding is one thing the gate found.
type finding struct {
	rule string
	doc  string
	line int
	text string
}

func (f finding) String() string {
	return fmt.Sprintf("%s: %s:%d — %s", f.rule, f.doc, f.line, f.text)
}

// Run is the gate's entry point. The exit codes are the binary's contract:
// 0 green, 1 red, 2 the gate could not run.
func Run(args []string) int {
	minCites := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-cites":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-cites needs a number")
				return gate.Broken
			}
			i++
			n, err := strconv.Atoi(args[i])
			if err != nil || n < 0 {
				fmt.Fprintln(os.Stderr, "--min-cites needs a number")
				return gate.Broken
			}
			minCites = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-doc-anchors [--min-cites N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	docs, err := gate.TrackedFiles(root, ".md")
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot list the tracked markdown files:", err)
		return gate.Broken
	}

	scanned := 0
	var findings []finding
	byBase := basenameIndex(root, docs)
	for _, doc := range docs {
		if !strings.HasPrefix(doc, "docs/") && doc != "AGENTS.md" && doc != "CLAUDE.md" {
			continue
		}
		text, err := os.ReadFile(filepath.Join(root, doc))
		if err != nil {
			fmt.Fprintln(os.Stderr, "cannot read", doc, err)
			return gate.Broken
		}
		body := string(text)
		if !normative.MatchString(body) {
			continue
		}
		n, found := scanDocument(root, doc, body, byBase)
		scanned += n
		findings = append(findings, found...)
	}

	fmt.Printf("normative documents: %d\n", normativeCount(docs, root))
	fmt.Printf("citations checked:    %d\n", scanned)
	for _, f := range findings {
		fmt.Println(f.String())
	}
	if scanned < minCites {
		fmt.Printf("FAIL: the gate checked %d citations, the floor says %d\n", scanned, minCites)
		return gate.Red
	}
	if len(findings) > 0 {
		fmt.Printf("FAIL: %d finding(s) — red\n", len(findings))
		return gate.Red
	}
	fmt.Println("OK: every citation in a normative document points at a line that carries code.")
	return gate.Green
}

// normativeCount counts the documents the gate read. It is printed so the
// number is on the record: a gate that scanned nothing must not look like a
// gate that found nothing.
func normativeCount(docs []string, root string) int {
	n := 0
	for _, doc := range docs {
		if !strings.HasPrefix(doc, "docs/") && doc != "AGENTS.md" && doc != "CLAUDE.md" {
			continue
		}
		text, err := os.ReadFile(filepath.Join(root, doc))
		if err != nil {
			continue
		}
		if normative.MatchString(string(text)) {
			n++
		}
	}
	return n
}

// basenameIndex maps a bare file name to the tracked files that carry it.
//
// The documents write a bare name when the same table already named the
// path: "`InMemoryEventBus.cs:353`" under a row that says
// "src/Harbor.Registries/Events/InMemoryEventBus.cs". The index is the
// same one `check-doc-cites` builds, and the same exclusions: `contrib/`
// is unmaintained and is not part of the closure, and a name that several
// tracked files share is NOT resolved — a citation that cannot say which
// file it means is `check-doc-cites`' finding, not this gate's.
func basenameIndex(root string, tracked []string) map[string][]string {
	byBase := map[string][]string{}
	for _, f := range tracked {
		if strings.HasPrefix(f, "contrib/") {
			continue
		}
		ext := filepath.Ext(f)
		if !citableExt[ext] {
			continue
		}
		base := filepath.Base(f)
		byBase[base] = append(byBase[base], f)
	}
	return byBase
}

// citableExt is the extension list the citation grammar accepts, as data
// rather than as a second copy of the regex: a gate that lists the
// extensions in one place and matches them in another is a gate that
// disagrees with itself.
var citableExt = map[string]bool{
	".cs": true, ".json": true, ".yml": true, ".yaml": true, ".csproj": true,
	".slnx": true, ".props": true, ".targets": true, ".py": true,
	".axaml": true, ".xaml": true, ".editorconfig": true,
}

// resolve turns the path of a citation into a path in the tree.
//
// A path with a directory is itself. A bare name is resolved through the
// index, and only when exactly one tracked file carries it.
func resolve(path string, byBase map[string][]string) (string, bool) {
	if strings.Contains(path, "/") {
		return path, true
	}
	candidates := byBase[path]
	if len(candidates) != 1 {
		return "", false
	}
	return candidates[0], true
}

// scanDocument checks every citation in one document. It returns the number
// of citations it checked and the findings.
func scanDocument(root string, doc string, body string, byBase map[string][]string) (int, []finding) {
	scanned := 0
	var findings []finding
	lines := strings.Split(body, "\n")
	for i, line := range lines {
		for _, m := range citation.FindAllStringSubmatchIndex(line, -1) {
			if m[0] > 0 {
				prev := line[m[0]-1]
				if prev == '.' || prev == '/' || prev == '-' ||
					(prev >= '0' && prev <= '9') ||
					(prev >= 'a' && prev <= 'z') ||
					(prev >= 'A' && prev <= 'Z') || prev == '_' {
					// The tail of a longer path: not a citation of its own.
					continue
				}
			}
			path := line[m[2]:m[3]]
			nums := line[m[4]:m[5]]
			anchor := ""
			if m[6] >= 0 {
				anchor = line[m[6]:m[7]]
			}
			resolved, ok := resolve(path, byBase)
			if !ok {
				// A name the tree does not have, or a name several files
				// share: `check-doc-cites` owns both.
				continue
			}
			source, err := gate.ReadLines(filepath.Join(root, resolved))
			if err != nil {
				continue
			}
			scanned++
			findings = append(findings, checkLines(doc, i+1, resolved, nums, anchor, source)...)
		}
	}
	return scanned, findings
}

// checkLines applies the two rules to one citation.
//
// The rules are PER LISTED LINE: a citation that names four lines says
// four things, and each of them has to be true. A citation that names a
// range says "something in here", and the range is read as a range.
func checkLines(doc string, docLine int, path string, nums string, anchor string, source []string) []finding {
	var findings []finding
	for _, part := range strings.Split(nums, ",") {
		first, last := part, part
		if dash := strings.Index(part, "-"); dash >= 0 {
			first, last = part[:dash], part[dash+1:]
		}
		a, err1 := strconv.Atoi(first)
		if err1 != nil {
			continue
		}
		b := a
		if last != "" {
			parsed, err2 := strconv.Atoi(last)
			if err2 != nil {
				continue
			}
			b = parsed
		} else {
			// An open range — "the file from here on" — runs to the end.
			b = len(source)
		}
		if b < a {
			b = a
		}
		if a < 1 || b > len(source) {
			// Past the end of the file: `check-doc-cites`' rule.
			continue
		}
		codeSeen := false
		anchorSeen := false
		for n := a; n <= b; n++ {
			text := source[n-1]
			if carriesCode(text) {
				codeSeen = true
			}
			if anchor != "" && strings.Contains(text, anchor) {
				anchorSeen = true
			}
		}
		if !codeSeen {
			findings = append(findings, finding{
				rule: "DOC-ANCHOR-NO-CODE",
				doc:  doc,
				line: docLine,
				text: fmt.Sprintf("%s:%s names a line that carries no code (blank or a comment)", path, part),
			})
		}
		if anchor != "" && !anchorSeen {
			findings = append(findings, finding{
				rule: "DOC-ANCHOR-DRIFT",
				doc:  doc,
				line: docLine,
				text: fmt.Sprintf("%s:%s names the anchor %q, and it is not on the line", path, part, anchor),
			})
		}
	}
	return findings
}

// carriesCode says whether a line holds code.
//
// A line that is empty, or that is only a comment, holds no code: a
// citation that names such a line names a place where the thing the
// document says is there is NOT there. That is the shape a drifted
// citation takes when the code above it moved.
func carriesCode(text string) bool {
	trimmed := strings.TrimSpace(text)
	if trimmed == "" {
		return false
	}
	if strings.HasPrefix(trimmed, "//") || strings.HasPrefix(trimmed, "/*") ||
		strings.HasPrefix(trimmed, "*") {
		return false
	}
	return true
}
