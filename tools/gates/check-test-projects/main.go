// Package testprojects is the compiled port of tools/check-test-projects.py.
//
// The gate over test-project completeness: does every test project in the
// solution actually run in CI?
//
// A test project has TWO ways to reach a CI run: a list names it, and its
// own assembly is executed; or a project that a list names REFERENCES it,
// and its test classes are discovered inside that referencing project's
// process. A project with no list entry AND no incoming reference from a
// listed project is the hole.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could
// not run.
package testprojects

import (
	"fmt"
	"os"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	report := false
	minProjects := 0
	minTested := 0
	minLists := 0
	minClaims := 0
	minContexts := 0
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--root needs a path")
				return gate.Broken
			}
			i++
			root = args[i]
		case arg == "--report":
			report = true
		case arg == "--min-projects":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-projects needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-projects needs a number")
				return gate.Broken
			}
			minProjects = value
		case arg == "--min-tested":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-tested needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-tested needs a number")
				return gate.Broken
			}
			minTested = value
		case arg == "--min-lists":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-lists needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-lists needs a number")
				return gate.Broken
			}
			minLists = value
		case arg == "--min-claims":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-claims needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-claims needs a number")
				return gate.Broken
			}
			minClaims = value
		case arg == "--min-contexts":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-contexts needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-contexts needs a number")
				return gate.Broken
			}
			minContexts = value
		case arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-test-projects [--root PATH] [--report] [--min-projects N] [--min-tested N] [--min-lists N] [--min-claims N] [--min-contexts N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	repoRoot, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}
	if root != "" {
		repoRoot = root
	}

	result, findings := analyse(repoRoot)
	totals := result.totals

	if report {
		printReport(result, findings)
		return gate.Green
	}

	type floor struct {
		name   string
		floor  int
		actual int
	}
	var floors []floor
	floors = append(floors, floor{"projects", minProjects, totals["csproj"]})
	floors = append(floors, floor{"test projects with tests", minTested, totals["with_tests"]})
	floors = append(floors, floor{"CI lists", minLists, totals["lists"]})
	floors = append(floors, floor{"claimed projects", minClaims, totals["claims"]})
	floors = append(floors, floor{"discovered projects", minContexts, totals["contexts"]})

	var breached []floor
	for _, one := range floors {
		if one.floor != 0 && one.actual < one.floor {
			breached = append(breached, one)
		}
	}

	var blocking, advisory []finding
	for _, item := range findings {
		if item.blocking {
			blocking = append(blocking, item)
		} else {
			advisory = append(advisory, item)
		}
	}

	if len(blocking) > 0 {
		fmt.Print("Test-project completeness gate — findings:\n\n")
		for _, item := range blocking {
			fmt.Printf("  %s\n", item.String())
		}
		fmt.Println()
	}
	if len(advisory) > 0 {
		fmt.Print("carried by the baseline (known holes):\n\n")
		for _, item := range advisory {
			fmt.Printf("  %s\n", item.String())
		}
		fmt.Println()
	}
	if len(breached) > 0 {
		for _, one := range breached {
			fmt.Printf("  SCAN-BLIND  scan floor: %s compared %d, floor is %d. The gate saw less than "+
				"the tree holds, so it cannot report that the tree is complete.\n",
				one.name, one.actual, one.floor)
		}
		fmt.Println()
	}

	if len(blocking) > 0 || len(breached) > 0 {
		return gate.Red
	}

	fmt.Printf("OK: %d test project(s) in %s (%d with tests), %d CI list(s), %d claimed project(s), "+
		"%d discovered by a CI run, %d known hole(s) carried by the baseline\n",
		totals["test_projects"], solutionFile, totals["with_tests"], totals["lists"],
		totals["claims"], totals["contexts"], len(advisory))
	return gate.Green
}

// printReport prints the truth table: every project, and where its tests
// actually run.
func printReport(result census, findings []finding) {
	totals := result.totals
	fmt.Printf("universe: %d test project(s) in %s, %d of them with [Test] methods\n",
		totals["test_projects"], solutionFile, totals["with_tests"])
	fmt.Printf("csproj under %s/: %d\n", testsDir, totals["csproj"])
	fmt.Println()
	for _, entry := range result.lists {
		condition := entry.condition
		if condition == "" {
			condition = "(always)"
		}
		fmt.Printf("list %s  [%s :: %s]  %d entr(ies)\n",
			entry.id, entry.file, entry.job, len(entry.projects))
		fmt.Printf("  condition: %s\n", condition)
		fmt.Printf("  primary: %s\n", yesNo(entry.primary))
		fmt.Printf("  treenode filter: %s\n", yesNo(entry.appliesFilter))
		fmt.Println()
	}
	fmt.Printf("%-40s %5s  %-44s runs in\n", "project", "tests", "lists")
	names := make([]string, 0, len(result.universe))
	for name := range result.universe {
		names = append(names, name)
	}
	sortStrings(names)
	for _, name := range names {
		project := result.universe[name]
		var ids []string
		for _, index := range result.claims[name] {
			ids = append(ids, result.lists[index].id)
		}
		list := strings.Join(ids, ", ")
		if list == "" {
			list = "-"
		}
		where := strings.Join(result.contexts[name], ", ")
		if where == "" {
			where = "-"
		}
		fmt.Printf("%-40s %5d  %-44s %s\n", name, project.tests, list, where)
	}
	fmt.Println()
	if result.nukeTarget != nil {
		runs := strings.Join(result.nukeRuns, ", ")
		if runs == "" {
			runs = "-"
		}
		fmt.Printf("local NUKE `Test` target runs %d project(s): %s\n", len(result.nukeRuns), runs)
		fmt.Println()
	}
	for _, note := range result.notes {
		fmt.Printf("note  %s\n", note)
	}
	fmt.Println()
	if len(findings) > 0 {
		fmt.Println("findings:")
		for _, item := range findings {
			fmt.Printf("  %s\n", item.String())
		}
	}
}

// yesNo renders a boolean the way the report does.
func yesNo(value bool) string {
	if value {
		return "yes"
	}
	return "no"
}
