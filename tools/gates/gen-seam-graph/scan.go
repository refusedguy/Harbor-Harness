package genseamgraph

// scan.go — the tree walk: tokenise, collect sources, extract declarations.
//
// This is a transliteration of tools/gen-seam-graph.py (sections 1-3). Every
// pattern is either a hand-written scanner (where the python regex uses a
// construct RE2 does not have: lazy quantifiers, lookbehind) or a Go regexp
// with the same leftmost-first semantics as python's `re`.

import (
	"bytes"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// ---------------------------------------------------------------------------
// 1. Source normalisation
// ---------------------------------------------------------------------------

// findClose finds the first occurrence of the two-byte sequence `open close`
// at or after `from`, returning the index of `open`.
func findClose(text []byte, from int, open, close byte) (int, bool) {
	for j := from; j+1 < len(text); j++ {
		if text[j] == open && text[j+1] == close {
			return j, true
		}
	}
	return 0, false
}

// findVerbatimClose closes a `@"..."` literal. The content is a sequence of
// items: one char that is not a quote, or the pair `""`. A lone quote closes.
func findVerbatimClose(text []byte, from int) (int, bool) {
	n := len(text)
	j := from
	lastPairStart := -1
	for j < n {
		if text[j] != '"' {
			j++
			continue
		}
		if j+1 < n && text[j+1] == '"' {
			lastPairStart = j
			j += 2
			continue
		}
		return j + 1, true
	}
	// Unterminated: backtrack over the last `""` pair and close on its first
	// quote, which is what the regex engine's backtracking produces.
	if lastPairStart >= 0 {
		return lastPairStart + 1, true
	}
	return 0, false
}

// findRawClose closes a raw string literal. The python pattern is
// `"""..."""` OPENED with three quotes and CLOSED with four
// (`r'|"\"\"\".*?"\"\"\""'`), so a closing run of four quotes is what the
// pattern asks for. The tree has none, and the raw-string alternative
// therefore never fires there; the port keeps the same shape so that both
// implementations blank the same bytes.
func findRawClose(text []byte, from int) (int, bool) {
	for j := from; j+3 < len(text); j++ {
		if text[j] == '"' && text[j+1] == '"' && text[j+2] == '"' && text[j+3] == '"' {
			return j + 4, true
		}
	}
	return 0, false
}

// findStringClose closes a `"..."` literal.
func findStringClose(text []byte, from int) (int, bool) {
	n := len(text)
	for j := from; j < n; j++ {
		switch text[j] {
		case '\\':
			j++ // the escape eats the next char
		case '"':
			return j + 1, true
		case '\n':
			return 0, false
		}
	}
	return 0, false
}

// findCharClose closes a `'...'` literal.
func findCharClose(text []byte, from int) (int, bool) {
	n := len(text)
	for j := from; j < n; j++ {
		switch text[j] {
		case '\\':
			j++
		case '\'':
			return j + 1, true
		case '\n':
			return 0, false
		}
	}
	return 0, false
}

// nextToken returns the span of the next comment/literal at or after pos.
// The alternatives are tried in the python order: line comment, block
// comment, verbatim string, raw string, string, char literal. An alternative
// that fails at a position falls through to the next alternative at the SAME
// position, exactly as a regex alternation does.
func nextToken(text []byte, pos int) (int, int) {
	n := len(text)
	for i := pos; i < n; i++ {
		switch text[i] {
		case '/':
			if i+1 < n && text[i+1] == '/' {
				j := i + 2
				for j < n && text[j] != '\n' {
					j++
				}
				return i, j
			}
			if i+1 < n && text[i+1] == '*' {
				if j, ok := findClose(text, i+2, '*', '/'); ok {
					return i, j + 2
				}
			}
		case '@':
			if i+1 < n && text[i+1] == '"' {
				if j, ok := findVerbatimClose(text, i+2); ok {
					return i, j
				}
			}
		case '"':
			if i+2 < n && text[i+1] == '"' && text[i+2] == '"' {
				if j, ok := findRawClose(text, i+3); ok {
					return i, j
				}
			}
			if j, ok := findStringClose(text, i+1); ok {
				return i, j
			}
		case '\'':
			if j, ok := findCharClose(text, i+1); ok {
				return i, j
			}
		}
	}
	return -1, -1
}

// stripCommentsAndLiterals blanks out comments, string literals and char
// literals. The output has the same length and the same line structure as
// the input, so a match found in it has the same line number as in the
// source.
func stripCommentsAndLiterals(text []byte) []byte {
	out := make([]byte, len(text))
	copy(out, text)
	last := 0
	for pos := 0; pos < len(text); {
		start, end := nextToken(text, pos)
		if start < 0 {
			break
		}
		for k := last; k < start; k++ {
			// keep
			_ = k
		}
		for k := start; k < end; k++ {
			if text[k] != '\n' {
				out[k] = ' '
			}
		}
		last = end
		pos = end
	}
	return out
}

// ---------------------------------------------------------------------------
// 2. The tree
// ---------------------------------------------------------------------------

type sourceFile struct {
	rel        string
	project    string
	code       []byte // comments and literals blanked, same length
	lineStarts []int
}

// lineOf returns the 1-based line an offset falls on.
func (s *sourceFile) lineOf(offset int) int {
	lo, hi := 0, len(s.lineStarts)
	for lo < hi {
		mid := (lo + hi) / 2
		if s.lineStarts[mid] <= offset {
			lo = mid + 1
		} else {
			hi = mid
		}
	}
	return lo
}

type typeDecl struct {
	name      string
	kind      string // class | interface | struct | enum | record
	bases     []string
	file      *sourceFile
	line      int
	namespace string
	baseSpan  []int // [lo, hi] char offsets of the base list, or nil
	sites     []site
}

type site struct {
	rel  string
	line int
}

func (d *typeDecl) rel() string     { return d.file.rel }
func (d *typeDecl) project() string { return d.file.project }

var buildOutputDirs = map[string]bool{"bin": true, "obj": true}

// pathLess orders two repository-relative paths the way python's
// `sorted()` orders `pathlib.Path` objects: by the TUPLE of path parts, not
// by the string. The difference shows up in this tree, where
// "Harbor.Abstractions/..." sorts before "Harbor.Abstractions.Contracts/..."
// (the shorter part is the smaller one), while a plain string comparison
// puts the dot before the slash and flips the order.
func pathLess(a, b string) bool {
	as := strings.Split(a, "/")
	bs := strings.Split(b, "/")
	for i := 0; i < len(as) && i < len(bs); i++ {
		if as[i] != bs[i] {
			return as[i] < bs[i]
		}
	}
	return len(as) < len(bs)
}

func isBuildOutput(rel string) bool {
	for _, part := range strings.Split(rel, "/") {
		if buildOutputDirs[part] {
			return true
		}
	}
	return false
}

// findProjectRoots maps every directory that owns a .csproj to that
// project's name.
func findProjectRoots(repo string) map[string]string {
	roots := map[string]string{}
	for _, tree := range consumerTrees {
		base := filepath.Join(repo, tree)
		info, err := os.Stat(base)
		if err != nil || !info.IsDir() {
			continue
		}
		var found []string
		_ = filepath.WalkDir(base, func(path string, d os.DirEntry, err error) error {
			if err != nil {
				return nil
			}
			if d.IsDir() {
				return nil
			}
			if strings.HasSuffix(path, ".csproj") {
				found = append(found, path)
			}
			return nil
		})
		sort.Slice(found, func(i, j int) bool { return pathLess(found[i], found[j]) })
		for _, csproj := range found {
			rel, rerr := filepath.Rel(repo, csproj)
			if rerr != nil || isBuildOutput(filepath.ToSlash(rel)) {
				continue
			}
			dir := filepath.ToSlash(filepath.Dir(rel))
			roots[dir] = strings.TrimSuffix(filepath.Base(csproj), ".csproj")
		}
	}
	return roots
}

// projectOf is the project a source file belongs to. A directory with no
// .csproj of its own is a LINKED-SOURCE tree; the files are read and the
// graph counts them, but they are attributed to the tree's own name.
func projectOf(rel string, roots map[string]string) string {
	parts := strings.Split(rel, "/")
	for i := len(parts) - 1; i >= 0; i-- {
		dir := strings.Join(parts[:i], "/")
		if name, ok := roots[dir]; ok {
			return name
		}
	}
	if len(parts) >= 2 {
		return parts[len(parts)-2]
	}
	return ""
}

// collectSources reads every .cs file under the given trees.
func collectSources(repo string, trees []string) []*sourceFile {
	roots := findProjectRoots(repo)
	var files []*sourceFile
	for _, tree := range trees {
		base := filepath.Join(repo, tree)
		info, err := os.Stat(base)
		if err != nil || !info.IsDir() {
			continue
		}
		var found []string
		_ = filepath.WalkDir(base, func(path string, d os.DirEntry, err error) error {
			if err != nil {
				return nil
			}
			if d.IsDir() {
				return nil
			}
			if strings.HasSuffix(path, ".cs") {
				found = append(found, path)
			}
			return nil
		})
		sort.Slice(found, func(i, j int) bool { return pathLess(found[i], found[j]) })
		for _, path := range found {
			rel, rerr := filepath.Rel(repo, path)
			if rerr != nil {
				continue
			}
			rel = filepath.ToSlash(rel)
			if isBuildOutput(rel) {
				continue
			}
			raw, rerr := os.ReadFile(path)
			if rerr != nil {
				continue
			}
			lineStarts := []int{0}
			for i, b := range raw {
				if b == '\n' {
					lineStarts = append(lineStarts, i+1)
				}
			}
			files = append(files, &sourceFile{
				rel:        rel,
				project:    projectOf(rel, roots),
				code:       stripCommentsAndLiterals(raw),
				lineStarts: lineStarts,
			})
		}
	}
	return files
}

// ---------------------------------------------------------------------------
// 3. Declarations
// ---------------------------------------------------------------------------

var (
	typeKeywordRe = regexp.MustCompile(`\b(?:record\s+(?:class|struct)|class|interface|struct|enum|record)[ \t]+([A-Za-z_][A-Za-z0-9_]*)`)
	namespaceRe   = regexp.MustCompile(`(?m)^[ \t]*namespace[ \t]+([A-Za-z_][A-Za-z0-9_.]*)`)
)

// declModifiers are the modifiers a declaration may carry before its keyword.
var declModifiers = map[string]bool{
	"public": true, "internal": true, "protected": true, "private": true,
	"sealed": true, "abstract": true, "static": true, "partial": true,
	"readonly": true, "ref": true, "new": true, "unsafe": true,
	"file": true, "record": true,
}

// declPrefixOK is the DECL_PREFIX_RE check: the text between the start of
// the line and the type keyword must be whitespace, attributes and modifier
// keywords, and nothing else.
func declPrefixOK(prefix string) bool {
	i := 0
	skipSpace := func() {
		for i < len(prefix) && (prefix[i] == ' ' || prefix[i] == '\t') {
			i++
		}
	}
	skipSpace()
	// attributes
	for i < len(prefix) && prefix[i] == '[' {
		j := i + 1
		for j < len(prefix) && prefix[j] != ']' && prefix[j] != '\n' {
			j++
		}
		if j >= len(prefix) || prefix[j] != ']' {
			return false
		}
		i = j + 1
		skipSpace()
	}
	// modifiers, each followed by whitespace
	for i < len(prefix) {
		start := i
		for i < len(prefix) && isIdentChar(prefix[i]) {
			i++
		}
		word := prefix[start:i]
		if !declModifiers[word] {
			return false
		}
		if i >= len(prefix) || (prefix[i] != ' ' && prefix[i] != '\t') {
			return false
		}
		skipSpace()
	}
	return true
}

func isIdentChar(b byte) bool {
	return b == '_' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
}

// readBases walks forward to the `:` that opens the base list, then to the
// `{` or `;` that closes the declaration.
func readBases(code []byte, start int) ([]string, []int) {
	n := len(code)
	i := start
	depth := 0
	for i < n {
		ch := code[i]
		switch {
		case ch == '<' || ch == '(' || ch == '[':
			depth++
		case ch == '>' || ch == ')' || ch == ']':
			if depth > 0 {
				depth--
			}
		case depth == 0 && ch == ':':
			goto found
		case depth == 0 && (ch == '{' || ch == ';'):
			return nil, nil
		}
		i++
	}
	return nil, nil
found:
	i++
	depth = 0
	bodyStart := i
	for i < n {
		ch := code[i]
		switch {
		case ch == '<' || ch == '(' || ch == '[':
			depth++
		case ch == '>' || ch == ')' || ch == ']':
			if depth > 0 {
				depth--
			}
		case depth == 0:
			if ch == '{' || ch == ';' || ch == ',' {
				goto done
			}
			if bytes.HasPrefix(code[i:], []byte("where")) && (i == 0 || !isAlnum(code[i-1])) {
				goto done
			}
		}
		i++
	}
done:
	body := string(code[bodyStart:i])
	if strings.TrimSpace(body) == "" {
		return nil, nil
	}
	return splitBases(body), []int{bodyStart, i}
}

func isAlnum(b byte) bool {
	return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
}

// splitBases splits a base list on commas that are not inside generic
// arguments.
func splitBases(body string) []string {
	var parts []string
	depth := 0
	var cur strings.Builder
	for _, ch := range body {
		switch ch {
		case '<', '(', '[':
			depth++
		case '>', ')', ']':
			if depth > 0 {
				depth--
			}
		}
		if ch == ',' && depth == 0 {
			parts = append(parts, cur.String())
			cur.Reset()
			continue
		}
		cur.WriteRune(ch)
	}
	parts = append(parts, cur.String())

	var names []string
	for _, part := range parts {
		cleaned := strings.TrimSpace(stripGeneric(part))
		if cleaned == "" {
			continue
		}
		tail := cleaned
		if idx := strings.LastIndex(cleaned, "."); idx >= 0 {
			tail = cleaned[idx+1:]
		}
		tail = strings.TrimSpace(tail)
		if idx := strings.IndexAny(tail, " \t"); idx >= 0 {
			tail = strings.TrimSpace(tail[:idx])
		}
		if tail != "" {
			names = append(names, tail)
		}
	}
	return names
}

// stripGeneric removes `<...>` groups from a type expression.
func stripGeneric(text string) string {
	var out strings.Builder
	depth := 0
	for _, ch := range text {
		if ch == '<' {
			depth++
			continue
		}
		if ch == '>' {
			if depth > 0 {
				depth--
			}
			continue
		}
		if depth == 0 {
			out.WriteRune(ch)
		}
	}
	return out.String()
}

// mergePartials merges the declarations of a `partial` type into one.
func mergePartials(decls []*typeDecl) []*typeDecl {
	merged := map[string]*typeDecl{}
	var order []string
	for _, decl := range decls {
		key := decl.namespace + "\x00" + decl.name
		current, ok := merged[key]
		if !ok {
			merged[key] = decl
			order = append(order, key)
			decl.sites = []site{{decl.rel(), decl.line}}
			continue
		}
		current.sites = append(current.sites, site{decl.rel(), decl.line})
		for _, base := range decl.bases {
			found := false
			for _, b := range current.bases {
				if b == base {
					found = true
					break
				}
			}
			if !found {
				current.bases = append(current.bases, base)
			}
		}
		if current.baseSpan == nil && decl.baseSpan != nil {
			current.baseSpan = decl.baseSpan
			current.file = decl.file
			current.line = decl.line
		}
		if current.kind == "record" && decl.kind == "class" {
			current.kind = "record"
		}
	}
	out := make([]*typeDecl, 0, len(order))
	for _, key := range order {
		out = append(out, merged[key])
	}
	return out
}

// extractTypes finds every type declaration in the tree.
func extractTypes(files []*sourceFile) []*typeDecl {
	var decls []*typeDecl
	for _, source := range files {
		code := source.code
		var namespaces []namespaceMatch
		for _, ns := range findNamespaces(code) {
			namespaces = append(namespaces, ns)
		}
		for _, m := range findTypeDecls(code) {
			name := m.name
			line := source.lineOf(m.start)
			lineStart := source.lineStarts[line-1]
			if !declPrefixOK(string(code[lineStart:m.start])) {
				continue
			}
			whole := m.whole
			kind := strings.SplitN(whole, " ", 2)[0]
			if strings.HasPrefix(whole, "record") {
				kind = "record"
			}
			namespace := ""
			for _, ns := range namespaces {
				if ns.start < m.start {
					namespace = ns.name
				} else {
					break
				}
			}
			bases, baseSpan := readBases(code, m.end)
			decls = append(decls, &typeDecl{
				name:      name,
				kind:      kind,
				bases:     bases,
				file:      source,
				line:      line,
				namespace: namespace,
				baseSpan:  baseSpan,
			})
		}
	}
	return mergePartials(decls)
}
