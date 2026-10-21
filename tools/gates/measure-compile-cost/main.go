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
	Percent  float64
	Seconds  float64
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

// options are the flags of the gate.
type options struct {
	log           string
	solution      string
	baseline      string
	writeBaseline string
	jsonOut       string
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
	if len(costs) < opts.minProjects {
		fmt.Fprintf(os.Stderr, "the measurement covers %d projects, and the floor is %d\n",
			len(costs), opts.minProjects)
		return gate.Broken
	}

	ratio := buildcost.AttributionRatio(costs, log.CscMillis)
	if ratio > maxAttributionRatio {
		fmt.Fprintf(os.Stderr,
			"the log %s is from a build that ran its compilers in parallel: the\n"+
				"per-project times add up to %.1f times the compiler's own total, so they\n"+
				"are dominated by WAITING and not by compilation. A build that ran one\n"+
				"compiler at a time puts the same numbers at about 3.\n"+
				"  measure with: dotnet build Harbor.slnx -c Release --no-restore \\\n"+
				"                   -maxcpucount:1 -v:m -clp:PerformanceSummary\n",
			opts.log, ratio)
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
		baseline.CscCalls = log.CscCalls
		baseline.CscSeconds = float64(log.CscMillis) / 1000.0
		baseline.CoreCompileSeconds = float64(log.CoreCompileMillis) / 1000.0
		baseline.AttributionRatio = ratio
		for _, row := range measurement.Projects {
			baseline.Projects[row.Project] = row.Seconds
			baseline.ProjectFiles[row.Project] = row.Files
			baseline.ProjectLines[row.Project] = row.Lines
		}
		for _, row := range measurement.Radii {
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
type projectRow struct {
	Project string `json:"project"`
	// OwnSeconds is the time MSBuild reported for the project itself.
	OwnSeconds float64 `json:"own_seconds"`
	// Seconds is the project's share of the compiler's measured time.
	Seconds float64 `json:"seconds"`
	Files   int     `json:"files"`
	Lines   int     `json:"lines"`
}

// radiusRow is one row of the radius table.
type radiusRow struct {
	Project   string  `json:"project"`
	Direct    int     `json:"direct_consumers"`
	Consumers int     `json:"transitive_consumers"`
	Projects  int     `json:"radius_projects"`
	Seconds   float64 `json:"seconds"`
	Share     float64 `json:"share"`
}

// measurement is everything one run of the instrument produced.
type measurement struct {
	Source            string       `json:"source"`
	CscCalls          int          `json:"csc_calls"`
	CscSeconds        float64      `json:"csc_seconds"`
	CoreCompileSecond float64      `json:"corecompile_seconds"`
	AttributionRatio  float64      `json:"attribution_ratio"`
	TotalSeconds      float64      `json:"total_seconds"`
	Projects          []projectRow `json:"projects"`
	Radii             []radiusRow  `json:"radii"`
}

// measure turns the log and the graph into the two tables.
//
// The seconds of a project are its SHARE of the compiler's measured time:
// the log says how long the build spent on each project, and the compiler's
// total says how much of the build was compilation. The share is what the
// radius is priced with, and the shares of the whole tree add up to the
// compiler's total.
func measure(graph *buildcost.Graph, costs map[string]buildcost.Cost,
	log *buildcost.Log, ratio float64) *measurement {
	out := &measurement{
		Source:            log.Source,
		CscCalls:          log.CscCalls,
		CscSeconds:        float64(log.CscMillis) / 1000.0,
		CoreCompileSecond: float64(log.CoreCompileMillis) / 1000.0,
		AttributionRatio:  ratio,
	}
	var ownTotal float64
	for _, cost := range costs {
		ownTotal += cost.Seconds()
	}
	scale := 0.0
	if ownTotal > 0 {
		scale = out.CscSeconds / ownTotal
	}
	for _, cost := range buildcost.SortedCosts(costs) {
		if cost.Millis == 0 && cost.Source.Files == 0 {
			continue
		}
		out.Projects = append(out.Projects, projectRow{
			Project:    cost.Project,
			OwnSeconds: cost.Seconds(),
			Seconds:    cost.Seconds() * scale,
			Files:      cost.Source.Files,
			Lines:      cost.Source.Lines,
		})
		out.TotalSeconds += cost.Seconds() * scale
	}
	for _, project := range graph.SortedProjects() {
		radius := graph.RadiusOf(project)
		seconds := costs[project].Seconds() * scale
		for _, consumer := range radius.Consumers {
			seconds += costs[consumer].Seconds() * scale
		}
		out.Radii = append(out.Radii, radiusRow{
			Project:   project,
			Direct:    radius.Direct,
			Consumers: len(radius.Consumers),
			Projects:  len(radius.Consumers) + 1,
			Seconds:   seconds,
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
	fmt.Printf("the whole tree               : %.1f s of compiler time over %d projects\n",
		m.TotalSeconds, len(m.Projects))
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
		fmt.Printf("%-46s | %6d | %9d | %5.1f | %5.1f %%\n",
			row.Project, row.Direct, row.Consumers, row.Seconds, row.Share)
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
// THE GROWTH IS A SHARE, NOT A CLOCK
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
