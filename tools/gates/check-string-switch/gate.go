// Package stringswitch is the compiled port of tools/check-string-switch.py.
//
// The gate asks whether the product dispatches on a string literal with a
// `case` ladder, and whether every ladder that is still in the tree is
// named in the exceptions file with a reason.
//
// It is a RATCHET in both directions: a ladder the tree does not have
// fails the gate, and so does a row the tree no longer produces. A closed
// hole cannot stay blessed.
//
// The reviewed rewrites the `--apply` mode replays live in `rewrites.go`,
// the port of tools/string-switch-rewrites.py.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 clean, 1 a finding, 2 no
// input.
package stringswitch

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// skipDirParts are the directories the walk does not enter.
var skipDirParts = map[string]bool{"bin": true, "obj": true}

// The noise stripper: comments and literals are blanked (positions
// preserved) so that a ladder inside a raw string is not a ladder, and a
// `case` inside a comment is not a `case`.
//
// The branch order is safe because the four constructs cannot overlap in
// their STARTING positions: a `/` opens a comment, a `"` opens a string, a
// `'` opens a char. A `/` inside a string is never reached, because the
// string branch consumed the whole literal from its opening quote; a `"`
// inside a comment is never reached for the same reason. The raw-string
// branch is first because `"""` would otherwise be read as a string, a
// string and a string.
var noise = regexp.MustCompile(
	`"""[\s\S]*?"""` +
		`|//[^\n]*` +
		`|/\*[\s\S]*?\*/` +
		`|"(?:[^"\\\n]|\\.)*"` +
		"|'(?:[^'\\\n]|\\.)*'")

// switchKeyword is the `switch` keyword. The python original fences it
// with a lookbehind and a lookahead — RE2 has neither — so the word
// boundaries are checked by hand.
var switchKeyword = regexp.MustCompile(`switch`)

// The arms of a switch body.
var (
	whenGuard = regexp.MustCompile(`^\s*when\b`)
	selector  = regexp.MustCompile(
		`([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\([^()]*\))?)*)\s*$`)
	armReturn = regexp.MustCompile(`(?s)^\s*return\s+([^;]+);\s*$`)
	armAssign = regexp.MustCompile(
		`(?s)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(=|(\|\|=))\s*([^;]+);\s*$`)
	trailingBreak = regexp.MustCompile(`\bbreak\s*;\s*$`)
)

// site is one string-dispatched switch.
type site struct {
	path       string
	line       int
	kind       string
	selector   string
	labels     []string
	guarded    bool
	hasDefault bool
}

// key is the identifier the exceptions file is keyed on.
func (s site) key() string { return s.path + " | " + s.selector }

// String is the one-line form the census prints.
func (s site) String() string {
	selector := s.selector
	if selector == "" {
		selector = "?"
	}
	return fmt.Sprintf("%s:%d  %s  %s  [%d labels]",
		s.path, s.line, s.kind, selector, len(s.labels))
}

// exception is one row of the exceptions file.
type exception struct {
	key      string
	path     string
	selector string
	reason   string
	line     int
}

