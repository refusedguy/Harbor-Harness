// Package resultchains is the compiled port of tools/check-result-chains.py.
//
// The gate is the lever for issue #1558: it finds every
// `if (x.IsFailure) return ...;` guard site in `src/` and `apps/`,
// classifies it, and — with a baseline — fails when the number of chain
// candidates goes UP.
//
// The categories are the same as the python original's: a guard that
// re-types the same failure and unwraps the value further down is a chain
// candidate; a guard whose body is not a plain return is a side effect; a
// guard that does not return a Result at all is not a Result ladder; and
// the one shape a `.Bind()` rewrite applies to mechanically is
// `rewriteable`.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package resultchains

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode/utf8"

	"harbor.local/gates/internal/gate"
)

// The categories, in the order the report prints them.
const (
	kindChainSame      = "chain-same"
	kindChainMapped    = "chain-mapped"
	kindTerminalSame   = "terminal-same"
	kindTerminalMapped = "terminal-mapped"
	kindSideEffect     = "side-effect"
	kindNonResult      = "non-result"
	kindRewriteable    = "rewriteable"
)

// chainKinds are the kinds a chain could replace.
var chainKinds = map[string]bool{
	kindChainSame:   true,
	kindChainMapped: true,
	kindRewriteable: true,
}

// ident is the shape of an identifier, with the dotted form the gate
// accepts.
const ident = `[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*`

// ifGuard is the guard the census looks for.
var ifGuard = regexp.MustCompile(`\bif\s*\(\s*(` + ident + `)\s*\.\s*IsFailure\s*\)`)

// assignValue is the `var v = x.Value;` shape the rewrite needs.
var assignValue = regexp.MustCompile(
	`^(?:var|Result<[^;]*?>|readonly\s+Result<[^;]*?>|[A-Za-z_][A-Za-z0-9_]*)\s+(` +
		ident + `)\s*=\s*(` + ident + `)\.Value\s*;$`)

// The classification patterns, built per guard variable.
var (
	convertFailureRe    = regexp.MustCompile(`^(.+)\.ConvertFailure\(\)$`)
	convertFailureGenRe = regexp.MustCompile(`^(.+)\.ConvertFailure<(.+)>\(\)$`)
	resultFailureErrRe  = regexp.MustCompile(`^Result\.Failure(<[^>]+>)?\(\s*(.+)\.Error\s*\)$`)
	resultFailureRe     = regexp.MustCompile(`^Result\.Failure(<[^>]+>)?\(.*\)$`)
	resultAnyRe         = regexp.MustCompile(`^Result\.(Success|Failure)\b`)
)

// site is one guard site.
type site struct {
	path          string
	line          int
	guardVar      string
	kind          string
	detail        string
	rewrite       string
	start         int
	end           int
	guardStart    int
	nextStatement string
}

