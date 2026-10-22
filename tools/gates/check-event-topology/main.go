// Package eventtopology checks that the producer table of
// docs/EVENT_TOPOLOGY.md still names the tree's real PublishAsync call sites.
//
// WHY THIS GATE EXISTS
//
//	docs/EVENT_TOPOLOGY.md carries the line "Status: normative", so its
//	`file:line` citations are fenced by tools/check-doc-cites.py. That fence
//	asks ONE question: does the line exist? For a linear citation that is the
//	right question. For the producer table of §1 it is the wrong one, because
//	there the line number is the CONTENT of the row rather than a pointer to a
//	place. A row that named four call sites where the file has seven passes
//	the fence, because every one of the four lines still exists and still
//	holds a publish.
//
//	#2119 is that defect: the four producer rows of the diagram cited 18
//	lines, the tree had 39 call sites in 8 files, and the four rows named
//	three files that publish and one file that does not. The gate exists so
//	the next разъезд is a red build instead of a wrong document.
//
// WHAT IT CHECKS
//
//	EVT-TOPO-CITE-UNRESOLVED   a cited file cannot be resolved to a tracked
//	                           .cs file under src/ or apps/.
//	EVT-TOPO-CITE-BLANK        a cited line is past the end of the file.
//	EVT-TOPO-NOT-PUBLISH       a cited producer line holds no PublishAsync
//	                           call.
//	EVT-TOPO-SITE-UNDOCUMENTED the file holds a PublishAsync call on a line
//	                           the row does not cite.
//	EVT-TOPO-PRODUCER-MISSING  a file under src/ or apps/ holds a
//	                           PublishAsync call and has no producer row.
//	EVT-TOPO-PRODUCER-EXTRA    a producer row names a file that holds no
//	                           PublishAsync call.
//	EVT-TOPO-DUPLICATE-ROW     two producer rows name the same file.
//	EVT-TOPO-BUS-DECL          the bus row of the diagram does not cite the
//	                           declaration of PublishAsync.
//	EVT-TOPO-NOT-SUBSCRIBE     a cited subscriber line holds no Subscribe
//	                           call.
//	EVT-TOPO-TABLE-MISSING     the sinks document has no subscriber table.
//	EVT-TOPO-BLOCK-BROKEN      the producer block does not end where the bus
//	                           row begins, or the subscriber block does not
//	                           end at the fence. A truncated block would
//	                           silently drop rows, so the truncation is a
//	                           finding rather than a shorter list.
//
// WHAT IT DOES NOT CHECK, AND WHY
//
//	Whether a `.Subscribe(` call is a BUS subscription or an IAgent
//	subscription. Both interfaces have a Subscribe method, and the two are
//	told apart by the TYPE OF THE RECEIVER, which no line of text states.
//	src/Harbor.Telemetry.Core/TracingAgentProxy.cs:32 is the live example:
//	`inner.Subscribe(listener)` forwards to IAgent.Subscribe. So the
//	subscriber rows are checked for presence, and their exhaustiveness stays
//	a claim a reader checks by reading. The producer rows have no such
//	ambiguity: PublishAsync belongs to IEventBus alone.
//
// TWO DOCUMENTS
//
//	docs/EVENT_TOPOLOGY.md carries the producer table, and
//	docs/EVENT_BUS_SINKS.md carries a SUBSCRIBER table that names the same
//	subscribers. Both drifted the same way: the DiagnosticsAggregator row
//	still pointed at 102,103 in both when the two Subscribe calls moved to
//	106,107. One gate reads both.
//
// The text this prints is the text a reader of the document needs: the row,
//
//	the file, the line, and what the tree holds there.
package eventtopology

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

// docPath is the document this gate reads.
const docPath = "docs/EVENT_TOPOLOGY.md"