// Run is the gate entry point.
func Run(args []string) int {
	var roots []string
	exceptionsPath := ""
	reportMode := false
	jsonOut := false
	minLabels := 0
	propose := ""
	applyMode := false
	tree := ""
	selfTest := false

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--root needs a path")
				return gate.Broken
			}
			i++
			roots = append(roots, args[i])
		case "--exceptions":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--exceptions needs a path")
				return gate.Broken
			}
			i++
			exceptionsPath = args[i]
		case "--report":
			reportMode = true
		case "--json-out":
			jsonOut = true
		case "--check":
			// The python original accepts the flag and always runs the
			// ratchet: the flag says what the run IS, not what it does.
		case "--min-labels":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-labels needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-labels needs a number")
				return gate.Broken
			}
			minLabels = n
		case "--propose":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--propose needs PATH:LINE")
				return gate.Broken
			}
			i++
			propose = args[i]
		case "--apply":
			applyMode = true
		case "--tree":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--tree needs a path")
				return gate.Broken
			}
			i++
			tree = args[i]
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-string-switch [--root DIR] [--exceptions FILE] [--report] [--json-out] [--check] [--min-labels N] [--propose PATH:LINE] [--apply] [--tree DIR] [--self-test]")
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
	if exceptionsPath == "" {
		exceptionsPath = filepath.Join(root, "tools", "string-switch-exceptions.txt")
	}
	if tree == "" {
		tree = root
	}
	if len(roots) == 0 {
		roots = []string{"src", "apps"}
	}
	for _, r := range roots {
		if !isDir(joinRoot(root, r)) {
			fmt.Fprintf(os.Stderr, "no such tree: %s\n", r)
			return gate.Broken
		}
	}

	if applyMode {
		return applyModeRun(roots, tree)
	}

	sites := census(roots, root)

	if propose != "" {
		return proposeRun(roots, tree, propose)
	}

	if jsonOut {
		return reportJSON(sites)
	}

	if reportMode {
		return reportText(sites)
	}

	exceptions, err := loadExceptions(exceptionsPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	return runGate(sites, exceptions, minLabels)
}

// parseInt reads a non-negative integer.
func parseInt(s string) (int, bool) {
	if s == "" {
		return 0, false
	}
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, false
		}
		n = n*10 + int(r-'0')
	}
	return n, true
}

// joinRoot is python's `os.path.join(base, path)`: an absolute path
// replaces the base.
//
// Go's `filepath.Join` does NOT do that — it concatenates and cleans, so
// an absolute root would land under the repository — and the `--root` and
// `--tree` flags take absolute paths.
func joinRoot(base string, p string) string {
	if filepath.IsAbs(p) {
		return p
	}
	return filepath.Join(base, p)
}

