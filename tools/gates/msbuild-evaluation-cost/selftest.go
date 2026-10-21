package msbuildevaluationcost

// selftest.go -- the gate's own proof that it can fail.
//
// A rule edited until it matches nothing is indistinguishable from a rule
// that found nothing, so the fixture has to have numbers this gate asserts,
// not just rows.

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
)

const selfTestFixture = `Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:06:12.34

Project Evaluation Performance Summary:
    1234 ms  /w/src/Harbor.Core/Harbor.Core.csproj                       1 calls
    5678 ms  /w/src/Harbor.Application/Harbor.Application.csproj         2 calls
      * ms  /w/src/Harbor.Reentrant/Harbor.Reentrant.csproj               1 calls

Project Performance Summary:
    9000 ms  /w/src/Harbor.Core/Harbor.Core.csproj                       1 calls

Target Performance Summary:
    3000 ms  GetTargetFrameworks                                          50 calls
    1000 ms  ResolveProjectReferences                                     20 calls
     500 ms  _GetProjectReferenceTargetFrameworkProperties               20 calls
    4000 ms  CoreCompile                                                 109 calls

Task Performance Summary:
    2500 ms  Csc                                                          109 calls
`

// selfTestRun asserts the parser against a fixture whose numbers are known,
// and then checks the failure path.
func selfTestRun() int {
	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}

	tmp, err := os.MkdirTemp("", "msbuild-evaluation-cost-selftest")
	if err != nil {
		fmt.Println("::error::the evaluation-cost self-test failed:")
		fmt.Printf("  - cannot create a temporary directory: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	good := filepath.Join(tmp, "perf.log")
	if err := os.WriteFile(good, []byte(selfTestFixture), 0o644); err != nil {
		fmt.Println("::error::the evaluation-cost self-test failed:")
		fmt.Printf("  - cannot write the fixture: %v\n", err)
		return gate.Red
	}
	result := readRun("fixture", good, "372")
	if result.err != "" {
		check(false, fmt.Sprintf("the fixture should parse: %s", result.err))
	} else {
		check(result.evaluationCounted == 3,
			fmt.Sprintf("3 evaluations have a value, got %d", result.evaluationCounted))
		check(result.evaluationTotalMS == 1234+5678,
			fmt.Sprintf("the evaluation total is 6912 ms, got %d", result.evaluationTotalMS))
		check(result.evaluationReentry == 1,
			fmt.Sprintf("1 evaluation is re-entrant, got %d", result.evaluationReentry))
		check(result.projectsEvaluated == 3,
			fmt.Sprintf("3 distinct projects, got %d", result.projectsEvaluated))
		targets := map[string]targetTotal{}
		for _, target := range result.targets {
			targets[target.name] = target
		}
		_, wrong := targets["GetTargetFrameways"]
		check(!wrong, "a target name that is not one of the reference targets must not be counted")
		got, ok := targets["GetTargetFrameworks"]
		check(ok && got.ms == 3000 && got.calls == 50,
			fmt.Sprintf("GetTargetFrameworks is 3000 ms / 50 calls, got %+v", got))
		got, ok = targets["ResolveProjectReferences"]
		check(ok && got.ms == 1000 && got.calls == 20,
			fmt.Sprintf("ResolveProjectReferences is 1000 ms / 20 calls, got %+v", got))
		got, ok = targets["_GetProjectReferenceTargetFrameworkProperties"]
		check(ok && got.ms == 500 && got.calls == 20,
			fmt.Sprintf("_GetProjectReferenceTargetFrameworkProperties is 500 ms / 20 calls, got %+v", got))
	}

	// The failure path: a summary without the evaluation section must say
	// so, not report zero.
	empty := filepath.Join(tmp, "empty.log")
	if err := os.WriteFile(empty, []byte("Build succeeded.\n"), 0o644); err != nil {
		fmt.Println("::error::the evaluation-cost self-test failed:")
		fmt.Printf("  - cannot write the empty fixture: %v\n", err)
		return gate.Red
	}
	broken := readRun("empty", empty, "10")
	check(broken.err != "", "a summary without the evaluation section must be reported as not measured")

	missing := readRun("missing", filepath.Join(tmp, "nope.log"), "10")
	check(missing.err != "", "a missing summary file must be reported as not measured")

	if len(problems) > 0 {
		fmt.Println("::error::the evaluation-cost self-test failed:")
		for _, problem := range problems {
			fmt.Printf("  - %s\n", problem)
		}
		return gate.Red
	}
	fmt.Println("ok: the performance summary is parsed into the numbers it says it is")
	return gate.Green
}
