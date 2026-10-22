// Package measurecompilecost is the instrument of issue #2112: what every
// project of the tree costs the compiler, and what a change in one of them
// costs everybody who depends on it.
//
// It reads a build log (the MSBuild performance summary CI already prints),
// the solution and the tree, and it prints two tables:
//
//   - the cost of every project: the seconds the compiler spent on it, how
//     many files it reads and how many lines they hold;
//   - the price of a change: for every project, how many projects depend on
//     it (directly or through another project) and what that set costs.
//
// With --baseline it compares the measurement against a recorded one and
// goes red when a project grew past its budget: more than 20 % AND more
// than 10 seconds. Both, so a small project that grew by a third and is
// worth half a second is not a finding.
//
// It is a MEASUREMENT first and a gate second: the numbers come from a log,
// and the log has to be one that can carry them (see the buildcost package
// for why a parallel build cannot).
package measurecompilecost

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/buildcost"
	"harbor.local/gates/internal/gate"
)

// Budget is the growth a project may have before the gate goes red.
//
// Both conditions have to hold: the growth is more than the percentage AND
// more than the seconds. A percentage alone flags a project that went from
// half a second to one second; a number of seconds alone flags a project
// that was already expensive.
type Budget struct {
	Percent float64
	Seconds float64
}

// DefaultBudget is the budget the repository records.
var DefaultBudget = Budget{Percent: 20, Seconds: 10}

// maxAttributionRatio is the largest ratio of the per-project times over the
// compiler's total that still means "the rows are the work".
//
// A build that ran one compiler at a time measures about 3 (the rows hold
// the work and the negotiation of the target frameworks). A build that ran
// four compilers at once measures 25 and more, because every project's row
// then holds the builds of the projects it references as well.
//
// The line between them is far from the numbers, so the gate refuses a log
// that is on the wrong side of it rather than pricing a radius with the
// scheduler.
const maxAttributionRatio = 12

// minAttributionRatio is the smallest ratio of the per-project times over the
// compiler's total that still means "the rows are the work".
//
// The compiler's time is INSIDE the rows: every Csc call happens while some
// project is being built, so the rows of a complete log add up to at least
// the compiler's total. A ratio below 1 therefore means the log is missing
// rows — a truncated log, a log of a different build, or a log whose project
// paths do not match the solution.
//
// A ratio below 1 is refused for the same reason a ratio above 12 is: the
// numbers would not be what they claim to be. With the rows short, the
// instrument would spread the compiler's whole total over the few rows it
// has, and every project would be priced with a share of a total it did not
// earn — a plausible-looking number that the log does not support.
//
// The floor is 1 and not higher because the rows of a complete log add up to
// the compiler's total PLUS the negotiation of the target frameworks, so a
// complete log measures above 1, and a log that measures below 1 has rows
// missing.
const minAttributionRatio = 1

// priceRefusal says whether a log's per-project rows can be priced at all.
//
// The empty string means they can. Anything else is the text that says why
// not, and the caller REFUSES the log: it exits 2, "the gate could not run",
// and prints no table. The two refusals are the two directions of the same
// tell:
//
//   - a ratio above the ceiling is a build that ran its compilers in
//     parallel, so the rows hold waiting rather than work;
//   - a ratio below the floor is a log whose rows are short, so part of the
//     compiler's time is in no row at all.
//
// Both are refusals rather than measurements on purpose: a plausible-looking
// number that the log does not support is worse than no number, because
// nothing in the table says where it came from.
//
// The function is separate from Run so that the self-test can assert the
// refusal path, and not only the path that prices.
func priceRefusal(logPath string, ratio float64) string {
	if ratio > maxAttributionRatio {
		return fmt.Sprintf(
			"the log %s is from a build that ran its compilers in parallel: the\n"+
				"per-project times add up to %.1f times the compiler's own total, so they\n"+
				"are dominated by WAITING and not by compilation. A build that ran one\n"+
				"compiler at a time puts the same numbers at about 3.\n"+
				"  measure with: dotnet build Harbor.slnx -c Release --no-restore \\\n"+
				"                   -maxcpucount:1 -v:m -clp:PerformanceSummary\n",
			logPath, ratio)
	}
	if ratio < minAttributionRatio {
		return fmt.Sprintf(
			"the log %s does not account for the compiler's own work: the per-project\n"+
				"times add up to only %.2f times the compiler's own total, so some of the\n"+
				"compilation is in no project row at all. Spreading the compiler's whole\n"+
				"total over the rows that are there would price every project with a\n"+
				"share it did not earn, so this log is refused rather than priced.\n"+
				"  a complete log's rows add up to at least the compiler's total\n",
			logPath, ratio)
	}
	return ""
}

