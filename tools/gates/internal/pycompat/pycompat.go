// Package pycompat holds the few places where Go and Python disagree about
// the same operation, and where a port has to answer the way the python
// original answered.
//
// The rule this package exists for: a gate's OUTPUT is a contract with the
// workflows that read it and with the self-tests that compare it. When the
// python original and the Go gate disagree about a line ending, a sort order
// or a string's repr, the gate's verdict changes, and the disagreement shows
// up as a mysterious CI failure instead of as a porting bug. So the two
// behaviours are written down HERE, once, and every gate uses them.
package pycompat

import (
	"bytes"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
	"unicode"
)

// SplitNL splits on "\n" and KEEPS a trailing empty element, exactly like
// python's str.split("\n").
//
// The difference from SplitLines is the point: python's splitlines() drops
// the empty string after a final newline, and split("\n") keeps it. A gate
// that joins lines back together must use the same one the original used or
// the file it writes differs by a newline.
func SplitNL(s string) []string {
	return strings.Split(s, "\n")
}

// SplitLines splits like python's str.splitlines(): no trailing empty
// element, and the other line boundaries python recognises (\r, \v, \f,
// \x1c-\x1e, \x85, U+2028, U+2029) are honoured.
//
// python's splitlines() on "" returns [], not [""]. That matters: a file that
// is empty has no lines, and a gate that counts them must count zero.
func SplitLines(s string) []string {
	if s == "" {
		return nil
	}
	var out []string
	var start int
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c == '\n' {
			out = append(out, s[start:i])
			start = i + 1
			continue
		}
		if c == '\r' {
			out = append(out, s[start:i])
			if i+1 < len(s) && s[i+1] == '\n' {
				i++
			}
			start = i + 1
			continue
		}
		if c == '\v' || c == '\f' {
			out = append(out, s[start:i])
			start = i + 1
			continue
		}
		if c == 0x1c || c == 0x1d || c == 0x1e {
			out = append(out, s[start:i])
			start = i + 1
			continue
		}
		if c == 0x85 {
			out = append(out, s[start:i])
			start = i + 1
			continue
		}
		// U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR.
		if c == 0xe2 && i+2 < len(s) && s[i+1] == 0x80 && (s[i+2] == 0xa8 || s[i+2] == 0xa9) {
			out = append(out, s[start:i])
			i += 2
			start = i + 1
			continue
		}
	}
	out = append(out, s[start:])
	return out
}

// PathLess compares two paths the way python's pathlib compares them: by
// their PARTS, not by their text.
//
// The difference is not academic. "src/Harbor.A/Harbor.A.csproj" and
// "src/Harbor.A.B/Harbor.A.B.csproj" sort one way by text and the other way
// by parts, and the text order is the one python does NOT use. A gate that
// enumerates the packable projects in the wrong order reports the wrong
// first problem, and the self-test that compares the two implementations
// says they differ.
func PathLess(a, b string) bool {
	return pathPartsCompare(a, b) < 0
}

// SortPaths sorts paths the way python's sorted() sorts pathlib.Paths.
func SortPaths(paths []string) []string {
	out := make([]string, len(paths))
	copy(out, paths)
	sort.SliceStable(out, func(i, j int) bool { return PathLess(out[i], out[j]) })
	return out
}

func pathPartsCompare(a, b string) int {
	ap := splitPathParts(a)
	bp := splitPathParts(b)
	for i := 0; i < len(ap) && i < len(bp); i++ {
		if ap[i] != bp[i] {
			if ap[i] < bp[i] {
				return -1
			}
			return 1
		}
	}
	switch {
	case len(ap) < len(bp):
		return -1
	case len(ap) > len(bp):
		return 1
	default:
		return 0
	}
}

func splitPathParts(p string) []string {
	parts := strings.Split(p, "/")
	out := parts[:0]
	for _, part := range parts {
		if part != "" {
			out = append(out, part)
		}
	}
	return out
}

// Repr renders a string the way python's repr() does.
//
// The gate messages quote values with !r, and the self-tests compare those
// messages. python's repr uses single quotes, switches to double quotes when
// the string holds a single quote and no double quote, and escapes the
// control characters.
func Repr(s string) string {
	quote := byte('\'')
	if strings.Contains(s, "'") && !strings.Contains(s, "\"") {
		quote = '"'
	}
	var b strings.Builder
	b.WriteByte(quote)
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch {
		case c == '\\':
			b.WriteString(`\\`)
		case c == quote:
			b.WriteByte('\\')
			b.WriteByte(c)
		case c == '\n':
			b.WriteString(`\n`)
		case c == '\r':
			b.WriteString(`\r`)
		case c == '\t':
			b.WriteString(`\t`)
		case c < 0x20 || c == 0x7f:
			fmt.Fprintf(&b, `\x%02x`, c)
		default:
			b.WriteByte(c)
		}
	}
	b.WriteByte(quote)
	return b.String()
}

// ListRepr renders a list of strings the way python's repr() of a list does.
func ListRepr(items []string) string {
	if len(items) == 0 {
		return "[]"
	}
	parts := make([]string, 0, len(items))
	for _, item := range items {
		parts = append(parts, Repr(item))
	}
	return "[" + strings.Join(parts, ", ") + "]"
}

// JSONIndent renders a value the way python's json.dumps(value, indent=2,
// ensure_ascii=False) does.
//
// The difference from encoding/json's default is the HTML escaping: python
// writes <, > and & verbatim, and a document that python wrote must not gain
// three characters per angle bracket when it is regenerated.
func JSONIndent(v any) ([]byte, error) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	enc.SetIndent("", "  ")
	if err := enc.Encode(v); err != nil {
		return nil, err
	}
	// json.Encoder.Encode appends a newline; json.dumps does not.
	return bytes.TrimRight(buf.Bytes(), "\n"), nil
}

// IsPrintable reports whether a rune is one python's repr keeps verbatim.
//
// Everything above the ASCII range is kept: python 3 writes non-ASCII
// characters as themselves, and the repository's documents are UTF-8.
func IsPrintable(r rune) bool {
	if r == '\t' {
		return true
	}
	return unicode.IsPrint(r)
}
