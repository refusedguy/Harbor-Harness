// Package mdlint is the compiled port of tools/md-lint.py.
//
// The gate lints the tracked markdown against the invariants the repository
// claims: encoding (LF, no BOM), heading levels, ATX spacing, fences, and
// exactly one trailing newline.
//
// It is the sibling of check-md-links: that one answers "does this link
// resolve?", this one answers "is this file shaped the way the repo says it
// is?". The two must not overlap.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package mdlint

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/mdgate"
)

// bom is the UTF-8 byte order mark.
var bom = []byte{0xef, 0xbb, 0xbf}

// fenceRun is an opening/closing fence: a run of 3+ backticks or 3+ tildes
// at the line start.
var fenceRun = regexp.MustCompile(`^[\t\n\v\f\r ]{0,3}(` + "`" + `{3,}|~{3,})(.*)$`)

// atx is an ATX heading: 1-6 hashes at column 0. No leading spaces — see
// the KNOWN LIMITATION in the python original's header.
var atx = regexp.MustCompile(`^(#{1,6})(.*)$`)

// The messages the python original printed, one per rule.
const (
	textEncBom  = "file starts with a UTF-8 BOM"
	textEncCrlf = "file contains CR LF (repo is LF-only)"
	textMd010   = "hard tab in indentation"
	textMd001   = "heading level jumps by more than one"
	textMd003   = "setext heading (`===` underline) instead of an ATX `#`"
	textMd018   = "no space after the `#`s of an ATX heading"
	textMd019   = "more than one space after the `#`s of an ATX heading"
	textMd047   = "file must end with exactly one newline"
	textMd048   = "use a ``` fence, not ~~~"
	textFence   = "code fence is never closed — everything after it escapes this linter"
)

// problem is one violation, with the 1-based line it is on.
type problem struct {
	line    int
	code    string
	message string
}

// Run is the gate entry point.
func Run(args []string) int {
	minFiles := 0
	minLines := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-files":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = n
		case "--min-lines":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-lines needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-lines needs a number")
				return gate.Broken
			}
			minLines = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates md-lint [--min-files N] [--min-lines N] [--self-test]")
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

	listed, err := mdgate.TrackedMD(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot list the tracked markdown:", err)
		return gate.Broken
	}
	if len(listed) == 0 {
		fmt.Fprintln(os.Stderr, "no tracked markdown files found")
		return gate.Broken
	}

	findings := map[string][]problem{}
	linesRead := 0
	var unreadable []string

	for _, rel := range listed {
		data, err := os.ReadFile(filepath.Join(root, rel))
		if err != nil {
			unreadable = append(unreadable, rel)
			continue
		}
		linesRead += countByte(data, '\n')
		problems := lintBytes(data)
		if len(problems) > 0 {
			findings[rel] = problems
		}
	}

	total := 0
	for _, problems := range findings {
		total += len(problems)
	}
	fmt.Printf("linted %d lines across %d markdown files: %d violation(s)\n",
		linesRead, len(listed), total)

	for _, rel := range mdgate.SortedKeys(findings) {
		fmt.Printf("\n%s\n", rel)
		for _, p := range findings[rel] {
			fmt.Printf("  L%d: %s %s\n", p.line, p.code, p.message)
		}
	}

	if len(unreadable) > 0 {
		fmt.Printf("\n=== UNREADABLE (%d) ===\n", len(unreadable))
		for _, rel := range mdgate.SortedStrings(unreadable) {
			fmt.Printf("  %s\n", rel)
		}
	}

	guard := mdgate.RequireNonVacuous(
		"md-lint", len(listed)-len(unreadable), linesRead, "lines", minFiles, minLines)
	if len(guard) > 0 {
		fmt.Print("\n=== VACUITY GUARD ===\n")
		for _, problem := range guard {
			fmt.Printf("  %s\n", problem)
		}
	}

	if len(guard) > 0 || len(unreadable) > 0 || total > 0 {
		fmt.Printf("\nFAIL: %d violation(s), %d unreadable, %d vacuity-guard problem(s).\n",
			total, len(unreadable), len(guard))
		return gate.Red
	}
	fmt.Print("\nOK: no markdown violations.\n")
	return gate.Green
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

// countByte counts the occurrences of a byte in the data.
func countByte(data []byte, b byte) int {
	n := 0
	for _, c := range data {
		if c == b {
			n++
		}
	}
	return n
}