// options are the flags of the gate.
type options struct {
	log           string
	solution      string
	baseline      string
	writeBaseline string
	jsonOut       string
	sourceLabel   string
	budget        Budget
	minProjects   int
	selfTest      bool
}

// Run is the gate entry point.
func Run(args []string) int {
	opts := options{
		solution:    "Harbor.slnx",
		budget:      DefaultBudget,
		minProjects: 1,
	}
	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--log":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--log needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			opts.log = value
		case "--solution":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--solution needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			opts.solution = value
		case "--baseline":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--baseline needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			opts.baseline = value
		case "--write-baseline":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--write-baseline needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			opts.writeBaseline = value
		case "--json":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--json needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			opts.jsonOut = value
		case "--source-label":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--source-label needs a text")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			opts.sourceLabel = value
		case "--budget-percent":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--budget-percent needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, err := parseFloat(value)
			if err != nil {
				fmt.Fprintln(os.Stderr, "--budget-percent needs a number")
				return gate.Broken
			}
			opts.budget.Percent = parsed
		case "--budget-seconds":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--budget-seconds needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, err := parseFloat(value)
			if err != nil {
				fmt.Fprintln(os.Stderr, "--budget-seconds needs a number")
				return gate.Broken
			}
			opts.budget.Seconds = parsed
		case "--min-projects":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-projects needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, err := parseInt(value)
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-projects needs a number")
				return gate.Broken
			}
			opts.minProjects = parsed
		case "--self-test":
			opts.selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates measure-compile-cost --log <build.log> [--solution Harbor.slnx]")
			fmt.Println("                                              [--baseline <file>] [--write-baseline <file>]")
			fmt.Println("                                              [--source-label <text>]")
			fmt.Println("                                              [--budget-percent 20] [--budget-seconds 10]")
			fmt.Println("                                              [--min-projects 1] [--json out.json] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if opts.selfTest {
		return selfTestRun()
	}
	if opts.log == "" {
		fmt.Fprintln(os.Stderr, "measure-compile-cost needs --log: there is nothing to measure")
		return gate.Broken
	}

	root, err := gate.RepoRoot()
	if err != nil {
		root = "."
	}
	solution := opts.solution
	if !filepath.IsAbs(solution) {
		candidate := filepath.Join(root, solution)
		if _, statErr := os.Stat(candidate); statErr == nil {
			solution = candidate
		}
	}

	log, err := buildcost.ParseLog(opts.log)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	if !buildcost.LogHasCompilerTotals(log) {
		fmt.Fprintf(os.Stderr,
			"the log %s carries no Csc totals: it was not asked for a performance summary\n"+
				"(build with -clp:PerformanceSummary)\n", opts.log)
		return gate.Broken
	}
	graph, err := buildcost.ReadGraph(solution)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the solution:", err)
		return gate.Broken
	}
	sources := buildcost.ReadSources(graph)
	costs := buildcost.ComputeCosts(graph, log, sources)
	measuredProjects := buildcost.MeasuredCount(costs)
	if measuredProjects < opts.minProjects {
		fmt.Fprintf(os.Stderr,
			"the log measured %d of the %d projects of the solution, and the floor is %d\n",
			measuredProjects, len(graph.Projects), opts.minProjects)
		return gate.Broken
	}

	ratio := buildcost.AttributionRatio(costs, log.CscMillis)
	if refusal := priceRefusal(opts.log, ratio); refusal != "" {
		fmt.Fprint(os.Stderr, refusal)
		return gate.Broken
	}
	measurement := measure(graph, costs, log, ratio)

	printMeasurement(measurement, opts.log, log)

	if opts.jsonOut != "" {
		if err := os.WriteFile(opts.jsonOut, []byte(renderJSON(measurement)), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the json:", err)
			return gate.Broken
		}
	}

	if opts.writeBaseline != "" {
		baseline := buildcost.NewBaseline()
		baseline.Source = opts.log
		if opts.sourceLabel != "" {
			baseline.Source = opts.sourceLabel
		}
		baseline.CscCalls = log.CscCalls
		baseline.CscSeconds = float64(log.CscMillis) / 1000.0
		baseline.CoreCompileSeconds = float64(log.CoreCompileMillis) / 1000.0
		baseline.AttributionRatio = ratio
		baseline.SolutionProjects = measurement.SolutionProjects
		baseline.MeasuredProjects = measurement.MeasuredProjects
		for _, row := range measurement.Projects {
			baseline.Projects[row.Project] = row.Seconds
			baseline.ProjectFiles[row.Project] = row.Files
			baseline.ProjectLines[row.Project] = row.Lines
		}
		for _, row := range measurement.Radii {
			// A radius whose set holds a project this log did not
			// measure is priced with the measured part of it, and
			// that price is NOT the price of the set. It is not
			// recorded: a baseline that holds a lower bound as
			// if it were the price compares a fresh measurement
			// against a number that was never true.
			if row.Measured < row.Projects {
				continue
			}
			baseline.Radius[row.Project] = row.Seconds
			baseline.RadiusConsumers[row.Project] = row.Consumers
		}
		if err := buildcost.WriteBaseline(opts.writeBaseline, baseline); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the baseline:", err)
			return gate.Broken
		}
		fmt.Printf("\nthe baseline is written: %s\n", opts.writeBaseline)
	}

	if opts.baseline == "" {
		return gate.Green
	}

	recorded, err := buildcost.ReadBaseline(opts.baseline)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	verdict := compare(recorded, measurement, opts.budget)
	printVerdict(verdict, opts.baseline, opts.budget)
	if len(verdict.OverBudget) > 0 {
		return gate.Red
	}
	return gate.Green
}

