package measurecompilecost

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/buildcost"
	"harbor.local/gates/internal/gate"
)

// The fixture the self-test measures: the log and the solution under
// testdata/, which are in the tree so the instrument can be checked without
// a build and without a network.
const (
	fixtureLog      = "tools/gates/measure-compile-cost/testdata/perf-summary.log"
	fixtureSolution = "tools/gates/measure-compile-cost/testdata/Fixture.slnx"
)

// The numbers the fixture carries. They are stated here, not derived, so a
// parser that stopped reading the log cannot agree with the tool that
// checks it.
//
// The fixture is a build that ran ONE compiler at a time, so every
// project's row is its own time, and the rows add up to the compiler's
// total:
//
//	Core        12000 ms
//	Mid         20000 ms
//	App         40000 ms
//	Core.Tests  28000 ms
//	                       100000 ms  = the compiler's total
//
// The attribution ratio is therefore 1.00, which is what a serial build
// looks like.
const (
	fixtureCscCalls       = 4
	fixtureCscMillis      = 100000
	fixtureCoreCompileMs  = 102000
	fixtureOwnCore        = 12000
	fixtureOwnMid         = 20000
	fixtureOwnApp         = 40000
	fixtureOwnCoreTests   = 28000
	fixtureRatioTolerance = 0.02
)

