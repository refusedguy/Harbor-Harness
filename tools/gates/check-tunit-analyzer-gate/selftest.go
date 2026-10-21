package tunitanalyzer

// selftest.go -- the analysis must REJECT every synthetic log that breaks a
// rule.
//
// The fixtures are the failure paths of the control, one each: the clean log
// passes, an empty log is rejected, the same diagnostic printed twice is
// still one diagnostic, the waived site being reported is rejected, the
// diagnostic on another line is rejected, the diagnostic in another file is
// rejected, and two diagnostics are rejected.
//
// A parser that cannot fail is not a parser.

import (
	"fmt"
	"os"

	"harbor.local/gates/internal/gate"
)

func selfTestRun(root string) int {
	source, err := os.ReadFile(root + "/" + canarySource)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the canary source:", err)
		return gate.Broken
	}
	planted, err := methodLine(string(source), plantedMethod)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	waived, err := methodLine(string(source), waivedMethod)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}

	good := fmt.Sprintf("/repo/%s(%d,9): error TUnitAssertions0005: "+
		"Assert.That(...) should not be used with a constant value [/repo/%s]\n",
		canarySource, planted, canaryProject)
	// What MSBuild actually prints: the same diagnostic twice — once as it
	// happens (with the runner's `##[error]` marker in front) and once in
	// the error summary. One finding, printed twice.
	printedTwice := fmt.Sprintf("##[error]  /repo/%s(%d,9): error TUnitAssertions0005: x\n"+
		"##[error]  /repo/%s(%d,9): error TUnitAssertions0005: x\n",
		canarySource, planted, canarySource, planted)

	type testCase struct {
		name       string
		log        string
		shouldPass bool
	}
	cases := []testCase{
		{"the clean log must pass", good, true},
		{"a log with no diagnostic must be rejected", "", false},
		{"a log that prints the diagnostic twice must still pass", printedTwice, true},
		{
			"a log with the waived site reported must be rejected",
			good + fmt.Sprintf("/repo/%s(%d,9): error TUnitAssertions0005: x\n", canarySource, waived),
			false,
		},
		{
			"a log with the diagnostic on another line must be rejected",
			fmt.Sprintf("/repo/%s(%d,9): error TUnitAssertions0005: x\n", canarySource, waived+40),
			false,
		},
		{
			"a log with the diagnostic in another file must be rejected",
			fmt.Sprintf("/repo/tests/SomethingElse/Other.cs(%d,9): error TUnitAssertions0005: x\n", planted),
			false,
		},
		{
			"a log with two diagnostics must be rejected",
			good + fmt.Sprintf("/repo/%s(%d,9): error TUnitAssertions0005: x\n", canarySource, waived+40),
			false,
		},
	}

	failures := 0
	for _, test := range cases {
		problems := Analyse(test.log, planted, waived)
		passed := len(problems) == 0
		if passed != test.shouldPass {
			failures++
			expected := "failure"
			if test.shouldPass {
				expected = "pass"
			}
			got := "pass"
			if !passed {
				got = "failure"
			}
			fmt.Printf("FAIL selftest: %s (expected %s, got %s)\n", test.name, expected, got)
			for _, problem := range problems {
				fmt.Printf("       %s\n", problem)
			}
		} else {
			fmt.Printf("ok   selftest: %s\n", test.name)
		}
	}

	if failures > 0 {
		fmt.Printf("\n%d selftest case(s) failed — the analysis is not trustworthy\n", failures)
		return gate.Red
	}
	fmt.Println("\nOK: the analysis rejects every broken log it was shown")
	return gate.Green
}