// sinksDocPath is the SECOND document the gate reads. Its subscriber table
// names the same subscribers as the topology document's, and it had drifted
// the same way: the DiagnosticsAggregator row still pointed at 102,103 when
// the two Subscribe calls moved to 106,107.
const sinksDocPath = "docs/EVENT_BUS_SINKS.md"

// sinksTableHeader is the header of that table. The gate finds the table by
// its header, not by its position, so a paragraph inserted above it does not
// move the check onto the wrong table.
var sinksTableHeader = []string{"Site", "Verdict", "Reason"}

// perimeterDirs are the trees the producer list must be exhaustive over.
var perimeterDirs = []string{"src", "apps"}

// publishCallRe finds a bus publish CALL: a receiver, a dot, the method, an
// open parenthesis.
//
// The dot is the whole point. `PublishAsync` with no receiver is the
// DECLARATION of the method (IEventBus.cs:37, InMemoryEventBus.cs:353,
// NullEventBus.cs:13), and a helper whose name merely ends in `PublishAsync`
// (CompactionBehavior.CompactAndPublishAsync) is not a call either.
var publishCallRe = regexp.MustCompile(`\.PublishAsync\s*\(`)

// subscribeCallRe finds a subscription call, generic form included:
//
//	eventBus.Subscribe<ToolExecutionEndEvent>(OnToolEnded)
var subscribeCallRe = regexp.MustCompile(`\.Subscribe\s*(<[^<>()]*>)?\s*\(`)

// declarationRe finds a PublishAsync DECLARATION: the name with no receiver
// in front of it. The `[^.\w]` before the name keeps `CompactAndPublishAsync`
// out, because the character in front of `PublishAsync` there is a letter.
var declarationRe = regexp.MustCompile(`(^|[^.\w])PublishAsync\s*\(`)

// citesRe is the citations part of a diagram row: one or more `path:spec`
// groups joined by " + ". It is anchored at the END of the line.
//
// Anchoring at the end is what makes the parse independent of the label: a
// row's label may hold dots, a colon, or nothing at all before the leader,
// and the citations are the part that has a shape. The leader of dots is
// optional, because one row of the diagram has a label too long to be
// followed by a leader.
var citesRe = regexp.MustCompile(
	`[A-Za-z0-9_][A-Za-z0-9_./\-]*:\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*` +
		`(?:\s+\+\s+[A-Za-z0-9_][A-Za-z0-9_./\-]*:\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)*$`)

// citeRe is one `path:spec` citation. The spec is a comma-separated list of
// line numbers and `a-b` ranges.
var citeRe = regexp.MustCompile(`^([A-Za-z0-9_][A-Za-z0-9_./\-]*):(\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)$`)

// citation is one `path:lines` group of a diagram row.
type citation struct {
	path string
	spec string
}

// row is one line of the diagram: a label and the citations it names.
type row struct {
	label   string
	cites   []citation
	docLine int
}

// diagram is what the gate reads out of the document.
type diagram struct {
	producers   []row
	bus         *row
	subscribers []row
}

// finding is one disagreement between the document and the tree.
type finding struct {
	doc  string
	line int
	rule string
	msg  string
}

// stats are the counts the floors are measured against.
type stats struct {
	producerRows   int
	subscriberRows int
	citedSites     int
	treeSites      int
	treeProducers  int
}

