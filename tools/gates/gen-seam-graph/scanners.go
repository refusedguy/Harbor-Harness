package genseamgraph

// scanners.go — hand-written scanners for the hot patterns.
//
// The python original uses `re` for these. Go's `regexp` is RE2, and RE2 is
// slow on the tree-wide scans (a `\b`-anchored alternation over 45 MB of C#
// costs seconds). The scanners below are byte-for-byte equivalent to the
// python patterns and are what makes the port fast.
//
// They are hand-written for a second reason: the Rust port has no regex in
// its standard library, so the same scanners have to exist there anyway.
// Writing them once, in one shape, keeps the two ports comparable.
//
// Every scanner follows python's `re` semantics: a match is the LEFTMOST one,
// the alternatives are tried in the order the pattern lists them, and a
// failure at a position advances the scan to the next candidate position
// (which, for a `\b`-anchored pattern, is the next word start).

import (
	"sort"
	"strings"
)

// isWordByte reports whether b is a word character (`\w` in ASCII).
func isWordByte(b byte) bool {
	return b == '_' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
}

// isSpaceByte reports whether b is a whitespace character (`\s` in ASCII).
func isSpaceByte(b byte) bool {
	return b == ' ' || b == '\t' || b == '\n' || b == '\r' || b == '\f' || b == '\v'
}

// isIdentStart reports whether b may start an identifier ([A-Za-z_]).
func isIdentStart(b byte) bool {
	return b == '_' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z')
}

// wordAt reads the word that starts at pos and returns its end.
func wordAt(code []byte, pos int) int {
	j := pos
	for j < len(code) && isWordByte(code[j]) {
		j++
	}
	return j
}

// wordStarts returns the offsets of every word start in code.
func wordStarts(code []byte) []int {
	var out []int
	for i := 0; i < len(code); {
		if !isWordByte(code[i]) {
			i++
			continue
		}
		out = append(out, i)
		i = wordAt(code, i)
	}
	return out
}

// ---------------------------------------------------------------------------
// type declarations
// ---------------------------------------------------------------------------

// typeMatch is one hit of TYPE_KEYWORD_RE.
type typeMatch struct {
	start, end int    // the span of the whole match
	name       string // the captured name
	whole      string // the whole matched text
}

// findTypeDecls is `\b(?:record\s+(?:class|struct)|class|interface|struct|enum|record)[ \t]+([A-Za-z_]\w*)`.
func findTypeDecls(code []byte) []typeMatch {
	var out []typeMatch
	n := len(code)
	for i := 0; i < n; {
		if !isWordByte(code[i]) {
			i++
			continue
		}
		end := wordAt(code, i)
		word := string(code[i:end])
		rest := -1
		switch word {
		case "record":
			// The first alternative: `record\s+(?:class|struct)`.
			j := skipAnySpace(code, end)
			if j > end {
				if j < n && (code[j] == 'c' || code[j] == 's') {
					k := j
					for k < n && isWordByte(code[k]) {
						k++
					}
					kw := string(code[j:k])
					if kw == "class" || kw == "struct" {
						m := skipHTTab(code, k)
						if m > k {
							if name, nameEnd, ok := readIdent(code, m); ok {
								out = append(out, typeMatch{i, nameEnd, name, string(code[i:nameEnd])})
								i = nameEnd
								rest = 0
							}
						}
					}
				}
			}
			if rest < 0 {
				if name, nameEnd, ok := readIdentAfterHT(code, end); ok {
					out = append(out, typeMatch{i, nameEnd, name, string(code[i:nameEnd])})
					i = nameEnd
					continue
				}
			}
		case "class", "interface", "struct", "enum":
			if name, nameEnd, ok := readIdentAfterHT(code, end); ok {
				out = append(out, typeMatch{i, nameEnd, name, string(code[i:nameEnd])})
				i = nameEnd
				continue
			}
		}
		i = end
	}
	return out
}

// skipAnySpace skips a run of whitespace (`\s+`).
func skipAnySpace(code []byte, pos int) int {
	j := pos
	for j < len(code) && isSpaceByte(code[j]) {
		j++
	}
	return j
}

