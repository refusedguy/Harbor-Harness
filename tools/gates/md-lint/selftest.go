package mdlint

// selftest.go -- the gate's own proof that it can fail.
//
// The python original planted broken documents and asserted the exit code
// AND the specific rule code, so a linter that degraded into "prints
// nothing, exits 0" could not pass. The port asserts the same thing about
// the same documents: the rule code, not only the verdict.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/mdgate"
)

// codesOf is the rule codes the document produced, in order.
func codesOf(data []byte) []string {
	var out []string
	for _, p := range lintBytes(data) {
		out = append(out, p.code)
	}
	return out
}

// hasCode reports whether the document produced the rule code.
func hasCode(data []byte, code string) bool {
	for _, c := range codesOf(data) {
		if c == code {
			return true
		}
	}
	return false
}

func selfTestRun() int {
	ok := true
	checks := 0
	expect := func(name string, good bool, detail string) {
		checks++
		if good {
			fmt.Printf("  ok   %s\n", name)
			return
		}
		ok = false
		fmt.Printf("  FAIL %s%s\n", name, detail)
	}

	clean := "# Title\n\nBody with no tabs.\n\n```bash\ndotnet build\n```\n"
	expect("a clean document has no violation", len(lintBytes([]byte(clean))) == 0,
		fmt.Sprintf(" codes=%v", codesOf([]byte(clean))))

	expect("a heading level jump is MD001",
		hasCode([]byte("# T\n\n#### Deep\n"), "MD001"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("# T\n\n#### Deep\n"))))
	expect("a hard tab is MD010",
		hasCode([]byte("# T\n\n\tindented with a tab\n"), "MD010"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("# T\n\n\tindented with a tab\n"))))
	expect("no newline at EOF is MD047",
		hasCode([]byte("# T\n\nbody"), "MD047"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("# T\n\nbody"))))
	expect("a blank line at EOF is MD047",
		hasCode([]byte("# T\n\nbody\n\n"), "MD047"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("# T\n\nbody\n\n"))))
	expect("a tilde fence is MD048",
		hasCode([]byte("# T\n\n~~~\ncode\n~~~\n"), "MD048"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("# T\n\n~~~\ncode\n~~~\n"))))
	expect("no space after the hashes is MD018",
		hasCode([]byte("#T\n"), "MD018"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("#T\n"))))
	expect("two spaces after the hashes is MD019",
		hasCode([]byte("#  T\n"), "MD019"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("#  T\n"))))
	expect("a setext heading is MD003",
		hasCode([]byte("Title\n=====\n"), "MD003"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("Title\n=====\n"))))
	expect("an unclosed fence is FENCE-UNCLOSED",
		hasCode([]byte("# T\n\n```bash\ndotnet build\n"), "FENCE-UNCLOSED"),
		fmt.Sprintf(" codes=%v", codesOf([]byte("# T\n\n```bash\ndotnet build\n"))))
	expect("an empty file is MD047",
		hasCode([]byte(""), "MD047"),
		fmt.Sprintf(" codes=%v", codesOf([]byte(""))))

	// An indented `#` is list content in every real case in this repo; it
	// must not be reported as a heading (false positives are how a lint
	// dies).
	list := "1. Do a thing\n   #249 is a PR\n"
	expect("an indented hash in a list item is not a heading",
		len(lintBytes([]byte(list))) == 0,
		fmt.Sprintf(" codes=%v", codesOf([]byte(list))))

	// The encoding rules need byte-level fixtures.
	bomDoc := append([]byte{}, bom...)
	bomDoc = append(bomDoc, []byte("# T\n")...)
	expect("a BOM is ENC-BOM", hasCode(bomDoc, "ENC-BOM"),
		fmt.Sprintf(" codes=%v", codesOf(bomDoc)))
	crlf := []byte("# T\r\n\r\nbody\r\n")
	expect("a CRLF is ENC-CRLF", hasCode(crlf, "ENC-CRLF"),
		fmt.Sprintf(" codes=%v", codesOf(crlf)))

	// A fence must not be able to hide the rest of the file from the
	// linter...
	hidden := "# T\n\n```\n#### buried jump\n```\n"
	expect("fenced content is not linted as headings",
		len(lintBytes([]byte(hidden))) == 0,
		fmt.Sprintf(" codes=%v", codesOf([]byte(hidden))))

	// ...and an unclosed one must not be able to hide the whole document.
	buried := "# T\n\n```\n#### buried jump\n"
	expect("a fenced-away heading is reported via the unclosed fence",
		hasCode([]byte(buried), "FENCE-UNCLOSED"),
		fmt.Sprintf(" codes=%v", codesOf([]byte(buried))))

	// Anti-vacuity: a real-but-tiny scan must not pass silently.
	guard := mdgate.RequireNonVacuous("md-lint", 1, 10, "lines", 250, 0)
	expect("the file floor below the count fires", len(guard) == 1 &&
		strings.Contains(guard[0], "floor is 250"),
		fmt.Sprintf(" guard=%v", guard))
	guard = mdgate.RequireNonVacuous("md-lint", 1, 10, "lines", 0, 100000)
	expect("the line floor below the count fires", len(guard) == 1 &&
		strings.Contains(guard[0], "floor is 100000"),
		fmt.Sprintf(" guard=%v", guard))
	guard = mdgate.RequireNonVacuous("md-lint", 0, 0, "lines", 250, 0)
	expect("zero markdown files is a guard problem", len(guard) == 1 &&
		strings.Contains(guard[0], "scanned ZERO markdown files"),
		fmt.Sprintf(" guard=%v", guard))

	if ok {
		fmt.Printf("\nOK: md-lint self-test — %d checks passed.\n", checks)
		return gate.Green
	}
	fmt.Println("\nFAIL: md-lint self-test — a check failed.")
	return gate.Red
}
