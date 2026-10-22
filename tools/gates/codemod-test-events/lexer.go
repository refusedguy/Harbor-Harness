package codemodtestevents

// lexer.go -- the reading of a C# source file the codemod does by hand: the
// mask that hides strings and comments, the split of an argument list, and
// the comparison of two argument texts.
//
// The python original worked on CHARACTERS and this works on BYTES. The
// two agree because every offset the codemod uses is the start of a match
// or the start of an ASCII delimiter, and the mask preserves the byte
// offsets of the text it masks.

import (
	"regexp"
	"strings"
	"unicode"
	"unicode/utf8"
)

// isSpaceRune whether a rune is whitespace the way python's str.isspace()
// says it is.
//
// Go's unicode.IsSpace does not count the four control characters
// U+001C..U+001F that python counts, so they are added here.
func isSpaceRune(r rune) bool {
	if r >= 0x1c && r <= 0x1f {
		return true
	}
	return unicode.IsSpace(r)
}

// pyStrip strips the whitespace from both ends of a string, the way
// python's str.strip() does.
func pyStrip(s string) string {
	start := 0
	for start < len(s) {
		r, size := utf8.DecodeRuneInString(s[start:])
		if !isSpaceRune(r) {
			break
		}
		start += size
	}
	end := len(s)
	for end > start {
		r, size := utf8.DecodeLastRuneInString(s[:end])
		if !isSpaceRune(r) {
			break
		}
		end -= size
	}
	return s[start:end]
}

// stringEnd is the index just past the string literal that starts at start.
func stringEnd(text string, start int) int {
	n := len(text)
	if strings.HasPrefix(text[start:], `"""`) {
		end := strings.Index(text[start+3:], `"""`)
		if end < 0 {
			return n
		}
		return start + 3 + end + 3
	}
	verbatim := start > 0 && text[start-1] == '@'
	i := start + 1
	for i < n {
		if !verbatim && text[i] == '\\' {
			i += 2
			continue
		}
		if text[i] == '"' {
			if verbatim && i+1 < n && text[i+1] == '"' {
				i += 2
				continue
			}
			return i + 1
		}
		i++
	}
	return n
}

// charEnd is the index just past the character literal that starts at start.
func charEnd(text string, start int) int {
	n := len(text)
	i := start + 1
	for i < n {
		if text[i] == '\\' {
			i += 2
			continue
		}
		if text[i] == '\'' {
			return i + 1
		}
		i++
	}
	return n
}