// Run is the gate entry point.
func Run(args []string) int {
	repo := ""
	var roots []string
	check := false
	apply := false
	shapes := false
	selfTest := false
	baseline := ""
	writeBaseline := ""
	asJSON := false
	perFile := false

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--repo":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--repo needs a path")
				return gate.Broken
			}
			i++
			repo = args[i]
		case "--roots":
			roots = nil
			for i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				i++
				roots = append(roots, args[i])
			}
		case "--check":
			check = true
		case "--apply":
			apply = true
		case "--shapes":
			shapes = true
		case "--self-test":
			selfTest = true
		case "--baseline":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--baseline needs a path")
				return gate.Broken
			}
			i++
			baseline = args[i]
		case "--write-baseline":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--write-baseline needs a path")
				return gate.Broken
			}
			i++
			writeBaseline = args[i]
		case "--json":
			asJSON = true
		case "--per-file":
			perFile = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-result-chains [--repo DIR] [--roots DIR...] [--check] [--apply] [--shapes] [--baseline FILE] [--write-baseline FILE] [--json] [--per-file] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if repo == "" {
		root, err := gate.RepoRoot()
		if err != nil {
			fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
			return gate.Broken
		}
		repo = root
	}
	if len(roots) == 0 {
		roots = []string{"src", "apps"}
	}

	base, err := filepath.Abs(repo)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot resolve the repository root:", err)
		return gate.Broken
	}

	sites := collect(roots, base)
	byKind, byKindOrder, perFileCounts, perFileOrder, survivors := census(sites)

	if writeBaseline != "" {
		payload := jsonObject(
			[]jsonField{
				{"total_guards", jsonNumber(len(sites))},
				{"by_kind", jsonCounts(byKind, byKindOrder, true)},
				{"survivors", jsonNumber(len(survivors))},
				{"per_file", jsonCounts(perFileCounts, perFileOrder, true)},
			}, true) + "\n"
		if err := os.WriteFile(writeBaseline, []byte(payload), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		fmt.Printf("baseline written to %s\n", writeBaseline)
		return gate.Green
	}

	if apply {
		changed, notes := applySites(sites, base)
		for _, note := range notes {
			fmt.Printf("rewrote %s\n", note)
		}
		fmt.Printf("rewrote %d site(s); %d site(s) left for a human\n", changed, len(sites)-changed)
		return gate.Green
	}

	if shapes {
		for _, s := range sites {
			if !chainKinds[s.kind] {
				continue
			}
			fmt.Printf("%s:%d  [%s] %s\n", s.path, s.line, s.kind, s.guardVar)
			fmt.Printf("    guard : %s\n", s.detail)
			fmt.Printf("    next  : %s\n", s.nextStatement)
		}
		fmt.Printf("\n%d chain candidate(s)\n", len(survivors))
		return gate.Green
	}

	if asJSON {
		fmt.Print(jsonReport(sites, byKind, byKindOrder, perFileCounts, perFileOrder, survivors))
	} else {
		fmt.Printf("guard sites (`if (x.IsFailure)`) in %s: %d\n",
			strings.Join(roots, ", "), len(sites))
		for _, kind := range sortedCountKeys(byKind) {
			fmt.Printf("  %-16s %d\n", kind, byKind[kind])
		}
		fmt.Printf("  chain candidates (a chain could replace the guard): %d\n", len(survivors))
		if perFile {
			fmt.Println()
			fmt.Printf("%-70s sites\n", "file")
			for _, entry := range sortedFileCounts(perFileCounts) {
				fmt.Printf("%-70s %d\n", entry.path, entry.count)
			}
		}
	}

	if check {
		if baseline == "" {
			fmt.Print("report mode: no baseline given, so nothing is compared. " +
				"Pass --baseline FILE for the gate (exit 1 above the recorded count).\n")
			return gate.Green
		}
		allowed, ok, why := baselineSurvivors(baseline)
		if !ok {
			fmt.Fprintf(os.Stderr, "baseline unreadable: %s\n", why)
			return gate.Broken
		}
		if len(survivors) > allowed {
			fmt.Fprintf(os.Stderr,
				"FAIL: %d chain candidate(s) (baseline %d). Chain them, or record in "+
					"the baseline why not.\n", len(survivors), allowed)
			return gate.Red
		}
		fmt.Printf("OK: %d chain candidate(s), baseline %d.\n", len(survivors), allowed)
	}
	return gate.Green
}

// collect walks the roots and returns every guard site.
//
// The walk is python's `os.walk`: the FILES of a directory are sorted, and
// the DIRECTORIES are visited in the order the filesystem hands them over.
// The order matters: it is the order the sites come in, and the report
// prints them in that order.
func collect(roots []string, base string) []site {
	var sites []site
	for _, root := range roots {
		tree := root
		if !filepath.IsAbs(tree) {
			tree = filepath.Join(base, root)
		}
		for _, path := range walkPython(tree) {
			if !strings.HasSuffix(path, ".cs") {
				continue
			}
			rel, err := filepath.Rel(base, path)
			if err != nil {
				continue
			}
			sites = append(sites, scanFile(path, rel)...)
		}
	}
	return sites
}

// walkPython lists the `.cs` files of a tree the way python's os.walk
// does: the files of a directory sorted, the directories in the order the
// filesystem returns them.
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

