package testskips

// selftest.go -- the gate's own proof that it can fail.
//
// The fixture is the shape that made an earlier version of this gate wrong:
// a skip whose reason the "Skipped:" marker still covered, and a skip with no
// reason at all. A gate that printed the marker as the reason, or that
// dropped the reasonless skip, would look right on a table of skips that all
// declare one.

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
)

const sampleTRX = `<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="a" testName="Golden_Frame" outcome="NotExecuted">
      <Output><DebugTrace>Skipped: pixel-golden baselines are machine-pinned</DebugTrace></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Passes" outcome="Passed" />
    <UnitTestResult testId="c" testName="Silent_Skip" outcome="NotExecuted" />
  </Results>
  <TestDefinitions>
    <UnitTest name="Golden_Frame" id="a">
      <TestMethod className="Harbor.X.Tests.GoldenTests" name="Golden_Frame" />
    </UnitTest>
    <UnitTest name="Silent_Skip" id="c">
      <TestMethod className="Harbor.X.Tests.OtherTests" name="Silent_Skip" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
`

// selfTestRun proves the extraction still works on a synthetic TRX.
func selfTestRun() int {
	tmp, err := os.MkdirTemp("", "test-skips-selftest")
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
	if len(rows) != 2 {
		problems = append(problems, fmt.Sprintf("expected 2 skipped rows (the passing test must not be one), got %d", len(rows)))
	} else {
		project, name, reason := rows[0].project, rows[0].name, rows[0].reason
		if project != "Harbor.X.Tests" {
			problems = append(problems, fmt.Sprintf("project = %q", project))
		}
		if name != "GoldenTests.Golden_Frame" {
			problems = append(problems, fmt.Sprintf("name = %q (must be Class.Method)", name))
		}
		if reason != "pixel-golden baselines are machine-pinned" {
			problems = append(problems, fmt.Sprintf("reason = %q (the 'Skipped:' marker must be stripped)", reason))
		}
		if rows[1].reason != "(no reason given)" {
			problems = append(problems, fmt.Sprintf("a skip with no reason must say so: %q", rows[1].reason))
		}
	}

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: 2 skips extracted, class-qualified, reason kept, reasonless skip flagged.")
	return gate.Green
}