// maskCode blanks out string literals AND comments, preserving offsets.
//
// A `new AgentStartEvent(` inside a string literal is TEXT, not code — the
// architecture suite keeps source snippets in strings on purpose, and this
// file's own header quotes a call site. A `new AgentStartEvent(` inside a
// comment is a sentence, not a code. Masking both keeps the search from
// ever seeing one, and keeping the length keeps every offset in the masked
// text valid in the original.
//
// The scan checks for a COMMENT before it checks for a quote, because a
// quote inside a comment is not the start of a string: an apostrophe in
// "don't" would otherwise open a character literal and blank out half the
// file.
func maskCode(text string) string {
	out := []byte(text)
	blank := func(start, end int) {
		if start < 0 {
			start = 0
		}
		if end > len(out) {
			end = len(out)
		}
		for k := start; k < end; k++ {
			if out[k] != '\n' {
				out[k] = ' '
			}
		}
	}
	i := 0
	n := len(text)
	for i < n {
		c := text[i]
		if c == '/' && i+1 < n && text[i+1] == '/' {
			end := strings.IndexByte(text[i:], '\n')
			if end < 0 {
				end = n
			} else {
				end = i + end
			}
			blank(i, end)
			i = end
			continue
		}
		if c == '/' && i+1 < n && text[i+1] == '*' {
			end := strings.Index(text[i+2:], "*/")
			if end < 0 {
				end = n
			} else {
				end = i + 2 + end + 2
			}
			blank(i, end)
			i = end
			continue
		}
		if c == '"' {
			end := stringEnd(text, i)
			blank(i+1, maxInt(i+1, end-1))
			i = end
			continue
		}
		if c == '\'' {
			end := charEnd(text, i)
			blank(i+1, maxInt(i+1, end-1))
			i = end
			continue
		}
		i++
	}
	return string(out)
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

// hasComment whether the text carries a comment outside its string literals.
//
// The check cannot be done on the masked text: masking blanks the comment
// out, so the marker disappears with it. And it cannot be a plain substring
// search either: `/* body */` inside a string literal is DATA, not a
// comment. So the text is walked the way the lexer walks it.
func hasComment(text string) bool {
	i := 0
	n := len(text)
	for i < n {
		c := text[i]
		if c == '/' && i+1 < n && (text[i+1] == '/' || text[i+1] == '*') {
			return true
		}
		if c == '"' {
			i = stringEnd(text, i)
			continue
		}
		if c == '\'' {
			i = charEnd(text, i)
			continue
		}
		i++
	}
	return false
}

// collapseWhitespace folds an argument that spans several lines onto one
// line.
//
// The second return value is false when the fold is not safe — an argument
// that carries a comment, where a newline ends the comment and folding it
// would comment out the rest of the argument.
func collapseWhitespace(text string) (string, bool) {
	if !strings.Contains(text, "\n") {
		return text, true
	}
	if hasComment(text) {
		return "", false
	}
	var pieces []string
	i := 0
	n := len(text)
	for i < n {
		r, size := utf8.DecodeRuneInString(text[i:])
		if r == '"' {
			end := stringEnd(text, i)
			pieces = append(pieces, text[i:end])
			i = end
			continue
		}
		if isSpaceRune(r) {
			j := i
			for j < n {
				rr, ss := utf8.DecodeRuneInString(text[j:])
				if !isSpaceRune(rr) {
					break
				}
				j += ss
			}
			following := ""
			if j < n {
				rr, _ := utf8.DecodeRuneInString(text[j:])
				following = string(rr)
			}
			last := ""
			if len(pieces) > 0 {
				last = pieces[len(pieces)-1]
			}
			if len(pieces) > 0 && !strings.Contains("([{", last) && !strings.Contains(")]}", following) {
				pieces = append(pieces, " ")
			}
			i = j
			continue
		}
		pieces = append(pieces, text[i:i+size])
		i += size
	}
	return strings.Join(pieces, ""), true
}

// normalizeCode removes the whitespace outside string literals.
//
// Two spellings of the same expression differ only in spacing, and C# does
// not read spacing. Inside a string literal the spacing is DATA, so it is
// kept — which is what makes `AssistantMessage.Empty("s1", "m")` and
// `AssistantMessage.Empty("s1","m")` compare equal, and `"a b"` and `"ab"`
// compare different.
func normalizeCode(text string) string {
	var b strings.Builder
	i := 0
	n := len(text)
	for i < n {
		r, size := utf8.DecodeRuneInString(text[i:])
		if r == '"' {
			end := stringEnd(text, i)
			b.WriteString(text[i:end])
			i = end
			continue
		}
		if isSpaceRune(r) {
			i += size
			continue
		}
		b.WriteString(text[i : i+size])
		i += size
	}
	return b.String()
}

// sameValue whether two argument texts stand for the same value.
func sameValue(left string, right string) bool {
	return normalizeCode(left) == normalizeCode(right)
}

// argument is one argument of a constructor call: positional, or `Name: value`.
type argument struct {
	name  string
	value string
	named bool
}

var namedArgRe = regexp.MustCompile(`(?s)^([A-Za-z_]\w*)\s*:\s*(.*)$`)

// splitArguments splits an argument list on its top-level commas.
func splitArguments(text string) []argument {
	var args []argument
	depth := 0
	var current strings.Builder
	i := 0
	n := len(text)
	for i < n {
		c := text[i]
		if c == '"' {
			end := stringEnd(text, i)
			current.WriteString(text[i:end])
			i = end
			continue
		}
		if c == '(' || c == '[' || c == '{' {
			depth++
		} else if c == ')' || c == ']' || c == '}' {
			depth--
		}
		if c == ',' && depth == 0 {
			args = append(args, argument{value: pyStrip(current.String())})
			current.Reset()
			i++
			continue
		}
		current.WriteByte(c)
		i++
	}
	tail := pyStrip(current.String())
	if tail != "" {
		args = append(args, argument{value: tail})
	}
	for idx := range args {
		if m := namedArgRe.FindStringSubmatch(args[idx].value); m != nil {
			args[idx].name = m[1]
			args[idx].value = pyStrip(m[2])
			args[idx].named = true
		}
	}
	return args
}

// matchingParen is the index of the ')' that closes the '(' at openParen, or
// -1.
func matchingParen(text string, openParen int) int {
	depth := 1
	i := openParen + 1
	n := len(text)
	for i < n && depth > 0 {
		c := text[i]
		if c == '"' {
			i = stringEnd(text, i)
			continue
		}
		if c == '(' {
			depth++
		} else if c == ')' {
			depth--
			if depth == 0 {
				return i
			}
		}
		i++
	}
	return -1
}

// call is one `new <Event>(...)` or `new <Message>(...)` in the text.
type call struct {
	typename string
	start    int
	end      int
	args     []argument
}

// callRe finds every construction of a type the codemod knows.
//
// The alternation is the KNOWN_TYPES list in the python original's order,
// and the leftmost-first semantics of the two engines agree on it.
var callRe = regexp.MustCompile(`\bnew\s+(` + strings.Join(knownTypes, "|") + `)\s*\(`)

// findCalls every `new <Event>(...)` and `new <Message>(...)` in the text,
// with its arguments.
func findCalls(text string) []call {
	masked := maskCode(text)
	var calls []call
	for _, idx := range callRe.FindAllStringSubmatchIndex(masked, -1) {
		typename := masked[idx[2]:idx[3]]
		openParen := idx[1] - 1
		close := matchingParen(masked, openParen)
		if close == -1 {
			continue
		}
		calls = append(calls, call{
			typename: typename,
			start:    idx[0],
			end:      close + 1,
			args:     splitArguments(text[openParen+1 : close]),
		})
	}
	return calls
}

// newExprRe matches a `new <Type>(...)` as an argument value.
var newExprRe = regexp.MustCompile(`^new\s+([A-Za-z_]\w*)\s*\(`)

// parseNewExpression parses `new <Type>(...)` as an argument value.
func parseNewExpression(text string) (string, []argument, bool) {
	stripped := pyStrip(text)
	m := newExprRe.FindStringSubmatchIndex(stripped)
	if m == nil {
		return "", nil, false
	}
	openParen := m[1] - 1
	close := matchingParen(stripped, openParen)
	if close == -1 {
		return "", nil, false
	}
	return stripped[m[2]:m[3]], splitArguments(stripped[openParen+1 : close]), true
}

// isEmptyList whether the argument is an empty list of some element type.
func isEmptyList(text string) bool {
	stripped := pyStrip(text)
	if stripped == emptyList {
		return true
	}
	return strings.HasPrefix(stripped, "Array.Empty<") && strings.HasSuffix(stripped, ">()")
}

// isNull whether the argument is a null.
func isNull(text string) bool {
	stripped := pyStrip(text)
	return stripped == "null" || stripped == "null!"
}
