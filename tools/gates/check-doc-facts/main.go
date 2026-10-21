// Package docfacts is the compiled port of tools/check-doc-facts.py.
//
// The gate compares a COUNT that a document states against the COUNT the
// tree holds: "docs/ — 64 top-level docs" in the onboarding documents,
// against the number of tracked files that really are top-level docs.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package docfacts

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The documents the claim is read from. The perimeter is data, not
// inference: these two documents carry the repo map, and nothing else does.
var perimeter = []string{"AGENTS.md", "CLAUDE.md"}

// measure is one repo-map claim: how to find the stated number, and how to
// recount it.
//
// The pattern carries exactly one capture group — the stated count — and the
// recount is a pure function of the tracked-file list, so the comparison is
// prose against the filesystem and not prose against prose.
//
// The population a recount measures is written out in its own pattern rather
// than shared between rules, because sharing one is how a rule ends up
// comparing a number against the wrong denominator.
type measure struct {
	rule       string
	label      string
	pattern    *regexp.Regexp
	recount    func(tracked []string) int
	population string
	// directories means the measure is served by LISTING the directory, not
	// by counting tracked paths: a tracked-file list cannot see an empty
	// directory, and `tests/fixtures/` is data.
	directories bool
}

func countMatching(pattern string) func([]string) int {
	rx := regexp.MustCompile(pattern)
	return func(tracked []string) int {
		n := 0
		for _, path := range tracked {
			if rx.MatchString(path) {
				n++
			}
		}
		return n
	}
}

// topLevelDocs counts the markdown files sitting DIRECTLY in docs/.
//
// The distinction is the whole rule. `docs/` also holds adr/, specs/,
// standards/, ui/, themes/, notes/ and .kilo-docs/, so a recursive count is
// far above a top-level count on this tree, and a gate that picked the
// wrong one would be red on a correct document.
func topLevelDocs(tracked []string) int {
	return countMatching(`^docs/[^/]+\.md$`)(tracked)
}

func measures() []measure {
	return []measure{
		{
			rule:       "DOC-COUNT-DRIFT",
			label:      "docs/ top-level",
			pattern:    regexp.MustCompile(`(?m)^docs/\s+—\s+(\d+)\s+top-level docs\b`),
			recount:    topLevelDocs,
			population: `^docs/[^/]+\.md$`,
		},
		{
			rule:       "DOC-COUNT-DRIFT",
			label:      "docs/specs/",
			pattern:    regexp.MustCompile(`(?m)^docs/specs/\s+—\s+(\d+)\s+design specification documents\b`),
			recount:    countMatching(`^docs/specs/[^/]+\.md$`),
			population: `^docs/specs/[^/]+\.md$`,
		},
		{
			rule:       "DOC-COUNT-DRIFT",
			label:      "providers/",
			pattern:    regexp.MustCompile(`(?m)^providers/\s+—\s+(\d+)\s+JSON LLM provider configs\b`),
			recount:    countMatching(`^providers/[^/]+\.json$`),
			population: `^providers/[^/]+\.json$`,
		},
		{
			rule:       "DOC-COUNT-DRIFT",
			label:      "tests/ projects",
			pattern:    regexp.MustCompile(`(?m)^tests/\s+—\s+(\d+)\s+test/bench projects\b`),
			recount:    countMatching(`^tests/[^/]+/[^/]+\.csproj$`),
			population: `^tests/[^/]+/[^/]+\.csproj$`,
		},
		{
			rule:        "DOC-COUNT-DRIFT",
			label:       "tests/ directories",
			pattern:     regexp.MustCompile("(?m)\\(`tests/` holds (\\d+) directories"),
			recount:     func([]string) int { return 0 },
			population:  "(see below — directories are counted from the tree, not tracked files)",
			directories: true,
		},
	}
}

// The waivers: a label with no reason is a finding, not a waiver.
var (
	allowRe = regexp.MustCompile(
		`<!--\s*check-doc-cites:\s*allow-count\s+"(?P<label>[^"]+)"\s*—\s*(?P<why>(?U:.+?))\s*-->`)
	allowNoReasonRe = regexp.MustCompile(
		`<!--\s*check-doc-cites:\s*allow-count\s+"(?P<label>[^"]+)"\s*-->`)
)

// finding is one disagreement between a document and the tree.
type finding struct {
	doc  string
	line int
	rule string
	msg  string
}

