// Package checkeventtopology checks the PRODUCERS block of
// docs/EVENT_TOPOLOGY.md against the code that publishes events.
//
// The document is a census: it names every producer that awaits
// `PublishAsync`, every subscriber, and the bus's own fan-out steps. A
// census that names the wrong lines is worse than no census, because a
// reader follows the citation and lands on a line that has nothing to do
// with the claim.
//
// # WHAT THIS GATE CLOSES
//
// `tools/check-doc-cites.py` fences a `file:line`: the file must exist, the
// line must be inside it, and the line must not be blank. That is the whole
// of what a line fence can see, and it is blind to the failure this document
// had: the producers block cited `AgentLoop.cs:196,241,248,260`, four lines
// that exist and are not blank and are NOT the publish calls. The calls are
// at 305, 389, 442, 456, 471 and 482. The document was wrong, the gate was
// green, and the two are not the same sentence.
//
// The fix is not a bigger fence. It is to make the CLAIM checkable: the
// block says "these are the lines that publish", so the gate reads the
// lines and asks whether they publish.
//
// THE RULES
//
//	TOPOLOGY-PRODUCER-DRIFT   a line the block cites is not a publish call
//	                          in the file it names.
//	TOPOLOGY-PRODUCER-MISSING a publish call in a file the block names is
//	                          not cited.
//	TOPOLOGY-PRODUCER-FILE    a file under src/ or apps/ that publishes is
//	                          not named in the block at all.
//
// The third rule is what makes the block a census rather than a sample. The
// document claims the list is exhaustive; this gate holds it to that.
//
// # WHAT A PUBLISH CALL IS
//
// A call: `.PublishAsync(`, dot-qualified. Not a declaration — the bus and
// its test doubles DECLARE `PublishAsync`, and a declaration is not a
// producer. Not a helper that happens to end in the same letters —
// `CompactAndPublishAsync(` is a private method that publishes INSIDE, and
// the calls to it are not publishes.
//
// THE EXIT CODES are the binary's contract: 0 green, 1 red, 2 the gate
// could not run.
package checkeventtopology

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The document this gate reads, and the marker that selects the block
// inside it.
//
// The marker is a SENTENCE the document states, not a line number and not a
// heading: a heading moves, a sentence is a claim. The block that follows
// it and ends at the first line that is not a producer row is the census.
const (
	defaultDoc  = "docs/EVENT_TOPOLOGY.md"
	blockHeader = "producers (await PublishAsync directly)"
)

// publishCallRe is a publish CALL: a dot-qualified `PublishAsync(`.
//
// The dot is what separates a call from a declaration and from
// `CompactAndPublishAsync(`. A declaration has no dot before the name; the
// compaction helper's own name has letters before it.
var publishCallRe = regexp.MustCompile(`\.PublishAsync\s*\(`)

// producerRowRe is one row of the producers block:
//
//	AgentLoop ..................... src/Harbor.AgentRuntime/Agents/AgentLoop.cs:305,389
//
// The label, a run of dots, the path, a colon, and the line numbers.
var producerRowRe = regexp.MustCompile(
	`^\s{2,}(?P<label>[A-Za-z][A-Za-z0-9_]*)\s+\.{3,}\s+(?P<path>[^\s:]+\.cs):(?P<lines>[0-9,]+)\s*$`)

// row is one producer the block names.
type row struct {
	label   string
	path    string
	lines   []int
	docLine int
}

// finding is one disagreement between the document and the code.
type finding struct {
	doc    string
	line   int
	rule   string
	source string
	msg    string
}

// tree is the code the document is compared against: a path and its lines.
type tree map[string][]string

// Run is the gate entry point.
func Run(args []string) int {
	docPath := defaultDoc
	minProducers := 0
	selfTest := false

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--doc":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--doc needs a path")
				return gate.Broken
			}
			i++
			docPath = args[i]
		case "--min-producers":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-producers needs a number")
				return gate.Broken
			}
			i++
			n, err := strconv.Atoi(args[i])
			if err != nil || n < 0 {
				fmt.Fprintln(os.Stderr, "--min-producers needs a number")
				return gate.Broken
			}
			minProducers = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-event-topology [--doc PATH] [--min-producers N] [--self-test]")
			fmt.Println()
			fmt.Println("Checks the producers block of docs/EVENT_TOPOLOGY.md against the code that publishes events.")
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

	doc, err := os.ReadFile(filepath.Join(root, docPath))
	if err != nil {
		fmt.Fprintf(os.Stderr, "cannot read %s: %v\n", docPath, err)
		return gate.Broken
	}

	tracked, err := gate.TrackedFiles(root, ".cs")
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot list the tracked files:", err)
		return gate.Broken
	}

	code := tree{}
	for _, path := range tracked {
		if !strings.HasPrefix(path, "src/") && !strings.HasPrefix(path, "apps/") {
			continue
		}
		lines, err := gate.ReadLines(filepath.Join(root, path))
		if err != nil {
			fmt.Fprintf(os.Stderr, "cannot read %s: %v\n", path, err)
			return gate.Broken
		}
		code[path] = lines
	}

	findings, rowsCompared, sitesCompared := compare(docPath, string(doc), code)

	fmt.Printf("compared %d producer(s) and %d publish call site(s) in %s\n",
		rowsCompared, sitesCompared, docPath)

	if len(findings) > 0 {
		fmt.Println()
		for _, f := range findings {
			where := f.doc
			if f.line > 0 {
				where = fmt.Sprintf("%s:%d", f.doc, f.line)
			}
			fmt.Printf("%s [%s] %s\n", where, f.rule, f.msg)
			if f.source != "" {
				fmt.Printf("      %s\n", f.source)
			}
		}
	}

	problems := len(findings)
	if minProducers > 0 && rowsCompared < minProducers {
		fmt.Printf("\nTOPOLOGY-PRODUCER-BLIND: compared %d producer(s), floor is %d — the rule matched less than it should\n",
			rowsCompared, minProducers)
		problems++
	}

	if problems > 0 {
		fmt.Printf("\nFAIL: %d problem(s)\n", problems)
		return gate.Red
	}
	fmt.Println("\nOK: the producers block names exactly the publish calls the tree holds.")
	return gate.Green
}

