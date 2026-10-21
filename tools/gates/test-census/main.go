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
}

// projectRecord is one TRX file: one test project, one assembly run.
type projectRecord struct {
	shard       string
	project     string
	tests       []testRecord
	wall        float64
	hasWall     bool
	sumDuration float64
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

	if summary != "" {
		if err := appendSummary(summary, body); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the summary:", err)
			return gate.Broken
		}
	}

	fmt.Println(body)

	discovered := 0
	for _, record := range projects {
		discovered += record.discovered()
	}
	fmt.Printf("test-census: %d test(s) across %d project(s) in %d TRX file(s)",
		discovered, len(projects), len(trxPaths))
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
			record.tests = append(record.tests, testRecord{
				shard:    shard,
				project:  projectName,
				name:     name,
				outcome:  outcome,
				duration: duration,
				hasTime:  hasTime,
			})
			if hasTime {
				record.sumDuration += duration
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

// renderProjectTable is one row per test project: discovered, passed/failed/
// skipped, wall, factor.
func renderProjectTable(projects []*projectRecord) string {
	out := []string{
		"| shard | project | discovered | passed | failed | skipped | sum of durations | wall | parallelism |",
		"|---|---|---:|---:|---:|---:|---:|---:|---:|",
	}
	for _, record := range projects {
		outcomes := make([]string, 0, len(record.tests))
		for _, t := range record.tests {
			outcomes = append(outcomes, t.outcome)
		}
		passed, failed, skipped, _ := outcomeCounts(outcomes)
		factor, hasFactor := record.factor()
		out = append(out, fmt.Sprintf("| %s | `%s` | %d | %d | %d | %d | %s | %s | %s |",
			record.shard, record.project, record.discovered(), passed, failed, skipped,
			fmtSeconds(record.countedDuration(), true), fmtSeconds(record.wall, record.hasWall),
			fmtFactor(factor, hasFactor)))
	}
	return strings.Join(out, "\n")
}

// renderTotals is the run's totals: the number a baseline compares against.
func renderTotals(projects []*projectRecord) string {
	var outcomes []string
	totalDuration := 0.0
	var walls []float64
	for _, record := range projects {
		for _, t := range record.tests {
			outcomes = append(outcomes, t.outcome)
		}
		totalDuration += record.countedDuration()
		if record.hasWall {
			walls = append(walls, record.wall)
		}
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
	return fmt.Sprintf("| **TOTAL** | **%d project(s)** | **%d** | **%d** | **%d** | **%d** | **%s** | **%s** | **%s** |",
		len(projects), len(outcomes), passed, failed, skipped,
		fmtSeconds(totalDuration, true), fmtSeconds(totalWall, hasTotalWall), fmtFactor(factor, hasFactor))
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
	writeRow([]string{"shard", "project", "test", "outcome", "duration_s"})
	for _, record := range projects {
		for _, test := range record.tests {
			duration := ""
			if test.hasTime {
				duration = fmt.Sprintf("%.6f", test.duration)
			}
			writeRow([]string{test.shard, test.project, test.name, test.outcome, duration})
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