// Run is the gate entry point.
func Run(args []string) int {
	minProducers := 0
	minSites := 0
	minSubscribers := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-producers":
			n, ok := nextInt(args, &i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-producers needs a number")
				return gate.Broken
			}
			minProducers = n
		case "--min-sites":
			n, ok := nextInt(args, &i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-sites needs a number")
				return gate.Broken
			}
			minSites = n
		case "--min-subscribers":
			n, ok := nextInt(args, &i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-subscribers needs a number")
				return gate.Broken
			}
			minSubscribers = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-event-topology [--min-producers N] [--min-sites N] [--min-subscribers N] [--self-test]")
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
	text, err := os.ReadFile(filepath.Join(root, docPath))
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read", docPath, ":", err)
		return gate.Broken
	}
	sources, err := gate.TrackedFiles(root, ".cs")
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot list the tracked files:", err)
		return gate.Broken
	}

	findings, st := scan(scanInput{
		root:    root,
		docText: string(text),
		sources: sources,
	})

	fmt.Printf("compared %d producer row(s) and %d subscriber row(s) against %d PublishAsync call site(s) in %d producer file(s) in %s\n",
		st.producerRows, st.subscriberRows, st.treeSites, st.treeProducers, docPath)

	if len(findings) > 0 {
		fmt.Println()
		for _, f := range findings {
			fmt.Printf("%s:%d [%s] %s\n", f.doc, f.line, f.rule, f.msg)
		}
	}

	problems := len(findings)
	if minProducers > 0 && st.producerRows < minProducers {
		fmt.Printf("\nEVT-TOPO-BLIND: read %d producer rows, floor is %d — the rule matched less than it should\n",
			st.producerRows, minProducers)
		problems++
	}
	if minSites > 0 && st.treeSites < minSites {
		fmt.Printf("\nEVT-TOPO-BLIND: the tree holds %d PublishAsync call site(s), floor is %d — the rule matched less than it should\n",
			st.treeSites, minSites)
		problems++
	}
	if minSubscribers > 0 && st.subscriberRows < minSubscribers {
		fmt.Printf("\nEVT-TOPO-BLIND: read %d subscriber rows, floor is %d — the rule matched less than it should\n",
			st.subscriberRows, minSubscribers)
		problems++
	}

	if problems > 0 {
		fmt.Printf("\nFAIL: %d problem(s)\n", problems)
		return gate.Red
	}
	fmt.Println("\nOK: every producer row names exactly the PublishAsync call sites its file holds, and every producer file has a row.")
	return gate.Green
}

// nextInt reads the number after a flag.
func nextInt(args []string, i *int) (int, bool) {
	if *i+1 >= len(args) {
		return 0, false
	}
	*i++
	n, err := strconv.Atoi(args[*i])
	if err != nil || n < 0 {
		return 0, false
	}
	return n, true
}

// scanInput is what scan reads. The self-test builds a synthetic tree and
// calls scan directly, so the gate's logic never has to run against the real
// repository to be proved.
type scanInput struct {
	root    string
	docText string
	sources []string
}

