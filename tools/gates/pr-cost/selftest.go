package prcost

// selftest.go -- the instrument's own proof that it can fail.
//
// The load-bearing fixture is the STRIPE whose jobs do not touch: two jobs
// that overlap, one that waits for a runner, and one that is skipped. An
// instrument that counted the wait as work, or the skipped job as a job,
// would report a bill and a wall that are both wrong, and the wrongness
// would look like a measurement.
//
// The second fixture is the malformed timestamp: it must be an ERROR, not a
// zero. A bill that reads zero because a timestamp could not be parsed is
// the worst kind of wrong: it is small, and it looks like good news.

import (
	"fmt"
	"os"
	"path/filepath"
)

// selfTestRun proves the instrument reads a stripe the way it claims to.
func selfTestRun() int {
	var problems []string

	// ---- fixture 1: one stripe, four jobs, one of them skipped ----
	//
	//   10:00:00  build          10:00:00 -> 10:04:00   (240 s)
	//   10:04:00  test (core)    10:04:10 -> 10:05:10   ( 60 s, 10 s of wait)
	//   10:05:10  pack           10:05:10 -> 10:05:40   ( 30 s)
	//   (skipped) aot-publish
	//
	// runner-minutes: (240 + 60 + 30) / 60 = 5.5
	// wall:           10:00:00 -> 10:05:40 = 340 s
	// busy:           240 + 60 + 30 = 330 s
	// idle:           10 s  (the wait before test (core))
	// peak width:     1     (the jobs are sequential, never two at once)
	first := stripe{
		Label: "fixture",
		Runs: []run{{
			ID:       1,
			Workflow: "ci",
			Jobs: []job{
				{Name: "build", StartedAt: "2026-10-21T10:00:00Z", CompletedAt: "2026-10-21T10:04:00Z", Conclusion: "success"},
				{Name: "test (core)", StartedAt: "2026-10-21T10:04:10Z", CompletedAt: "2026-10-21T10:05:10Z", Conclusion: "success"},
				{Name: "pack", StartedAt: "2026-10-21T10:05:10Z", CompletedAt: "2026-10-21T10:05:40Z", Conclusion: "success"},
				{Name: "aot-publish", StartedAt: "2026-10-21T10:00:00Z", CompletedAt: "2026-10-21T10:00:00Z", Conclusion: "skipped"},
			},
		}},
	}
	metrics, err := measure(first)
	if err != nil {
		problems = append(problems, fmt.Sprintf("the fixture stripe does not measure: %v", err))
	} else {
		check(problems, "runner-minutes", metrics.runner/60, 5.5, 0.001)
		check(problems, "wall", metrics.wall, 340, 0.001)
		check(problems, "busy", metrics.busy, 330, 0.001)
		check(problems, "idle", metrics.idle, 10, 0.001)
		checkInt(problems, "jobs that ran", len(metrics.jobs), 3)
		checkInt(problems, "jobs skipped", metrics.skipped, 1)
		checkInt(problems, "peak width", metrics.peak, 1)
	}

	// ---- fixture 2: two jobs at once ----
	//
	// The peak width has to be 2 when two jobs overlap, and the busy time
	// has to count the overlap ONCE.
	overlap := stripe{
		Label: "overlap",
		Runs: []run{{
			ID:       2,
			Workflow: "ci",
			Jobs: []job{
				{Name: "build", StartedAt: "2026-10-21T10:00:00Z", CompletedAt: "2026-10-21T10:04:00Z", Conclusion: "success"},
				{Name: "pack", StartedAt: "2026-10-21T10:01:00Z", CompletedAt: "2026-10-21T10:02:00Z", Conclusion: "success"},
			},
		}},
	}
	measured, err := measure(overlap)
	if err != nil {
		problems = append(problems, fmt.Sprintf("the overlapping stripe does not measure: %v", err))
	} else {
		checkInt(problems, "peak width of two overlapping jobs", measured.peak, 2)
		check(problems, "busy of two overlapping jobs", measured.busy, 240, 0.001)
		check(problems, "runner of two overlapping jobs", measured.runner, 300, 0.001)
	}

	// ---- fixture 3: a job that ends exactly when the next starts ----
	//
	// The two are SEQUENTIAL, not concurrent: the width is 1, not 2. An
	// instrument that counts them as two at once reports a lane that is
	// wider than it is, and the "more parallelism" decision is made on it.
	adjacent := stripe{
		Label: "adjacent",
		Runs: []run{{
			ID:       3,
			Workflow: "ci",
			Jobs: []job{
				{Name: "build", StartedAt: "2026-10-21T10:00:00Z", CompletedAt: "2026-10-21T10:01:00Z", Conclusion: "success"},
				{Name: "pack", StartedAt: "2026-10-21T10:01:00Z", CompletedAt: "2026-10-21T10:02:00Z", Conclusion: "success"},
			},
		}},
	}
	measured, err = measure(adjacent)
	if err != nil {
		problems = append(problems, fmt.Sprintf("the adjacent stripe does not measure: %v", err))
	} else {
		checkInt(problems, "peak width of adjacent jobs", measured.peak, 1)
	}

	// ---- fixture 4: the categories ----
	//
	// A job this repository has never seen is `other`, not a guess.
	if got := CategoryOf("ci", "build"); got != catBuild {
		problems = append(problems, fmt.Sprintf(
			"the build job is charged to %q, and it must be charged to %q", got, catBuild))
	}
	if got := CategoryOf("ci", "a job that does not exist"); got != catOther {
		problems = append(problems, fmt.Sprintf(
			"an unknown job is charged to %q, and it must be charged to %q", got, catOther))
	}
	if got := CategoryOf("docs", "internal links"); got != catDocsGates {
		problems = append(problems, fmt.Sprintf(
			"a docs.yml gate is charged to %q, and it must be charged to %q", got, catDocsGates))
	}
	if got := CategoryOf("ci", "test (arch)"); got != catTests {
		problems = append(problems, fmt.Sprintf(
			"a test shard is charged to %q, and it must be charged to %q", got, catTests))
	}

	// ---- fixture 5: a timestamp that does not parse ----
	//
	// It must be an ERROR. An instrument that reads it as "no time" reports
	// a bill of zero, and zero is the number that looks like good news.
	broken := stripe{
		Label: "broken",
		Runs: []run{{
			ID:       4,
			Workflow: "ci",
			Jobs: []job{
				{Name: "build", StartedAt: "yesterday", CompletedAt: "2026-10-21T10:04:00Z", Conclusion: "success"},
			},
		}},
	}
	if _, err := measure(broken); err == nil {
		problems = append(problems, "a timestamp that cannot be parsed is accepted, and it must be an error")
	}

	// ---- fixture 6: a job that ends before it starts ----
	//
	// Same reason: the seconds would be negative, and a negative bill is
	// worse than a missing one.
	inverted := stripe{
		Label: "inverted",
		Runs: []run{{
			ID:       5,
			Workflow: "ci",
			Jobs: []job{
				{Name: "build", StartedAt: "2026-10-21T10:04:00Z", CompletedAt: "2026-10-21T10:00:00Z", Conclusion: "success"},
			},
		}},
	}
	if _, err := measure(inverted); err == nil {
		problems = append(problems, "a job that ends before it starts is accepted, and it must be an error")
	}

	// ---- fixture 7: the file the tool reads ----
	//
	// The instrument has to read what `gh api` writes, and it has to refuse
	// a file that is not a measurement.
	dir, err := os.MkdirTemp("", "pr-cost-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return 1
	}
	defer os.RemoveAll(dir)

	good := filepath.Join(dir, "runs.json")
	err = os.WriteFile(good, []byte(`{"stripes":[{"label":"fixture","runs":[`+
		`{"id":1,"workflow":"ci","jobs":[`+
		`{"name":"build","started_at":"2026-10-21T10:00:00Z","completed_at":"2026-10-21T10:04:00Z","conclusion":"success"}]}]}]}`),
		0o644)
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the fixture:", err)
		return 1
	}
	stripes, err := readStripes([]string{good})
	if err != nil || len(stripes) != 1 {
		problems = append(problems, fmt.Sprintf(
			"the instrument does not read its own fixture: %d stripes, %v", len(stripes), err))
	}

	bad := filepath.Join(dir, "bad.json")
	if err := os.WriteFile(bad, []byte(`{"nothing":true}`), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the broken fixture:", err)
		return 1
	}
	if _, err := readStripes([]string{bad}); err == nil {
		problems = append(problems, "a file that is not a measurement is accepted, and it must be refused")
	}

	if len(problems) > 0 {
		for _, problem := range problems {
			fmt.Println("  SELF-TEST FAIL:", problem)
		}
		return 1
	}
	fmt.Println("  SELF-TEST OK: 7 fixtures, the wall, the busy time, the width and the refusal all hold.")
	return 0
}

// check compares one number.
func check(problems []string, what string, got, want, epsilon float64) {
	difference := got - want
	if difference < 0 {
		difference = -difference
	}
	if difference > epsilon {
		problems = append(problems, fmt.Sprintf("%s is %g, and it must be %g", what, got, want))
	}
}

// checkInt compares one count.
func checkInt(problems []string, what string, got, want int) {
	if got != want {
		problems = append(problems, fmt.Sprintf("%s is %d, and it must be %d", what, got, want))
	}
}
