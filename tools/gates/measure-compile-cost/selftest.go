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

// runCapturing runs the gate and returns what it printed.
//
// The gate writes its table to os.Stdout and its refusals to os.Stderr, so
// the self-test swaps both for a file while it runs and reads them back.
// The point of the capture is the refusal: a refused log must print NO
// table, and the self-test has to see the empty stdout to say so.
func runCapturing(args []string) (code int, out string, errOut string) {
	outFile, err := os.CreateTemp("", "compile-cost-selftest-out")
	if err != nil {
		return gate.Broken, "", "cannot make a temporary file for the captured output"
	}
	defer os.Remove(outFile.Name())
	errFile, err := os.CreateTemp("", "compile-cost-selftest-err")
	if err != nil {
		outFile.Close()
		os.Remove(outFile.Name())
		return gate.Broken, "", "cannot make a temporary file for the captured error output"
	}
	defer os.Remove(errFile.Name())

	originalOut, originalErr := os.Stdout, os.Stderr
	os.Stdout, os.Stderr = outFile, errFile
	code = Run(args)
	os.Stdout, os.Stderr = originalOut, originalErr
	outFile.Close()
	errFile.Close()

	written, err := os.ReadFile(outFile.Name())
	if err != nil {
		return code, "", "cannot read the captured output"
	}
	failed, err := os.ReadFile(errFile.Name())
	if err != nil {
		return code, "", "cannot read the captured error output"
	}
	return code, string(written), string(failed)
}

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

	// THE REFUSALS. An instrument that prices a log it cannot read is an
	// instrument that prints plausible numbers nobody can check, so the two
	// logs the rows cannot support are written here and the gate has to
	// REFUSE them: exit code 2, "the gate could not run", and no table.
	//
	// The parallel log: the same project rows, but the compiler's own
	// total is a fraction of them, which is what a build that ran its
	// compilers at once looks like.
	//
	// The short log: the compiler's own total is bigger than the rows
	// add up to, which is what a truncated log, or a log of another build,
	// looks like.
	refusals := []struct {
		name    string
		from    string
		to      string
		wantSay string
	}{
		{
			name:    "a build that ran its compilers in parallel",
			from:    "    100000 ms  Csc",
			to:      "      3000 ms  Csc",
			wantSay: "ran its compilers in parallel",
		},
		{
			name:    "a log whose rows do not add up to the compiler's total",
			from:    "     40000 ms  /home/runner/work/Harbor-Harness/Harbor-Harness/src/App/App.csproj",
			to:      "      5000 ms  /home/runner/work/Harbor-Harness/Harbor-Harness/src/App/App.csproj",
			wantSay: "does not account for the compiler's own work",
		},
	}
	for _, refusal := range refusals {
		path := filepath.Join(directory, refusal.name+".log")
		edited := strings.ReplaceAll(string(original), refusal.from, refusal.to)
		if edited == string(original) {
			check(false, "the fixture log does not carry the row the "+refusal.name+" log edits")
			continue
		}
		if err := os.WriteFile(path, []byte(edited), 0o644); err != nil {
			check(false, "cannot write the "+refusal.name+" log: "+err.Error())
			continue
		}
		code, out, errOut := runCapturing([]string{
			"--log", path,
			"--solution", solutionPath,
			"--min-projects", "1",
		})
		check(code == gate.Broken,
			fmt.Sprintf("the gate refuses %s: it exited %d, and the refusal is %d",
				refusal.name, code, gate.Broken))
		check(out == "",
			fmt.Sprintf("the gate refuses %s, and it printed a table anyway: %q",
				refusal.name, out))
		check(strings.Contains(errOut, refusal.wantSay),
			fmt.Sprintf("the refusal of %s does not say why: %q", refusal.name, errOut))
	}

	// The fixture itself is admissible: the same question on the log the
	// fixture wrote must not refuse it, or the refusals above prove
	// nothing.
	check(priceRefusal(logPath, ratio) == "",
		"the fixture log is refused, so the refusal path proves nothing")

	// A project the log says NOTHING about is not priced. Its zero is the
	// absence of a measurement, and a table that holds it would print a
	// number the log does not support.
	missingPath := filepath.Join(directory, "a log with a missing project row.log")
	missing := strings.ReplaceAll(string(original),
		"214.851 |     40000 ms  /home/runner/work/Harbor-Harness/Harbor-Harness/src/App/App.csproj   3 calls\n",
		"")
	if missing == string(original) {
		check(false, "the fixture log does not carry the row the missing-row log removes")
	} else if err := os.WriteFile(missingPath, []byte(missing), 0o644); err != nil {
		check(false, "cannot write the missing-row log: "+err.Error())
	} else if missingLog, err := buildcost.ParseLog(missingPath); err != nil {
		check(false, "the missing-row log does not parse: "+err.Error())
	} else {
		missingCosts := buildcost.ComputeCosts(graph, missingLog, sources)
		missingMeasurement := measure(graph, missingCosts, missingLog,
			buildcost.AttributionRatio(missingCosts, missingLog.CscMillis))
		priced := map[string]bool{}
		for _, row := range missingMeasurement.Projects {
			priced[row.Project] = true
		}
		check(len(missingMeasurement.Projects) == 3,
			fmt.Sprintf("the missing-row log priced 3 projects, got %d", len(missingMeasurement.Projects)))
		check(len(missingMeasurement.Unmeasured) == 1,
			fmt.Sprintf("the missing-row log reported 1 unmeasured project, got %d",
				len(missingMeasurement.Unmeasured)))
		check(!priced["src/App/App.csproj"],
			"a project the log says nothing about is priced, and its zero is not a measurement")
		if len(missingMeasurement.Unmeasured) == 1 {
			check(missingMeasurement.Unmeasured[0].Project == "src/App/App.csproj",
				fmt.Sprintf("the unmeasured project is App, got %q",
					missingMeasurement.Unmeasured[0].Project))
		}
	}

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
