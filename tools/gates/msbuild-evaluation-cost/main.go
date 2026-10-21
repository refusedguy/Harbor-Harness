// Package msbuildevaluationcost is the compiled port of
// tools/msbuild-evaluation-cost.py.
//
// The gate reads the MSBuild PERFORMANCE SUMMARY and answers what a Release
// compile of this tree spends on PROJECT EVALUATION (issue #1919).
//
// It is a MEASUREMENT, not a gate: it reports into the job summary and never
// fails a build. The only non-zero exit is when the summaries could not be
// read — a measurement instrument that cannot read its input must say so
// rather than report zero.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package msbuildevaluationcost

import (
	"fmt"
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// The four sections of the MSBuild performance summary, and the key the rows
// under each are grouped by.
var sectionKeys = map[string]string{
	"Project Evaluation Performance Summary:": "evaluation",
	"Project Performance Summary:":            "project",
	"Target Performance Summary:":             "target",
	"Task Performance Summary:":               "task",
}

// rowRe is `  1234 ms  GetTargetFrameworks                       109 calls`.
var rowRe = regexp.MustCompile(`^\s*(?P<ms>\d+|\*)\s+ms\s+(?P<scope>\S.*?)\s+(?P<calls>\d+)\s+calls\s*$`)

// elapsedRe is "Time Elapsed 00:06:12.34" — MSBuild's own report of the
// build's wall time.
var elapsedRe = regexp.MustCompile(`(?m)^Time Elapsed (\d+):(\d{2}):(\d{2}(?:\.\d+)?)\s*$`)

// referenceTargets are the targets whose time IS a project evaluation of
// another project: `GetTargetFrameworks` asks a referenced project for its
// target frameworks (which evaluates it), and the two `_GetProjectReference…`
// targets do the same through the project-reference protocol.
var referenceTargets = map[string]bool{
	"GetTargetFrameworks":                           true,
	"GetTargetFrameworksWithTargetFrameworkMoniker": true,
	"ResolveProjectReferences":                      true,
	"_GetProjectReferenceTargetFrameworkProperties": true,
	"GetProjectReferenceTargetFrameworkProperties":  true,
}

// summaryRow is one row of a performance summary section.
//
// ms is -1 for a RE-ENTRANT evaluation: the same project was evaluated
// again while an evaluation of it was already in flight, and the counter has
// no value for it.
type summaryRow struct {
	scope string
	ms    int
	calls int
}

// runResult is one build's numbers, or an error.
type runResult struct {
	label             string
	path              string
	err               string
	evaluationTotalMS int
	evaluationCounted int
	evaluationReentry int
	projectsEvaluated int
	slowest           []summaryRow
	targets           []targetTotal
	wallSeconds       float64
	hasWall           bool
}

// targetTotal is the total time and the call count of one target.
type targetTotal struct {
	name  string
	ms    int
	calls int
}

// Run is the gate entry point.
func Run(args []string) int {
	var runs []string
	summary := ""
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--run":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Println("::error::--run needs LABEL:SUMMARY_FILE:WALL_SECONDS")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			runs = append(runs, value)
		case "--summary":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Println("::error::--summary needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			summary = value
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates msbuild-evaluation-cost --run LABEL:SUMMARY_FILE:WALL_SECONDS [--summary PATH] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "::error::unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if len(runs) == 0 {
		fmt.Println("::error::no --run given: there is nothing to measure")
		return gate.Red
	}

	results := make([]runResult, 0, len(runs))
	for _, spec := range runs {
		parts := strings.SplitN(spec, ":", 3)
		if len(parts) != 3 {
			fmt.Printf("::error::malformed --run %s: expected LABEL:SUMMARY_FILE:WALL_SECONDS\n", gatekit.PyRepr(spec))
			return gate.Red
		}
		results = append(results, readRun(parts[0], parts[1], parts[2]))
	}

	report := render(results)
	fmt.Println(report)
	if summary != "" {
		if err := appendFile(summary, report); err != nil {
			fmt.Fprintln(os.Stderr, "::error::cannot write the summary:", err)
			return gate.Broken
		}
	}
	for _, result := range results {
		if result.err != "" {
			return gate.Red
		}
	}
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

// parseSummary reads the sections of a performance summary.
//
// section -> the rows under it, in file order.
func parseSummary(text string) map[string][]summaryRow {
	sections := map[string][]summaryRow{}
	current := ""
	for _, line := range strings.Split(text, "\n") {
		header := strings.TrimSpace(line)
		if key, ok := sectionKeys[header]; ok {
			current = key
			if _, seen := sections[current]; !seen {
				sections[current] = nil
			}
			continue
		}
		if current == "" {
			continue
		}
		match := rowRe.FindStringSubmatch(line)
		if match == nil {
			// A blank line ends the section; anything else that does not
			// parse is not a row and is ignored.
			if strings.TrimSpace(line) == "" {
				current = ""
			}
			continue
		}
		ms := -1
		if match[1] != "*" {
			parsed, err := strconv.Atoi(match[1])
			if err != nil {
				continue
			}
			ms = parsed
		}
		calls, err := strconv.Atoi(match[3])
		if err != nil {
			continue
		}
		sections[current] = append(sections[current], summaryRow{scope: match[2], ms: ms, calls: calls})
	}
	return sections
}

// totalEvaluationMS is (total ms, evaluations counted, evaluations without a
// value).
func totalEvaluationMS(rows []summaryRow) (int, int, int) {
	total, counted, reentrant := 0, 0, 0
	for _, row := range rows {
		if row.ms < 0 {
			reentrant += row.calls
			continue
		}
		total += row.ms
		counted += row.calls
	}
	return total, counted, reentrant
}

// targetTotals is target name -> (total ms, calls), in first-seen order.
func targetTotals(rows []summaryRow) []targetTotal {
	var totals []targetTotal
	index := map[string]int{}
	for _, row := range rows {
		if row.ms < 0 {
			continue
		}
		name := strings.TrimSpace(row.scope)
		at, ok := index[name]
		if !ok {
			index[name] = len(totals)
			totals = append(totals, targetTotal{name: name})
			at = len(totals) - 1
		}
		totals[at].ms += row.ms
		totals[at].calls += row.calls
	}
	return totals
}

// readRun reads one build's numbers, or the reason they are not there.
func readRun(label string, path string, wall string) runResult {
	result := runResult{label: label, path: path}
	info, err := os.Stat(path)
	if err != nil || info.IsDir() {
		result.err = fmt.Sprintf("the performance summary %s does not exist", path)
		return result
	}
	data, err := os.ReadFile(path)
	if err != nil {
		result.err = fmt.Sprintf("the performance summary %s does not exist", path)
		return result
	}
	text := string(data)
	sections := parseSummary(text)
	evalRows, ok := sections["evaluation"]
	if !ok {
		result.err = fmt.Sprintf("%s has no 'Project Evaluation Performance Summary' section — the build was not asked for one (add -clp:PerformanceSummary)", path)
		return result
	}
	total, counted, reentrant := totalEvaluationMS(evalRows)
	result.evaluationTotalMS = total
	result.evaluationCounted = counted
	result.evaluationReentry = reentrant
	result.projectsEvaluated = len(evalRows)

	// The slowest evaluations, the largest first. The sort is stable, so
	// equal times keep their file order.
	var measured []summaryRow
	for _, row := range evalRows {
		if row.ms >= 0 {
			measured = append(measured, row)
		}
	}
	sort.SliceStable(measured, func(i, j int) bool { return measured[i].ms > measured[j].ms })
	if len(measured) > 10 {
		measured = measured[:10]
	}
	result.slowest = measured
	result.targets = targetTotals(sections["target"])

	if wall != "" {
		if parsed, err := strconv.ParseFloat(wall, 64); err == nil {
			result.wallSeconds = parsed
			result.hasWall = true
		}
	}
	if !result.hasWall {
		// The build's own report of its wall time, which the performance
		// summary does not carry. The LAST one is the build's: a log can
		// hold the "Time Elapsed" of a nested build too.
		matches := elapsedRe.FindAllStringSubmatch(text, -1)
		if len(matches) > 0 {
			last := matches[len(matches)-1]
			hours, _ := strconv.Atoi(last[1])
			minutes, _ := strconv.Atoi(last[2])
			seconds, _ := strconv.ParseFloat(last[3], 64)
			result.wallSeconds = float64(hours)*3600 + float64(minutes)*60 + seconds
			result.hasWall = true
		}
	}
	return result
}

// fmtSeconds renders a wall time.
func fmtSeconds(value float64, has bool) string {
	if !has {
		return "n/a"
	}
	return fmt.Sprintf("%.1f s", value)
}

// fmtMS renders a millisecond count as seconds.
func fmtMS(value int) string {
	return fmt.Sprintf("%.1f s", float64(value)/1000.0)
}

// render is the report.
func render(runs []runResult) string {
	var lines []string
	var ok []runResult
	var broken []runResult
	for _, run := range runs {
		if run.err != "" {
			broken = append(broken, run)
		} else {
			ok = append(ok, run)
		}
	}

	lines = append(lines, "## Project-evaluation cost of the Release compile (issue #1919)")
	lines = append(lines, "")
	if len(broken) > 0 {
		lines = append(lines, "**Not measured:**")
		lines = append(lines, "")
		for _, run := range broken {
			lines = append(lines, fmt.Sprintf("- `%s`: %s", run.label, run.err))
		}
		lines = append(lines, "")
	}
	if len(ok) == 0 {
		return strings.Join(lines, "\n") + "\n"
	}

	lines = append(lines, "| run | evaluations | distinct projects | evaluation time | build wall |")
	lines = append(lines, "|---|---|---|---|---|")
	for _, run := range ok {
		lines = append(lines, fmt.Sprintf("| `%s` | %d | %d | %s | %s |",
			run.label, run.evaluationCounted, run.projectsEvaluated,
			fmtMS(run.evaluationTotalMS), fmtSeconds(run.wallSeconds, run.hasWall)))
	}
	lines = append(lines, "")
	lines = append(lines, "_The evaluation time is the SUM over every evaluation, so it is larger than the wall-clock share: the evaluations run in parallel on the runner's nodes, and the same project is evaluated more than once (once for its target frameworks, once for the build)._")
	lines = append(lines, "")
	for _, run := range ok {
		if run.evaluationCounted > 0 {
			mean := float64(run.evaluationTotalMS) / float64(run.evaluationCounted)
			lines = append(lines, fmt.Sprintf("**`%s`: %.0f ms per evaluation** — the number dotnet/msbuild#4025 quotes as 180–400 ms.",
				run.label, mean))
			lines = append(lines, "")
		}
	}

	if len(ok) >= 2 {
		first, second := ok[0], ok[1]
		deltaMS := second.evaluationTotalMS - first.evaluationTotalMS
		lines = append(lines, fmt.Sprintf("**The difference between the two runs is the NuGet SDK resolver's cost on this tree**: %s of evaluation with the resolver enabled, %s with it disabled (`MSBUILDDISABLENUGETSDKRESOLVER=1`) — a difference of **%s**.",
			fmtMS(first.evaluationTotalMS), fmtMS(second.evaluationTotalMS), fmtMS(deltaMS)))
		lines = append(lines, "")
		if first.hasWall && second.hasWall {
			deltaWall := second.wallSeconds - first.wallSeconds
			lines = append(lines, fmt.Sprintf("The same difference in the build's own wall time is **%+.1f s** (%s -> %s).",
				deltaWall, fmtSeconds(first.wallSeconds, first.hasWall), fmtSeconds(second.wallSeconds, second.hasWall)))
			lines = append(lines, "")
		}
	}

	lines = append(lines, "### Where the evaluation time goes (the reference-resolution targets)")
	lines = append(lines, "")
	lines = append(lines, "_The times below are INCLUSIVE: a target that runs inside itself is counted at every nesting level, and `ResolveProjectReferences` does — it resolves a project's references, and resolving them resolves THEIR references. So these numbers are not a cost and must not be added up; the CALL counts are the meaningful part, and the evaluation time above is the cost._")
	lines = append(lines, "")
	lines = append(lines, "| target | inclusive time | calls |")
	lines = append(lines, "|---|---|---|")
	for _, run := range ok {
		var rows []targetTotal
		for _, target := range run.targets {
			if referenceTargets[target.name] {
				rows = append(rows, target)
			}
		}
		sort.SliceStable(rows, func(i, j int) bool { return rows[i].ms > rows[j].ms })
		for _, target := range rows {
			lines = append(lines, fmt.Sprintf("| `%s` (%s) | %s | %d |", target.name, run.label, fmtMS(target.ms), target.calls))
		}
	}
	lines = append(lines, "")

	lines = append(lines, "### The slowest project evaluations")
	lines = append(lines, "")
	for _, run := range ok {
		lines = append(lines, fmt.Sprintf("**`%s`**", run.label))
		lines = append(lines, "")
		lines = append(lines, "| project | evaluation time |")
		lines = append(lines, "|---|---|")
		for _, row := range run.slowest {
			ms := row.ms
			if ms < 0 {
				ms = 0
			}
			lines = append(lines, fmt.Sprintf("| `%s` | %s |", row.scope, fmtMS(ms)))
		}
		lines = append(lines, "")
	}

	for _, run := range ok {
		if run.evaluationReentry > 0 {
			lines = append(lines, fmt.Sprintf("_%s: %d re-entrant evaluation(s) (the same project evaluated while an evaluation of it was still in flight) have no value in MSBuild's summary and are not in the totals above — the totals are a lower bound._",
				run.label, run.evaluationReentry))
			lines = append(lines, "")
		}
	}
	return strings.Join(lines, "\n") + "\n"
}

// appendFile appends text to a file.
func appendFile(path string, text string) error {
	fh, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer fh.Close()
	_, err = fh.WriteString(text)
	return err
}