// scan compares the document with the tree.
func scan(in scanInput) ([]finding, stats) {
	var findings []finding
	var st stats

	d, problems := parseDiagram(in.docText)
	findings = append(findings, problems...)

	res := newResolver(in.root, in.sources)

	// The tree's own truth: the publish call sites of every file in the
	// perimeter, and the files that hold any.
	treeSites := map[string][]int{}
	for _, src := range in.sources {
		if !inPerimeter(src) {
			continue
		}
		lines, err := readLines(in.root, src)
		if err != nil {
			continue
		}
		sites := publishSites(lines)
		if len(sites) == 0 {
			continue
		}
		treeSites[src] = sites
		st.treeProducers++
		st.treeSites += len(sites)
	}

	// The producer rows.
	seen := map[string]bool{}
	for _, r := range d.producers {
		st.producerRows++
		if len(r.cites) != 1 {
			findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-ROW-SHAPE",
				fmt.Sprintf("row %q names %d citations; a producer row names one file", r.label, len(r.cites))})
			continue
		}
		c := r.cites[0]
		path, reason := res.resolve(c.path)
		if path == "" {
			findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-CITE-UNRESOLVED",
				fmt.Sprintf("%s: %s", c.path, reason)})
			continue
		}
		if seen[path] {
			findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-DUPLICATE-ROW",
				fmt.Sprintf("%s has more than one producer row", path)})
			continue
		}
		seen[path] = true

		lines, err := readLines(in.root, path)
		if err != nil {
			findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-CITE-UNRESOLVED",
				fmt.Sprintf("%s: cannot read the file: %v", path, err)})
			continue
		}

		cited := expandSpec(c.spec)
		st.citedSites += len(cited)
		for _, n := range cited {
			if n < 1 || n > len(lines) {
				findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-CITE-BLANK",
					fmt.Sprintf("%s:%d is past the end of the file (%d lines)", path, n, len(lines))})
				continue
			}
			if !publishCallRe.MatchString(maskComment(lines[n-1])) {
				findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-NOT-PUBLISH",
					fmt.Sprintf("%s:%d holds no PublishAsync call; it reads %q", path, n, strings.TrimSpace(lines[n-1]))})
			}
		}

		// The other direction: a publish the row does not cite.
		for _, site := range treeSites[path] {
			if !contains(cited, site) {
				findings = append(findings, finding{docPath, r.docLine, "EVT-TOPO-SITE-UNDOCUMENTED",
					fmt.Sprintf("%s:%d is a PublishAsync call the row does not cite; the row cites %s",
						path, site, c.spec)})
			}
		}
	}

	// A producer file with no row.
	for path := range treeSites {
		if !seen[path] {
			findings = append(findings, finding{docPath, 0, "EVT-TOPO-PRODUCER-MISSING",
				fmt.Sprintf("%s holds %d PublishAsync call site(s) and has no producer row", path, len(treeSites[path]))})
		}
	}
	// A producer row over a file with no publish at all.
	for path := range seen {
		if _, ok := treeSites[path]; !ok {
			findings = append(findings, finding{docPath, 0, "EVT-TOPO-PRODUCER-EXTRA",
				fmt.Sprintf("%s holds no PublishAsync call, and it has a producer row", path)})
		}
	}

	// The bus row: the DECLARATION of PublishAsync, not a call site.
	if d.bus == nil {
		findings = append(findings, finding{docPath, 0, "EVT-TOPO-BUS-ROW-MISSING",
			"the diagram has no bus row (the line after the arrow)"})
	} else if len(d.bus.cites) == 1 {
		c := d.bus.cites[0]
		path, reason := res.resolve(c.path)
		if path == "" {
			findings = append(findings, finding{docPath, d.bus.docLine, "EVT-TOPO-CITE-UNRESOLVED",
				fmt.Sprintf("%s: %s", c.path, reason)})
		} else {
			lines, err := readLines(in.root, path)
			if err != nil {
				findings = append(findings, finding{docPath, d.bus.docLine, "EVT-TOPO-CITE-UNRESOLVED",
					fmt.Sprintf("%s: cannot read the file: %v", path, err)})
			} else {
				decl := declarationLine(lines)
				cited := expandSpec(c.spec)
				if len(cited) != 1 || cited[0] != decl {
					findings = append(findings, finding{docPath, d.bus.docLine, "EVT-TOPO-BUS-DECL",
						fmt.Sprintf("the bus row cites %s; the PublishAsync declaration of %s is on line %d",
							c.spec, path, decl)})
				}
			}
		}
	}

	// The subscriber rows of the topology document: every cited line holds a
	// Subscribe call.
	st.subscriberRows += len(d.subscribers)
	findings = append(findings, checkSubscriberRows(in.root, res, d.subscribers, docPath)...)

	// The subscriber table of the sinks document: the same claim, the same
	// shape, and it drifted the same way.
	sinksFindings, sinksRows, err := checkSinksSubscribers(in.root, res)
	if err != nil {
		findings = append(findings, finding{docPath, 0, "EVT-TOPO-DOC-MISSING",
			fmt.Sprintf("cannot read %s: %v", sinksDocPath, err)})
	} else {
		findings = append(findings, sinksFindings...)
		st.subscriberRows += sinksRows
	}

	sort.SliceStable(findings, func(i, j int) bool {
		if findings[i].doc != findings[j].doc {
			return findings[i].doc < findings[j].doc
		}
		if findings[i].line != findings[j].line {
			return findings[i].line < findings[j].line
		}
		return findings[i].rule < findings[j].rule
	})
	return findings, st
}

