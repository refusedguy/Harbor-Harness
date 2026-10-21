package aggregatebuildvcpu

// selftest.go -- the gate's own proof that it can fail.
//
// The load-bearing fixture is the LEGACY cpu metric: a run measured with the
// earlier (wrong) metric stored the FRACTION of the machine that was busy
// instead of the number of busy cores. A reader that trusted the stored
// summary instead of recomputing the series would report "the machine was
// 75 % busy" where the truth is "three of four cores were busy" — and the
// whole table is about cores.

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
)

// selfTestRun proves the aggregation still reads the runs the way the table
// says it does.
func selfTestRun() int {
	tmp, err := os.MkdirTemp("", "aggregate-build-vcpu-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	// Two runs of the baseline arm and one of the other arm. The baseline
	// run carries the LEGACY metric: a fraction of the machine, not cores.
	fixtures := []struct {
		name string
		body string
	}{
		{"a-prop0-r1", `{
      "label": "a-prop0-r1",
      "wall_seconds": 100.0,
      "ncpu": 4,
      "ncpu_stat": 4,
      "cpu_samples": [[1.0, 0.5], [2.0, 0.75], [3.0, 0.5]],
      "tasks": {"Exec": {"total_ms": 10000, "calls": 1}, "Csc": {"total_ms": 50000, "calls": 4}},
      "projects_in_flight": {"available": true, "avg_in_flight": 3.0, "max_in_flight": 4,
                             "fraction_at_or_above_4": 0.25, "projects_started": 10, "projects_finished": 10}
    }`},
		{"a-prop0-r2", `{
      "label": "a-prop0-r2",
      "wall_seconds": 200.0,
      "ncpu": 4,
      "ncpu_stat": 4,
      "cpu_samples": [[1.0, 2.0], [2.0, 2.5]],
      "tasks": {"Exec": {"total_ms": 20000, "calls": 1}, "Csc": {"total_ms": 100000, "calls": 8}}
    }`},
		{"c-m1-r1", `{
      "label": "c-m1-r1",
      "wall_seconds": 300.0,
      "ncpu": 4,
      "ncpu_stat": 4,
      "cpu_samples": [[1.0, 3.0]],
      "tasks": {"Exec": {"total_ms": 30000, "calls": 1}, "Csc": {"total_ms": 150000, "calls": 12}}
    }`},
	}
	for _, fixture := range fixtures {
		if err := writeRun(tmp, fixture.name, fixture.body); err != nil {
			fmt.Println("  SELF-TEST FAIL: cannot write the fixture:", err)
			return gate.Red
		}
	}

	runs, err := load(tmp)
	if err != nil {
		fmt.Println("  SELF-TEST FAIL:", err)
		return gate.Red
	}
	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}
	check(len(runs) == 3, fmt.Sprintf("3 runs, got %d", len(runs)))
	if len(runs) != 3 {
		for _, problem := range problems {
			fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
		}
		return gate.Red
	}

	// The legacy series is scaled to CORES: 0.5, 0.75 and 0.5 of a
	// four-cpu machine are 2, 3 and 2 busy cores.
	legacy := cpuSeries(runs[0].data)
	check(len(legacy) == 3, fmt.Sprintf("3 samples, got %d", len(legacy)))
	if len(legacy) == 3 {
		check(legacy[0] == 2.0 && legacy[1] == 3.0 && legacy[2] == 2.0,
			fmt.Sprintf("the legacy series is scaled to cores, got %v", legacy))
	}
	// A run measured in cores is NOT scaled.
	modern := cpuSeries(runs[1].data)
	if len(modern) == 2 {
		check(modern[0] == 2.0 && modern[1] == 2.5,
			fmt.Sprintf("a series already in cores is left alone, got %v", modern))
	}

	stats := statsOf(runs[0].data)
	check(stats.peak == 3.0, fmt.Sprintf("the peak is 3 busy cores, got %v", stats.peak))
	check(stats.avg == 7.0/3.0, fmt.Sprintf("the average is 2.333 busy cores, got %v", stats.avg))

	// The compile window is the wall minus the architecture gate.
	window, ok := compileWindow(runs[0].data)
	check(ok, "the compile window must be readable")
	check(window == 90.0, fmt.Sprintf("the compile window is 90.0 s, got %v", window))

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: the legacy cpu metric is scaled to cores, the compile window excludes the gate, the runs are read.")
	return gate.Green
}

// writeRun writes one measurement file.
func writeRun(dir string, name string, body string) error {
	path := filepath.Join(dir, name+".json")
	return os.WriteFile(path, []byte(body), 0o644)
}
