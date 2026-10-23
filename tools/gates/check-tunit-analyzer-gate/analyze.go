package tunitanalyzer

// analyze.go -- the analysis: one build log, one set of problems.
//
// The parser is a REGEX, and the regex is the python original's:
//
//	(?P<file>[^\r\n(]*?)\((?P<line>\d+),(?P<col>\d+)\):\s+
//	(?P<severity>error|warning)\s+
//	(?P<id>TUnit[A-Za-z]*\d+):\s*(?P<message>.*?)\s*$
//
// The file group is NOT anchored to the start of the line: a build log can
// carry a prefix (the runner's `##[error]` marker, a step name with
// parentheses in it), and the diagnostic is what matters, not what is in
// front of it. The file is normalised to its last path segment below.
//
// Go's regexp is RE2 with Perl's leftmost-first semantics, and it does have
// the lazy quantifier, so the pattern is the same pattern.

import (
	"fmt"
	"path"
	"regexp"
	"strconv"
	"strings"
)

// diagnosticRe is the compiler diagnostic line, e.g.
//
//	/repo/tests/TUnitAnalyzerCanary/PlantedOffender.cs(31,9): error TUnitAssertions0005: Assert.That(...) should not be used with a constant value [/repo/...csproj]
var diagnosticRe = regexp.MustCompile(`(?m)(?P<file>[^\r\n(]*?)\((?P<line>\d+),(?P<col>\d+)\):\s+(?P<severity>error|warning)\s+(?P<id>TUnit[A-Za-z]*\d+):\s*(?P<message>.*?)\s*$`)

// diagnostic is one reported diagnostic.
type diagnostic struct {
	name    string
	line    int
	column  string
	id      string
	message string
}

// Analyse returns the problems with one build log. An empty list means the
// control held.
func Analyse(log string, plantedLine int, waivedLine int) []string {
	var problems []string

	// MSBuild prints the SAME diagnostic twice: once as it happens (prefixed
	// with the runner's `##[error]` marker) and once in the error summary.
	// They are one diagnostic, so they are counted once — the dedup key is
	// the file, the position and the id, which is what makes the two the
	// same finding.
	seen := map[string]bool{}
	var diagnostics []diagnostic
	for _, match := range diagnosticRe.FindAllStringSubmatch(log, -1) {
		if match[5] != "TUnitAssertions0005" {
			continue
		}
		file := match[1]
		if index := strings.LastIndex(file, "##[error]"); index >= 0 {
			file = file[index+len("##[error]"):]
		}
		file = strings.TrimSpace(file)
		name := path.Base(strings.ReplaceAll(file, "\\", "/"))
		key := name + "\x00" + match[2] + "\x00" + match[3] + "\x00" + match[5]
		if seen[key] {
			continue
		}
		seen[key] = true
		line, err := strconv.Atoi(match[2])
		if err != nil {
			continue
		}
		diagnostics = append(diagnostics, diagnostic{
			name:    name,
			line:    line,
			column:  match[3],
			id:      match[5],
			message: match[6],
		})
	}

	if len(diagnostics) == 0 {
		problems = append(problems,
			"the analyzer reported no TUnitAssertions0005 at all: either the "+
				"analyzer stopped shipping, or the promotion to an error stopped "+
				"applying, or the planted offender stopped being one. The gate is "+
				"not armed.")
		return problems
	}

	for _, found := range diagnostics {
		switch {
		case found.name != canarySourceName:
			problems = append(problems, fmt.Sprintf(
				"a TUnitAssertions0005 was reported in %s, which is not the canary's source (%s). The gate is measuring something else.",
				found.name, canarySourceName))
		case found.line == waivedLine:
			problems = append(problems, fmt.Sprintf(
				"the waived site (line %d) was reported: the suppression did not cover its own site and nothing else.",
				waivedLine))
		case found.line != plantedLine:
			problems = append(problems, fmt.Sprintf(
				"a TUnitAssertions0005 was reported at line %d, which is neither the planted offender (line %d) nor the waived one (line %d).",
				found.line, plantedLine, waivedLine))
		}
	}

	if len(diagnostics) > 1 {
		problems = append(problems, fmt.Sprintf(
			"%d TUnitAssertions0005 diagnostics were reported; the canary plants exactly one.",
			len(diagnostics)))
	}

	return problems
}

// methodLine is the 1-based line of the Assert.That(...) inside the named
// method.
//
// The method is found by its DECLARATION, not by its name: the canary's doc
// comments mention both method names, and a search that stopped at the first
// mention would point the tool at a comment.
func methodLine(source string, method string) (int, error) {
	lines := splitLines(source)
	start := -1
	for index, line := range lines {
		stripped := strings.TrimSpace(line)
		if !strings.HasPrefix(stripped, "public ") &&
			!strings.HasPrefix(stripped, "private ") &&
			!strings.HasPrefix(stripped, "internal ") &&
			!strings.HasPrefix(stripped, "protected ") {
			continue
		}
		if !strings.Contains(stripped, " "+method+"(") {
			continue
		}
		start = index
		break
	}
	if start < 0 {
		return 0, fmt.Errorf("canary source has no method named %s", method)
	}
	for index := start; index < len(lines); index++ {
		if strings.Contains(lines[index], "Assert.That(") {
			return index + 1, nil
		}
	}
	return 0, fmt.Errorf("method %s has no Assert.That(...) call", method)
}

// splitLines splits a text into its lines, the way python's str.splitlines
// does: on \n, \r\n, \r and the rarer line boundaries, and without a
// trailing empty line for a text that ends with a boundary.
func splitLines(text string) []string {
	var lines []string
	start := 0
	runes := []rune(text)
	for index := 0; index < len(runes); index++ {
		boundary := false
		width := 1
		switch runes[index] {
		case '\n':
			boundary = true
		case '\r':
			boundary = true
			if index+1 < len(runes) && runes[index+1] == '\n' {
				width = 2
			}
		case '\v', '\f', '\u0085', '\u2028', '\u2029':
			boundary = true
		case '\u001c', '\u001d', '\u001e':
			boundary = true
		}
		if !boundary {
			continue
		}
		lines = append(lines, string(runes[start:index]))
		index += width - 1
		start = index + 1
	}
	if start < len(runes) {
		lines = append(lines, string(runes[start:]))
	}
	return lines
}