// skipHTTab skips a run of spaces and tabs (`[ \t]+`).
func skipHTTab(code []byte, pos int) int {
	j := pos
	for j < len(code) && (code[j] == ' ' || code[j] == '\t') {
		j++
	}
	return j
}

// readIdent reads an identifier at pos, if there is one.
func readIdent(code []byte, pos int) (string, int, bool) {
	if pos >= len(code) || !isIdentStart(code[pos]) {
		return "", 0, false
	}
	end := wordAt(code, pos)
	return string(code[pos:end]), end, true
}

// readIdentAfterHT reads `[ \t]+([A-Za-z_]\w*)` at pos and returns the name,
// its end, and whether it matched.
func readIdentAfterHT(code []byte, pos int) (string, int, bool) {
	j := skipHTTab(code, pos)
	if j == pos {
		return "", 0, false
	}
	return readIdent(code, j)
}

// ---------------------------------------------------------------------------
// namespaces
// ---------------------------------------------------------------------------

// namespaceMatch is one hit of NAMESPACE_RE.
type namespaceMatch struct {
	start int
	name  string
}

// findNamespaces is `(?m)^[ \t]*namespace[ \t]+([A-Za-z_][\w.]*)`.
func findNamespaces(code []byte) []namespaceMatch {
	var out []namespaceMatch
	n := len(code)
	for i := 0; i < n; {
		if i > 0 && code[i-1] != '\n' {
			// not at a line start
			i++
			continue
		}
		j := skipHTTab(code, i)
		if !bytesHas(code, j, "namespace") {
			i++
			continue
		}
		k := skipHTTab(code, j+len("namespace"))
		if k == j+len("namespace") {
			i++
			continue
		}
		if k >= n || !isIdentStart(code[k]) {
			i++
			continue
		}
		end := k + 1
		for end < n && (isWordByte(code[end]) || code[end] == '.') {
			end++
		}
		out = append(out, namespaceMatch{i, string(code[k:end])})
		i = end
	}
	return out
}

func bytesHas(code []byte, pos int, lit string) bool {
	if pos < 0 || pos+len(lit) > len(code) {
		return false
	}
	return string(code[pos:pos+len(lit)]) == lit
}

// ---------------------------------------------------------------------------
// interface mentions
// ---------------------------------------------------------------------------

// findInterfaceMentions is the union of `\bI[A-Z]\w*\b` and
// `\b(name1|name2|...)\b`, restricted to the names in `wanted`.
//
// The python code runs the two patterns in sequence; a name is in exactly one
// of them (`prefixed` and `named` are disjoint), so a single pass over the
// identifiers gives the same per-name line lists in the same order.
func findInterfaceMentions(code []byte, wanted map[string]bool) []identMatch {
	var out []identMatch
	for _, i := range wordStarts(code) {
		end := wordAt(code, i)
		name := string(code[i:end])
		if !wanted[name] {
			continue
		}
		out = append(out, identMatch{i, end, name})
	}
	return out
}

// identMatch is an identifier hit with its span.
type identMatch struct {
	start, end int
	name       string
}

// ---------------------------------------------------------------------------
// type-then-name (the signature bypass shape)
// ---------------------------------------------------------------------------

// findTypeThenName is `\b([A-Z]\w*)\s+([A-Za-z_]\w*)\b`.
func findTypeThenName(code []byte) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; {
		if !isWordByte(code[i]) {
			i++
			continue
		}
		if code[i] < 'A' || code[i] > 'Z' {
			i = wordAt(code, i)
			continue
		}
		end := wordAt(code, i)
		j := skipAnySpace(code, end)
		if j == end {
			i = end
			continue
		}
		if j >= len(code) || !isIdentStart(code[j]) {
			i = end
			continue
		}
		out = append(out, identMatch{i, end, string(code[i:end])})
		i = end
	}
	return out
}

// ---------------------------------------------------------------------------
// constructions
// ---------------------------------------------------------------------------