// projectRow is one row of the per-project table.
//
// Every row of the table is a MEASURED project: a project the log carried
// no row for is not in the table at all, because its zero is the absence of
// a measurement and not a measurement of zero.
type projectRow struct {
	Project string `json:"project"`
	// OwnSeconds is the time MSBuild reported for the project itself.
	OwnSeconds float64 `json:"own_seconds"`
	// Seconds is the project's share of the compiler's measured time.
	Seconds float64 `json:"seconds"`
	Files   int     `json:"files"`
	Lines   int     `json:"lines"`
}

// unmeasuredRow is a project the log said nothing about.
//
// Its cost is UNKNOWN, and the row says so: the file and line counts are
// read from the tree and are true, and there is no cost because there was
// no measurement.
type unmeasuredRow struct {
	Project string `json:"project"`
	Files   int    `json:"files"`
	Lines   int    `json:"lines"`
}

// radiusRow is one row of the radius table.
type radiusRow struct {
	Project   string  `json:"project"`
	Direct    int     `json:"direct_consumers"`
	Consumers int     `json:"transitive_consumers"`
	Projects  int     `json:"radius_projects"`
	Seconds   float64 `json:"seconds"`
	Share     float64 `json:"share"`
	// Measured is the number of the projects of the radius set the log
	// measured. The set holds Projects of them. A row with fewer measured
	// projects than the set holds is a LOWER BOUND: it is the price of
	// the measured part of the set, and not the price of the set.
	Measured int `json:"measured_projects"`
}