// scanFile returns the guard sites of one file.
func scanFile(path string, rel string) []site {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	code := []byte(blankCode(string(data)))
	var sites []site

	for _, m := range ifGuard.FindAllSubmatchIndex(code, -1) {
		guardVar := string(code[m[2]:m[3]])
		lineNo := countByte(code[:m[0]], '\n') + 1
		after := skipWS(code, m[1])

		var body string
		var bodyEnd int
		if after < len(code) && code[after] == '{' {
			close := matchPair(code, after, '{', '}')
			if close < 0 {
				continue
			}
			inner := string(code[after+1 : close])
			// A brace body that holds exactly one statement is still a
			// "plain return" guard.
			var stmts []string
			for _, s := range strings.Split(inner, ";") {
				if strings.TrimSpace(s) != "" {
					stmts = append(stmts, s)
				}
			}
			if len(stmts) == 1 && strings.HasPrefix(strings.TrimSpace(stmts[0]), "return") {
				body = strings.TrimSpace(stmts[0]) + ";"
				bodyEnd = close + 1
			} else {
				kind, detail := classify(inner, guardVar)
				sites = append(sites, site{path: rel, line: lineNo, guardVar: guardVar, kind: kind, detail: detail})
				continue
			}
		} else {
			body, bodyEnd = readStatement(code, after)
			if !strings.HasPrefix(strings.TrimSpace(body), "return") {
				kind, detail := classify(body, guardVar)
				sites = append(sites, site{path: rel, line: lineNo, guardVar: guardVar, kind: kind, detail: detail})
				continue
			}
		}

		kind, detail := classify(body, guardVar)
		sites = append(sites, site{
			path: rel, line: lineNo, guardVar: guardVar,
			kind: kind, detail: detail, guardStart: m[0],
		})
		// The site is a POINTER into the slice, not a copy: the python
		// original appended an object and mutated it afterwards, and the
		// mutation is what the report shows.
		s := &sites[len(sites)-1]
		maybeRewrite(s, code, guardVar, bodyEnd)
		if (s.kind == kindChainSame || s.kind == kindChainMapped) && !unwrapsAfter(code, bodyEnd, guardVar) {
			if s.kind == kindChainSame {
				s.kind = kindTerminalSame
			} else {
				s.kind = kindTerminalMapped
			}
			s.detail = "terminal guard: the guarded value is never unwrapped"
		}
		recordNext(s, code, bodyEnd)
	}
	return sites
}

// blankCode returns the text with comments and string/char literals
// replaced by spaces.
//
// Line structure and length are preserved, so offsets in the blanked text
// still point at the original source. This is what lets the gate ignore an
// `IsFailure` that only appears inside a comment or a string literal.
func blankCode(text string) string {
	out := []byte(text)
	i := 0
	n := len(out)
	for i < n {
		ch := out[i]
		var nxt byte
		if i+1 < n {
			nxt = out[i+1]
		}
		switch {
		case ch == '/' && nxt == '/':
			for i < n && out[i] != '\n' {
				out[i] = ' '
				i++
			}
		case ch == '/' && nxt == '*':
			out[i] = ' '
			out[i+1] = ' '
			i += 2
			for i < n && !(out[i] == '*' && i+1 < n && out[i+1] == '/') {
				if out[i] != '\n' {
					out[i] = ' '
				}
				i++
			}
			if i < n {
				out[i] = ' '
				if i+1 < n {
					out[i+1] = ' '
				}
				i += 2
			}
		case ch == '@' && nxt == '"':
			i++
			if i < n && out[i] == '"' {
				out[i] = ' '
				i++
			}
			for i < n {
				if out[i] == '"' {
					if i+1 < n && out[i+1] == '"' {
						out[i] = ' '
						out[i+1] = ' '
						i += 2
						continue
					}
					out[i] = ' '
					i++
					break
				}
				if out[i] != '\n' {
					out[i] = ' '
				}
				i++
			}
		case ch == '"' || ch == '\'':
			quote := ch
			i++
			for i < n {
				if out[i] == '\\' {
					out[i] = ' '
					if i+1 < n && out[i+1] != '\n' {
						out[i+1] = ' '
					}
					i += 2
					continue
				}
				if out[i] == quote {
					out[i] = ' '
					i++
					break
				}
				if out[i] != '\n' {
					out[i] = ' '
				}
				i++
			}
		default:
			i++
		}
	}
	return string(out)
}