// findConstructions is `\bnew\s+([A-Z]\w*)\s*(?:<[^>]*>)?\s*\(`.
func findConstructions(code []byte) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; {
		if !isWordByte(code[i]) {
			i++
			continue
		}
		end := wordAt(code, i)
		if string(code[i:end]) != "new" {
			i = end
			continue
		}
		j := skipAnySpace(code, end)
		if j == end || j >= n || code[j] < 'A' || code[j] > 'Z' {
			i = end
			continue
		}
		nameEnd := wordAt(code, j)
		k := skipAnySpace(code, nameEnd)
		// The optional generic argument list.
		if k < n && code[k] == '<' {
			m := k + 1
			for m < n && code[m] != '>' {
				m++
			}
			if m < n {
				k = skipAnySpace(code, m+1)
			} else {
				// No closing '>': the optional group does not match.
				k = skipAnySpace(code, nameEnd)
			}
		}
		if k >= n || code[k] != '(' {
			i = end
			continue
		}
		out = append(out, identMatch{i, nameEnd, string(code[j:nameEnd])})
		i = end
	}
	return out
}

// ---------------------------------------------------------------------------
// DI registrations
// ---------------------------------------------------------------------------

// diMatch is one hit of DI_RE.
type diMatch struct {
	start    int
	lifetime string
	args     string
}

// findDiRegistrations is
// `\b(TryAdd|Add)(Singleton|Scoped|Transient)(?:Keyed)?<([^<>]*(?:<[^<>]*>[^<>]*)*)>`.
//
// The call name and the lifetime are ONE word in the source ("AddSingleton"),
// so the scan looks for the call name at a word start and the lifetime right
// after it.
func findDiRegistrations(code []byte) []diMatch {
	var out []diMatch
	n := len(code)
	for i := 0; i < n; {
		if !isWordByte(code[i]) {
			i++
			continue
		}
		end := wordAt(code, i)
		callLen := 0
		if bytesHas(code, i, "TryAdd") {
			callLen = len("TryAdd")
		} else if bytesHas(code, i, "Add") {
			callLen = len("Add")
		}
		if callLen == 0 {
			i = end
			continue
		}
		j := i + callLen
		lifetime := ""
		switch {
		case bytesHas(code, j, "Singleton"):
			lifetime = "Singleton"
		case bytesHas(code, j, "Scoped"):
			lifetime = "Scoped"
		case bytesHas(code, j, "Transient"):
			lifetime = "Transient"
		}
		if lifetime == "" {
			i = end
			continue
		}
		j += len(lifetime)
		if bytesHas(code, j, "Keyed") {
			j += len("Keyed")
		}
		if j >= n || code[j] != '<' {
			i = end
			continue
		}
		// The argument list: `[^<>]*(?:<[^<>]*>[^<>]*)*`.
		k := j + 1
		for k < n && code[k] != '>' && code[k] != '<' {
			k++
		}
		if k >= n {
			i = end
			continue
		}
		if code[k] == '<' {
			// One level of nesting: `<...>` then more plain text.
			m := k + 1
			for m < n && code[m] != '>' && code[m] != '<' {
				m++
			}
			if m >= n || code[m] != '>' {
				i = end
				continue
			}
			k = m + 1
			for k < n && code[k] != '>' && code[k] != '<' {
				k++
			}
			if k >= n || code[k] != '>' {
				i = end
				continue
			}
		}
		out = append(out, diMatch{i, lifetime, string(code[j+1 : k])})
		i = end
	}
	return out
}

// ---------------------------------------------------------------------------
// the axis registry and the route registry
// ---------------------------------------------------------------------------

// axisRow is one hit of AXIS_ROW_RE.
type axisRow struct {
	marker string
	door   string
}