// checkSubscriberRows checks that every cited line of a subscriber row holds
// a Subscribe call.
//
// A row may cite a RANGE (`300-303`), and then the claim is "the subscription
// happens somewhere in this block", so one line of the range has to hold the
// call. A row that cites single lines claims each of them.
func checkSubscriberRows(root string, res *resolver, rows []row, doc string) []finding {
	var findings []finding
	for _, r := range rows {
		for _, c := range r.cites {
			path, reason := res.resolve(c.path)
			if path == "" {
				findings = append(findings, finding{doc, r.docLine, "EVT-TOPO-CITE-UNRESOLVED",
					fmt.Sprintf("%s: %s", c.path, reason)})
				continue
			}
			lines, err := readLines(root, path)
			if err != nil {
				findings = append(findings, finding{doc, r.docLine, "EVT-TOPO-CITE-UNRESOLVED",
					fmt.Sprintf("%s: cannot read the file: %v", path, err)})
				continue
			}
			findings = append(findings, checkSubscribeGroups(path, c, lines, doc, r.docLine)...)
		}
	}
	return findings
}

// checkSubscribeGroups checks the line groups of one citation.
func checkSubscribeGroups(path string, c citation, lines []string, doc string, docLine int) []finding {
	var findings []finding
	for _, group := range expandGroups(c.spec) {
		holds := false
		for _, n := range group {
			if n < 1 || n > len(lines) {
				findings = append(findings, finding{doc, docLine, "EVT-TOPO-CITE-BLANK",
					fmt.Sprintf("%s:%d is past the end of the file (%d lines)", path, n, len(lines))})
				continue
			}
			if subscribeCallRe.MatchString(maskComment(lines[n-1])) {
				holds = true
			}
		}
		if !holds {
			findings = append(findings, finding{doc, docLine, "EVT-TOPO-NOT-SUBSCRIBE",
				fmt.Sprintf("%s:%s holds no Subscribe call", path, c.spec)})
		}
	}
	return findings
}

// checkSinksSubscribers reads the subscriber table of the sinks document and
// checks every citation in its first column.
func checkSinksSubscribers(root string, res *resolver) ([]finding, int, error) {
	data, err := os.ReadFile(filepath.Join(root, sinksDocPath))
	if err != nil {
		return nil, 0, err
	}
	rows, ok := markdownTable(string(data), sinksTableHeader)
	if !ok {
		return []finding{{sinksDocPath, 0, "EVT-TOPO-TABLE-MISSING",
			fmt.Sprintf("no table with the header %s", strings.Join(sinksTableHeader, " | "))}}, 0, nil
	}
	var findings []finding
	count := 0
	for _, cells := range rows {
		if len(cells) == 0 {
			continue
		}
		count++
		for _, c := range citationsIn(cells[0]) {
			path, reason := res.resolve(c.path)
			if path == "" {
				findings = append(findings, finding{sinksDocPath, 0, "EVT-TOPO-CITE-UNRESOLVED",
					fmt.Sprintf("%s: %s", c.path, reason)})
				continue
			}
			lines, err := readLines(root, path)
			if err != nil {
				findings = append(findings, finding{sinksDocPath, 0, "EVT-TOPO-CITE-UNRESOLVED",
					fmt.Sprintf("%s: cannot read the file: %v", path, err)})
				continue
			}
			findings = append(findings, checkSubscribeGroups(path, c, lines, sinksDocPath, 0)...)
		}
	}
	return findings, count, nil
}