// matchPair returns the index of the closer matching the opener, or -1.
func matchPair(text []byte, openIdx int, opener byte, closer byte) int {
	depth := 0
	for i := openIdx; i < len(text); i++ {
		switch text[i] {
		case opener:
			depth++
		case closer:
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// classify classifies the body of an `if (X.IsFailure)` guard.
//
// `same` means the guard re-types the failure the guarded result already
// carries; `mapped` means the guard builds a different message.
func classify(body string, guardVar string) (string, string) {
	b := strings.Join(strings.Fields(body), " ")
	if !strings.HasPrefix(b, "return") {
		return kindSideEffect, "guard body does not return"
	}
	expr := strings.TrimSpace(strings.TrimRight(strings.TrimSpace(b[len("return"):]), ";"))
	if m := convertFailureRe.FindStringSubmatch(expr); m != nil && m[1] == guardVar {
		return kindChainSame, "ConvertFailure() re-type"
	}
	if m := convertFailureGenRe.FindStringSubmatch(expr); m != nil && m[1] == guardVar {
		return kindChainSame, fmt.Sprintf("ConvertFailure<%s>() re-type", m[2])
	}
	if m := resultFailureErrRe.FindStringSubmatch(expr); m != nil && m[2] == guardVar {
		return kindChainSame, "Result.Failure(X.Error) re-type"
	}
	if resultFailureRe.MatchString(expr) {
		return kindChainMapped, "guard returns a different error message"
	}
	if resultAnyRe.MatchString(expr) {
		return kindChainMapped, "guard returns a Result the chain cannot rebuild"
	}
	return kindNonResult, "the guard does not return a Result — this is not a Result ladder"
}

// skipWS returns the index of the first non-whitespace byte at or after i.
func skipWS(code []byte, i int) int {
	for i < len(code) && (code[i] == ' ' || code[i] == '\t' || code[i] == '\r' || code[i] == '\n') {
		i++
	}
	return i
}

// readStatement reads one statement starting at i; it returns the text and
// the index after it.
func readStatement(code []byte, i int) (string, int) {
	start := i
	depth := 0
	for i < len(code) {
		c := code[i]
		switch {
		case c == '(' || c == '[' || c == '{':
			depth++
		case c == ')' || c == ']' || c == '}':
			if depth == 0 {
				return string(code[start:i]), i
			}
			depth--
		case c == ';' && depth == 0:
			return string(code[start : i+1]), i + 1
		}
		i++
	}
	return string(code[start:i]), i
}

// unwrapsAfter reports whether the guarded variable is unwrapped with
// `.Value` further down.
//
// The scan stops at the end of the enclosing block, so a `.Value` read in a
// SIBLING method does not count.
//
// The pattern is matched AT the position, not anywhere in the rest of the
// text: `\b` looks at the character BEFORE the position, and a slice would
// hide it.
func unwrapsAfter(code []byte, bodyEnd int, guardVar string) bool {
	rel := 0
	for i := bodyEnd; i < len(code); i++ {
		c := code[i]
		if c == '{' {
			rel++
			continue
		}
		if c == '}' {
			if rel == 0 {
				return false
			}
			rel--
			continue
		}
		if rel != 0 {
			continue
		}
		if matchesValueUse(code, i, guardVar) {
			return true
		}
	}
	return false
}

// matchesValueUse reports whether `<guardVar>.Value` starts at the
// position, with the word boundaries the python pattern's `\b` asks for.
func matchesValueUse(code []byte, i int, guardVar string) bool {
	if i > 0 && isWordByte(code[i-1]) {
		return false
	}
	if !strings.HasPrefix(string(code[i:]), guardVar) {
		return false
	}
	j := i + len(guardVar)
	if !strings.HasPrefix(string(code[j:]), ".Value") {
		return false
	}
	j += len(".Value")
	if j < len(code) && isWordByte(code[j]) {
		return false
	}
	return true
}

// isWordByte reports whether the byte is a word character, the ASCII half
// of python's `\w`.
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

// recordNext remembers the statement that follows the guard, for
// --shapes.
func recordNext(s *site, code []byte, bodyEnd int) {
	j := skipWS(code, bodyEnd)
	stmt, _ := readStatement(code, j)
	s.nextStatement = truncateRunes(strings.Join(strings.Fields(stmt), " "), 160)
}

// maybeRewrite tries the mechanical Bind rewrite on a chain-same guard.
func maybeRewrite(s *site, code []byte, guardVar string, bodyEnd int) {
	if s.kind != kindChainSame {
		return
	}
	i := skipWS(code, bodyEnd)

	stmt, stmtEnd := readStatement(code, i)
	var exprSrc string
	var local string
	var retEnd int
	if m := assignValue.FindStringSubmatch(strings.TrimSpace(stmt)); m != nil && m[2] == guardVar {
		local = m[1]
		j := skipWS(code, stmtEnd)
		retStmt, retEnd2 := readStatement(code, j)
		rs := strings.TrimSpace(retStmt)
		if !strings.HasPrefix(rs, "return ") || !strings.HasSuffix(rs, ";") {
			return
		}
		exprSrc = strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(rs[len("return"):]), ";"))
		retEnd = retEnd2
	} else {
		// The return reads X.Value directly.
		rs := strings.TrimSpace(stmt)
		if !strings.HasPrefix(rs, "return ") || !strings.HasSuffix(rs, ";") {
			return
		}
		exprSrc = strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(rs[len("return"):]), ";"))
		local = "value"
		pat := regexp.MustCompile(`\b` + regexp.QuoteMeta(guardVar) + `\.Value\b`)
		if !pat.MatchString(exprSrc) {
			return
		}
		exprSrc = pat.ReplaceAllString(exprSrc, local)
		retEnd = stmtEnd
	}

	if exprSrc == "" {
		return
	}
	// The return must be the last statement before the method's closing
	// brace.
	k := skipWS(code, retEnd)
	if k >= len(code) || code[k] != '}' {
		return
	}
	// The local must be used by this return and by nothing else in the block.
	localPat := regexp.MustCompile(`\b` + regexp.QuoteMeta(local) + `\b`)
	if !localPat.MatchString(exprSrc) {
		return
	}
	if localPat.MatchString(string(code[retEnd:k])) {
		return
	}

	s.kind = kindRewriteable
	s.detail = fmt.Sprintf("guard + unwrap + single return -> Bind(%s => ...)", local)
	s.start = s.guardStart
	s.end = retEnd
	s.rewrite = fmt.Sprintf("return %s.Bind(%s => %s);", guardVar, local, exprSrc)
}