// findAxisRows is
// `new\(\s*"([A-Za-z_]\w*)"\s*,\s*nameof\(\s*(?:[A-Za-z_]\w*\.)?([A-Za-z_]\w*)\s*\)\s*\)`.
func findAxisRows(text []byte) []axisRow {
	var out []axisRow
	n := len(text)
	for i := 0; i+3 < n; i++ {
		if !bytesHas(text, i, "new(") {
			continue
		}
		j := skipAnySpace(text, i+4)
		if j >= n || text[j] != '"' {
			continue
		}
		k := j + 1
		for k < n && text[k] != '"' && isWordByte(text[k]) {
			k++
		}
		if k >= n || text[k] != '"' {
			continue
		}
		marker := string(text[j+1 : k])
		l := skipAnySpace(text, k+1)
		if l >= n || text[l] != ',' {
			continue
		}
		l = skipAnySpace(text, l+1)
		if !bytesHas(text, l, "nameof(") {
			continue
		}
		l = skipAnySpace(text, l+len("nameof("))
		// An optional `Type.` prefix.
		if _, nameEnd, ok := readIdent(text, l); ok {
			p := skipAnySpace(text, nameEnd)
			if p < n && text[p] == '.' {
				l = skipAnySpace(text, p+1)
			}
		}
		door, doorEnd, ok := readIdent(text, l)
		if !ok {
			continue
		}
		m := skipAnySpace(text, doorEnd)
		if m >= n || text[m] != ')' {
			continue
		}
		m = skipAnySpace(text, m+1)
		if m >= n || text[m] != ')' {
			continue
		}
		out = append(out, axisRow{marker, door})
		i = m
	}
	return out
}

// routeConst is one hit of ROUTE_CONST_RE.
type routeConst struct {
	name  string
	value string
}

// findRouteConsts is
// `public\s+const\s+string\s+([A-Za-z_]\w*)\s*=\s*"([^"]*)"\s*;`.
func findRouteConsts(text []byte) []routeConst {
	var out []routeConst
	n := len(text)
	for i := 0; i < n; i++ {
		if !bytesHas(text, i, "public") {
			continue
		}
		j := skipAnySpace(text, i+len("public"))
		if !bytesHas(text, j, "const") {
			continue
		}
		j = skipAnySpace(text, j+len("const"))
		if !bytesHas(text, j, "string") {
			continue
		}
		j = skipAnySpace(text, j+len("string"))
		name, nameEnd, ok := readIdent(text, j)
		if !ok {
			continue
		}
		k := skipAnySpace(text, nameEnd)
		if k >= n || text[k] != '=' {
			continue
		}
		k = skipAnySpace(text, k+1)
		if k >= n || text[k] != '"' {
			continue
		}
		l := k + 1
		for l < n && text[l] != '"' {
			l++
		}
		if l >= n {
			continue
		}
		value := string(text[k+1 : l])
		m := skipAnySpace(text, l+1)
		if m < n && text[m] == ';' {
			out = append(out, routeConst{name, value})
			i = m
		}
	}
	return out
}

// routeRowMatch is one hit of ROUTE_ROW_RE.
type routeRowMatch struct {
	route    string
	door     string
	carriage string
	reason   string
}

// findRouteRows is
// `new\(\s*([A-Za-z_]\w*)\s*,\s*nameof\(\s*(?:[A-Za-z_]\w*\s*\.\s*)*([A-Za-z_]\w*)\s*\)\s*,\s*PluginAxisCarriage\s*\.\s*([A-Za-z_]\w*)\s*,\s*((?:"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+)`.
func findRouteRows(text []byte) []routeRowMatch {
	var out []routeRowMatch
	n := len(text)
	for i := 0; i+3 < n; i++ {
		if !bytesHas(text, i, "new(") {
			continue
		}
		j := skipAnySpace(text, i+4)
		route, routeEnd, ok := readIdent(text, j)
		if !ok {
			continue
		}
		k := skipAnySpace(text, routeEnd)
		if k >= n || text[k] != ',' {
			continue
		}
		k = skipAnySpace(text, k+1)
		if !bytesHas(text, k, "nameof(") {
			continue
		}
		k = skipAnySpace(text, k+len("nameof("))
		// An optional `Type.` prefix.
		for {
			_, nameEnd, ok := readIdent(text, k)
			if !ok {
				break
			}
			p := skipAnySpace(text, nameEnd)
			if p < n && text[p] == '.' {
				k = skipAnySpace(text, p+1)
				continue
			}
			break
		}
		door, doorEnd, ok := readIdent(text, k)
		if !ok {
			continue
		}
		m := skipAnySpace(text, doorEnd)
		if m >= n || text[m] != ')' {
			continue
		}
		m = skipAnySpace(text, m+1)
		if m >= n || text[m] != ',' {
			continue
		}
		m = skipAnySpace(text, m+1)
		if !bytesHas(text, m, "PluginAxisCarriage") {
			continue
		}
		m = skipAnySpace(text, m+len("PluginAxisCarriage"))
		if m >= n || text[m] != '.' {
			continue
		}
		m = skipAnySpace(text, m+1)
		carriage, carriageEnd, ok := readIdent(text, m)
		if !ok {
			continue
		}
		p := skipAnySpace(text, carriageEnd)
		if p >= n || text[p] != ',' {
			continue
		}
		p = skipAnySpace(text, p+1)
		// The reason: one or more string literals, optionally joined with '+'.
		reason, reasonEnd, ok := readStringLiteralRun(text, p)
		if !ok {
			continue
		}
		out = append(out, routeRowMatch{route, door, carriage, reason})
		i = reasonEnd - 1
	}
	return out
}

