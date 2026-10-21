package testcensus

// selftest.go -- the gate's own proof that it can fail.
//
// The load-bearing fixture is the SILENT test: test-measurements.py skips it
// by design, and a census that skipped it too would report a suite of 1 test
// where the runner discovered 3.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const sampleTRX = `<?xml version="1.0" encoding="utf-8"?>
<TestRun id="run" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="2026-10-20T12:00:00.0000000+00:00" queuing="2026-10-20T12:00:00.0000000+00:00" start="2026-10-20T12:00:00.1000000+00:00" finish="2026-10-20T12:00:10.0000000+00:00" />
  <Results>
    <UnitTestResult testId="a" testName="Measures_Thing" outcome="Passed" duration="00:00:02.0000000">
      <Output><StdOut>alloc: 42 B</StdOut></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Silent" outcome="Passed" duration="00:00:03.0000000" />
    <UnitTestResult testId="c" testName="Broken" outcome="Failed" duration="00:00:01.0000000" />
  </Results>
  <TestDefinitions>
    <UnitTest name="Measures_Thing" id="a">
      <TestMethod className="Harbor.X.Tests.ThingTests" name="Measures_Thing" />
    </UnitTest>
    <UnitTest name="Silent" id="b">
      <TestMethod className="Harbor.X.Tests.ThingTests" name="Silent" />
    </UnitTest>
    <UnitTest name="Broken" id="c">
      <TestMethod className="Harbor.X.Tests.OtherTests" name="Broken" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
`

// selfTestRun proves the census reads EVERY result, not only the ones that
// printed.
func selfTestRun() int {
	var problems []string

	tmp, err := os.MkdirTemp("", "test-census-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	shardDir := filepath.Join(tmp, "core")
	if err := os.MkdirAll(shardDir, 0o755); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create the shard directory:", err)
		return gate.Red
	}
	path := filepath.Join(shardDir, "Harbor.X.Tests.trx")
	if err := os.WriteFile(path, []byte(sampleTRX), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the fixture:", err)
		return gate.Red
	}
	projects := collect([]string{path})

	if len(projects) != 1 {
		problems = append(problems, fmt.Sprintf("expected 1 project, got %d", len(projects)))
	} else {
		record := projects[0]
		if record.project != "Harbor.X.Tests" {
			problems = append(problems, fmt.Sprintf("project = %q", record.project))
		}
		if record.shard != "core" {
			problems = append(problems, fmt.Sprintf("shard = %q (the parent dir)", record.shard))
		}
		// The census counts the silent and the failed test too.
		if record.discovered() != 3 {
			problems = append(problems, fmt.Sprintf("discovered = %d (must be 3: the census counts every result, printed or not)", record.discovered()))
		}
		outcomes := make([]string, 0, len(record.tests))
		for _, t := range record.tests {
			outcomes = append(outcomes, t.outcome)
		}
		passed, failed, _, _ := outcomeCounts(outcomes)
		if passed != 2 || failed != 1 {
			problems = append(problems, fmt.Sprintf("passed/failed = %d/%d (must be 2/1)", passed, failed))
		}
		if record.countedDuration() < 6.0-1e-6 || record.countedDuration() > 6.0+1e-6 {
			problems = append(problems, fmt.Sprintf("sum of durations = %v (must be 6.0)", record.countedDuration()))
		}
		if !record.hasWall || record.wall < 10.0-1e-6 || record.wall > 10.0+1e-6 {
			problems = append(problems, fmt.Sprintf("wall = %v (must be 10.0 s)", record.wall))
		}
		factor, hasFactor := record.factor()
		if !hasFactor || factor < 0.6-1e-6 || factor > 0.6+1e-6 {
			problems = append(problems, fmt.Sprintf("parallelism = %v (must be 0.6)", factor))
		}
		found := false
		for _, t := range record.tests {
			if t.name == "ThingTests.Silent" {
				found = true
			}
		}
		if !found {
			problems = append(problems, "the silent test is missing from the census")
		}

		table := renderProjectTable(projects)
		if !strings.Contains(table, "| 3 | 2 | 1 | 0 |") {
			problems = append(problems, "the project row does not carry discovered/passed/failed/skipped")
		}
		totals := renderTotals(projects)
		if !strings.Contains(totals, "**3**") {
			problems = append(problems, "the totals row does not carry the discovered count")
		}

		csvText := renderCSV(projects, ",")
		if len(strings.Split(strings.TrimSpace(csvText), "\n")) != 4 {
			problems = append(problems, fmt.Sprintf("csv has %d lines (must be 4: header + 3 tests)",
				len(strings.Split(strings.TrimSpace(csvText), "\n"))))
		}

		slowest := slowestTests(projects, 1)
		if len(slowest) != 1 || slowest[0].name != "ThingTests.Silent" {
			name := ""
			if len(slowest) > 0 {
				name = slowest[0].name
			}
			problems = append(problems, fmt.Sprintf("the slowest test is %q, must be ThingTests.Silent (3.0 s)", name))
		}
	}

	// A corrupt TRX is a fact to print, never a failure.
	broken := filepath.Join(tmp, "broken.trx")
	if err := os.WriteFile(broken, []byte("<TestRun><not-xml"), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the corrupt fixture:", err)
		return gate.Red
	}
	if len(collect([]string{broken})) != 0 {
		problems = append(problems, "a corrupt TRX must be skipped, not parsed")
	}

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: every result counted (silent and failed included), duration summed, wall and parallelism read, corrupt TRX skipped.")
	return gate.Green
}
