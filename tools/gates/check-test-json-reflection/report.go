package jsonreflection

// report.go -- the census, the counter, and the two ways of printing them.
//
// THE COUNTER (#2122)
//
// One rule, and every path of this gate counts the same way:
//
//	An exception is USED when a reflection finding matched THAT exception:
//	the finding's path is the exception's path, and the exception's text
//	appears in the call the finding names. An exception that no finding
//	matched is STALE, and a stale exception fails the gate.
//
// The rule is per EXCEPTION, not per file: two exceptions for two different
// calls in one file are two permissions, and closing one of them says
// nothing about the other.
//
// The python original had TWO counters and did not say so. Its plain path --
// the one CI runs -- marked every exception used as soon as one of them was
// used, because its set comprehension named the finding but never the
// exception it was adding. Its report path counted per exception. On a tree
// where one exception was used and another was stale, the plain path said
// green and the report path said red: one tree, two verdicts, and nothing in
// the repository said which was right.
//
// The report path's rule is the one the exceptions file already states -- "a
// row the tree no longer produces is RED" -- so it is the rule here, and the
// plain path, `--report` and `--json-out` answer with the same verdict on
// the same tree.

import (
	"fmt"
	"io"
	"strings"
)

// partition splits the findings into the violations and the exceptions that
// covered one, and returns the exceptions that covered nothing.
//
// This is THE counter. Every path of the gate goes through it.
func partition(findings []finding, exceptions []exception) ([]finding, []exception) {
	var violations []finding
	used := map[int]bool{}
	for _, found := range findings {
		if found.sourceGenerated {
			continue
		}
		cover := matchException(found, exceptions)
		if cover != nil {
			used[cover.line] = true
			continue
		}
		violations = append(violations, found)
	}
	var stale []exception
	for _, item := range exceptions {
		if !used[item.line] {
			stale = append(stale, item)
		}
	}
	return violations, stale
}

// census is the shape the JSON path prints.
type census struct {
	calls           int
	sourceGenerated int
	reflection      int
	violations      []finding
	stale           []exception
}

// countCensus fills the census from the findings and the partition.
func countCensus(findings []finding, violations []finding, stale []exception) census {
	sourceGenerated := 0
	for _, found := range findings {
		if found.sourceGenerated {
			sourceGenerated++
		}
	}
	return census{
		calls:           len(findings),
		sourceGenerated: sourceGenerated,
		reflection:      len(findings) - sourceGenerated,
		violations:      violations,
		stale:           stale,
	}
}

// report prints the census and every group. It returns 1 when there is
// something to report: a violation or a stale exception.
//
// The JSON shape fails on the same things the text shape fails on. The
// python original counted the stale exceptions in the census and left them
// out of the verdict, which is the same split the counter had: the census
// knew, the verdict did not.
func report(w io.Writer, findings []finding, exceptions []exception, asJSON bool) int {
	violations, stale := partition(findings, exceptions)
	shape := countCensus(findings, violations, stale)

	if asJSON {
		// The newline is part of the contract: the python original prints
		// the census with print(), and the byte comparison is against
		// those bytes.
		fmt.Fprintln(w, censusDocument(shape))
		return verdict(shape)
	}

	fmt.Fprintln(w, "reflection-JSON census (tests/, excluding tests/Harbor.Architecture.Tests)")
	fmt.Fprintf(w, "  JsonSerializer calls      : %d\n", shape.calls)
	fmt.Fprintf(w, "  source-generated          : %d\n", shape.sourceGenerated)
	fmt.Fprintf(w, "  reflection (grandfathered): %d\n", shape.calls-shape.sourceGenerated-len(shape.violations))
	fmt.Fprintf(w, "  reflection (VIOLATION)    : %d\n", len(shape.violations))
	for _, violation := range shape.violations {
		fmt.Fprintf(w, "    %s:%d  %s\n", violation.path, violation.line, truncate(violation.call, 110))
	}
	for _, item := range shape.stale {
		fmt.Fprintf(w, "  STALE EXCEPTION %s:%d — %s\n", item.path, item.line, item.reason)
	}
	return verdict(shape)
}

// verdict is the exit code of a census: red when there is a violation or a
// stale exception.
func verdict(shape census) int {
	if len(shape.violations) > 0 || len(shape.stale) > 0 {
		return 1
	}
	return 0
}