// readStringLiteralRun reads `(?:"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+` and
// returns the CONCATENATED contents of the literals.
func readStringLiteralRun(text []byte, pos int) (string, int, bool) {
	n := len(text)
	var sb strings.Builder
	i := pos
	for {
		j := skipAnySpace(text, i)
		if j >= n || text[j] != '"' {
			break
		}
		k := j + 1
		for k < n {
			if text[k] == '\\' && k+1 < n {
				k += 2
				continue
			}
			if text[k] == '"' {
				break
			}
			k++
		}
		if k >= n || text[k] != '"' {
			break
		}
		sb.Write(text[j+1 : k])
		i = k + 1
		// An optional `+` after the literal.
		j = skipAnySpace(text, i)
		if j < n && text[j] == '+' {
			i = j + 1
			continue
		}
		i = j
		break
	}
	if sb.Len() == 0 {
		return "", pos, false
	}
	return sb.String(), i, true
}

// findStringLiterals is `"((?:[^"\\]|\\.)*)"`: the contents of every string
// literal, in order.
func findStringLiterals(text []byte) []string {
	var out []string
	n := len(text)
	for i := 0; i < n; i++ {
		if text[i] != '"' {
			continue
		}
		k := i + 1
		for k < n {
			if text[k] == '\\' && k+1 < n {
				k += 2
				continue
			}
			if text[k] == '"' {
				break
			}
			k++
		}
		if k >= n || text[k] != '"' {
			continue
		}
		out = append(out, string(text[i+1:k]))
		i = k
	}
	return out
}

// findDoorDecls is `public[ \t]+Result[ \t]+(Register\w*)[ \t]*\(`.
func findDoorDecls(text []byte) []string {
	var out []string
	n := len(text)
	for i := 0; i < n; i++ {
		if !bytesHas(text, i, "public") {
			continue
		}
		j := i + len("public")
		k := j
		for k < n && (text[k] == ' ' || text[k] == '\t') {
			k++
		}
		if k == j {
			continue
		}
		if !bytesHas(text, k, "Result") {
			continue
		}
		k += len("Result")
		j = k
		for j < n && (text[j] == ' ' || text[j] == '\t') {
			j++
		}
		if j == k {
			continue
		}
		k = j
		if !bytesHas(text, k, "Register") {
			continue
		}
		end := wordAt(text, k)
		m := skipHTTab(text, end)
		if m < n && text[m] == '(' {
			out = append(out, string(text[k:end]))
		}
	}
	return out
}

// findDispatchSites is `\bis\s+([A-Z]\w*)\b`.
func findDispatchSites(code []byte) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; i++ {
		if !bytesHas(code, i, "is") {
			continue
		}
		if i > 0 && isWordByte(code[i-1]) {
			continue
		}
		if i+2 < n && isWordByte(code[i+2]) {
			continue
		}
		j := skipAnySpace(code, i+2)
		if j == i+2 || j >= n || code[j] < 'A' || code[j] > 'Z' {
			continue
		}
		end := wordAt(code, j)
		out = append(out, identMatch{i, end, string(code[j:end])})
		i = end - 1
	}
	return out
}

