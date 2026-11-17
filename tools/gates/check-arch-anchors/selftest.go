package archanchors

// selftest.go -- the gate's own proof that it can fail.
//
// A gate whose failure path is not tested is a gate that is green because
// it is blind. The python original built a throwaway git repo with a
// planted `file:line` anchor in an architecture rule and asserted four
// things:
//
//	1. a planted anchor is RED, and the report names it;
//	2. the same anchor with a recorded exception is GREEN;
//	3. an exception the tree no longer produces is RED (STALE);
//	4. an exception row without a reason is RED (MALFORMED).
//
// The fixture here is the same rule file, byte for byte, so a port that
// changed the scanner's reading of the fixture fails here.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// fixtureRule is the rule file the python original planted: a comment that
// names a line on purpose.
const fixtureRule = "// The rule expects the anchor to live in WidgetFactory.BuildWidget.\n" +
	"// src/Harbor.Core/WidgetFactory.cs:42 does the thing.\n" +
	"internal sealed class SomeRule { }\n"

// fixtureRuleNoAnchor is the same file with the anchor taken away: the
// exception row must then be stale.
const fixtureRuleNoAnchor = "// The rule expects the anchor to live in WidgetFactory.BuildWidget.\n" +
	"internal sealed class SomeRule { }\n"

// selfTestRun plants a divergence and requires the gate to notice it.
func selfTestRun() int {
	tmp, err := os.MkdirTemp("", "harbor-archanchors-selftest")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	defer os.RemoveAll(tmp)

	ok := true
	checks := 0
	report := func(name string, good bool, detail string) {
		checks++
		if good {
			fmt.Printf("  ok   %s\n", name)
			return
		}
		ok = false
		fmt.Printf("  FAIL %s%s\n", name, detail)
	}

	// 1. A planted anchor is a finding.
	findings := scanText("tests/Harbor.Architecture.Tests/SomeRule.cs", fixtureRule)
	report("a planted file:line anchor is found",
		len(findings) == 1 && strings.Contains(findings[0].anchor, "WidgetFactory.cs:42"),
		fmt.Sprintf(" findings=%v", findings))
	if len(findings) == 1 {
		report("the anchor's line is the one the fixture puts it on",
			findings[0].line == 2,
			fmt.Sprintf(" line=%d", findings[0].line))
	}

	// 2. The same anchor with a recorded exception is excused.
	rows, malformed := loadExceptionsText(
		"tests/Harbor.Architecture.Tests/SomeRule.cs | src/Harbor.Core/WidgetFactory.cs:42 | " +
			"the fixture names a line on purpose\n")
	report("a well-formed exception row is not malformed", len(malformed) == 0,
		fmt.Sprintf(" malformed=%v", malformed))
	report("the exception excuses the anchor",
		len(findings) == 1 && matchException(findings[0], rows) != nil,
		" the row did not match")

	// 3. The anchor goes away: the exception must be stale.
	staleFindings := scanText("tests/Harbor.Architecture.Tests/SomeRule.cs", fixtureRuleNoAnchor)
	report("the anchor is gone with it", len(staleFindings) == 0, "")
	stale := staleRows(rows, staleFindings)
	report("an exception the tree no longer produces is stale", len(stale) == 1, "")

	// 4. A row without a reason is rejected.
	_, malformedRows := loadExceptionsText(
		"tests/Harbor.Architecture.Tests/SomeRule.cs | src/Harbor.Core/WidgetFactory.cs:42\n")
	report("an exception row without a reason is malformed", len(malformedRows) == 1,
		fmt.Sprintf(" malformed=%v", malformedRows))

	// 5. A comment that names no line is not a finding.
	clean := scanText("tests/Harbor.Architecture.Tests/SomeRule.cs",
		"// The rule expects the anchor to live in WidgetFactory.BuildWidget.\n"+
			"internal sealed class SomeRule { }\n")
	report("a rule with no line-number anchor is clean", len(clean) == 0,
		fmt.Sprintf(" findings=%v", clean))

	// 6. The grammar is the one the citation gate fences with: a `file.cs`
	//    with no line number is not an anchor.
	noLine := scanText("tests/Harbor.Architecture.Tests/SomeRule.cs",
		"// see src/Harbor.Core/WidgetFactory.cs for the type.\n")
	report("a file name without a line number is not an anchor", len(noLine) == 0,
		fmt.Sprintf(" findings=%v", noLine))

	// 7. The file is written to disk and read back, because that is the
	//    path the gate really takes.
	path := filepath.Join(tmp, "SomeRule.cs")
	if err := os.WriteFile(path, []byte(fixtureRule), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	onDisk := scanFiles(tmp, []string{"SomeRule.cs"})
	report("the same anchor is found through the file on disk",
		len(onDisk) == 1 && onDisk[0].line == 2,
		fmt.Sprintf(" findings=%v", onDisk))

	if ok {
		fmt.Printf("\nOK: check-arch-anchors self-test — %d checks passed.\n", checks)
		return gate.Green
	}
	fmt.Println("\nFAIL: check-arch-anchors self-test — a check failed.")
	return gate.Red
}

// scanText scans one file's text as if it were the named file.
func scanText(rel, body string) []finding {
	var out []finding
	for _, c := range findCitations(body) {
		out = append(out, finding{
			file:   rel,
			line:   lineOf(body, c.start),
			anchor: body[c.start:c.end],
			text:   strings.TrimSpace(lineAt(body, c.start)),
		})
	}
	return out
}

// loadExceptionsText parses the text of an exceptions file.
func loadExceptionsText(text string) ([]exceptionRow, []string) {
	path := filepath.Join(os.TempDir(), "harbor-archanchors-selftest-exceptions.txt")
	if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
		return nil, nil
	}
	defer os.Remove(path)
	return loadExceptions(path)
}

// staleRows is the exceptions the tree no longer produces.
func staleRows(rows []exceptionRow, findings []finding) []exceptionRow {
	used := map[string]bool{}
	for _, f := range findings {
		if row := matchException(f, rows); row != nil {
			used[row.file+"\x00"+row.anchor] = true
		}
	}
	var stale []exceptionRow
	for _, row := range rows {
		if !used[row.file+"\x00"+row.anchor] {
			stale = append(stale, row)
		}
	}
	return stale
}