// applySites performs the mechanical rewrite for every rewriteable site.
func applySites(sites []site, root string) (int, []string) {
	byFile := map[string][]site{}
	var order []string
	for _, s := range sites {
		if s.kind != kindRewriteable {
			continue
		}
		if _, ok := byFile[s.path]; !ok {
			order = append(order, s.path)
		}
		byFile[s.path] = append(byFile[s.path], s)
	}

	changed := 0
	var notes []string
	for _, rel := range order {
		path := filepath.Join(root, rel)
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		text := string(data)
		fileSites := byFile[rel]
		sorted := append([]site{}, fileSites...)
		sort.SliceStable(sorted, func(i, j int) bool { return sorted[i].start > sorted[j].start })
		for _, s := range sorted {
			lineStart := strings.LastIndex(text[:s.start], "\n") + 1
			indent := text[lineStart:]
			trimmed := strings.TrimLeft(indent, " \t")
			indent = indent[:len(indent)-len(trimmed)]
			replacement := indent + s.rewrite + "\n"
			text = text[:lineStart] + replacement + text[s.end:]
			changed++
			notes = append(notes, fmt.Sprintf("%s:%d -> Bind", rel, countByte([]byte(text[:s.start]), '\n')+1))
		}
		if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
			continue
		}
	}
	return changed, notes
}

// census returns the counts per kind, the counts per file and the chain
// candidates.
//
// The orders are the orders the census FIRST SAW the keys in, because the
// python original wrote the JSON with the keys in that order and the
// baseline file is compared byte for byte.
func census(sites []site) (map[string]int, []string, map[string]int, []string, []site) {
	byKind := map[string]int{}
	var byKindOrder []string
	perFile := map[string]int{}
	var perFileOrder []string
	var survivors []site
	for _, s := range sites {
		if _, ok := byKind[s.kind]; !ok {
			byKindOrder = append(byKindOrder, s.kind)
		}
		byKind[s.kind]++
		if _, ok := perFile[s.path]; !ok {
			perFileOrder = append(perFileOrder, s.path)
		}
		perFile[s.path]++
		if chainKinds[s.kind] {
			survivors = append(survivors, s)
		}
	}
	return byKind, byKindOrder, perFile, perFileOrder, survivors
}

// countByte counts the occurrences of a byte.
func countByte(data []byte, b byte) int {
	n := 0
	for _, c := range data {
		if c == b {
			n++
		}
	}
	return n
}

// truncateRunes cuts a string to width characters, the way python's
// `s[:160]` does.
func truncateRunes(s string, width int) string {
	if utf8.RuneCountInString(s) <= width {
		return s
	}
	count := 0
	for i := range s {
		if count == width {
			return s[:i]
		}
		count++
	}
	return s
}

// sortedCountKeys returns the map's keys, sorted.
func sortedCountKeys(m map[string]int) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// fileCount is one row of the per-file table.
type fileCount struct {
	path  string
	count int
}

// sortedFileCounts returns the per-file rows, by count descending and then
// by path.
func sortedFileCounts(m map[string]int) []fileCount {
	out := make([]fileCount, 0, len(m))
	for path, count := range m {
		out = append(out, fileCount{path: path, count: count})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].count != out[j].count {
			return out[i].count > out[j].count
		}
		return out[i].path < out[j].path
	})
	return out
}