// markdownTable returns the data rows of the table whose header row holds
// the given cells.
func markdownTable(text string, header []string) ([][]string, bool) {
	lines := strings.Split(text, "\n")
	for i, line := range lines {
		if !strings.HasPrefix(strings.TrimSpace(line), "|") {
			continue
		}
		cells := tableCells(line)
		if len(cells) != len(header) {
			continue
		}
		match := true
		for k := range header {
			if cells[k] != header[k] {
				match = false
				break
			}
		}
		if !match {
			continue
		}
		var rows [][]string
		for _, candidate := range lines[i+1:] {
			if !strings.HasPrefix(strings.TrimSpace(candidate), "|") {
				break
			}
			row := tableCells(candidate)
			if len(row) == 0 || allDashes(row) {
				continue
			}
			rows = append(rows, row)
		}
		return rows, true
	}
	return nil, false
}

// tableCells splits a markdown table row into its cells.
func tableCells(line string) []string {
	text := strings.TrimSpace(line)
	text = strings.TrimPrefix(text, "|")
	text = strings.TrimSuffix(text, "|")
	parts := strings.Split(text, "|")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		out = append(out, strings.TrimSpace(p))
	}
	return out
}

// allDashes reports whether every cell of a row is the separator.
func allDashes(cells []string) bool {
	for _, c := range cells {
		for _, r := range c {
			if r != '-' && r != ':' && r != ' ' {
				return false
			}
		}
	}
	return true
}

// citationsIn reads the `path:spec` citations out of a piece of text.
func citationsIn(text string) []citation {
	var out []citation
	for _, m := range citeInTextRe.FindAllStringSubmatch(text, -1) {
		out = append(out, citation{path: m[1], spec: m[2]})
	}
	return out
}

// citeInTextRe finds a `path:spec` citation anywhere in a piece of text.
var citeInTextRe = regexp.MustCompile(`([A-Za-z0-9_][A-Za-z0-9_./\-]*):(\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)`)

// parseDiagram reads the producer block, the bus row and the subscriber
// block out of the document.
func parseDiagram(text string) (diagram, []finding) {
	var d diagram
	var findings []finding

	lines := strings.Split(text, "\n")
	producersAt, subscribersAt, arrowAt := -1, -1, -1
	for i, line := range lines {
		t := strings.TrimSpace(line)
		switch {
		case producersAt < 0 && strings.HasPrefix(t, "producers ("):
			producersAt = i
		case subscribersAt < 0 && strings.HasPrefix(t, "subscribers ("):
			subscribersAt = i
		case arrowAt < 0 && t == "▼":
			arrowAt = i
		}
	}

	if producersAt < 0 {
		findings = append(findings, finding{docPath, 0, "EVT-TOPO-BLOCK-MISSING",
			"the document has no 'producers (' header; the producer table is gone"})
	} else {
		rows, end := collectRows(lines, producersAt+1)
		d.producers = rows
		// The producer block must end where the bus row begins. A block that
		// ends earlier means rows were dropped, and a dropped row is a
		// shorter list that reads as a complete one.
		if end >= len(lines) {
			findings = append(findings, finding{docPath, producersAt + 2, "EVT-TOPO-BLOCK-BROKEN",
				"the producer block runs to the end of the document"})
		} else if !strings.HasPrefix(strings.TrimSpace(lines[end]), "│") {
			findings = append(findings, finding{docPath, end + 1, "EVT-TOPO-BLOCK-BROKEN",
				fmt.Sprintf("the producer block ends at a line that is not the diagram's connector: %q",
					strings.TrimSpace(lines[end]))})
		}
	}

	if arrowAt < 0 {
		findings = append(findings, finding{docPath, 0, "EVT-TOPO-BUS-ROW-MISSING",
			"the diagram has no '▼' connector, so the bus row cannot be found"})
	} else if arrowAt+1 < len(lines) {
		if r, ok := parseRow(lines[arrowAt+1]); ok {
			r.docLine = arrowAt + 2
			d.bus = &r
		} else {
			findings = append(findings, finding{docPath, arrowAt + 2, "EVT-TOPO-BUS-ROW-MISSING",
				"the line after the '▼' connector is not a citation row"})
		}
	}

	if subscribersAt < 0 {
		findings = append(findings, finding{docPath, 0, "EVT-TOPO-BLOCK-MISSING",
			"the document has no 'subscribers (' header; the subscriber table is gone"})
	} else {
		rows, end := collectRows(lines, subscribersAt+1)
		d.subscribers = rows
		if end >= len(lines) || strings.TrimSpace(lines[end]) != "```" {
			findings = append(findings, finding{docPath, end + 1, "EVT-TOPO-BLOCK-BROKEN",
				"the subscriber block does not end at the end of the diagram's fence"})
		}
	}

	return d, findings
}

