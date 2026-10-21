package testdup

// lex.go -- the C# lexer and the method extraction.
//
// The detection method, in four steps:
//
//  1. Lex each file into tokens. Comments are dropped; string literals,
//     char literals and numeric literals are kept as distinct token kinds,
//     because a `}` inside a string is not a brace and a `//` inside a raw
//     string is not a comment.
//  2. Find method bodies by brace matching.
//  3. Normalise the body into a token stream: comments dropped, string
//     literals -> S, char literals -> C, numeric literals -> N, identifiers
//     -> I, everything else kept, whitespace collapsed.
//  4. Group the bodies by that fingerprint.
//
// A shape match alone is NOT enough, and the reason is worth stating: two
// bodies can have the same token shape and nothing else in common. So a
// group is only a duplicate when its members also share at least
// `--min-similarity` of their IDENTIFIER VOCABULARY (Jaccard over the
// identifier tokens).

import (
	"sort"
	"strings"
	"unicode"
	"unicode/utf8"
)

// tokenKind is the kind of a token.
type tokenKind int

// The token kinds.
const (
	tokenIdent tokenKind = iota
	tokenNumber
	tokenString
	tokenChar
	tokenPunct
)

// token is one lexed token: its kind and its span in the source.
type token struct {
	kind  tokenKind
	start int
	end   int
}

// lex tokenises C# source.
//
// String literals are ONE token, so a `}` inside a string never closes a
// brace and a `"` inside a comment never opens one. That is the whole
// reason this is a lexer and not a regex.
//
// The tokens are CHARACTERS, not bytes: a `█` inside a character literal is
// one token of the shape stream, and a lexer that cut it into three bytes
// would hash a different shape than the python original for every body that
// holds one.
func lex(src string) []token {
	var tokens []token
	i := 0
	n := len(src)
	for i < n {
		ch := src[i]
		if isSpace(src, i) {
			i += runeWidth(src, i)
			continue
		}
		if ch == '/' && i+1 < n && src[i+1] == '/' {
			j := strings.IndexByte(src[i:], '\n')
			if j < 0 {
				i = n
			} else {
				i += j
			}
			continue
		}
		if ch == '/' && i+1 < n && src[i+1] == '*' {
			j := strings.Index(src[i+2:], "*/")
			if j < 0 {
				i = n
			} else {
				i = i + 2 + j + 2
			}
			continue
		}
		// String-ish: `@"..."`, `$"..."`, `$@"..."`, `"""..."""`, `$"""..."""`.
		quoteAt := -1
		verbatim := false
		raw := false
		switch {
		case ch == '"':
			quoteAt = i
			raw = strings.HasPrefix(src[i:], `"""`)
		case ch == '@' && i+1 < n && src[i+1] == '"':
			quoteAt = i + 1
			verbatim = true
		case ch == '$' && i+1 < n && src[i+1] == '"':
			quoteAt = i + 1
			raw = strings.HasPrefix(src[i+1:], `"""`)
		case ch == '$' && i+2 < n && src[i+1] == '@' && src[i+2] == '"':
			quoteAt = i + 2
			verbatim = true
		}
		if quoteAt >= 0 {
			end := scanString(src, quoteAt, verbatim, raw)
			tokens = append(tokens, token{tokenString, i, end})
			i = end
			continue
		}
		if ch == '\'' {
			end := scanChar(src, i)
			if end > 0 {
				tokens = append(tokens, token{tokenChar, i, end})
				i = end
				continue
			}
		}
		if end := scanIdent(src, i); end > i {
			tokens = append(tokens, token{tokenIdent, i, end})
			i = end
			continue
		}
		if end := scanNumber(src, i); end > i {
			tokens = append(tokens, token{tokenNumber, i, end})
			i = end
			continue
		}
		width := runeWidth(src, i)
		tokens = append(tokens, token{tokenPunct, i, i + width})
		i += width
	}
	return tokens
}

// runeWidth is the width of the character at `start`, in bytes.
func runeWidth(src string, start int) int {
	_, size := utf8.DecodeRuneInString(src[start:])
	if size <= 0 {
		return 1
	}
	return size
}

// isSpace is python's str.isspace for the character at `start`.
func isSpace(src string, start int) bool {
	r, _ := utf8.DecodeRuneInString(src[start:])
	return unicode.IsSpace(r)
}

// scanString returns the end offset of the string literal whose opening
// quote is at `start`.
func scanString(src string, start int, verbatim bool, raw bool) int {
	n := len(src)
	if raw {
		quotes := 3
		for start+quotes < n && src[start+quotes] == '"' {
			quotes++
		}
		i := start + quotes
		for i < n {
			if strings.HasPrefix(src[i:], strings.Repeat(`"`, quotes)) {
				return i + quotes
			}
			i++
		}
		return n
	}
	i := start + 1
	if verbatim {
		for i < n {
			if src[i] == '"' {
				if i+1 < n && src[i+1] == '"' {
					i += 2
					continue
				}
				return i + 1
			}
			i++
		}
		return n
	}
	for i < n {
		ch := src[i]
		if ch == '\\' {
			i += 2
			continue
		}
		if ch == '"' {
			return i + 1
		}
		if ch == '\n' {
			return i
		}
		i++
	}
	return n
}

