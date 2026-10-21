package testmeasurements

// selftest.go -- the gate's own proof that it can fail.
//
// The load-bearing fixture is the pipe: a measurement that prints one breaks
// an unescaped markdown table into two rows, and the row that survives looks
// like a measurement that was never printed. The duration is the second
// load-bearing fact (#1884): the column has to read the result element's own
// @duration, not the assembly's.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const sampleTRX = `<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="a" testName="Measures_Thing" outcome="Passed" duration="00:00:01.2500000">
      <Output><StdOut>alloc: 42 B | p99 = 1.5 ms</StdOut></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Silent" outcome="Passed" duration="00:00:00.0100000" />
  </Results>
  <TestDefinitions>
    <UnitTest name="Measures_Thing" id="a">
      <TestMethod className="Harbor.X.Tests.ThingTests" name="Measures_Thing" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
`

// selfTestRun proves the extraction still works on a synthetic TRX.
func selfTestRun() int {
	tmp, err := os.MkdirTemp("", "test-measurements-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	path := filepath.Join(tmp, "Harbor.X.Tests.trx")
	if err := os.WriteFile(path, []byte(sampleTRX), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the fixture:", err)
		return gate.Red
	}
	rows := collect([]string{path})

	var problems []string
	if len(rows) != 1 {
		problems = append(problems, fmt.Sprintf("expected 1 row (the silent test must be skipped), got %d", len(rows)))
	} else {
		project, name, text := rows[0].project, rows[0].name, rows[0].text
		duration := rows[0].duration
		if project != "Harbor.X.Tests" {
			problems = append(problems, fmt.Sprintf("project = %q", project))
		}
		if name != "ThingTests.Measures_Thing" {
			problems = append(problems, fmt.Sprintf("name = %q (must be Class.Method)", name))
		}
		// The pipe is what breaks an unescaped markdown table.
		if !strings.Contains(text, `alloc: 42 B \| p99 = 1.5 ms`) {
			problems = append(problems, fmt.Sprintf("pipe not escaped: %q", text))
		}
		// #1884: the duration column reads the result element's own @duration.
		if !rows[0].hasTime || duration < 1.25-1e-6 || duration > 1.25+1e-6 {
			problems = append(problems, fmt.Sprintf("duration = %v (must be 1.25 s)", duration))
		}
		table := render(rows, 10)
		if !strings.Contains(table, "| test | duration | measurement |") {
			problems = append(problems, "the rendered table has no duration column")
		}
		if !strings.Contains(table, "1.25 s") {
			problems = append(problems, "the duration cell is missing from the rendered table")
		}
	}

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: row extracted, class-qualified, pipe escaped, duration read, silent test skipped.")
	return gate.Green
}