// collectRows reads the run of citation rows that starts at `start`.
func collectRows(lines []string, start int) ([]row, int) {
	var rows []row
	i := start
	for i < len(lines) {
		r, ok := parseRow(lines[i])
		if !ok {
			break
		}
		r.docLine = i + 1
		rows = append(rows, r)
		i++
	}
	return rows, i
}

// parseRow reads one diagram row: a label, and the citations it names.
//
// The citations are read from the END of the line, because they are the part
// with a shape, and the label is whatever is left in front of them. A row
// whose label is empty is not a row: that is the continuation of a row whose
// label sits on the line above.
func parseRow(line string) (row, bool) {
	m := citesRe.FindStringIndex(line)
	if m == nil {
		return row{}, false
	}
	head := strings.TrimSpace(line[:m[0]])
	// The leader of dots is decoration; the label is what it introduces.
	label := strings.TrimSpace(strings.TrimRight(head, "."))
	if label == "" {
		return row{}, false
	}
	parts := strings.Split(line[m[0]:], " + ")
	cites := make([]citation, 0, len(parts))
	for _, p := range parts {
		cm := citeRe.FindStringSubmatch(strings.TrimSpace(p))
		if cm == nil {
			return row{}, false
		}
		cites = append(cites, citation{path: cm[1], spec: cm[2]})
	}
	return row{label: label, cites: cites}, true
}

// resolver turns a cited path into a tracked file under src/ or apps/.
type resolver struct {
	root   string
	byBase map[string][]string
}

func newResolver(root string, sources []string) *resolver {
	r := &resolver{root: root, byBase: map[string][]string{}}
	for _, s := range sources {
		if !inPerimeter(s) {
			continue
		}
		base := filepath.Base(s)
		r.byBase[base] = append(r.byBase[base], s)
	}
	for k := range r.byBase {
		sort.Strings(r.byBase[k])
	}
	return r
}

// resolve returns the tracked file a citation names, or an empty string and a
// reason.
func (r *resolver) resolve(cited string) (string, string) {
	clean := strings.TrimSpace(cited)
	if clean == "" {
		return "", "the citation names no file"
	}
	if filepath.IsAbs(clean) {
		return "", "the citation names an absolute path"
	}
	if strings.Contains(clean, "..") {
		return "", "the citation names a path outside the repository"
	}
	if st, err := os.Stat(filepath.Join(r.root, clean)); err == nil && !st.IsDir() {
		return clean, ""
	}
	base := filepath.Base(clean)
	cands := r.byBase[base]
	switch len(cands) {
	case 0:
		return "", "no tracked .cs file with that name under src/ or apps/"
	case 1:
		return cands[0], ""
	default:
		return "", fmt.Sprintf("%d tracked files have that name under src/ or apps/ (%s)",
			len(cands), strings.Join(cands, ", "))
	}
}

// inPerimeter reports whether a path is under src/ or apps/.
func inPerimeter(path string) bool {
	for _, dir := range perimeterDirs {
		if strings.HasPrefix(path, dir+"/") {
			return true
		}
	}
	return false
}

// readLines reads a file's lines.
func readLines(root, rel string) ([]string, error) {
	data, err := os.ReadFile(filepath.Join(root, rel))
	if err != nil {
		return nil, err
	}
	text := string(data)
	if text == "" {
		return nil, nil
	}
	lines := strings.Split(text, "\n")
	if lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	return lines, nil
}