// selfTestRun measures the planted fixture and asserts its shape.
//
// The negative example is the point: the fixture is measured twice, once as
// it is and once with one project inflated, and the gate has to go red on
// the second and stay green on the first.
func selfTestRun() int {
	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}

	root, err := gate.RepoRoot()
	if err != nil {
		root = "."
	}
	logPath := filepath.Join(root, fixtureLog)
	solutionPath := filepath.Join(root, fixtureSolution)

	log, err := buildcost.ParseLog(logPath)
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: the fixture log does not parse: %v\n", err)
		return gate.Red
	}
	check(log.CscCalls == fixtureCscCalls,
		fmt.Sprintf("the fixture has %d Csc calls, got %d", fixtureCscCalls, log.CscCalls))
	check(log.CscMillis == fixtureCscMillis,
		fmt.Sprintf("the fixture has %d ms of Csc, got %d", fixtureCscMillis, log.CscMillis))
	check(log.CoreCompileMillis == fixtureCoreCompileMs,
		fmt.Sprintf("the fixture has %d ms of CoreCompile, got %d",
			fixtureCoreCompileMs, log.CoreCompileMillis))

	graph, err := buildcost.ReadGraph(solutionPath)
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: the fixture solution does not parse: %v\n", err)
		return gate.Red
	}
	check(len(graph.Projects) == 4,
		fmt.Sprintf("the fixture has 4 projects, got %d", len(graph.Projects)))
	edges := 0
	for _, references := range graph.References {
		edges += len(references)
	}
	check(edges == 3, fmt.Sprintf("the fixture has 3 edges, got %d", edges))

	sources := buildcost.ReadSources(graph)
	costs := buildcost.ComputeCosts(graph, log, sources)
	ratio := buildcost.AttributionRatio(costs, log.CscMillis)

	own := func(project string) int64 { return costs[project].Millis }
	check(own("src/Core/Core.csproj") == fixtureOwnCore,
		fmt.Sprintf("Core costs %d ms, got %d", fixtureOwnCore, own("src/Core/Core.csproj")))
	check(own("src/Mid/Mid.csproj") == fixtureOwnMid,
		fmt.Sprintf("Mid costs %d ms, got %d", fixtureOwnMid, own("src/Mid/Mid.csproj")))
	check(own("src/App/App.csproj") == fixtureOwnApp,
		fmt.Sprintf("App costs %d ms, got %d", fixtureOwnApp, own("src/App/App.csproj")))
	check(own("tests/Core.Tests/Core.Tests.csproj") == fixtureOwnCoreTests,
		fmt.Sprintf("Core.Tests costs %d ms, got %d",
			fixtureOwnCoreTests, own("tests/Core.Tests/Core.Tests.csproj")))
	check(ratio > 1-fixtureRatioTolerance && ratio < 1+fixtureRatioTolerance,
		fmt.Sprintf("the attribution ratio is about 1, got %.2f", ratio))

	// The radius: Core is referenced by Mid and by Core.Tests, and App
	// depends on it through Mid. Its radius is therefore every project of
	// the fixture.
	radius := graph.RadiusOf("src/Core/Core.csproj")
	check(radius.Direct == 2,
		fmt.Sprintf("Core has 2 direct consumers, got %d", radius.Direct))
	check(len(radius.Consumers) == 3,
		fmt.Sprintf("Core has 3 transitive consumers, got %d", len(radius.Consumers)))
	mid := graph.RadiusOf("src/Mid/Mid.csproj")
	check(len(mid.Consumers) == 1,
		fmt.Sprintf("Mid has 1 transitive consumer, got %d", len(mid.Consumers)))
	app := graph.RadiusOf("src/App/App.csproj")
	check(len(app.Consumers) == 0,
		fmt.Sprintf("App has no consumers, got %d", len(app.Consumers)))

	measured := measure(graph, costs, log, ratio)
	byProject := map[string]projectRow{}
	for _, row := range measured.Projects {
		byProject[row.Project] = row
	}
	check(byProject["src/App/App.csproj"].Seconds == 40,
		fmt.Sprintf("App costs 40.0 s of the compiler's time, got %.1f",
			byProject["src/App/App.csproj"].Seconds))
	var coreRadius radiusRow
	for _, row := range measured.Radii {
		if row.Project == "src/Core/Core.csproj" {
			coreRadius = row
		}
	}
	check(coreRadius.Consumers == 3,
		fmt.Sprintf("Core's radius holds 3 consumers, got %d", coreRadius.Consumers))
	check(coreRadius.Seconds == 100,
		fmt.Sprintf("Core's radius costs 100.0 s, got %.1f", coreRadius.Seconds))

	// THE NEGATIVE EXAMPLE. The baseline is written from the fixture, the
	// fixture is compared against it (green), and then one project is
	// inflated in the log (red).
	directory, err := os.MkdirTemp("", "compile-cost-selftest")
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: cannot make a temporary directory: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(directory)

	baselinePath := filepath.Join(directory, "baseline.txt")
	recorded := buildcost.NewBaseline()
	recorded.Source = logPath
	recorded.CscCalls = log.CscCalls
	recorded.CscSeconds = float64(log.CscMillis) / 1000
	recorded.CoreCompileSeconds = float64(log.CoreCompileMillis) / 1000
	recorded.AttributionRatio = ratio
	for _, row := range measured.Projects {
		recorded.Projects[row.Project] = row.Seconds
	}
	for _, row := range measured.Radii {
		recorded.Radius[row.Project] = row.Seconds
		recorded.RadiusConsumers[row.Project] = row.Consumers
	}
	if err := buildcost.WriteBaseline(baselinePath, recorded); err != nil {
		fmt.Printf("  SELF-TEST FAIL: cannot write the baseline: %v\n", err)
		return gate.Red
	}
	reread, err := buildcost.ReadBaseline(baselinePath)
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: the baseline does not read back: %v\n", err)
		return gate.Red
	}
	check(len(reread.Projects) == len(recorded.Projects),
		fmt.Sprintf("the baseline holds %d projects, read back %d",
			len(recorded.Projects), len(reread.Projects)))
	check(len(reread.Radius) == len(recorded.Radius),
		fmt.Sprintf("the baseline holds %d radii, read back %d",
			len(recorded.Radius), len(reread.Radius)))

	verdict := compare(reread, measured, DefaultBudget)
	check(len(verdict.OverBudget) == 0,
		fmt.Sprintf("the fixture against its own baseline is green, got %d rows over budget",
			len(verdict.OverBudget)))

	// The inflation: App's own time grows from 40 s to 100 s, and the
	// compiler's total grows with it, because the compiler really did spend
	// that time.
	inflatedPath := filepath.Join(directory, "inflated.log")
	original, err := os.ReadFile(logPath)
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: cannot read the fixture log: %v\n", err)
		return gate.Red
	}
	inflated := strings.ReplaceAll(string(original),
		"     40000 ms  /home/runner/work/Harbor-Harness/Harbor-Harness/src/App/App.csproj",
		"    100000 ms  /home/runner/work/Harbor-Harness/Harbor-Harness/src/App/App.csproj")
	inflated = strings.ReplaceAll(inflated, "    100000 ms  Csc", "    160000 ms  Csc")
	inflated = strings.ReplaceAll(inflated, "    102000 ms  CoreCompile", "    162000 ms  CoreCompile")
	if inflated == string(original) {
		fmt.Println("  SELF-TEST FAIL: the fixture log does not carry the rows the inflation edits")
		return gate.Red
	}
	if err := os.WriteFile(inflatedPath, []byte(inflated), 0o644); err != nil {
		fmt.Printf("  SELF-TEST FAIL: cannot write the inflated log: %v\n", err)
		return gate.Red
	}
	inflatedLog, err := buildcost.ParseLog(inflatedPath)
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: the inflated log does not parse: %v\n", err)
		return gate.Red
	}
	inflatedCosts := buildcost.ComputeCosts(graph, inflatedLog, sources)
	inflatedRatio := buildcost.AttributionRatio(inflatedCosts, inflatedLog.CscMillis)
	inflatedMeasurement := measure(graph, inflatedCosts, inflatedLog, inflatedRatio)

	verdict = compare(reread, inflatedMeasurement, DefaultBudget)
	check(len(verdict.OverBudget) > 0,
		"the inflated project is NOT over the budget: the gate cannot see a project that got more expensive")
	named := false
	for _, row := range verdict.OverBudget {
		if row.Project == "src/App/App.csproj" {
			named = true
		}
	}
	check(named, "the inflated project is not named in the verdict")

	// A project that is not in the baseline is reported, not ignored.
	unknown := compare(buildcost.NewBaseline(), measured, DefaultBudget)
	check(len(unknown.Unknown) == len(measured.Projects),
		fmt.Sprintf("a project that is not in the baseline is reported: %d of %d",
			len(unknown.Unknown), len(measured.Projects)))

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: the fixture measured to 4 projects, 3 edges, a ratio of 1.00,")
	fmt.Println("             and the gate went red on the project the self-test inflated.")
	return gate.Green
}