// lintBytes returns the violations of one document.
func lintBytes(data []byte) []problem {
	var out []problem

	if len(data) >= 3 && data[0] == bom[0] && data[1] == bom[1] && data[2] == bom[2] {
		out = append(out, problem{1, "ENC-BOM", textEncBom})
	}
	if i := indexOf(data, []byte("\r\n")); i >= 0 {
		out = append(out, problem{countByte(data[:i], '\n') + 1, "ENC-CRLF", textEncCrlf})
	}

	if len(data) == 0 {
		return []problem{{1, "MD047", "empty file — delete it or give it a heading"}}
	}

	if data[len(data)-1] != '\n' {
		out = append(out, problem{countByte(data, '\n') + 1, "MD047", textMd047 + " (no newline at EOF)"})
	} else if len(data) >= 2 && data[len(data)-2] == '\n' {
		out = append(out, problem{countByte(data, '\n'), "MD047", textMd047 + " (blank line at EOF)"})
	}

	lines := strings.Split(decodeLossy(data), "\n")
	if len(lines) > 0 && lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}

	fenceChar := ""
	fenceLen := 0
	fenceLine := 0
	lastLevel := 0
	haveLevel := false

	for idx, raw := range lines {
		lineNo := idx + 1
		fence := fenceRun.FindStringSubmatch(raw)

		if fenceChar != "" {
			// A closing fence is the same character, at least as long, and
			// carries nothing else. That is what lets a ``` block contain a
			// ```` block.
			if fence != nil &&
				fence[1][0] == fenceChar[0] &&
				len(fence[1]) >= fenceLen &&
				strings.TrimSpace(fence[2]) == "" {
				fenceChar = ""
			}
			continue
		}

		if fence != nil {
			fenceChar = fence[1][:1]
			fenceLen = len(fence[1])
			fenceLine = lineNo
			if fenceChar == "~" {
				out = append(out, problem{lineNo, "MD048", textMd048})
			}
			continue
		}

		if strings.Contains(raw, "\t") {
			out = append(out, problem{lineNo, "MD010", textMd010})
		}

		// Setext, unambiguous form only: `---` is also a thematic break and
		// a front-matter delimiter, so underlining a heading with dashes is
		// not checked.
		trimmed := strings.TrimSpace(raw)
		if trimmed != "" && isAllEquals(trimmed) {
			if lineNo > 1 && strings.TrimSpace(lines[idx-1]) != "" {
				out = append(out, problem{lineNo, "MD003", textMd003})
			}
			continue
		}

		heading := atx.FindStringSubmatch(raw)
		if heading == nil {
			continue
		}

		hashes, rest := heading[1], heading[2]
		if rest == "" || (rest[0] != ' ' && rest[0] != '\t') {
			out = append(out, problem{lineNo, "MD018", textMd018})
		} else if strings.HasPrefix(rest, "  ") {
			out = append(out, problem{lineNo, "MD019", textMd019})
		}

		level := len(hashes)
		if haveLevel && level > lastLevel+1 {
			out = append(out, problem{lineNo, "MD001",
				fmt.Sprintf("%s (h%d -> h%d)", textMd001, lastLevel, level)})
		}
		lastLevel = level
		haveLevel = true
	}

	if fenceChar != "" {
		out = append(out, problem{fenceLine, "FENCE-UNCLOSED", textFence})
	}

	return out
}

// indexOf returns the index of the first occurrence of the needle.
func indexOf(haystack []byte, needle []byte) int {
	if len(needle) == 0 {
		return 0
	}
	for i := 0; i+len(needle) <= len(haystack); i++ {
		match := true
		for j := range needle {
			if haystack[i+j] != needle[j] {
				match = false
				break
			}
		}
		if match {
			return i
		}
	}
	return -1
}

// decodeLossy decodes the bytes as UTF-8, replacing what is not valid.
//
// The python original decoded with `errors="replace"`, so an invalid byte
// becomes U+FFFD instead of raising.
func decodeLossy(data []byte) string {
	return strings.ToValidUTF8(string(data), "�")
}

// isAllEquals reports whether the text is made of "=" characters only.
func isAllEquals(text string) bool {
	for _, r := range text {
		if r != '=' {
			return false
		}
	}
	return true
}

// sortedProblems is used by the self-test to keep the output stable.
func sortedProblems(problems []problem) []problem {
	out := append([]problem{}, problems...)
	sort.SliceStable(out, func(i, j int) bool { return out[i].line < out[j].line })
	return out
}