// gateVerdict is the plain path: it says what is wrong, and returns the
// exit code.
//
// It is a function of the findings and the exceptions, not of the command
// line, so the self-test can run it on a planted tree and compare its
// verdict with the report path's.
func gateVerdict(w io.Writer, findings []finding, exceptions []exception) int {
	violations, stale := partition(findings, exceptions)
	if len(violations) > 0 {
		fmt.Fprintf(w,
			"reflection JSON in tests/: %d call(s) do not name a source-generated type info.\n",
			len(violations))
		for _, violation := range violations {
			fmt.Fprintf(w, "  %s:%d  %s\n", violation.path, violation.line, truncate(violation.call, 110))
		}
		fmt.Fprintf(w,
			"\nPass the type info from a source-generated context (JsonSerializer.Serialize(value, SomeContext.Default.SomeType)), or add a documented exception to %s.\n",
			exceptionsFile)
	}
	if len(stale) > 0 {
		fmt.Fprintf(w,
			"reflection JSON in tests/: %d exception(s) no longer apply — delete them.\n",
			len(stale))
		for _, item := range stale {
			fmt.Fprintf(w, "  %s (%s:%d)\n", item.path, exceptionsFile, item.line)
		}
	}
	if len(violations) > 0 || len(stale) > 0 {
		return 1
	}
	return 0
}

// censusDocument renders the census the way python's json.dumps(indent=2)
// does.
//
// Go's encoding/json is NOT used here: python escapes every character
// outside the printable ASCII range, and Go writes UTF-8. The reasons of the
// stale exceptions carry an em dash, so the two encoders would disagree on
// the bytes.
func censusDocument(shape census) string {
	var b strings.Builder
	b.WriteString("{\n")
	fmt.Fprintf(&b, "  \"calls\": %d,\n", shape.calls)
	fmt.Fprintf(&b, "  \"source_generated\": %d,\n", shape.sourceGenerated)
	fmt.Fprintf(&b, "  \"reflection\": %d,\n", shape.reflection)
	b.WriteString("  \"violations\": ")
	if len(shape.violations) == 0 {
		b.WriteString("[],\n")
	} else {
		b.WriteString("[\n")
		for i, violation := range shape.violations {
			b.WriteString("    {\n")
			fmt.Fprintf(&b, "      \"path\": %s,\n", jsonString(violation.path))
			fmt.Fprintf(&b, "      \"line\": %d,\n", violation.line)
			fmt.Fprintf(&b, "      \"call\": %s\n", jsonString(violation.call))
			b.WriteString("    }")
			if i < len(shape.violations)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  ],\n")
	}
	b.WriteString("  \"stale_exceptions\": ")
	if len(shape.stale) == 0 {
		b.WriteString("[]\n")
	} else {
		b.WriteString("[\n")
		for i, item := range shape.stale {
			b.WriteString("    {\n")
			fmt.Fprintf(&b, "      \"path\": %s,\n", jsonString(item.path))
			fmt.Fprintf(&b, "      \"line\": %d,\n", item.line)
			fmt.Fprintf(&b, "      \"reason\": %s\n", jsonString(item.reason))
			b.WriteString("    }")
			if i < len(shape.stale)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  ]\n")
	}
	b.WriteString("}")
	return b.String()
}

// jsonString escapes a string the way python's json.dumps does.
//
// The rule is python's: the quote and the backslash, the six control
// characters python has a short form for, and every character outside the
// printable ASCII range as a \\uXXXX escape -- a surrogate pair for a
// character outside the basic plane.
func jsonString(text string) string {
	var b strings.Builder
	b.WriteByte('"')
	for _, r := range text {
		switch r {
		case '"':
			b.WriteString("\\\"")
		case '\\':
			b.WriteString("\\\\")
		case '\n':
			b.WriteString("\\n")
		case '\r':
			b.WriteString("\\r")
		case '\t':
			b.WriteString("\\t")
		case '\b':
			b.WriteString("\\b")
		case '\f':
			b.WriteString("\\f")
		default:
			if r < 0x20 || r > 0x7e {
				if r > 0xffff {
					rest := r - 0x10000
					fmt.Fprintf(&b, "\\u%04x\\u%04x", 0xd800+(rest>>10), 0xdc00+(rest&0x3ff))
				} else {
					fmt.Fprintf(&b, "\\u%04x", r)
				}
				continue
			}
			b.WriteRune(r)
		}
	}
	b.WriteByte('"')
	return b.String()
}

// truncate cuts a text to a length, the way python's slice does.
//
// The length is in CHARACTERS, not bytes: the python original slices a str,
// and a call whose arguments carry a non-ASCII character would be cut in
// the middle of it by a byte slice.
func truncate(text string, length int) string {
	runes := []rune(text)
	if len(runes) <= length {
		return text
	}
	return string(runes[:length])
}