// scanChar returns the end offset of the char literal at `start`, or -1
// when it is not one.
func scanChar(src string, start int) int {
	n := len(src)
	i := start + 1
	if i < n && src[i] == '\\' {
		if i+1 < n {
			return i + 2
		}
		return n
	}
	if i < n && src[i] == '\'' {
		return i + 1
	}
	return -1
}

// scanIdent returns the end offset of the identifier at `start`.
func scanIdent(src string, start int) int {
	n := len(src)
	i := start
	if i >= n {
		return i
	}
	c := src[i]
	if !(c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
		return i
	}
	i++
	for i < n {
		c = src[i]
		if !(c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) {
			break
		}
		i++
	}
	return i
}

// scanNumber returns the end offset of the numeric literal at `start`.
func scanNumber(src string, start int) int {
	n := len(src)
	i := start
	if i+1 < n && src[i] == '0' && (src[i+1] == 'x' || src[i+1] == 'X') {
		j := i + 2
		for j < n && (isHexDigit(src[j]) || src[j] == '_') {
			j++
		}
		if j > i+2 {
			return j
		}
	}
	if i+1 < n && src[i] == '0' && (src[i+1] == 'b' || src[i+1] == 'B') {
		j := i + 2
		for j < n && (src[j] == '0' || src[j] == '1' || src[j] == '_') {
			j++
		}
		if j > i+2 {
			return j
		}
	}
	if i >= n || src[i] < '0' || src[i] > '9' {
		return i
	}
	j := i + 1
	for j < n && ((src[j] >= '0' && src[j] <= '9') || src[j] == '_') {
		j++
	}
	if j < n && src[j] == '.' {
		j++
		for j < n && ((src[j] >= '0' && src[j] <= '9') || src[j] == '_') {
			j++
		}
	}
	if j < n && (src[j] == 'e' || src[j] == 'E') {
		k := j + 1
		if k < n && (src[k] == '+' || src[k] == '-') {
			k++
		}
		if k < n && src[k] >= '0' && src[k] <= '9' {
			k++
			for k < n && src[k] >= '0' && src[k] <= '9' {
				k++
			}
			j = k
		}
	}
	for j < n && strings.IndexByte("fFdDmMuUlL", src[j]) >= 0 {
		j++
	}
	return j
}

// isHexDigit reports whether a byte is a hexadecimal digit.
func isHexDigit(c byte) bool {
	switch {
	case c >= '0' && c <= '9':
		return true
	case c >= 'a' && c <= 'f':
		return true
	case c >= 'A' && c <= 'F':
		return true
	}
	return false
}

// notADeclaration are the identifiers that, when they precede a `(`, mean
// the `(` belongs to an EXPRESSION rather than to a declaration.
var notADeclaration = map[string]bool{
	"return": true, "await": true, "new": true, "throw": true, "yield": true,
	"nameof": true, "typeof": true, "sizeof": true, "default": true, "is": true,
	"as": true, "in": true, "out": true, "ref": true, "when": true, "case": true,
	"else": true, "do": true, "var": true, "let": true, "value": true, "this": true,
	"base": true, "true": true, "false": true, "null": true, "switch": true,
	"if": true, "while": true, "for": true, "foreach": true, "lock": true,
	"using": true, "fixed": true, "checked": true, "unchecked": true,
	"stackalloc": true, "delegate": true, "from": true, "group": true,
	"join": true, "into": true, "orderby": true, "select": true, "where": true,
	"operator": true,
}

// typeSuffixPunct are the tokens that may precede a method name and still
// mean "a type", because the type is written with a generic or an array
// suffix.
const typeSuffixPunct = ">]?"

// method is one method body found in a file.
type method struct {
	path      string
	name      string
	ordinal   int
	startLine int
	endLine   int
	lines     int
	isTest    bool
	shape     string
	exact     string
	idents    map[string]bool
}

// where is the method's place in its file.
func (m *method) where() string {
	return m.path + ":" + itoa(m.startLine) + "-" + itoa(m.endLine)
}

// itoa is strconv.Itoa, without the import in every file.
func itoa(value int) string {
	if value == 0 {
		return "0"
	}
	negative := value < 0
	if negative {
		value = -value
	}
	var digits []byte
	for value > 0 {
		digits = append([]byte{byte('0' + value%10)}, digits...)
		value /= 10
	}
	if negative {
		return "-" + string(digits)
	}
	return string(digits)
}

