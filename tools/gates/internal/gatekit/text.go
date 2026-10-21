// Package gatekit holds the helpers the ported gates share.
//
// The python originals each had their own copy of a handful of small
// functions: a text flattener for a table cell, a .NET TimeSpan reader, a
// TRX reader. The ports here share one copy of each, so a fix lands in one
// place instead of in fourteen.
package gatekit

import (
	"fmt"
	"strconv"
	"strings"
	"time"
)

// Flatten collapses every run of whitespace into a single space.
//
// The python originals wrote `" ".join(text.split())`: a reason a test
// declared, or a measurement a test printed, is one line in a table cell, and
// a newline in the middle of a markdown table row breaks the table.
func Flatten(text string) string {
	return strings.Join(strings.Fields(text), " ")
}

// EscapePipes escapes the raw pipes of a table cell.
//
// A markdown table row is split on "|", so a measurement that prints one
// ("alloc: 42 B | p99 = 1.5 ms") splits the row in two without this.
func EscapePipes(text string) string {
	return strings.ReplaceAll(text, "|", "\\|")
}

// Cell renders one table cell: flattened, pipe-escaped, truncated, and never
// empty.
//
// The truncation counts CHARACTERS, not bytes: the ellipsis is one character
// and the text before it is a reason a human reads.
func Cell(text string, maxCell int, empty string) string {
	flat := EscapePipes(Flatten(text))
	runes := []rune(flat)
	if len(runes) > maxCell {
		runes = append(runes[:maxCell-1:maxCell-1], []rune("…")...)
		flat = strings.TrimRight(string(runes), " \t\n\r\v\f") + "…"
	}
	if flat == "" {
		return empty
	}
	return flat
}

// DurationSeconds reads a .NET TimeSpan ("hh:mm:ss.fffffff") as seconds.
//
// The second return value is false when the attribute is absent or
// unparsable: a missing duration is a fact about the file, not an error, and
// a reporter must never fail a build over it.
func DurationSeconds(raw string) (float64, bool) {
	if raw == "" {
		return 0, false
	}
	parts := strings.Split(raw, ":")
	if len(parts) != 3 {
		return 0, false
	}
	hours, err := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
	if err != nil {
		return 0, false
	}
	minutes, err := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
	if err != nil {
		return 0, false
	}
	seconds, err := strconv.ParseFloat(strings.TrimSpace(parts[2]), 64)
	if err != nil {
		return 0, false
	}
	return hours*3600.0 + minutes*60.0 + seconds, true
}

// isoLayouts are the shapes an ISO-8601 stamp can have here.
//
// The TRX writer emits "2026-10-20T12:00:00.0000000+00:00" — seven
// fractional digits — so the fraction is trimmed to what the layouts below
// read, and "Z" is spelled as an offset.
var isoLayouts = []string{
	"2006-01-02T15:04:05.999999999Z07:00",
	"2006-01-02T15:04:05Z07:00",
	"2006-01-02T15:04:05.999999999Z0700",
	"2006-01-02T15:04:05Z0700",
	"2006-01-02T15:04:05.999999999",
	"2006-01-02T15:04:05",
	"2006-01-02 15:04:05.999999999Z07:00",
	"2006-01-02 15:04:05Z07:00",
	"2006-01-02 15:04:05",
}

// ParseTimestamp reads an ISO-8601 stamp.
//
// The second return value is false when the stamp cannot be read, which is
// the same fact the python originals reported as a None.
func ParseTimestamp(raw string) (time.Time, bool) {
	if raw == "" {
		return time.Time{}, false
	}
	text := strings.TrimSpace(raw)
	if strings.HasSuffix(text, "Z") {
		text = text[:len(text)-1] + "+00:00"
	}
	// Trim the fraction to nanosecond resolution: a longer fraction is a
	// fact about the writer, not about the interval.
	if dot := strings.IndexByte(text, '.'); dot >= 0 {
		head := text[:dot]
		tail := text[dot+1:]
		digits := 0
		for digits < len(tail) && tail[digits] >= '0' && tail[digits] <= '9' {
			digits++
		}
		fraction := tail[:digits]
		if len(fraction) > 9 {
			fraction = fraction[:9]
		}
		text = head + "." + fraction + tail[digits:]
	}
	for _, layout := range isoLayouts {
		if stamp, err := time.Parse(layout, text); err == nil {
			return stamp, true
		}
	}
	return time.Time{}, false
}

// SplitLines splits a text into lines the way python's str.splitlines() does.
//
// The difference that matters: a CRLF file yields lines without the CR, and
// a lone CR ends a line too. The repository is LF-only, but a fixture or a
// file from elsewhere need not be.
func SplitLines(text string) []string {
	if text == "" {
		return nil
	}
	var lines []string
	start := 0
	for index := 0; index < len(text); index++ {
		switch text[index] {
		case '\n':
			lines = append(lines, text[start:index])
			start = index + 1
		case '\r':
			lines = append(lines, text[start:index])
			if index+1 < len(text) && text[index+1] == '\n' {
				index++
			}
			start = index + 1
		}
	}
	if start < len(text) {
		lines = append(lines, text[start:])
	}
	return lines
}

// PyRepr renders a string the way python's repr() does.
//
// The python originals printed a few arguments with {spec!r}, and the
// workflows read those lines: the quote style is part of the text.
func PyRepr(text string) string {
	quote := byte('\'')
	if strings.Contains(text, "'") && !strings.Contains(text, `"`) {
		quote = '"'
	}
	var out strings.Builder
	out.WriteByte(quote)
	for _, r := range text {
		switch r {
		case '\\':
			out.WriteString(`\\`)
		case '\n':
			out.WriteString(`\n`)
		case '\r':
			out.WriteString(`\r`)
		case '\t':
			out.WriteString(`\t`)
		case rune(quote):
			out.WriteByte('\\')
			out.WriteRune(r)
		default:
			if r < 0x20 || r == 0x7f {
				out.WriteString(fmt.Sprintf(`\x%02x`, r))
				continue
			}
			out.WriteRune(r)
		}
	}
	out.WriteByte(quote)
	return out.String()
}

// SplitExt drops the extension of a file name.
//
// The python originals used os.path.splitext(os.path.basename(path))[0] to
// name a project after its TRX file; this is the same thing.
func SplitExt(name string) string {
	if dot := strings.LastIndexByte(name, '.'); dot > 0 {
		return name[:dot]
	}
	return name
}

// Basename is the last element of a path.
func Basename(path string) string {
	path = strings.TrimRight(path, "/")
	if path == "" {
		return ""
	}
	if idx := strings.LastIndexByte(path, '/'); idx >= 0 {
		return path[idx+1:]
	}
	return path
}

// Dirname is everything before the last element of a path.
func Dirname(path string) string {
	path = strings.TrimRight(path, "/")
	if idx := strings.LastIndexByte(path, '/'); idx >= 0 {
		return path[:idx]
	}
	return ""
}