// measurement is everything one run of the instrument produced.
type measurement struct {
	Source            string  `json:"source"`
	CscCalls          int     `json:"csc_calls"`
	CscSeconds        float64 `json:"csc_seconds"`
	CoreCompileSecond float64 `json:"corecompile_seconds"`
	AttributionRatio  float64 `json:"attribution_ratio"`
	TotalSeconds      float64 `json:"total_seconds"`
	// SolutionProjects is the number of projects the solution holds.
	SolutionProjects int `json:"solution_projects"`
	// MeasuredProjects is the number of them the log carried a row for.
	MeasuredProjects int `json:"measured_projects"`
	// Projects holds the measured projects, the most expensive first.
	Projects []projectRow `json:"projects"`
	// Unmeasured holds the projects the log said nothing about.
	Unmeasured []unmeasuredRow `json:"unmeasured"`
	// Radii holds every project of the solution.
	Radii []radiusRow `json:"radii"`
}

// measure turns the log and the graph into the two tables.
//
// The seconds of a project are its SHARE of the compiler's measured time:
// the log says how long the build spent on each project, and the compiler's
// total says how much of the build was compilation. The share is what the
// radius is priced with, and the shares of the whole tree add up to the
// compiler's total.
//
// A project the log says NOTHING about is not priced. Its zero would be a
// number the log does not support, and a tree of priced zeros is a tree of
// invented numbers.
func measure(graph *buildcost.Graph, costs map[string]buildcost.Cost,
	log *buildcost.Log, ratio float64) *measurement {
	out := &measurement{
		Source:            log.Source,
		CscCalls:          log.CscCalls,
		CscSeconds:        float64(log.CscMillis) / 1000.0,
		CoreCompileSecond: float64(log.CoreCompileMillis) / 1000.0,
		AttributionRatio:  ratio,
		SolutionProjects:  len(graph.Projects),
		MeasuredProjects:  buildcost.MeasuredCount(costs),
	}
	var ownTotal float64
	for _, cost := range buildcost.MeasuredCosts(costs) {
		ownTotal += cost.Seconds()
	}
	scale := 0.0
	if ownTotal > 0 {
		scale = out.CscSeconds / ownTotal
	}
	for _, cost := range buildcost.MeasuredCosts(costs) {
		out.Projects = append(out.Projects, projectRow{
			Project:    cost.Project,
			OwnSeconds: cost.Seconds(),
			Seconds:    cost.Seconds() * scale,
			Files:      cost.Source.Files,
			Lines:      cost.Source.Lines,
		})
		out.TotalSeconds += cost.Seconds() * scale
	}
	for _, cost := range buildcost.UnmeasuredCosts(costs) {
		out.Unmeasured = append(out.Unmeasured, unmeasuredRow{
			Project: cost.Project,
			Files:   cost.Source.Files,
			Lines:   cost.Source.Lines,
		})
	}
	for _, project := range graph.SortedProjects() {
		radius := graph.RadiusOf(project)
		seconds := 0.0
		measured := 0
		if cost, ok := costs[project]; ok && cost.Measured {
			seconds += cost.Seconds() * scale
			measured++
		}
		for _, consumer := range radius.Consumers {
			cost, ok := costs[consumer]
			if !ok || !cost.Measured {
				continue
			}
			seconds += cost.Seconds() * scale
			measured++
		}
		out.Radii = append(out.Radii, radiusRow{
			Project:   project,
			Direct:    radius.Direct,
			Consumers: len(radius.Consumers),
			Projects:  len(radius.Consumers) + 1,
			Seconds:   seconds,
			Measured:  measured,
		})
	}
	sort.Slice(out.Radii, func(i, j int) bool {
		if out.Radii[i].Seconds != out.Radii[j].Seconds {
			return out.Radii[i].Seconds > out.Radii[j].Seconds
		}
		return out.Radii[i].Project < out.Radii[j].Project
	})
	for index := range out.Radii {
		if out.TotalSeconds > 0 {
			out.Radii[index].Share = out.Radii[index].Seconds / out.TotalSeconds * 100
		}
	}
	return out
}

