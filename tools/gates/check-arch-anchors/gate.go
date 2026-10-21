// Package archanchors is the compiled port of tools/check-arch-anchors.py.
//
// The gate asks one question: may an architecture rule (a file under
// tests/Harbor.Architecture.Tests/) anchor on a LINE NUMBER?
//
// A `path/File.cs:123` shape is a coordinate, not a name. Every line
// inserted above it moves the coordinate, so the citation drifts the moment
// somebody adds a blank line. The house rule is a SYMBOLIC anchor
// (`Type.Member`), and this gate is what keeps the tree converted.
//
// The exceptions file (tools/arch-anchor-exceptions.txt) is a ratchet in
// BOTH directions: a row the tree no longer produces is a finding, so a
// closed hole cannot stay blessed, and every row carries a reason.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package archanchors

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// archDir is the tree the gate reads, relative to the repository root.
const archDir = "tests/Harbor.Architecture.Tests"

// exceptionsRel is the exceptions file, relative to the repository root.
const exceptionsRel = "tools/arch-anchor-exceptions.txt"

// citationTail is the TAIL of a citation: the extension and the line.
//
// The python original writes the whole citation as one pattern — a path, a
// dot, an extension, a colon and the line — and Go's regexp engine is
// RE2, which is QUADRATIC on that shape: the greedy path loop followed by a
// literal set makes RE2 track every position the path could end at. Measured
// on the architecture tree (5.3 MB), the whole pattern costs 1.09 s and
// this tail costs 64 ms — a factor of seventeen, on the same matches.
//
// The path is found by walking BACK from the tail instead: the path is the
// run of path characters that ends at the dot, and the run cannot hold two
// tails (the colon of one is not a path character), so the tail and the
// run together are the whole citation.
var citationTail = regexp.MustCompile(
	`\.(?:cs|json|yml|yaml|csproj|slnx|props|targets|py|axaml|xaml|editorconfig)` +
		`:(\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)`)

// anchorPrefixByte reports whether b is one of the characters the python
// lookbehind forbids in front of a citation.
func anchorPrefixByte(b byte) bool {
	switch {
	case b >= 'A' && b <= 'Z',
		b >= 'a' && b <= 'z',
		b >= '0' && b <= '9':
		return true
	case b == '_' || b == '.' || b == '/' || b == '-':
		return true
	}
	return false
}

// finding is one line-number anchor in the architecture tree.
type finding struct {
	file   string
	line   int
	anchor string
	text   string
}

// exceptionRow is one row of the exceptions file.
type exceptionRow struct {
	file   string
	anchor string
	reason string
}

// Run is the gate entry point.
func Run(args []string) int {
	minFiles := 0
	exceptions := ""
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
		case "--exceptions":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--exceptions needs a path")
				return gate.Broken
			}
			i++
			exceptions = args[i]
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-arch-anchors [--min-files N] [--exceptions PATH] [--self-test]")
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

	if exceptions == "" {
		exceptions = filepath.Join(root, exceptionsRel)
	}

	files, err := archFiles(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot list the architecture tree:", err)
		return gate.Broken
	}

	if minFiles > 0 && len(files) < minFiles {
		fmt.Printf(
			"FAIL: scanned %d architecture files, floor is %d — "+
				"the file set shrank or the path filter is wrong; lower the floor in "+
				".github/workflows/docs.yml only if the removal was intended.\n",
			len(files), minFiles)
		return gate.Red
	}

	findings := scanFiles(root, files)
	rows, malformed := loadExceptions(exceptions)
	code := report(findings, rows, malformed)
	fmt.Printf(
		"and scanned %d architecture rule file(s) for line-number anchors "+
			"(%d anchor(s) found)\n",
		len(files), len(findings))
	return code
}

// parseInt reads a non-negative integer, the way argparse's type=int does
// for the values the workflow passes.
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

// archFiles lists the tracked C# files of the architecture tree, relative
// to the repository root and sorted.
//
// The INDEX (`git ls-files`) and not a glob: an untracked scratch file must
// not be able to fail the build, and a tracked file must not be able to
// hide from it.
func archFiles(root string) ([]string, error) {
	tracked, err := gate.TrackedFiles(root, ".cs")
	if err != nil {
		return nil, err
	}
	prefix := archDir + "/"
	var out []string
	for _, rel := range tracked {
		if strings.HasPrefix(rel, prefix) {
			out = append(out, rel)
		}
	}
	sort.Strings(out)
	return out, nil
}

// scanFiles reads every file and returns the line-number anchors in it.
func scanFiles(root string, files []string) []finding {
	var out []finding
	for _, rel := range files {
		data, err := os.ReadFile(filepath.Join(root, rel))
		if err != nil {
			continue
		}
		body := string(data)
		for _, c := range findCitations(body) {
			out = append(out, finding{
				file:   rel,
				line:   lineOf(body, c.start),
				anchor: body[c.start:c.end],
				text:   strings.TrimSpace(lineAt(body, c.start)),
			})
		}
	}
	return out
}

// citation is one match of the CITATION grammar.
type citation struct {
	start int
	end   int
}