// publishSites returns the lines of a file that hold a PublishAsync call.
func publishSites(lines []string) []int {
	var out []int
	for i, line := range lines {
		if publishCallRe.MatchString(maskComment(line)) {
			out = append(out, i+1)
		}
	}
	return out
}

// subscribeSites returns the lines of a file that hold a Subscribe call.
//
// The generic form counts: `eventBus.Subscribe<ToolExecutionEndEvent>(…)` is a
// subscription too.
func subscribeSites(lines []string) []int {
	var out []int
	for i, line := range lines {
		if subscribeCallRe.MatchString(maskComment(line)) {
			out = append(out, i+1)
		}
	}
	return out
}

// declarationLine returns the line that declares PublishAsync, or 0.
func declarationLine(lines []string) int {
	for i, line := range lines {
		if declarationRe.MatchString(maskComment(line)) {
			return i + 1
		}
	}
	return 0
}

// expandGroups turns a citation spec into the groups of line numbers it
// names. A comma-separated list of single numbers and `a-b` ranges: one
// group per comma-separated part.
//
// The groups are kept apart because the two kinds of row read a group
// differently. A producer row names the call sites, so every line of a group
// has to be a call site. A subscriber row names a place where a subscription
// happens, and a range like `300-303` names a block in which ONE line holds
// the call.
func expandGroups(spec string) [][]int {
	var out [][]int
	for _, part := range strings.Split(spec, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		lo, hi, isRange := strings.Cut(part, "-")
		a, err := strconv.Atoi(strings.TrimSpace(lo))
		if err != nil {
			continue
		}
		b := a
		if isRange {
			trimmed := strings.TrimSpace(hi)
			if trimmed == "" {
				b = a
			} else {
				v, err := strconv.Atoi(trimmed)
				if err != nil {
					continue
				}
				b = v
			}
		}
		group := make([]int, 0, b-a+1)
		for n := a; n <= b; n++ {
			group = append(group, n)
		}
		out = append(out, group)
	}
	return out
}

// expandSpec turns a citation spec into the line numbers it names.
func expandSpec(spec string) []int {
	var out []int
	for _, group := range expandGroups(spec) {
		out = append(out, group...)
	}
	return out
}

// contains reports whether a list holds a value.
func contains(list []int, value int) bool {
	for _, v := range list {
		if v == value {
			return true
		}
	}
	return false
}

// maskComment blanks out the string literals and the line comment of a line,
// so a call that only appears in a literal or in a comment is not read as a
// call.
//
// The literals go first, because a `//` inside a literal is not a comment:
// the two masks would otherwise eat the rest of the line.
func maskComment(line string) string {
	masked := maskLiterals(line)
	idx := strings.Index(masked, "//")
	if idx < 0 {
		return masked
	}
	out := []byte(masked)
	for i := idx; i < len(out); i++ {
		out[i] = ' '
	}
	return string(out)
}

// maskLiterals blanks out the string and character literals of a line. The
// length is preserved, so a line number stays the line number.
func maskLiterals(line string) string {
	out := []byte(line)
	blank := func(from, to int) {
		for i := from; i < to && i < len(out); i++ {
			if out[i] != '\t' {
				out[i] = ' '
			}
		}
	}
	i := 0
	for i < len(line) {
		switch line[i] {
		case '"':
			j := i + 1
			for j < len(line) {
				if line[j] == '\\' {
					j += 2
					continue
				}
				if line[j] == '"' {
					j++
					break
				}
				j++
			}
			blank(i, j)
			i = j
		case '\'':
			j := i + 1
			for j < len(line) {
				if line[j] == '\\' {
					j += 2
					continue
				}
				if line[j] == '\'' {
					j++
					break
				}
				j++
			}
			blank(i, j)
			i = j
		default:
			i++
		}
	}
	return string(out)
}