// printMeasurement prints the two tables.
func printMeasurement(m *measurement, logPath string, log *buildcost.Log) {
	fmt.Printf("the build log                : %s\n", logPath)
	fmt.Printf("csc calls                    : %d\n", m.CscCalls)
	fmt.Printf("csc seconds                  : %.1f\n", m.CscSeconds)
	fmt.Printf("corecompile seconds          : %.1f\n", m.CoreCompileSecond)
	fmt.Printf("attribution ratio            : %.2f\n", m.AttributionRatio)
	fmt.Printf("the whole tree               : %.1f s of compiler time over %d of %d projects\n",
		m.TotalSeconds, m.MeasuredProjects, m.SolutionProjects)
	fmt.Println()
	fmt.Println("project                                       | csc s | own s | files | lines")
	fmt.Println("----------------------------------------------+-------+-------+-------+------")
	for _, row := range m.Projects {
		fmt.Printf("%-46s | %5.1f | %5.1f | %5d | %6d\n",
			row.Project, row.Seconds, row.OwnSeconds, row.Files, row.Lines)
	}
	fmt.Println()
	fmt.Println("the price of a change: the owner and everything that depends on it")
	fmt.Println("project                                       | direct | consumers | csc s | share")
	fmt.Println("----------------------------------------------+--------+-----------+-------+------")
	for _, row := range m.Radii {
		price := fmt.Sprintf("%5.1f", row.Seconds)
		if row.Measured < row.Projects {
			// The set holds a project this log did not measure, so
			// the price is the price of the measured part of it.
			price = fmt.Sprintf("%5.1f+", row.Seconds)
		}
		fmt.Printf("%-46s | %6d | %9d | %s | %5.1f %%\n",
			row.Project, row.Direct, row.Consumers, price, row.Share)
	}
	if len(m.Unmeasured) > 0 {
		fmt.Println()
		fmt.Printf("%d project(s) of the solution were not measured by this log.\n",
			len(m.Unmeasured))
		fmt.Println("Their cost is UNKNOWN, not zero, so they are not priced:")
		for _, row := range m.Unmeasured {
			fmt.Printf("  %-46s %5d files %7d lines\n", row.Project, row.Files, row.Lines)
		}
		fmt.Println("The rows marked + above are a lower bound: their set holds one of them.")
	}
}

// overBudget is one project (or one radius) that grew past the budget.
type overBudget struct {
	Project    string
	Recorded   float64
	Measured   float64
	Growth     float64
	Absolute   float64
	RadiusOnly bool
}

// verdict is the comparison of a measurement with a recorded one.
type verdict struct {
	OverBudget []overBudget
	Unknown    []string
	// RecordedTotal and MeasuredTotal are the totals the two sides were
	// compared in.
	RecordedTotal float64
	MeasuredTotal float64
}

// compare compares the measurement with the recorded baseline.
//
// # THE GROWTH IS A SHARE, NOT A CLOCK
//
// The rule is "+20 % AND +10 s", and both numbers are about the project's
// SHARE of the compiler's time, because a threshold on a live wall clock
// fails on a slow runner: a machine that is 30 % slower makes every project
// look 30 % more expensive, and the gate would then report a regression
// every time the runner image moved.
//
// The share is what a slow runner does not change. A project whose share
// grew took the time from the rest of the build: that is a regression, and
// it is the one the split decisions are about.
func compare(recorded *buildcost.Baseline, m *measurement, budget Budget) verdict {
	out := verdict{RecordedTotal: recordedTotal(recorded), MeasuredTotal: m.TotalSeconds}
	for _, row := range m.Projects {
		before, ok := recorded.Projects[row.Project]
		if !ok {
			out.Unknown = append(out.Unknown, row.Project)
			continue
		}
		out.OverBudget = append(out.OverBudget, overBudgetRows(row.Project, before,
			row.Seconds, out.RecordedTotal, out.MeasuredTotal, budget, false)...)
	}
	for _, row := range m.Radii {
		before, ok := recorded.Radius[row.Project]
		if !ok {
			continue
		}
		out.OverBudget = append(out.OverBudget, overBudgetRows(row.Project, before,
			row.Seconds, out.RecordedTotal, out.MeasuredTotal, budget, true)...)
	}
	sort.Slice(out.OverBudget, func(i, j int) bool {
		if out.OverBudget[i].Growth != out.OverBudget[j].Growth {
			return out.OverBudget[i].Growth > out.OverBudget[j].Growth
		}
		return out.OverBudget[i].Project < out.OverBudget[j].Project
	})
	sort.Strings(out.Unknown)
	return out
}