// findCitations finds every citation in the text.
//
// The python original's pattern begins with a negative lookbehind,
// `(?<![A-Za-z0-9_./\-])`, and RE2 has no lookbehind. The scan below
// reproduces `re.finditer` on that pattern, and the rules are:
//
//   - the path is the run of path characters that ends at the tail's dot;
//   - the match starts at the FIRST character of that run, and that
//     character must be a word character (`[A-Za-z0-9_]`);
//   - the character before the run is not a path character, which is what
//     the lookbehind asks for — and it holds by construction, because the
//     run is maximal.
//
// A run that starts with a non-word character yields NO citation at all,
// and so does a run the scan has already passed: the lookbehind would then
// look at a path character and fail, and every later position in the run
// fails the same way, because the character in front of it is a path
// character too.
func findCitations(text string) []citation {
	var out []citation
	pos := 0
	for _, loc := range citationTail.FindAllStringSubmatchIndex(text, -1) {
		dot := loc[0]
		end := loc[1]
		if dot < pos {
			continue
		}
		start := dot
		for start > 0 && isPathByte(text[start-1]) {
			start--
		}
		if start < pos {
			// The scan has already passed the start of the run: the
			// lookbehind would look at a path character.
			pos = start + 1
			continue
		}
		if !isWordByte(text[start]) {
			pos = start + 1
			continue
		}
		out = append(out, citation{start: start, end: end})
		pos = end
	}
	return out
}

// isPathByte reports whether the byte is one of the characters the citation
// grammar allows in a path.
func isPathByte(b byte) bool {
	switch {
	case b >= 'A' && b <= 'Z',
		b >= 'a' && b <= 'z',
		b >= '0' && b <= '9':
		return true
	case b == '_' || b == '.' || b == '/' || b == '-':
		return true
	}
	return false
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

// lineOf is the 1-based line an offset falls on.
func lineOf(text string, offset int) int {
	return strings.Count(text[:offset], "\n") + 1
}

// lineAt is the whole line an offset falls on.
func lineAt(text string, offset int) string {
	start := strings.LastIndex(text[:offset], "\n") + 1
	end := strings.Index(text[offset:], "\n")
	if end < 0 {
		return text[start:]
	}
	return text[start : offset+end]
}

// loadExceptions reads the exceptions file.
//
// A row without a reason is not a row: it is a finding, and the gate says
// so. A row the tree no longer produces is a finding too.
func loadExceptions(path string) ([]exceptionRow, []string) {
	var rows []exceptionRow
	var malformed []string
	data, err := os.ReadFile(path)
	if err != nil {
		return rows, malformed
	}
	for _, raw := range strings.Split(string(data), "\n") {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.Split(line, "|")
		for i := range parts {
			parts[i] = strings.TrimSpace(parts[i])
		}
		if len(parts) != 3 || parts[0] == "" || parts[1] == "" || parts[2] == "" {
			malformed = append(malformed, line)
			continue
		}
		rows = append(rows, exceptionRow{file: parts[0], anchor: parts[1], reason: parts[2]})
	}
	return rows, malformed
}

// matchException returns the row that excuses the finding, if there is one.
func matchException(f finding, rows []exceptionRow) *exceptionRow {
	for i := range rows {
		if rows[i].file == f.file && rows[i].anchor == f.anchor {
			return &rows[i]
		}
	}
	return nil
}

// report prints the verdict and returns the process exit code.
func report(findings []finding, rows []exceptionRow, malformed []string) int {
	var excused []finding
	var violations []finding
	for _, f := range findings {
		if matchException(f, rows) != nil {
			excused = append(excused, f)
		} else {
			violations = append(violations, f)
		}
	}

	used := map[string]bool{}
	for _, f := range excused {
		row := matchException(f, rows)
		used[row.file+"\x00"+row.anchor] = true
	}

	var stale []exceptionRow
	for _, row := range rows {
		if !used[row.file+"\x00"+row.anchor] {
			stale = append(stale, row)
		}
	}

	if len(violations) > 0 {
		fmt.Printf("=== LINE-NUMBER ANCHORS (%d) ===\n", len(violations))
		for _, f := range violations {
			fmt.Printf("  %s:%d  %s\n", f.file, f.line, f.anchor)
			fmt.Printf("      %s\n", truncate(f.text, 150))
		}
		fmt.Print(
			"\nAn architecture rule may not anchor on a line number: a line is a\n" +
				"coordinate, and every line inserted above it moves the coordinate.\n" +
				"Name the SYMBOL instead — `Type.Member` plus the condition that\n" +
				"makes the site the one being talked about — or search the content\n" +
				"instead of the position. If the number IS the checked fact (a\n" +
				"fixture that names a line on purpose), record it in\n" +
				"tools/arch-anchor-exceptions.txt with a reason.\n")
	}

	if len(stale) > 0 {
		fmt.Printf("=== STALE EXCEPTIONS (%d) ===\n", len(stale))
		for _, row := range stale {
			fmt.Printf("  %s  %s\n", row.file, row.anchor)
			fmt.Printf("      the tree no longer carries this anchor — delete the row in\n")
			fmt.Printf("      the same diff that removed it. Reason on file: %s\n", row.reason)
		}
	}

	if len(malformed) > 0 {
		fmt.Printf("=== MALFORMED EXCEPTION ROWS (%d) ===\n", len(malformed))
		for _, row := range malformed {
			fmt.Printf("  %s\n", row)
			fmt.Printf("      expected  <path> | <anchor> | <reason>\n")
		}
	}

	if len(violations) > 0 || len(stale) > 0 || len(malformed) > 0 {
		fmt.Printf("\nFAIL: %d line-number anchor(s), %d stale row(s).\n",
			len(violations), len(stale))
		return gate.Red
	}

	fmt.Printf(
		"OK: %d line-number anchor(s) in the architecture tree, "+
			"all of them recorded in tools/arch-anchor-exceptions.txt with a reason.\n",
		len(findings))
	return gate.Green
}

// truncate cuts a string to width characters, the way python's `s[:150]`
// does: a CHARACTERS, not bytes, so a multi-byte rune is not cut in half.
func truncate(s string, width int) string {
	runes := []rune(s)
	if len(runes) <= width {
		return s
	}
	return string(runes[:width])
}