// findDoorCalls is `\.\s*(Register\w*)\s*\(`.
func findDoorCalls(code []byte) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; i++ {
		if code[i] != '.' {
			continue
		}
		j := skipAnySpace(code, i+1)
		if j >= n || !bytesHas(code, j, "Register") {
			continue
		}
		end := wordAt(code, j)
		k := skipAnySpace(code, end)
		if k < n && code[k] == '(' {
			out = append(out, identMatch{i, end, string(code[j:end])})
			i = end - 1
		}
	}
	return out
}

// findCallsWithPrefix is `\.\s*(?:PREFIX1|PREFIX2|...)\s*\(`.
func findCallsWithPrefix(code []byte, prefixes []string) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; i++ {
		if code[i] != '.' {
			continue
		}
		j := skipAnySpace(code, i+1)
		for _, p := range prefixes {
			if !bytesHas(code, j, p) {
				continue
			}
			end := j + len(p)
			if end < n && isWordByte(code[end]) {
				continue
			}
			k := skipAnySpace(code, end)
			if k < n && code[k] == '(' {
				out = append(out, identMatch{i, end, p})
				i = end - 1
			}
			break
		}
	}
	return out
}

// findResultCalls is `Result\s*\.\s*(Success|Failure)\b`.
func findResultCalls(code []byte, name string) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; i++ {
		if !bytesHas(code, i, "Result") {
			continue
		}
		if i > 0 && isWordByte(code[i-1]) {
			continue
		}
		j := skipAnySpace(code, i+len("Result"))
		if j >= n || code[j] != '.' {
			continue
		}
		j = skipAnySpace(code, j+1)
		if !bytesHas(code, j, name) {
			continue
		}
		end := j + len(name)
		if end < n && isWordByte(code[end]) {
			continue
		}
		out = append(out, identMatch{i, end, name})
		i = end - 1
	}
	return out
}

// findForwardCalls is `\.\s*(?:Register\w*)\s*\(`: a call to a Register*
// method, the shape a door that forwards its contribution has.
func findForwardCalls(code []byte) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; i++ {
		if code[i] != '.' {
			continue
		}
		j := skipAnySpace(code, i+1)
		if !bytesHas(code, j, "Register") {
			continue
		}
		end := wordAt(code, j+len("Register"))
		k := skipAnySpace(code, end)
		if k < n && code[k] == '(' {
			out = append(out, identMatch{i, end, "Register"})
			i = end - 1
		}
	}
	return out
}

// findNamedCall is `\bNAME\s*\(`.
func findNamedCall(code []byte, name string) []identMatch {
	var out []identMatch
	n := len(code)
	for i := 0; i < n; i++ {
		if !bytesHas(code, i, name) {
			continue
		}
		if i > 0 && isWordByte(code[i-1]) {
			continue
		}
		end := i + len(name)
		if end < n && isWordByte(code[end]) {
			continue
		}
		j := skipAnySpace(code, end)
		if j < n && code[j] == '(' {
			out = append(out, identMatch{i, end, name})
			i = end - 1
		}
	}
	return out
}

// findTypeDeclBody is `\b(?:class|record|struct|interface)\s+NAME\b`.
func findTypeDeclBody(code []byte, name string) (int, bool) {
	n := len(code)
	for i := 0; i < n; i++ {
		if !isWordByte(code[i]) {
			continue
		}
		if i > 0 && isWordByte(code[i-1]) {
			continue
		}
		end := wordAt(code, i)
		switch string(code[i:end]) {
		case "class", "record", "struct", "interface":
		default:
			i = end - 1
			continue
		}
		j := skipAnySpace(code, end)
		if j == end {
			i = end - 1
			continue
		}
		if !bytesHas(code, j, name) {
			i = end - 1
			continue
		}
		k := j + len(name)
		if k < n && isWordByte(code[k]) {
			i = end - 1
			continue
		}
		return k, true
	}
	return 0, false
}

// sortedStrings returns the sorted keys of a set.
func sortedStrings(set map[string]bool) []string {
	out := make([]string, 0, len(set))
	for k := range set {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