// compare is the whole rule: the document's block against the code.
//
// It returns (findings, rows compared, call sites compared). The two counts
// are what the floor is measured against, and they are separate numbers on
// purpose: a block with eight rows can name forty call sites, and a gate
// that reported one number for both would hide a block that stopped naming
// files.
func compare(docPath, doc string, code tree) ([]finding, int, int) {
	rows, headerLine, err := parseBlock(doc)
	if err != nil {
		return []finding{{doc: docPath, line: 0, rule: "TOPOLOGY-BLOCK-MISSING", msg: err.Error()}}, 0, 0
	}

	var findings []finding
	sitesCompared := 0
	named := map[string]bool{}

	for _, r := range rows {
		named[r.path] = true
		lines, ok := code[r.path]
		if !ok {
			findings = append(findings, finding{
				doc: docPath, line: r.docLine, rule: "TOPOLOGY-PRODUCER-FILE",
				msg: fmt.Sprintf("%s names %s, which is not a tracked file under src/ or apps/", r.label, r.path),
			})
			continue
		}
		calls := publishCallSites(lines)
		sitesCompared += len(calls)
		callSet := map[int]bool{}
		for _, c := range calls {
			callSet[c] = true
		}
		cited := map[int]bool{}
		for _, n := range r.lines {
			cited[n] = true
			if n < 1 || n > len(lines) {
				findings = append(findings, finding{
					doc: docPath, line: r.docLine, rule: "TOPOLOGY-PRODUCER-DRIFT",
					source: fmt.Sprintf("%s has %d lines", r.path, len(lines)),
					msg:    fmt.Sprintf("%s cites %s:%d, which is past the end of the file", r.label, r.path, n),
				})
				continue
			}
			if !callSet[n] {
				findings = append(findings, finding{
					doc: docPath, line: r.docLine, rule: "TOPOLOGY-PRODUCER-DRIFT",
					source: fmt.Sprintf("%s:%d: %s", r.path, n, strings.TrimSpace(lines[n-1])),
					msg:    fmt.Sprintf("%s cites %s:%d, which is not a publish call", r.label, r.path, n),
				})
			}
		}
		for _, c := range calls {
			if !cited[c] {
				findings = append(findings, finding{
					doc: docPath, line: r.docLine, rule: "TOPOLOGY-PRODUCER-MISSING",
					source: fmt.Sprintf("%s:%d: %s", r.path, c, strings.TrimSpace(lines[c-1])),
					msg:    fmt.Sprintf("%s publishes at %s:%d, and the block does not cite it", r.label, r.path, c),
				})
			}
		}
	}

	// The census rule: a file that publishes and is not named is a producer
	// the document forgot.
	var unpublished []string
	for path := range code {
		if named[path] {
			continue
		}
		if len(publishCallSites(code[path])) > 0 {
			unpublished = append(unpublished, path)
		}
	}
	sort.Strings(unpublished)
	for _, path := range unpublished {
		findings = append(findings, finding{
			doc: docPath, line: headerLine, rule: "TOPOLOGY-PRODUCER-FILE",
			msg: fmt.Sprintf("%s publishes %d event(s) and the producers block does not name it",
				path, len(publishCallSites(code[path]))),
		})
	}

	return findings, len(rows), sitesCompared
}

// parseBlock reads the producers block out of the document.
//
// The block ends at the first line that is not a producer row: the diagram
// continues below it with the arrow that points at the bus, and that arrow
// is not a producer.
func parseBlock(doc string) ([]row, int, error) {
	lines := strings.Split(doc, "\n")
	headerLine := 0
	start := -1
	for i, l := range lines {
		if strings.TrimSpace(l) == blockHeader {
			headerLine = i + 1
			start = i + 1
			break
		}
	}
	if start < 0 {
		return nil, 0, fmt.Errorf("the document does not declare the block %q", blockHeader)
	}

	var rows []row
	for i := start; i < len(lines); i++ {
		l := lines[i]
		if strings.TrimSpace(l) == "" {
			continue
		}
		m := producerRowRe.FindStringSubmatch(l)
		if m == nil {
			break
		}
		var nums []int
		for _, part := range strings.Split(m[3], ",") {
			n, err := strconv.Atoi(part)
			if err != nil {
				return nil, headerLine, fmt.Errorf("the producer row at %d carries a line number that is not a number", i+1)
			}
			nums = append(nums, n)
		}
		rows = append(rows, row{
			label:   m[1],
			path:    m[2],
			lines:   nums,
			docLine: i + 1,
		})
	}
	if len(rows) == 0 {
		return nil, headerLine, fmt.Errorf("the block %q holds no producer row", blockHeader)
	}
	return rows, headerLine, nil
}

// publishCallSites returns the line numbers of the publish calls in a file.
func publishCallSites(lines []string) []int {
	var out []int
	for i, l := range lines {
		if publishCallRe.MatchString(l) {
			out = append(out, i+1)
		}
	}
	return out
}
