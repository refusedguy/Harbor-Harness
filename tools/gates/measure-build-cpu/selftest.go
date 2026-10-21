package measurebuildcpu

// selftest.go -- the gate's own proof that it can fail.
//
// The load-bearing fixture is the LOG: the tool's whole value is what it
// reads back from the log the run wrote. A reader that missed the "is
// building" line, or that read the completion marker wrong, would report a
// timeline that says the graph was serial when it was not.

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

const logFixture = `  0.100 |   1>Project "/w/Harbor.slnx" (1) is building "/w/src/A/A.csproj" (2) with 3 targets.
  0.200 |   1>Project "/w/Harbor.slnx" (1) is building "/w/src/B/B.csproj" (3) with 3 targets.
  0.300 |   1>Project "/w/Harbor.slnx" (1) is building "/w/src/C/C.csproj" (4) with 3 targets.
  1.500 |     434717 ms  Csc     112 calls
  1.600 |   2>Done Building Project "/w/src/A/A.csproj" (default targets).
  1.700 |   2>Done Building Project "/w/src/B/B.csproj" (default targets).
  1.800 |   2>Done Building Project "/w/src/C/C.csproj" (default targets).
  1.900 |   A -> /w/src/A/bin/Release/net10.0/A.dll
  2.000 |      5000 ms  Exec      1 calls
`

// selfTestRun proves the log reader still reads the timeline and the tasks.
func selfTestRun() int {
	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}

	tmp, err := os.MkdirTemp("", "measure-build-cpu-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	logPath := filepath.Join(tmp, "fixture.log")
	if err := os.WriteFile(logPath, []byte(logFixture), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the fixture:", err)
		return gate.Red
	}
	events, tasks, lastStamp := parseLog(logPath)

	check(len(events) == 7, fmt.Sprintf("7 timeline events, got %d", len(events)))
	if len(events) == 7 {
		first := events[0]
		check(gatekit.JSONObjectText(first, "kind") == "start", "the first event is a start")
		check(gatekit.JSONObjectText(first, "project") == "A", "the first project is A")
		last := events[6]
		check(gatekit.JSONObjectText(last, "kind") == "done", "the last event is a done")
		check(gatekit.JSONObjectText(last, "project") == "A", "the last project is A")
	}
	check(lastStamp == 2.0, fmt.Sprintf("the last output stamp is 2.0, got %v", lastStamp))

	csc := gatekit.JSONObjectChild(tasks, "Csc")
	if csc == nil {
		problems = append(problems, "the Csc task is missing")
	} else {
		check(gatekit.JSONObjectText(csc, "total_ms") == "434717",
			"the Csc task carries 434717 ms")
		check(gatekit.JSONObjectText(csc, "calls") == "112",
			"the Csc task carries 112 calls")
	}
	exec := gatekit.JSONObjectChild(tasks, "Exec")
	if exec == nil {
		problems = append(problems, "the Exec task is missing")
	} else {
		check(gatekit.JSONObjectText(exec, "total_ms") == "5000",
			"the Exec task carries 5000 ms")
	}

	// The in-flight profile: three projects start, and FOUR completion
	// markers are in the log — the three "Done Building Project" lines and
	// the arrow line of the build output. The count is what the log says.
	profile := inFlightProfile(events, 3.0)
	check(gatekit.JSONObjectText(profile, "available") == "True", "the profile is available")
	check(gatekit.JSONObjectText(profile, "projects_started") == "3", "three projects started")
	check(gatekit.JSONObjectText(profile, "projects_finished") == "4", "four completion markers")
	check(gatekit.JSONObjectText(profile, "max_in_flight") == "3", "the peak is three projects in flight")
	check(gatekit.JSONObjectText(profile, "avg_in_flight") == "0.75", "the average is 0.75 projects in flight")

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: the timeline, the task aggregates and the in-flight profile are read from the log.")
	return gate.Green
}