// lineStarts are the offsets at which each line starts, for O(log n) line
// lookups.
func lineStarts(src string) []int {
	starts := []int{0}
	for index := 0; index < len(src); index++ {
		if src[index] == '\n' {
			starts = append(starts, index+1)
		}
	}
	return starts
}

// lineOf is the 1-based line number of an offset, from a precomputed line
// table.
func lineOf(starts []int, offset int) int {
	return sort.SearchInts(starts, offset+1)
}

// matchParen is the index of the `)` closing the `(` at `index`, or -1.
func matchParen(tokens []token, index int, src string) int {
	depth := 0
	for i := index; i < len(tokens); i++ {
		if tokens[i].kind != tokenPunct {
			continue
		}
		switch src[tokens[i].start] {
		case '(':
			depth++
		case ')':
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// matchAngle is the index of the `>` closing the `<` at `index`, or -1.
func matchAngle(tokens []token, index int, src string) int {
	depth := 0
	for i := index; i < len(tokens); i++ {
		if tokens[i].kind != tokenPunct {
			continue
		}
		switch src[tokens[i].start] {
		case '<':
			depth++
		case '>':
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// matchBrace is the index of the `}` closing the `{` at `index`, or -1.
func matchBrace(tokens []token, index int, src string) int {
	depth := 0
	for i := index; i < len(tokens); i++ {
		if tokens[i].kind != tokenPunct {
			continue
		}
		switch src[tokens[i].start] {
		case '{':
			depth++
		case '}':
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// isDeclarationContext reports whether the identifier at `index` can be a
// method NAME.
//
// A method name is preceded by its return type or by a modifier. A call is
// preceded by a dot, by an operator, or by a keyword that makes the
// parentheses an expression.
func isDeclarationContext(tokens []token, index int, src string) bool {
	if index == 0 {
		return false
	}
	previous := tokens[index-1]
	switch previous.kind {
	case tokenIdent:
		return !notADeclaration[src[previous.start:previous.end]]
	case tokenPunct:
		return strings.IndexByte(typeSuffixPunct, src[previous.start]) >= 0
	}
	return false
}

// attributeNames are the names of the attributes written immediately before
// a declaration.
//
// The attribute list is found by walking back to the statement boundary (the
// `;`, `{` or `}` that ends the previous statement) and asking whether the
// declaration starts with an attribute list.
//
// Only the first identifier of each attribute is taken, which is the
// attribute's name: `[Test]`, `[TestCase(...)]`, `[Arguments(1)]`.
func attributeNames(tokens []token, index int, src string) []string {
	boundary := -1
	for i := index - 1; i >= 0; i-- {
		if tokens[i].kind == tokenPunct {
			switch src[tokens[i].start] {
			case ';', '{', '}':
				boundary = i
			}
			if boundary >= 0 {
				break
			}
		}
	}
	if boundary < 0 || boundary+1 >= index {
		return nil
	}
	if tokens[boundary+1].kind != tokenPunct || src[tokens[boundary+1].start] != '[' {
		return nil
	}
	depth := 0
	close := -1
	for i := boundary + 1; i < index; i++ {
		if tokens[i].kind != tokenPunct {
			continue
		}
		switch src[tokens[i].start] {
		case '[':
			depth++
		case ']':
			depth--
			if depth == 0 {
				close = i
			}
		}
		if close >= 0 {
			break
		}
	}
	if close < 0 {
		return nil
	}
	var names []string
	seen := map[string]bool{}
	for i := boundary + 2; i < close; i++ {
		if tokens[i].kind != tokenIdent {
			continue
		}
		name := src[tokens[i].start:tokens[i].end]
		if seen[name] {
			continue
		}
		seen[name] = true
		names = append(names, name)
	}
	return names
}

// normalise returns the shape stream (identifiers erased), the exact stream
// and the identifier vocabulary.
//
// The shape stream is what #1778 specifies: literals collapse to a marker
// so that two bodies differing only in a string or a number are the SAME
// body, and identifiers collapse so that two bodies differing only in the
// names they use are the same body too.
//
// The exact stream keeps the identifiers: two bodies that agree token for
// token, modulo literals, are the same CODE and are reported as such.
func normalise(tokens []token, src string, first int, last int) (string, string, map[string]bool) {
	shape := make([]string, 0, last-first+1)
	exact := make([]string, 0, last-first+1)
	idents := map[string]bool{}
	for i := first; i <= last; i++ {
		var token string
		switch tokens[i].kind {
		case tokenString:
			token = "S"
		case tokenChar:
			token = "C"
		case tokenNumber:
			token = "N"
		case tokenIdent:
			idents[src[tokens[i].start:tokens[i].end]] = true
			token = "I"
		default:
			token = src[tokens[i].start:tokens[i].end]
		}
		shape = append(shape, token)
		if tokens[i].kind == tokenIdent {
			exact = append(exact, src[tokens[i].start:tokens[i].end])
		} else {
			exact = append(exact, token)
		}
	}
	return strings.Join(shape, " "), strings.Join(exact, " "), idents
}

// extractMethods returns every method body in `src`, outermost first.
func extractMethods(path string, src string) []*method {
	tokens := lex(src)
	starts := lineStarts(src)
	var found []*method
	index := 0
	count := len(tokens)
	for index < count {
		if tokens[index].kind != tokenIdent {
			index++
			continue
		}
		name := src[tokens[index].start:tokens[index].end]
		if notADeclaration[name] {
			index++
			continue
		}
		if !isDeclarationContext(tokens, index, src) {
			index++
			continue
		}
		next := index + 1
		// A generic method: `void Foo<T>(...)`.
		if next < count && tokens[next].kind == tokenPunct && src[tokens[next].start] == '<' {
			closeAngle := matchAngle(tokens, next, src)
			if closeAngle < 0 {
				index++
				continue
			}
			next = closeAngle + 1
		}
		if next >= count || tokens[next].kind != tokenPunct || src[tokens[next].start] != '(' {
			index++
			continue
		}
		closeParen := matchParen(tokens, next, src)
		if closeParen < 0 {
			index++
			continue
		}
		tail := closeParen + 1
		if tail >= count || tokens[tail].kind != tokenPunct {
			index++
			continue
		}
		tailChar := src[tokens[tail].start]
		var bodyFirst, bodyLast, endOffset int
		switch {
		case tailChar == '{':
			bodyOpen := tail
			bodyClose := matchBrace(tokens, bodyOpen, src)
			if bodyClose < 0 {
				index++
				continue
			}
			bodyFirst = bodyOpen + 1
			bodyLast = bodyClose - 1
			endOffset = tokens[bodyClose].end
		case tailChar == '=' && tail+1 < count && src[tokens[tail+1].start] == '>':
			// Expression-bodied member: `int Foo() => 42;`
			bodyFirst = tail + 2
			bodyLast = bodyFirst
			semi := -1
			depth := 0
			for i := bodyFirst; i < count; i++ {
				if tokens[i].kind != tokenPunct {
					continue
				}
				ch := src[tokens[i].start]
				switch {
				case ch == '(' || ch == '[' || ch == '{':
					depth++
				case ch == ')' || ch == ']' || ch == '}':
					depth--
				case ch == ';' && depth == 0:
					semi = i
				}
				if semi >= 0 {
					break
				}
			}
			if semi < 0 {
				index++
				continue
			}
			bodyLast = semi - 1
			endOffset = tokens[semi].end
		default:
			index++
			continue
		}

		if bodyLast < bodyFirst {
			index = tail + 1
			continue
		}

		attributes := attributeNames(tokens, index, src)
		isTest := false
		for _, name := range attributes {
			if name == "Test" || name == "TestCase" {
				isTest = true
			}
		}
		shape, exact, idents := normalise(tokens, src, bodyFirst, bodyLast)
		lineSet := map[int]bool{}
		for i := bodyFirst; i <= bodyLast; i++ {
			lineSet[lineOf(starts, tokens[i].start)] = true
		}
		found = append(found, &method{
			path:      path,
			name:      name,
			startLine: lineOf(starts, tokens[bodyFirst].start),
			endLine:   lineOf(starts, endOffset),
			lines:     len(lineSet),
			isTest:    isTest,
			shape:     sha1Hex(shape),
			exact:     sha1Hex(exact),
			idents:    idents,
		})
		index = tail + 1
	}

	// Drop nested declarations: a local function's text is already part of
	// the enclosing body, and counting it twice would make one copy look
	// like two.
	sort.SliceStable(found, func(i, j int) bool {
		if found[i].startLine != found[j].startLine {
			return found[i].startLine < found[j].startLine
		}
		return found[i].endLine > found[j].endLine
	})
	var outermost []*method
	for _, one := range found {
		if len(outermost) > 0 && one.startLine >= outermost[len(outermost)-1].startLine &&
			one.endLine <= outermost[len(outermost)-1].endLine {
			continue
		}
		outermost = append(outermost, one)
	}

	// A method's identity must be unique inside its file: one test class can
	// hold five methods called `CreateContext`, and a key that collides would
	// make a group look unchanged when one more copy is added.
	perName := map[string][]*method{}
	var order []string
	for _, one := range outermost {
		if _, ok := perName[one.name]; !ok {
			order = append(order, one.name)
		}
		perName[one.name] = append(perName[one.name], one)
	}
	for _, name := range order {
		for index, one := range perName[name] {
			one.ordinal = index
		}
	}
	return outermost
}