// overBudgetRows is the verdict about one row: the project's share of the
// compiler's time, before and after.
//
// The two conditions of the budget are about the SAME quantity: the share
// grew by more than the percentage, and the share it took grew by more than
// the seconds.
func overBudgetRows(project string, recorded, measured, recordedTotal, measuredTotal float64,
	budget Budget, radiusOnly bool) []overBudget {
	if recordedTotal <= 0 || measuredTotal <= 0 || recorded <= 0 {
		return nil
	}
	shareBefore := recorded / recordedTotal
	shareAfter := measured / measuredTotal
	growth := 0.0
	if shareBefore > 0 {
		growth = (shareAfter - shareBefore) / shareBefore * 100
	}
	absolute := (shareAfter - shareBefore) * measuredTotal
	if growth <= budget.Percent || absolute <= budget.Seconds {
		return nil
	}
	return []overBudget{{
		Project:    project,
		Recorded:   recorded,
		Measured:   measured,
		Growth:     growth,
		Absolute:   absolute,
		RadiusOnly: radiusOnly,
	}}
}

// recordedTotal is the sum of the recorded per-project costs.
func recordedTotal(recorded *buildcost.Baseline) float64 {
	var total float64
	for _, seconds := range recorded.Projects {
		total += seconds
	}
	return total
}

// printVerdict prints the comparison with the recorded baseline.
func printVerdict(v verdict, baselinePath string, budget Budget) {
	fmt.Printf("\nthe baseline                  : %s\n", baselinePath)
	fmt.Printf("the budget                   : more than %.0f %% AND more than %.1f s\n",
		budget.Percent, budget.Seconds)
	fmt.Printf("the rule is a share          : the recorded tree cost %.1f s, this one %.1f s\n",
		v.RecordedTotal, v.MeasuredTotal)
	fmt.Println("  (the growth is the project's SHARE of the compiler's time, because")
	fmt.Println("   a threshold on a live wall clock fails on a slow runner)")
	if len(v.OverBudget) == 0 {
		fmt.Println("no project is over the budget")
		return
	}
	fmt.Printf("%d row(s) over the budget:\n", len(v.OverBudget))
	for _, row := range v.OverBudget {
		kind := "project"
		if row.RadiusOnly {
			kind = "radius "
		}
		fmt.Printf("  %s %-46s recorded %7.1f s, measured %7.1f s, share %+.1f %% (%+.1f s)\n",
			kind, row.Project, row.Recorded, row.Measured, row.Growth, row.Absolute)
	}
	if len(v.Unknown) > 0 {
		fmt.Printf("%d project(s) are not in the baseline:\n", len(v.Unknown))
		for _, project := range v.Unknown {
			fmt.Printf("  %s\n", project)
		}
	}
}

// renderJSON writes the measurement as JSON.
func renderJSON(m *measurement) string {
	out, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return "{}"
	}
	return string(out)
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if index := strings.IndexByte(arg, '='); index >= 0 {
		return arg[:index], arg[index+1:], true
	}
	return arg, "", false
}

// parseFloat parses a number, refusing anything that is not one.
func parseFloat(text string) (float64, error) {
	var value float64
	if _, err := fmt.Sscanf(text, "%g", &value); err != nil {
		return 0, err
	}
	return value, nil
}

// parseInt parses a whole number.
func parseInt(text string) (int, error) {
	var value int
	if _, err := fmt.Sscanf(text, "%d", &value); err != nil {
		return 0, err
	}
	return value, nil
}
