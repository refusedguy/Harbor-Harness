package docanchors

// selftest.go -- the gate's own proof that it can fail.
//
// A gate that cannot fail is not a gate. Every fixture below is a shape that
// makes this gate wrong, and each one is checked for the FINDING it must
// produce, not for a pass:
//
//	named_anchor_on_code     the anchor is on the line: the citation is
//	                         right, and the gate must say nothing.
//	named_anchor_drifted     the anchor is NOT on the line the citation
//	                         names: DOC-ANCHOR-DRIFT.
//	comment_only_line        the cited line holds a comment: DOC-ANCHOR-NO-CODE.
//	blank_line               the cited line is empty: DOC-ANCHOR-NO-CODE.
//	range_with_code          the range holds one line with code: the rule
//	                         looks at the RANGE, not at its first line.
//	tail_of_a_longer_path    `a/b.cs:12` inside `x/a/b.cs:12` is not a
//	                         citation of its own.
//
// The blindness case matters as much as the firing ones: a rule that reads
// nothing and reports nothing is indistinguishable from a rule that found
// nothing, and that is the one verdict CI passes.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

const (
	fixtureDoc = "# Fixture\n\n> Status: normative for the current implementation.\n\n" +
		"A citation of code: `src/Foo.cs:1#Widget`\n" +
		"A citation of a drifted anchor: `src/Foo.cs:2#Widget`\n" +
		"A citation of a comment: `src/Foo.cs:3`\n" +
		"A citation of a blank line: `src/Foo.cs:4`\n" +
		"A citation of a range: `src/Foo.cs:5-6#Widget`\n" +
		"A tail of a longer path: `./Foo.cs:1`\n"

	fixtureSource = "class Widget { }\n" + // 1: code, holds the anchor
		"class Other { }\n" + // 2: code, does NOT hold the anchor
		"// a comment\n" + // 3: a comment
		"\n" + // 4: blank
		"// a comment\n" + // 5: a comment
		"class Widget2 { }\n" // 6: code, holds the anchor
)

func selfTestRun() int {
	dir, err := os.MkdirTemp("", "check-doc-anchors")
	if err != nil {
		fmt.Fprintln(os.Stderr, "self-test: cannot create a fixture tree:", err)
		return 2
	}
	defer os.RemoveAll(dir)

	if err := os.MkdirAll(filepath.Join(dir, "src"), 0o755); err != nil {
		fmt.Fprintln(os.Stderr, "self-test: cannot create the fixture tree:", err)
		return 2
	}
	if err := os.WriteFile(filepath.Join(dir, "src", "Foo.cs"), []byte(fixtureSource), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "self-test: cannot write the fixture source:", err)
		return 2
	}
	docPath := filepath.Join(dir, "docs", "EVENT_TOPOLOGY.md")
	if err := os.MkdirAll(filepath.Dir(docPath), 0o755); err != nil {
		fmt.Fprintln(os.Stderr, "self-test: cannot create the fixture document:", err)
		return 2
	}
	if err := os.WriteFile(docPath, []byte(fixtureDoc), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "self-test: cannot write the fixture document:", err)
		return 2
	}

	scanned, findings := scanDocument(dir, "docs/EVENT_TOPOLOGY.md", fixtureDoc, map[string][]string{"Foo.cs": {"src/Foo.cs"}})

	// The population: five citations, and the tail of a longer path is not
	// one of them.
	if scanned != 5 {
		fmt.Printf("self-test FAIL: the scan counted %d citations, the fixture has 5\n", scanned)
		return 1
	}

	byRule := map[string]int{}
	for _, f := range findings {
		byRule[f.rule]++
	}

	expect := map[string]int{
		"DOC-ANCHOR-NO-CODE": 2, // the comment line and the blank line
		"DOC-ANCHOR-DRIFT":   1, // the anchor that is not on the line
	}
	for rule, want := range expect {
		if byRule[rule] != want {
			fmt.Printf("self-test FAIL: %s fired %d times, the fixture needs %d\n", rule, byRule[rule], want)
			return 1
		}
	}

	// The blindness case: a document with no citation must produce NO
	// finding, and the scan must say it checked nothing. A rule that cannot
	// read a document must not report it as clean.
	empty := "Nothing here at all.\n"
	scanned, findings = scanDocument(dir, "docs/EMPTY.md", empty, map[string][]string{"Foo.cs": {"src/Foo.cs"}})
	if scanned != 0 || len(findings) != 0 {
		fmt.Printf("self-test FAIL: an empty document produced %d citations and %d findings\n", scanned, len(findings))
		return 1
	}

	// The non-normative case: the population is the documents that carry the
	// banner. A document without it is not read at all.
	dated := "# Dated record\n\n> Status (2026-01-01): a snapshot.\n\n`src/Foo.cs:3`\n"
	if normative.MatchString(dated) {
		fmt.Println("self-test FAIL: a dated record was read as normative")
		return 1
	}

	// carriesCode is the rule the whole gate rests on, and it is checked
	// directly: a line that is only a comment is not code.
	if carriesCode("// just a comment") {
		fmt.Println("self-test FAIL: a comment-only line was read as code")
		return 1
	}
	if carriesCode("   ") {
		fmt.Println("self-test FAIL: a blank line was read as code")
		return 1
	}
	if !carriesCode("public void M() { }") {
		fmt.Println("self-test FAIL: a line of code was read as a comment")
		return 1
	}

	fmt.Println("OK: the gate fires on a drifted anchor, on a comment and on a blank line, and stays quiet on a good citation.")
	return 0
}

// unusedStrings keeps the import list honest if the fixtures change.
var _ = strings.TrimSpace