// Run is the gate entry point.
func Run(args []string) int {
	minFacts := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-facts":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-facts needs a number")
				return gate.Broken
			}
			i++
			n := 0
			for _, r := range args[i] {
				if r < '0' || r > '9' {
					fmt.Fprintln(os.Stderr, "--min-facts needs a number")
					return gate.Broken
				}
				n = n*10 + int(r-'0')
			}
			minFacts = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-doc-facts [--min-facts N] [--self-test]")
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

	findings, compared, blind := scan(root)

	fmt.Printf("compared %d repo-map count(s) against the tree in %d onboarding document(s): %s\n",
		compared, len(perimeter), strings.Join(perimeter, ", "))

	if len(blind) > 0 {
		fmt.Printf("\nBLIND — %d declared measure(s) matched no line in ANY document in the perimeter. A pattern that stops matching is a rule that stops ruling:\n",
			len(blind))
		for _, label := range blind {
			fmt.Printf("  no line matches the %q measure [DOC-COUNT-BLIND]\n", label)
		}
	}

	if len(findings) > 0 {
		fmt.Println()
		for _, f := range findings {
			fmt.Printf("%s:%d [%s] %s\n", f.doc, f.line, f.rule, f.msg)
		}
	}

	problems := len(findings)
	if minFacts > 0 && compared < minFacts {
		fmt.Printf("\nDOC-COUNT-BLIND: compared %d counts, floor is %d — the rule matched less than it should\n",
			compared, minFacts)
		problems++
	}

	if problems > 0 {
		fmt.Printf("\nFAIL: %d problem(s)\n", problems)
		return gate.Red
	}
	fmt.Println("\nOK: every repo-map count in the onboarding documents matches the tree.")
	return gate.Green
}

// scan returns (findings, compared, blind). `compared` is the floor's input.
//
// `blind` is per-MEASURE, not per-(document, measure). The first version
// reported a measure missing from every document it was not written for, and
// on this tree that produced four findings — CLAUDE.md has no repo map, so
// three of the five measures legitimately match nothing there. A signal that
// fires on documents that never made the claim trains a reader to ignore it,
// and the one thing that must never be ignored is the measure that stopped
// matching in the document that DID make it.
//
// So a measure is blind only when it matches nothing anywhere. Whether each
// document is supposed to carry each measure is a per-document declaration
// the perimeter already encodes, and it is data, not inference.
func scan(root string) ([]finding, int, []string) {
	var findings []finding
	compared := 0
	matchedAnywhere := map[string]bool{}
	tracked := trackedFiles(root)
	for _, doc := range perimeter {
		text, err := os.ReadFile(filepath.Join(root, doc))
		if err != nil {
			findings = append(findings, finding{doc, 0, "DOC-COUNT-READ", "cannot read: " + err.Error()})
			continue
		}
		body := string(text)
		allowed := allowances(body)
		noReason := map[string]bool{}
		for _, m := range allowNoReasonRe.FindAllStringSubmatch(body, -1) {
			noReason[m[1]] = true
		}
		for _, meas := range measures() {
			m := meas.pattern.FindStringSubmatchIndex(body)
			if m == nil {
				continue
			}
			matchedAnywhere[meas.label] = true
			_, isAllowed := allowed[meas.label]
			if noReason[meas.label] && !isAllowed {
				findings = append(findings, finding{
					doc, lineOf(body, m[0]), "DOC-ALLOW-NO-REASON",
					fmt.Sprintf("allow-count for %q carries no reason", meas.label),
				})
				continue
			}
			if isAllowed {
				compared++
				continue
			}
			actual := meas.recount(tracked)
			if meas.directories {
				actual = testsDirectories(root)
			}
			stated := 0
			fmt.Sscanf(body[m[2]:m[3]], "%d", &stated)
			compared++
			if stated != actual {
				findings = append(findings, finding{
					doc, lineOf(body, m[0]), meas.rule,
					fmt.Sprintf("%s: document states %d, the tree holds %d",
						meas.label, stated, actual),
				})
			}
		}
	}
	var blind []string
	for _, meas := range measures() {
		if !matchedAnywhere[meas.label] {
			blind = append(blind, meas.label)
		}
	}
	return findings, compared, blind
}

// lineOf is the 1-based line an offset falls on.
func lineOf(text string, offset int) int {
	return strings.Count(text[:offset], "\n") + 1
}

// allowances returns label -> reason.
func allowances(text string) map[string]string {
	allowed := map[string]string{}
	for _, m := range allowRe.FindAllStringSubmatch(text, -1) {
		allowed[m[1]] = strings.TrimSpace(m[2])
	}
	return allowed
}

// trackedFiles lists the paths git tracks.
func trackedFiles(root string) []string {
	paths, err := gate.TrackedFiles(root)
	if err != nil {
		return nil
	}
	sort.Strings(paths)
	return paths
}

// testsDirectories counts the directories under tests/.
//
// A tracked-file list cannot see an empty directory, so this one measure is
// served by listing the directory instead of by counting paths.
func testsDirectories(root string) int {
	entries, err := os.ReadDir(filepath.Join(root, "tests"))
	if err != nil {
		return -1
	}
	n := 0
	for _, entry := range entries {
		if entry.IsDir() {
			n++
		}
	}
	return n
}