// isDir reports whether the path is a directory.
func isDir(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

// applyModeRun replays the reviewed rewrites.
func applyModeRun(roots []string, tree string) int {
	if len(rewrites) == 0 {
		fmt.Fprintln(os.Stderr, "no rewrites to apply")
		return gate.Broken
	}
	before := census(roots, tree)
	applied, errors := applyRewrites(rewrites, tree)
	after := census(roots, tree)
	removed := len(before) - len(after)
	changed := 0
	for _, item := range applied {
		if item.didChange {
			changed++
		}
		note := item.note
		if note == "" {
			note = "reviewed rewrite"
		}
		state := "already applied"
		if item.didChange {
			state = "applied"
		}
		fmt.Printf("%s %s (%s)\n", state, item.path, note)
	}
	for _, message := range errors {
		fmt.Fprintf(os.Stderr, "  ERROR %s\n", message)
	}
	fmt.Printf("string switches: %d -> %d (%d removed)\n", len(before), len(after), removed)
	if len(errors) > 0 {
		return gate.Red
	}
	expected := 0
	for i, item := range applied {
		if item.didChange {
			expected += rewrites[i].expectRemoved
		}
	}
	if removed != expected {
		fmt.Fprintf(os.Stderr,
			"  ERROR the rewrites removed %d switch(es) but %d were expected\n",
			removed, expected)
		return gate.Red
	}
	if changed == 0 {
		fmt.Println("nothing to do: the tree already carries every rewrite.")
	}
	return gate.Green
}

// proposeRun prints the table the switch at PATH:LINE would become.
func proposeRun(roots []string, tree string, argument string) int {
	path, line, _ := strings.Cut(argument, ":")
	var target *site
	found := census(roots, tree)
	for i := range found {
		if found[i].path == path && fmt.Sprintf("%d", found[i].line) == line {
			target = &found[i]
			break
		}
	}
	if target == nil {
		fmt.Fprintf(os.Stderr, "no string switch at %s\n", argument)
		return gate.Red
	}
	data, err := os.ReadFile(filepath.Join(tree, target.path))
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	table, reason := proposeTable(*target, string(data))
	if table == "" {
		fmt.Fprintf(os.Stderr, "not mechanical: %s\n", reason)
		return gate.Red
	}
	fmt.Println(table)
	return gate.Green
}

// census returns every string-dispatched switch under the roots.
func census(roots []string, base string) []site {
	var sites []site
	for _, root := range roots {
		tree := joinRoot(base, root)
		for _, path := range walkPython(tree) {
			if !strings.HasSuffix(path, ".cs") {
				continue
			}
			data, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			rel, err := filepath.Rel(base, path)
			if err != nil {
				continue
			}
			sites = append(sites, findStringSwitches(string(data), filepath.ToSlash(rel))...)
		}
	}
	return sites
}

// walkPython lists the `.cs` files of a tree the way python's os.walk
// does: the files of a directory sorted, the directories in the order the
// filesystem hands them over, and the `bin` and `obj` directories skipped.
func walkPython(tree string) []string {
	var out []string
	var walk func(dir string)
	walk = func(dir string) {
		entries, err := readDirUnsorted(dir)
		if err != nil {
			return
		}
		var files, dirs []string
		for _, entry := range entries {
			if entry.IsDir() {
				if skipDirParts[entry.Name()] {
					continue
				}
				dirs = append(dirs, entry.Name())
			} else {
				files = append(files, entry.Name())
			}
		}
		sort.Strings(files)
		for _, name := range files {
			out = append(out, filepath.Join(dir, name))
		}
		for _, name := range dirs {
			walk(filepath.Join(dir, name))
		}
	}
	walk(tree)
	return out
}

// readDirUnsorted lists a directory in the order the filesystem returns the
// entries, the way python's os.scandir does.
func readDirUnsorted(dir string) ([]os.DirEntry, error) {
	fh, err := os.Open(dir)
	if err != nil {
		return nil, err
	}
	defer fh.Close()
	return fh.ReadDir(-1)
}

// maskSource blanks comments and literals, preserving every position and
// newline.
//
// The positions are BYTE positions: the mask has to be the same length as
// the text it masks, or every offset the scanner reads afterwards points at
// the wrong place. A rune is more than one byte, so a mask that replaces
// one rune with one space is SHORTER than the text — and in a file with a
// few em-dashes that is enough to move every switch by a line.
func maskSource(source string) string {
	return noise.ReplaceAllStringFunc(source, func(match string) string {
		var b strings.Builder
		b.Grow(len(match))
		for i := 0; i < len(match); i++ {
			if match[i] == '\n' {
				b.WriteByte('\n')
				continue
			}
			b.WriteByte(' ')
		}
		return b.String()
	})
}

// findStringSwitches returns every string-dispatched switch in one C#
// file.
func findStringSwitches(source string, path string) []site {
	masked := maskSource(source)
	var sites []site
	for _, match := range switchKeyword.FindAllStringIndex(masked, -1) {
		index := match[0]
		// The python original's pattern is `(?<![\w.])switch(?![\w])`.
		// The word boundaries are checked by hand.
		if index > 0 && isWordOrDot(masked[index-1]) {
			continue
		}
		if index+6 < len(masked) && isWordByte(masked[index+6]) {
			continue
		}
		after := index + len("switch")
		for after < len(masked) && isSpaceByte(masked[after]) {
			after++
		}
		if after >= len(masked) {
			continue
		}

		var selector string
		var brace int
		var kind string
		switch masked[after] {
		case '(':
			close := matchBracket(masked, after, '(', ')')
			if close < 0 {
				continue
			}
			selector = normalize(source[after+1 : close])
			brace = close + 1
			for brace < len(masked) && isSpaceByte(masked[brace]) {
				brace++
			}
			if brace >= len(masked) || masked[brace] != '{' {
				continue
			}
			kind = "statement"
		case '{':
			selector = selectorBefore(source, index)
			brace = after
			kind = "expression"
		default:
			continue
		}

		bodyEnd := matchBracket(masked, brace, '{', '}')
		if bodyEnd < 0 {
			continue
		}

		var labels []string
		guarded := false
		hasDefault := false
		for _, mark := range armMarks(masked, brace+1, bodyEnd) {
			switch mark.kind {
			case "case":
				cursor := mark.at + 4
				for cursor < len(source) && isSpaceByte(source[cursor]) {
					cursor++
				}
				value, end, ok := readLiteral(source, cursor)
				if !ok {
					continue
				}
				labels = append(labels, value)
				if whenGuard.MatchString(masked[end:]) {
					guarded = true
				}
			case "default":
				hasDefault = true
			case "=>":
				cursor := mark.at - 1
				for cursor >= 0 && isSpaceByte(source[cursor]) {
					cursor--
				}
				if cursor < 0 {
					continue
				}
				if source[cursor] == '"' {
					start := strings.LastIndex(source[:cursor], "\"")
					if start < 0 {
						continue
					}
					value, _, ok := readLiteral(source, start)
					if ok {
						labels = append(labels, value)
					}
				} else if source[cursor] == '_' {
					hasDefault = true
				}
			}
		}

		if len(labels) == 0 {
			continue
		}
		sites = append(sites, site{
			path:       path,
			line:       strings.Count(source[:index], "\n") + 1,
			kind:       kind,
			selector:   selector,
			labels:     labels,
			guarded:    guarded,
			hasDefault: hasDefault,
		})
	}
	return sites
}

// mark is one `case` / `default` / `=>` of a switch body.
type mark struct {
	kind string
	at   int
}

// armMarks returns the `case` / `default` / `=>` marks at the TOP level of
// a switch body.
//
// Top level means brace depth zero relative to the body, so a nested
// switch inside an arm does not contribute its arms to the outer switch.
func armMarks(masked string, start int, end int) []mark {
	var marks []mark
	depth := 0
	for i := start; i < end; i++ {
		ch := masked[i]
		if ch == '{' {
			depth++
			continue
		}
		if ch == '}' {
			depth--
			continue
		}
		if depth != 0 {
			continue
		}
		if strings.HasPrefix(masked[i:], "case") &&
			!isWordCharAt(masked, i-1) && !isWordCharAt(masked, i+4) {
			marks = append(marks, mark{"case", i})
			i += 4
			continue
		}
		if strings.HasPrefix(masked[i:], "default") &&
			!isWordCharAt(masked, i-1) && !isWordCharAt(masked, i+7) {
			marks = append(marks, mark{"default", i})
			i += 7
			continue
		}
		if strings.HasPrefix(masked[i:], "=>") {
			marks = append(marks, mark{"=>", i})
			i += 2
			continue
		}
	}
	return marks
}

// isWordCharAt reports whether the character at the index is a word
// character; an index outside the text is a space, which is not one.
func isWordCharAt(text string, index int) bool {
	if index < 0 || index >= len(text) {
		return false
	}
	return isWordByte(text[index])
}

// isWordByte reports whether the byte is a word character.
func isWordByte(b byte) bool {
	switch {
	case b >= 'A' && b <= 'Z',
		b >= 'a' && b <= 'z',
		b >= '0' && b <= '9':
		return true
	case b == '_':
		return true
	}
	return false
}

// isWordOrDot reports whether the byte is a word character or a dot.
func isWordOrDot(b byte) bool {
	return isWordByte(b) || b == '.'
}

// isSpaceByte reports whether the byte is whitespace.
func isSpaceByte(b byte) bool {
	switch b {
	case ' ', '\t', '\r', '\n', '\v', '\f':
		return true
	}
	return false
}

// matchBracket returns the index of the bracket closing the one at start,
// or -1.
func matchBracket(masked string, start int, openCh byte, closeCh byte) int {
	depth := 0
	for i := start; i < len(masked); i++ {
		switch masked[i] {
		case openCh:
			depth++
		case closeCh:
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// readLiteral reads the string literal that starts at index.
func readLiteral(source string, index int) (string, int, bool) {
	if index >= len(source) || source[index] != '"' {
		return "", index, false
	}
	i := index + 1
	var out strings.Builder
	for i < len(source) {
		ch := source[i]
		if ch == '\\' && i+1 < len(source) {
			next := source[i+1]
			switch next {
			case 'n':
				out.WriteByte('\n')
			case 't':
				out.WriteByte('\t')
			case 'r':
				out.WriteByte('\r')
			case '0':
				out.WriteByte(0)
			default:
				out.WriteByte(next)
			}
			i += 2
			continue
		}
		if ch == '"' {
			return out.String(), i + 1, true
		}
		out.WriteByte(ch)
		i++
	}
	return out.String(), len(source), true
}

// selectorBefore is the expression a switch EXPRESSION dispatches on.
func selectorBefore(source string, index int) string {
	head := source[:index]
	m := selector.FindStringSubmatchIndex(head)
	if m == nil {
		return ""
	}
	return head[m[2]:m[3]]
}

// normalize is python's `" ".join(text.split())`.
func normalize(text string) string {
	return strings.Join(strings.Fields(text), " ")
}

// loadExceptions reads the exceptions file.
func loadExceptions(path string) ([]exception, error) {
	var exceptions []exception
	data, err := os.ReadFile(path)
	if err != nil {
		return exceptions, nil
	}
	for number, raw := range strings.Split(string(data), "\n") {
		text := strings.TrimSpace(raw)
		if text == "" || strings.HasPrefix(text, "#") {
			continue
		}
		parts := strings.Split(text, "|")
		for i := range parts {
			parts[i] = strings.TrimSpace(parts[i])
		}
		if len(parts) < 3 {
			return nil, fmt.Errorf(
				"%s:%d: an exception needs three fields (path | selector | reason), got %d",
				path, number+1, len(parts))
		}
		exceptions = append(exceptions, exception{
			key:      parts[0] + " | " + parts[1],
			path:     parts[0],
			selector: parts[1],
			reason:   strings.Join(parts[2:], "|"),
			line:     number + 1,
		})
	}
	return exceptions, nil
}

// gateFindings is the ratchet's findings, without the printing.
func gateFindings(sites []site, exceptions []exception, minLabels int) []string {
	var findings []string

	var statements, expressions []site
	labels := 0
	for _, s := range sites {
		if s.kind == "statement" {
			statements = append(statements, s)
		} else {
			expressions = append(expressions, s)
		}
		labels += len(s.labels)
	}
	statementLabels := 0
	for _, s := range statements {
		statementLabels += len(s.labels)
	}

	listed := map[string]bool{}
	for _, e := range exceptions {
		listed[e.key] = true
	}
	for _, s := range statements {
		if !listed[s.key()] {
			findings = append(findings, fmt.Sprintf(
				"  UNLISTED  %s:%d  %s  [%d labels]: add it to "+
					"tools/string-switch-exceptions.txt with a reason, or convert it",
				s.path, s.line, selectorOrQuestion(s.selector), len(s.labels)))
		}
	}

	present := map[string]bool{}
	for _, s := range statements {
		present[s.key()] = true
	}
	for _, e := range exceptions {
		if !present[e.key] {
			findings = append(findings, fmt.Sprintf(
				"  STALE    %s (tools/string-switch-exceptions.txt:%d): the tree no "+
					"longer has that switch; delete the row", e.key, e.line))
		}
	}

	if labels < minLabels {
		findings = append(findings, fmt.Sprintf(
			"  FLOOR    the census found %d string labels, the floor is %d: the "+
				"scanner is probably not looking at the tree any more", labels, minLabels))
	}

	return findings
}

// runGateQuiet is the ratchet's verdict, without the printing.
//
// The self-test uses it: a self-test that prints the gate's own findings
// makes the CI log read like a failing run.
func runGateQuiet(sites []site, exceptions []exception, minLabels int) int {
	if len(gateFindings(sites, exceptions, minLabels)) > 0 {
		return gate.Red
	}
	return gate.Green
}

// runGate is the ratchet.
func runGate(sites []site, exceptions []exception, minLabels int) int {
	findings := gateFindings(sites, exceptions, minLabels)
	if len(findings) > 0 {
		fmt.Fprintf(os.Stderr, "string-switch gate: %d finding(s)\n", len(findings))
		for _, finding := range findings {
			fmt.Fprintln(os.Stderr, finding)
		}
		fmt.Fprint(os.Stderr,
			"\nEvery string-dispatched `switch` must be named in "+
				"tools/string-switch-exceptions.txt with a reason, or converted to a "+
				"table (tools/check-string-switch.py --propose names the table a "+
				"switch would become).\n")
		return gate.Red
	}

	var statements, expressions []site
	statementLabels := 0
	for _, s := range sites {
		if s.kind == "statement" {
			statements = append(statements, s)
			statementLabels += len(s.labels)
		} else {
			expressions = append(expressions, s)
		}
	}
	fmt.Printf(
		"string-switch gate: %d statement-form switch(es), %d label(s), all listed "+
			"in tools/string-switch-exceptions.txt (%d expression-form switch(es) "+
			"reported, not gated)\n",
		len(statements), statementLabels, len(expressions))
	return gate.Green
}

// selectorOrQuestion is the selector, or a question mark.
func selectorOrQuestion(selector string) string {
	if selector == "" {
		return "?"
	}
	return selector
}

// reportText prints the census.
func reportText(sites []site) int {
	var statements, expressions []site
	labels := 0
	for _, s := range sites {
		if s.kind == "statement" {
			statements = append(statements, s)
		} else {
			expressions = append(expressions, s)
		}
		labels += len(s.labels)
	}
	statementLabels := 0
	for _, s := range statements {
		statementLabels += len(s.labels)
	}
	expressionLabels := 0
	for _, s := range expressions {
		expressionLabels += len(s.labels)
	}

	fmt.Println("string-switch census")
	fmt.Printf("  switches (statement form) : %d\n", len(statements))
	fmt.Printf("  switches (expression form) : %d\n", len(expressions))
	fmt.Printf("  string labels, statements  : %d\n", statementLabels)
	fmt.Printf("  string labels, expressions : %d\n", expressionLabels)
	byFile := map[string][]site{}
	var fileOrder []string
	for _, s := range sites {
		if _, ok := byFile[s.path]; !ok {
			fileOrder = append(fileOrder, s.path)
		}
		byFile[s.path] = append(byFile[s.path], s)
	}
	sort.Strings(fileOrder)
	for _, path := range fileOrder {
		for _, s := range byFile[path] {
			fmt.Printf("    %s\n", s.String())
		}
	}
	return gate.Green
}

// appliedRewrite is the outcome of one rewrite.
type appliedRewrite struct {
	path      string
	note      string
	didChange bool
}

// applyRewrites applies every rewrite.
func applyRewrites(set []rewrite, root string) ([]appliedRewrite, []string) {
	var applied []appliedRewrite
	var errors []string
	for _, r := range set {
		path := filepath.Join(root, r.path)
		if _, err := os.Stat(path); err != nil {
			errors = append(errors, r.path+": no such file")
			continue
		}
		data, err := os.ReadFile(path)
		if err != nil {
			errors = append(errors, r.path+": no such file")
			continue
		}
		source := string(data)
		original := source
		failed := false
		for _, pair := range r.pairs {
			old, updated := pair[0], pair[1]
			// A pair is SKIPPED when its new text is already in the file: the
			// rewrite is already there. That is what makes --apply
			// idempotent.
			if strings.Contains(source, updated) {
				continue
			}
			count := strings.Count(source, old)
			if count == 0 {
				errors = append(errors,
					r.path+": the old text does not occur in the file (the tree moved)")
				failed = true
				break
			}
			if count > 1 {
				errors = append(errors,
					fmt.Sprintf("%s: the old text occurs %d times; the rewrite is ambiguous",
						r.path, count))
				failed = true
				break
			}
			source = strings.Replace(source, old, updated, 1)
		}
		if failed {
			continue
		}
		if source != original {
			if err := os.WriteFile(path, []byte(source), 0o644); err != nil {
				errors = append(errors, r.path+": "+err.Error())
				continue
			}
			applied = append(applied, appliedRewrite{r.path, r.note, true})
		} else {
			applied = append(applied, appliedRewrite{r.path, r.note, false})
		}
	}
	return applied, errors
}
