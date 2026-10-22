// Package testcensus is the compiled port of tools/test-census.py.
//
// The gate renders the per-test and per-project WALL census of a CI run: how
// many tests were discovered, how many passed, failed and were skipped, the
// sum of the per-test durations, the wall the runner measured, and the
// parallelism factor.
//
// It is REPORTING, never a gate: it reports, it never fails a build. A
// missing, empty or corrupt TRX set is a fact to print, not an error.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package testcensus

import (
	"fmt"
	"os"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// DEFAULT_TOP is the number of slowest tests the markdown view lists.
const DEFAULT_TOP = 25

// testRecord is one test result, with the class it belongs to.
type testRecord struct {
	shard    string
	project  string
	name     string
	outcome  string
	duration float64
	hasTime  bool
	// allocBytes is what the test allocated, when the allocation census ran.
	// hasAlloc is false when the census was off for this run.
	allocBytes int64
	hasAlloc   bool
	// allocScope says HOW the number was measured: "thread" is exact, and
	// "process" is the process-wide upper bound a parallel runner produces.
	allocScope string
}

// projectRecord is one TRX file: one test project, one assembly run.
type projectRecord struct {
	shard       string
	project     string
	tests       []testRecord
	wall        float64
	hasWall     bool
	sumDuration float64
	// sumAlloc is the project's total allocation, over the tests the census
	// measured. allocCount is how many tests carried a measurement.
	sumAlloc   int64
	allocCount int
}

// allocated is the project's total allocation, and whether any test carried a
// measurement at all.
func (p *projectRecord) allocated() (int64, bool) {
	if p.allocCount == 0 {
		return 0, false
	}
	return p.sumAlloc, true
}

// discovered is the number of results the runner produced.
func (p *projectRecord) discovered() int { return len(p.tests) }

// countedDuration is the sum of the durations the TRX actually carries.
//
// A result with no @duration contributes 0 to the sum but still counts as
// DISCOVERED: the count and the cost are separate facts.
func (p *projectRecord) countedDuration() float64 { return p.sumDuration }

// factor is the parallelism factor: the sum of the durations over the wall.
//
// ~1.0 means the project ran one lane at a time (a serial chain).
func (p *projectRecord) factor() (float64, bool) {
	if !p.hasWall || p.wall <= 0 {
		return 0, false
	}
	return p.countedDuration() / p.wall, true
}

// Run is the gate entry point.
func Run(args []string) int {
	dirs := []string{}
	summary := ""
	format := "markdown"
	top := DEFAULT_TOP
	selfTest := false
	seenDirs := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--summary":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "error: --summary needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			summary = value
		case "--format":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "error: --format needs a value")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			if value != "markdown" && value != "csv" && value != "tsv" {
				fmt.Fprintf(os.Stderr, "error: argument --format: invalid choice: %q (choose from 'markdown', 'csv', 'tsv')\n", value)
				return gate.Broken
			}
			format = value
		case "--top":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "error: --top needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			n, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "error: --top needs a number")
				return gate.Broken
			}
			top = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates test-census [dirs...] [--format markdown|csv|tsv] [--summary PATH] [--top N] [--self-test]")
			return gate.Green
		default:
			if strings.HasPrefix(arg, "-") && arg != "-" {
				fmt.Fprintf(os.Stderr, "error: unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			dirs = append(dirs, arg)
			seenDirs = true
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if !seenDirs {
		dirs = []string{"TestResults"}
	}
	if top < 0 {
		fmt.Fprintln(os.Stderr, "--top must be >= 0")
		return gate.Broken
	}

	trxPaths := findTRX(dirs)
	if len(trxPaths) == 0 {
		fmt.Printf("test-census: no .trx found under %s — nothing to report\n", strings.Join(dirs, " "))
		return gate.Green
	}

	projects := collect(trxPaths)
	if len(projects) == 0 {
		fmt.Printf("test-census: %d TRX file(s), none readable — nothing to report\n", len(trxPaths))
		return gate.Green
	}

	if format == "csv" || format == "tsv" {
		delimiter := ","
		if format == "tsv" {
			delimiter = "\t"
		}
		fmt.Println(renderCSV(projects, delimiter))
		return gate.Green
	}

	body := fmt.Sprintf("\n### Test census (%d project(s))\n", len(projects))
	body += "_Every result the runner discovered, from the TRX artifacts of this run. The wall is the TRX `<Times>` window; the parallelism factor is the sum of the per-test durations divided by that window._"
	body += "\n\n" + renderProjectTable(projects)
	body += "\n" + renderTotals(projects) + "\n"

	slowest := slowestTests(projects, top)
	if len(slowest) > 0 {
		body += fmt.Sprintf("\n#### The %d slowest test(s)\n\n", len(slowest))
		body += renderTestTable(slowest) + "\n"
	}

	greediest := greediestTests(projects, top)
	if len(greediest) > 0 {
		body += fmt.Sprintf("\n#### The %d greediest test(s) by allocation\n\n", len(greediest))
		body += "_`scope=thread` is the exact per-thread counter. `scope=process` is the process-wide counter, "
		body += "which under a parallel runner is an UPPER BOUND: it also bills whatever the tests running "
		body += "beside this one allocated._\n\n"
		body += renderAllocTable(greediest) + "\n"
	} else {
		body += "\n#### The allocation census\n\n"
		body += "_Not run: no test carried an allocation measurement. The census is written by "
		body += "`tests/AllocationCensus.cs` and stays off until `HARBOR_ALLOC_CENSUS=1` is set._\n"
	}

	if summary != "" {
		if err := appendSummary(summary, body); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the summary:", err)
			return gate.Broken
		}
	}

	fmt.Println(body)

	discovered := 0
	allocTests := 0
	var allocTotal int64
	for _, record := range projects {
		discovered += record.discovered()
		allocTests += record.allocCount
		allocTotal += record.sumAlloc
	}
	fmt.Printf("test-census: %d test(s) across %d project(s) in %d TRX file(s)",
		discovered, len(projects), len(trxPaths))
	if allocTests > 0 {
		fmt.Printf(" — allocation census: %d test(s) measured, %s in total",
			allocTests, fmtBytes(allocTotal, true))
	} else {
		fmt.Print(" — allocation census: not run (HARBOR_ALLOC_CENSUS was not set)")
	}
	if summary != "" {
		fmt.Print(" -> job summary")
	}
	fmt.Println()
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

// parseInt reads a decimal integer.
func parseInt(text string) (int, bool) {
	if text == "" {
		return 0, false
	}
	negative := false
	body := text
	if body[0] == '+' || body[0] == '-' {
		negative = body[0] == '-'
		body = body[1:]
	}
	if body == "" {
		return 0, false
	}
	value := 0
	for _, r := range body {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	if negative {
		value = -value
	}
	return value, true
}

// findTRX lists the TRX files the python original read, sorted.
func findTRX(targets []string) []string {
	var paths []string
	for _, target := range targets {
		info, err := os.Stat(target)
		if err == nil && !info.IsDir() {
			paths = append(paths, target)
			continue
		}
		paths = append(paths, gatekit.FindTRX([]string{target}, true)...)
	}
	sort.Strings(paths)
	return paths
}

// outcomeCounts counts the results by outcome.
func outcomeCounts(outcomes []string) (passed, failed, skipped, other int) {
	for _, outcome := range outcomes {
		switch strings.ToLower(outcome) {
		case "passed":
			passed++
		case "failed", "error", "timeout", "aborted":
			failed++
		case "notexecuted", "notrunnable", "inconclusive", "warning", "pending", "disconnected":
			skipped++
		default:
			other++
		}
	}
	return
}

// collect reads the TRX files into one record per file, in file order.
func collect(trxPaths []string) []*projectRecord {
	var projects []*projectRecord
	for _, path := range trxPaths {
		projectName := gatekit.SplitExt(gatekit.Basename(path))
		shard := gatekit.Basename(gatekit.Dirname(path))
		if shard == "" {
			shard = "-"
		}
		trx, err := gatekit.ParseTRX(path)
		if err != nil {
			fmt.Printf("  warn: unreadable TRX %s: %v\n", gatekit.Basename(path), err)
			continue
		}

		record := &projectRecord{shard: shard, project: projectName}

		// The wall: the <Times> window the runner reports as the assembly's
		// duration (creation -> finish).
		for _, times := range trx.Times {
			creation, okCreation := gatekit.ParseTimestamp(times.Creation)
			finish, okFinish := gatekit.ParseTimestamp(times.Finish)
			if okCreation && okFinish {
				record.wall = finish.Sub(creation).Seconds()
				record.hasWall = true
				break
			}
		}

		for _, result := range trx.Results {
			name, ok := trx.Owners[result.TestID]
			if !ok || name == "" {
				name = result.TestName
				if name == "" {
					name = "(unknown test)"
				}
			}
			outcome := result.Outcome
			if outcome == "" {
				outcome = "Unknown"
			}
			duration, hasTime := gatekit.DurationSeconds(result.Duration)
			allocBytes, allocScope, hasAlloc := parseAlloc(resultOutput(result))
			record.tests = append(record.tests, testRecord{
				shard:      shard,
				project:    projectName,
				name:       name,
				outcome:    outcome,
				duration:   duration,
				hasTime:    hasTime,
				allocBytes: allocBytes,
				hasAlloc:   hasAlloc,
				allocScope: allocScope,
			})
			if hasTime {
				record.sumDuration += duration
			}
			if hasAlloc {
				record.sumAlloc += allocBytes
				record.allocCount++
			}
		}

		projects = append(projects, record)
	}
	return projects
}

// fmtSeconds renders a duration the way the table shows it.
func fmtSeconds(value float64, has bool) string {
	if !has {
		return "—"
	}
	if value < 1.0 {
		return fmt.Sprintf("%.0f ms", value*1000)
	}
	return fmt.Sprintf("%.2f s", value)
}

// fmtFactor renders the parallelism factor.
func fmtFactor(value float64, has bool) string {
	if !has {
		return "—"
	}
	return fmt.Sprintf("%.2f", value)
}

// allocCensusLine is the marker the probe (tests/AllocationCensus.cs) writes
// into a test's own output block:
//
//	[alloc-census] bytes=123456 process=234567 scope=thread
const allocCensusLine = "[alloc-census]"

// resultOutput is the text of a result's output block: the standard output and
// the standard error, in the order the TRX lists them.
func resultOutput(result gatekit.TRXResult) string {
	var out strings.Builder
	for _, chunk := range result.Output {
		if chunk.Tag != "StdOut" && chunk.Tag != "StdErr" {
			continue
		}
		if chunk.Text == "" {
			continue
		}
		out.WriteString(chunk.Text)
		out.WriteString("\n")
	}
	return out.String()
}

// parseAlloc reads the allocation census line out of a test's output.
//
// The line is found by its marker, and the numbers are read from the fields
// after it. A line the probe did not write — or a run with the census off —
// reads as "no measurement", never as zero: zero is what a genuinely
// allocation-free test measures, and the two must not look alike.
func parseAlloc(text string) (int64, string, bool) {
	index := strings.Index(text, allocCensusLine)
	if index < 0 {
		return 0, "", false
	}
	rest := text[index+len(allocCensusLine):]
	if end := strings.IndexAny(rest, "\r\n"); end >= 0 {
		rest = rest[:end]
	}

	var bytes int64
	var scope string
	found := false
	for _, field := range strings.Fields(rest) {
		key, value, ok := strings.Cut(field, "=")
		if !ok {
			continue
		}
		switch key {
		case "bytes":
			if parsed, ok := parseInt64(value); ok {
				bytes = parsed
				found = true
			}
		case "scope":
			scope = value
		}
	}
	return bytes, scope, found
}

// parseInt64 reads a decimal integer.
func parseInt64(text string) (int64, bool) {
	if text == "" {
		return 0, false
	}
	negative := false
	body := text
	if body[0] == '+' || body[0] == '-' {
		negative = body[0] == '-'
		body = body[1:]
	}
	if body == "" {
		return 0, false
	}
	var value int64
	for _, r := range body {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int64(r-'0')
	}
	if negative {
		value = -value
	}
	return value, true
}

// fmtBytes renders a byte count the way the table shows it.
func fmtBytes(value int64, has bool) string {
	if !has {
		return "—"
	}
	sign := ""
	if value < 0 {
		sign = "-"
		value = -value
	}
	switch {
	case value < 1024:
		return fmt.Sprintf("%s%d B", sign, value)
	case value < 1024*1024:
		return fmt.Sprintf("%s%.1f KiB", sign, float64(value)/1024)
	case value < 1024*1024*1024:
		return fmt.Sprintf("%s%.1f MiB", sign, float64(value)/(1024*1024))
	default:
		return fmt.Sprintf("%s%.2f GiB", sign, float64(value)/(1024*1024*1024))
	}
}

// renderProjectTable is one row per test project: discovered, passed/failed/
// skipped, wall, factor, allocated.
func renderProjectTable(projects []*projectRecord) string {
	out := []string{
		"| shard | project | discovered | passed | failed | skipped | sum of durations | wall | parallelism | allocated |",
		"|---|---|---:|---:|---:|---:|---:|---:|---:|---:|",
	}
	for _, record := range projects {
		outcomes := make([]string, 0, len(record.tests))
		for _, t := range record.tests {
			outcomes = append(outcomes, t.outcome)
		}
		passed, failed, skipped, _ := outcomeCounts(outcomes)
		factor, hasFactor := record.factor()
		allocated, hasAlloc := record.allocated()
		out = append(out, fmt.Sprintf("| %s | `%s` | %d | %d | %d | %d | %s | %s | %s | %s |",
			record.shard, record.project, record.discovered(), passed, failed, skipped,
			fmtSeconds(record.countedDuration(), true), fmtSeconds(record.wall, record.hasWall),
			fmtFactor(factor, hasFactor), fmtBytes(allocated, hasAlloc)))
	}
	return strings.Join(out, "\n")
}

// renderTotals is the run's totals: the number a baseline compares against.
func renderTotals(projects []*projectRecord) string {
	var outcomes []string
	totalDuration := 0.0
	var walls []float64
	var totalAlloc int64
	allocTests := 0
	for _, record := range projects {
		for _, t := range record.tests {
			outcomes = append(outcomes, t.outcome)
		}
		totalDuration += record.countedDuration()
		if record.hasWall {
			walls = append(walls, record.wall)
		}
		totalAlloc += record.sumAlloc
		allocTests += record.allocCount
	}
	passed, failed, skipped, _ := outcomeCounts(outcomes)
	var totalWall float64
	hasTotalWall := len(walls) > 0
	for _, wall := range walls {
		totalWall += wall
	}
	var factor float64
	hasFactor := false
	if hasTotalWall && totalWall != 0 {
		factor = totalDuration / totalWall
		hasFactor = true
	}
	return fmt.Sprintf("| **TOTAL** | **%d project(s)** | **%d** | **%d** | **%d** | **%d** | **%s** | **%s** | **%s** | **%s** |",
		len(projects), len(outcomes), passed, failed, skipped,
		fmtSeconds(totalDuration, true), fmtSeconds(totalWall, hasTotalWall), fmtFactor(factor, hasFactor),
		fmtBytes(totalAlloc, allocTests > 0))
}

// slowestTests is the `top` most expensive tests of the run, most expensive
// first. The sort is stable, so equal durations keep their file order.
func slowestTests(projects []*projectRecord, top int) []testRecord {
	var measured []testRecord
	for _, record := range projects {
		for _, t := range record.tests {
			if t.hasTime {
				measured = append(measured, t)
			}
		}
	}
	sort.SliceStable(measured, func(i, j int) bool {
		return measured[i].duration > measured[j].duration
	})
	if top > 0 && len(measured) > top {
		measured = measured[:top]
	}
	return measured
}

// renderTestTable is the per-test table of the slowest tests.
func renderTestTable(tests []testRecord) string {
	out := []string{
		"| shard | project | test | outcome | duration |",
		"|---|---|---|---|---:|",
	}
	for _, test := range tests {
		out = append(out, fmt.Sprintf("| %s | `%s` | `%s` | %s | %s |",
			test.shard, test.project, test.name, test.outcome, fmtSeconds(test.duration, test.hasTime)))
	}
	return strings.Join(out, "\n")
}

// greediestTests is the `top` tests with the largest allocation, largest
// first. The sort is stable, so equal allocations keep their file order.
func greediestTests(projects []*projectRecord, top int) []testRecord {
	var measured []testRecord
	for _, record := range projects {
		for _, t := range record.tests {
			if t.hasAlloc {
				measured = append(measured, t)
			}
		}
	}
	sort.SliceStable(measured, func(i, j int) bool {
		return measured[i].allocBytes > measured[j].allocBytes
	})
	if top > 0 && len(measured) > top {
		measured = measured[:top]
	}
	return measured
}

// renderAllocTable is the per-test allocation ranking.
func renderAllocTable(tests []testRecord) string {
	out := []string{
		"| shard | project | test | allocated | scope |",
		"|---|---|---|---:|---|",
	}
	for _, test := range tests {
		out = append(out, fmt.Sprintf("| %s | `%s` | `%s` | %s | %s |",
			test.shard, test.project, test.name, fmtBytes(test.allocBytes, test.hasAlloc), test.allocScope))
	}
	return strings.Join(out, "\n")
}

// renderCSV is one row per test, machine-sortable: the census the
// maintainers sort.
func renderCSV(projects []*projectRecord, delimiter string) string {
	var out strings.Builder
	writeRow := func(fields []string) {
		for index, field := range fields {
			if index > 0 {
				out.WriteString(delimiter)
			}
			out.WriteString(csvField(field, delimiter))
		}
		out.WriteString("\n")
	}
	writeRow([]string{"shard", "project", "test", "outcome", "duration_s", "alloc_bytes", "alloc_scope"})
	for _, record := range projects {
		for _, test := range record.tests {
			duration := ""
			if test.hasTime {
				duration = fmt.Sprintf("%.6f", test.duration)
			}
			allocated := ""
			if test.hasAlloc {
				allocated = fmt.Sprintf("%d", test.allocBytes)
			}
			writeRow([]string{test.shard, test.project, test.name, test.outcome, duration, allocated, test.allocScope})
		}
	}
	return strings.TrimRight(out.String(), "\n")
}

// csvField quotes a field the way the python csv module does: minimally, and
// only when the field carries a delimiter, a quote or a newline.
func csvField(field string, delimiter string) string {
	needs := strings.Contains(field, delimiter) ||
		strings.Contains(field, "\"") ||
		strings.ContainsAny(field, "\r\n")
	if !needs {
		return field
	}
	return "\"" + strings.ReplaceAll(field, "\"", "\"\"") + "\""
}

// appendSummary appends the body to the summary file.
func appendSummary(path string, text string) error {
	fh, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer fh.Close()
	_, err = fh.WriteString(text + "\n")
	return err
}
