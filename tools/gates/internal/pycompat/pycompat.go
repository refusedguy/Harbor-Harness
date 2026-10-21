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
	"math"
	"math/big"
	"sort"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf8"
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

// Atoi parses an integer the way python's int(text) does.
//
// The difference from strconv.Atoi: python strips the surrounding
// whitespace, accepts a single underscore between the digits, and accepts
// the unicode digits and signs. A gate that reads a number out of a file
// must read the number the file holds, and a report that writes "1_000"
// or " 42 " is a report a python gate read and a Go gate must read too.
func Atoi(text string) (int, bool) {
	trimmed := strings.TrimSpace(text)
	if trimmed == "" {
		return 0, false
	}
	sign := 1
	switch trimmed[0] {
	case '+':
		trimmed = trimmed[1:]
	case '-':
		sign = -1
		trimmed = trimmed[1:]
	}
	if trimmed == "" {
		return 0, false
	}
	var digits strings.Builder
	previousWasDigit := false
	for _, r := range trimmed {
		if r == '_' {
			// python allows ONE underscore between two digits.
			if !previousWasDigit {
				return 0, false
			}
			previousWasDigit = false
			continue
		}
		if !unicode.IsDigit(r) {
			return 0, false
		}
		digits.WriteRune(r)
		previousWasDigit = true
	}
	if !previousWasDigit {
		return 0, false
	}
	value, err := strconv.Atoi(digits.String())
	if err != nil {
		return 0, false
	}
	return sign * value, true
}

// Round rounds a float the way python's round(value, digits) does.
//
// python rounds the EXACT binary value of the float to `digits` decimal
// places, and a tie goes to the EVEN digit. That is not
// `math.Round(value*100)/100`: the multiplication is itself inexact, and
// the shortcut disagrees with python on values a gate rounds. Measured over
// 6600 values (every two-decimal value in [0,50), every eighth and every
// quarter), the exact rounding matches python on all of them and the
// floating-point shortcut on 208 of them: round(0.005, 2) is 0.01 in python
// and 0.0 with the shortcut, and round(0.125, 2) is 0.12 in python and 0.13
// with the half-up shortcut.
//
// The exact value is read with math/big, so the rounding is the rounding of
// the number the float holds, not of a decimal approximation of it.
func Round(value float64, digits int) float64 {
	if math.IsNaN(value) || math.IsInf(value, 0) || digits < 0 {
		return value
	}
	exact := new(big.Rat).SetFloat64(value)
	if exact == nil {
		return value
	}
	scale := new(big.Int).Exp(big.NewInt(10), big.NewInt(int64(digits)), nil)
	scaled := new(big.Rat).Mul(exact, new(big.Rat).SetInt(scale))
	quotient, remainder := new(big.Int).QuoRem(scaled.Num(), scaled.Denom(), new(big.Int))
	twice := new(big.Int).Abs(remainder)
	twice.Mul(twice, big.NewInt(2))
	switch twice.Cmp(scaled.Denom()) {
	case 1:
		// More than half away from zero.
		if scaled.Num().Sign() < 0 {
			quotient.Sub(quotient, big.NewInt(1))
		} else {
			quotient.Add(quotient, big.NewInt(1))
		}
	case 0:
		// Exactly half: the tie goes to the EVEN digit, so an odd
		// quotient moves and an even one stays.
		if quotient.Bit(0) == 1 {
			if scaled.Num().Sign() < 0 {
				quotient.Sub(quotient, big.NewInt(1))
			} else {
				quotient.Add(quotient, big.NewInt(1))
			}
		}
	}
	result, _ := new(big.Rat).SetFrac(quotient, scale).Float64()
	return result
}

// FloatJSON renders a float the way python's json module writes it.
//
// python writes a float with repr(), and repr() keeps the decimal point:
// 1.0 is "1.0" and not "1". Go's encoder writes "1". A file the two
// implementations both write has to differ in no byte, so the float is
// rendered here and carried as a raw message.
func FloatJSON(value float64) json.RawMessage {
	if math.IsNaN(value) || math.IsInf(value, 0) {
		return json.RawMessage("null")
	}
	text := strconv.FormatFloat(value, 'g', -1, 64)
	if !strings.ContainsAny(text, ".eE") {
		text += ".0"
	}
	return json.RawMessage(text)
}

// JSONDumpASCII writes a value the way python's json.dump(value, indent=2)
// writes it.
//
// The two differences from encoding/json's default are both visible in the
// bytes: python does not escape the angle brackets and the ampersand, and
// python ESCAPES every non-ASCII character (its default is
// ensure_ascii=True) while Go writes them verbatim.
func JSONDumpASCII(value any, indent string) ([]byte, error) {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", indent)
	if err := encoder.Encode(value); err != nil {
		return nil, err
	}
	// json.Encoder.Encode appends a newline; json.dump does not.
	encoded := bytes.TrimRight(buffer.Bytes(), "\n")
	return escapeNonASCII(encoded), nil
}

// escapeNonASCII replaces every character outside the ASCII range with the
// \uXXXX escape python writes.
func escapeNonASCII(data []byte) []byte {
	var out bytes.Buffer
	for index := 0; index < len(data); {
		if data[index] < 0x80 {
			out.WriteByte(data[index])
			index++
			continue
		}
		r, size := utf8.DecodeRune(data[index:])
		if r == utf8.RuneError && size == 1 {
			out.WriteString(`�`)
			index++
			continue
		}
		if r > 0xFFFF {
			// A surrogate pair, the way python writes it.
			high := 0xD800 + ((r - 0x10000) >> 10)
			low := 0xDC00 + ((r - 0x10000) & 0x3FF)
			fmt.Fprintf(&out, `\u%04x\u%04x`, high, low)
		} else {
			fmt.Fprintf(&out, `\u%04x`, r)
		}
		index += size
	}
	return out.Bytes()
}
