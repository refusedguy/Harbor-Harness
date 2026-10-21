// Package aggregatebuildvcpu is the compiled port of tools/aggregate-build-vcpu.py.
//
// The gate turns the per-run measurements into the table: the wall-time
// range, the CPU utilisation, the compiler work and the RATIO against the
// baseline arm — the number that adjudicates issue #1929.
//
// It is a REPORTER, not a gate: it prints and never fails a build.
//
// The text this prints is the text the python original printed.
package aggregatebuildvcpu

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// Run is the gate entry point.
func Run(args []string) int {
	directory := ""
	baseline := "a-prop0"
	selfTest := false
	seenDirectory := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--baseline":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--baseline needs a name")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			baseline = value
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates aggregate-build-vcpu <directory> [--baseline a-prop0] [--self-test]")
			return gate.Green
		default:
			if strings.HasPrefix(arg, "-") && arg != "-" {
				fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			if seenDirectory {
				fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			directory = arg
			seenDirectory = true
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if !seenDirectory {
		fmt.Fprintln(os.Stderr, "aggregate-build-vcpu: the measurement directory is required")
		return gate.Broken
	}

	runs, err := load(directory)
	if err != nil {
		fmt.Fprintln(os.Stderr, "aggregate-build-vcpu:", err)
		return gate.Broken
	}
	if len(runs) == 0 {
		fmt.Printf("no measurements in %s\n", directory)
		return gate.Red
	}

	printTable(runs, baseline)
	return gate.Green
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// run is one measured run.
type run struct {
	label string
	data  map[string]any
}

// load reads every measurement file of a directory.
//
// The files are read in the order of their names, and the label of a run is
// the one it carries, or the name of the file without its extension.
func load(directory string) ([]run, error) {
	entries, err := os.ReadDir(directory)
	if err != nil {
		return nil, err
	}
	names := make([]string, 0, len(entries))
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		names = append(names, entry.Name())
	}
	sort.Strings(names)

	var runs []run
	for _, name := range names {
		if !strings.HasSuffix(name, ".json") {
			continue
		}
		data, err := gatekit.ReadJSONObject(filepath.Join(directory, name))
		if err != nil {
			return nil, fmt.Errorf("cannot read %s: %w", name, err)
		}
		label, ok := gatekit.JSONText(data, "label")
		if !ok {
			label = strings.TrimSuffix(name, ".json")
		}
		runs = append(runs, run{label: label, data: data})
	}
	return runs, nil
}

// armOf is the arm a run belongs to.
func armOf(label string) string {
	if idx := strings.Index(label, "-r"); idx >= 0 {
		return label[:idx]
	}
	return label
}

// fmtRange is the "min..max (median X)" cell.
func fmtRange(values []float64) string {
	if len(values) == 0 {
		return ""
	}
	ordered := append([]float64{}, values...)
	sort.Float64s(ordered)
	return fmt.Sprintf("%.1f..%.1f (median %.1f)",
		ordered[0], ordered[len(ordered)-1], median(ordered))
}

// median is the python statistics.median: the middle value, or the mean of
// the two middle values for an even count.
func median(sorted []float64) float64 {
	count := len(sorted)
	if count == 0 {
		return 0
	}
	middle := count / 2
	if count%2 == 1 {
		return sorted[middle]
	}
	return (sorted[middle-1] + sorted[middle]) / 2
}

// mean is the python statistics.mean.
func mean(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	total := 0.0
	for _, value := range values {
		total += value
	}
	return total / float64(len(values))
}

// compileWindow is the wall time minus the architecture gate (a serial Exec
// task).
func compileWindow(data map[string]any) (float64, bool) {
	wall, ok := gatekit.JSONNumber(data, "wall_seconds")
	if !ok {
		return 0, false
	}
	gate := taskTotalMS(data, "Exec")
	if gate == 0 {
		return wall, true
	}
	return wall - gate/1000.0, true
}

// taskTotalMS is the total time of one MSBuild task.
func taskTotalMS(data map[string]any, task string) float64 {
	tasks := gatekit.JSONMap(data, "tasks")
	entry := gatekit.JSONMap(tasks, task)
	if entry == nil {
		return 0
	}
	value, ok := gatekit.JSONNumber(entry, "total_ms")
	if !ok {
		return 0
	}
	return value
}

// taskCalls is the call count of one MSBuild task.
func taskCalls(data map[string]any, task string) float64 {
	tasks := gatekit.JSONMap(data, "tasks")
	entry := gatekit.JSONMap(tasks, task)
	if entry == nil {
		return 0
	}
	value, ok := gatekit.JSONNumber(entry, "calls")
	if !ok {
		return 0
	}
	return value
}

// cpuSeries is the busy-core series of a run, in CORES.
//
// The samples are recomputed from the raw series instead of trusting the
// stored summary, so that a run measured with the earlier (wrong) metric —
// which stored the fraction of the machine that was busy instead of the number
// of busy cores — is scaled correctly instead of silently wrong.
func cpuSeries(data map[string]any) []float64 {
	ncpu := ncpuOf(data)
	samples := gatekit.JSONList(data, "cpu_samples")
	if len(samples) == 0 {
		return nil
	}
	values := make([]float64, 0, len(samples))
	for _, sample := range samples {
		pair, ok := sample.([]any)
		if !ok || len(pair) < 2 {
			continue
		}
		value, ok := gatekit.JSONNumber(map[string]any{"v": pair[1]}, "v")
		if !ok {
			continue
		}
		values = append(values, value)
	}
	if len(values) == 0 {
		return nil
	}
	peak := values[0]
	for _, value := range values {
		if value > peak {
			peak = value
		}
	}
	if peak <= 1.5 {
		// legacy format: fraction of the machine, not cores
		scaled := make([]float64, len(values))
		for index, value := range values {
			scaled[index] = value * ncpu
		}
		return scaled
	}
	return values
}

// ncpuOf is how many CPUs the run measured against.
func ncpuOf(data map[string]any) float64 {
	if value, ok := gatekit.JSONNumber(data, "ncpu_stat"); ok && value != 0 {
		return value
	}
	if value, ok := gatekit.JSONNumber(data, "ncpu"); ok && value != 0 {
		return value
	}
	return 1
}

// cpuStats is the summary of the busy-core series.
type cpuStats struct {
	avg      float64
	peak     float64
	at35     float64
	below15  float64
	hasValue bool
}

// statsOf is the summary of the CPU series of one run.
func statsOf(data map[string]any) cpuStats {
	values := cpuSeries(data)
	if len(values) == 0 {
		return cpuStats{}
	}
	ncpu := ncpuOf(data)
	at35, below15 := 0.0, 0.0
	peak := values[0]
	for _, value := range values {
		if value >= 0.875*ncpu {
			at35++
		}
		if value < 0.375*ncpu {
			below15++
		}
		if value > peak {
			peak = value
		}
	}
	count := float64(len(values))
	return cpuStats{
		avg:      mean(values),
		peak:     peak,
		at35:     at35 / count,
		below15:  below15 / count,
		hasValue: true,
	}
}

// printTable is the report.
func printTable(runs []run, baseline string) {
	// The arms, in the order the runs were read.
	var armNames []string
	arms := map[string][]run{}
	for _, item := range runs {
		arm := armOf(item.label)
		if _, ok := arms[arm]; !ok {
			armNames = append(armNames, arm)
		}
		arms[arm] = append(arms[arm], item)
	}

	var baselineWalls []float64
	for _, item := range arms[baseline] {
		if wall, ok := gatekit.JSONNumber(item.data, "wall_seconds"); ok {
			baselineWalls = append(baselineWalls, wall)
		}
	}
	var baselineMedian float64
	hasBaselineMedian := len(baselineWalls) > 0
	if hasBaselineMedian {
		ordered := append([]float64{}, baselineWalls...)
		sort.Float64s(ordered)
		baselineMedian = median(ordered)
	}

	var baselineWindows []float64
	for _, item := range arms[baseline] {
		if window, ok := compileWindow(item.data); ok {
			baselineWindows = append(baselineWindows, window)
		}
	}
	var baselineWindowMedian float64
	hasBaselineWindowMedian := len(baselineWindows) > 0
	if hasBaselineWindowMedian {
		ordered := append([]float64{}, baselineWindows...)
		sort.Float64s(ordered)
		baselineWindowMedian = median(ordered)
	}

	// Runner-speed proxy. The architecture gate is the SAME single-process
	// workload in every arm, so its duration says how fast the machine that
	// ran the build was.
	var gates []float64
	for _, item := range arms[baseline] {
		gate := taskTotalMS(item.data, "Exec")
		if gate != 0 {
			gates = append(gates, gate)
		}
	}
	var baselineGateMedian float64
	hasBaselineGateMedian := len(gates) > 0
	if hasBaselineGateMedian {
		ordered := append([]float64{}, gates...)
		sort.Float64s(ordered)
		baselineGateMedian = median(ordered)
	}

	normalizedWall := func(data map[string]any) (float64, bool) {
		wall, ok := gatekit.JSONNumber(data, "wall_seconds")
		if !ok {
			return 0, false
		}
		gate := taskTotalMS(data, "Exec")
		if wall == 0 || gate == 0 || !hasBaselineGateMedian {
			return 0, false
		}
		return wall * (baselineGateMedian / gate), true
	}

	fmt.Printf("%-12s %2s %-30s %10s %10s %10s %9s %6s %9s %30s %30s\n",
		"arm", "n", "wall (s)", "avg cores", "max cores", "at 3.5+", "Csc (s)", "Csc n",
		"gate (s)", "wall ratio", "window ratio")

	sortedArms := append([]string{}, armNames...)
	sort.Strings(sortedArms)
	for _, arm := range sortedArms {
		data := arms[arm]
		var walls []float64
		var avg, peak, at4 []float64
		var cscMS, cscN, gateMS []float64
		for _, item := range data {
			if wall, ok := gatekit.JSONNumber(item.data, "wall_seconds"); ok {
				walls = append(walls, wall)
			}
			stats := statsOf(item.data)
			avg = append(avg, stats.avg)
			peak = append(peak, stats.peak)
			at4 = append(at4, stats.at35)
			cscMS = append(cscMS, taskTotalMS(item.data, "Csc"))
			cscN = append(cscN, taskCalls(item.data, "Csc"))
			gateMS = append(gateMS, taskTotalMS(item.data, "Exec"))
		}
		var ratios []float64
		if hasBaselineMedian {
			for _, wall := range walls {
				ratios = append(ratios, wall/baselineMedian)
			}
		}
		var windows []float64
		for _, item := range data {
			if window, ok := compileWindow(item.data); ok {
				windows = append(windows, window)
			}
		}
		var windowRatios []float64
		if hasBaselineWindowMedian {
			for _, window := range windows {
				windowRatios = append(windowRatios, window/baselineWindowMedian)
			}
		}
		wallCell := "n/a"
		if len(ratios) > 0 {
			wallCell = fmtRange(ratios)
		}
		windowCell := "n/a"
		if len(windowRatios) > 0 {
			windowCell = fmtRange(windowRatios)
		}
		fmt.Printf("%-12s %2d %-30s %10.2f %10.2f %10.2f %9.1f %6.1f %9.1f %30s %30s\n",
			arm, len(data), fmtRange(walls), mean(avg), mean(peak), mean(at4),
			mean(cscMS)/1000, mean(cscN), mean(gateMS)/1000, wallCell, windowCell)

		var normalized []float64
		for _, item := range data {
			if value, ok := normalizedWall(item.data); ok {
				normalized = append(normalized, value)
			}
		}
		if len(normalized) > 0 && hasBaselineMedian {
			var normRatios []float64
			for _, value := range normalized {
				normRatios = append(normRatios, value/baselineMedian)
			}
			fmt.Printf("%-12s %2s speed-normalized wall ratio: %s\n", "", "", fmtRange(normRatios))
		}
	}

	fmt.Println()
	sortedRuns := append([]run{}, runs...)
	sort.Slice(sortedRuns, func(i, j int) bool { return sortedRuns[i].label < sortedRuns[j].label })
	for _, item := range sortedRuns {
		profile := gatekit.JSONMap(item.data, "projects_in_flight")
		if profile == nil {
			continue
		}
		if !gatekit.JSONTruthy(profile, "available") {
			continue
		}
		fmt.Printf("%s: projects in flight — avg %s, max %s, >=4 for %s of the build (%s started, %s finished)\n",
			item.label,
			gatekit.JSONTextOf(profile, "avg_in_flight"),
			gatekit.JSONTextOf(profile, "max_in_flight"),
			gatekit.JSONTextOf(profile, "fraction_at_or_above_4"),
			gatekit.JSONTextOf(profile, "projects_started"),
			gatekit.JSONTextOf(profile, "projects_finished"))
	}
}
