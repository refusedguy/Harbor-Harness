// Package testpackages is the compiled port of tools/check-test-packages.py.
//
// The gate over the package set of the test and sample projects: which
// packages may each of them reference, directly or transitively, and is any
// BANNED one among them?
//
// The resolved set is obtained without a build and without a network: the
// `<PackageReference>` elements of the `.csproj` carry the direct half, and
// `obj/project.assets.json` -- the file NuGet writes during RESTORE --
// carries the transitive half. A project whose assets file is missing is
// reported as PACKAGE-BLIND rather than passing: "I could not verify this
// project" and "this project is clean" are not the same answer.
//
// The banned names are read out of BannedSymbols.txt, the file the
// BannedApiAnalyzers already consume. There is deliberately NO second list
// of banned packages here: a second list is a copy of the answer, and a copy
// of the answer drifts.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could
// not run.
package testpackages

import (
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	report := false
	allowMissingAssets := false
	outputOnly := false
	minProjects := 0
	minDeclared := 0
	minPackages := 0
	minOutputDirs := 0
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
		case arg == "--allow-missing-assets":
			allowMissingAssets = true
		case arg == "--output-only":
			outputOnly = true
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
		case arg == "--min-declared":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-declared needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-declared needs a number")
				return gate.Broken
			}
			minDeclared = value
		case arg == "--min-packages":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-packages needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-packages needs a number")
				return gate.Broken
			}
			minPackages = value
		case arg == "--min-output-dirs":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-output-dirs needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-output-dirs needs a number")
				return gate.Broken
			}
			minOutputDirs = value
		case arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-test-packages [--root PATH] [--report] [--allow-missing-assets] [--output-only] [--min-projects N] [--min-declared N] [--min-packages N] [--min-output-dirs N] [--self-test]")
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

	result, findings := analyse(repoRoot, allowMissingAssets, outputOnly)

	if report {
		printReport(result)
		return gate.Green
	}

	type floor struct {
		name   string
		floor  int
		actual int
	}
	var floors []floor
	floors = append(floors, floor{"projects", minProjects, result.totals["projects"]})
	if !outputOnly {
		// The two counters below are only meaningful when the resolved
		// graph was read, so `--output-only` does not carry them.
		floors = append(floors, floor{"declared references", minDeclared, result.totals["declared"]})
		floors = append(floors, floor{"resolved packages", minPackages, result.totals["resolved"]})
	}
	floors = append(floors, floor{"output directories", minOutputDirs, result.totals["output_dirs"]})

	var breached []floor
	for _, one := range floors {
		if one.floor != 0 && one.actual < one.floor {
			breached = append(breached, one)
		}
	}

	if len(findings) > 0 {
		fmt.Println("Test/sample package-set gate — findings:\n")
		for _, item := range findings {
			fmt.Printf("  %s\n", item.String())
		}
		fmt.Println()
	}
	if len(breached) > 0 {
		for _, one := range breached {
			fmt.Printf("  PACKAGE-BLIND  scan floor: %s compared %d, floor is %d\n",
				one.name, one.actual, one.floor)
		}
		fmt.Println()
	}

	if len(findings) > 0 || len(breached) > 0 {
		return gate.Red
	}

	totals := result.totals
	if outputOnly {
		fmt.Printf(
			"OK: %d test/sample projects, %d output directories, %d assemblies inspected, no banned assembly among them\n",
			totals["projects"], totals["output_dirs"], totals["output_dlls"])
		return gate.Green
	}
	fmt.Printf(
		"OK: %d test/sample projects, %d declared references, %d resolved package entries, no banned package among them\n",
		totals["projects"], totals["declared"], totals["resolved"])
	return gate.Green
}

// printReport prints the derived package census.
func printReport(result census) {
	banned := strings.Join(result.banned, ", ")
	if banned == "" {
		banned = "-"
	}
	fmt.Printf("banned names (from %s): %s\n", bannedSymbolsFile, banned)
	fmt.Printf("exceptions in force: %d\n", len(result.exceptions))
	for _, relative := range result.order {
		data := result.projects[relative]
		declared := strings.Join(data.declared, ", ")
		if declared == "" {
			declared = "-"
		}
		fmt.Printf("\n%s\n", relative)
		fmt.Printf("  declared PackageReference  %s\n", declared)
		fmt.Printf("  resolved packages          %d (%d direct)\n",
			len(data.resolved), len(data.direct))
		fmt.Printf("  output DLLs on disk       %d\n", data.outputDLLs)
	}
	totals := result.totals
	fmt.Printf("\ntotals: %d projects, %d declared references, %d resolved package entries\n",
		totals["projects"], totals["declared"], totals["resolved"])
	distinct := map[string]bool{}
	for _, data := range result.projects {
		for _, name := range data.resolved {
			distinct[name] = true
		}
	}
	names := make([]string, 0, len(distinct))
	for name := range distinct {
		names = append(names, name)
	}
	sort.Strings(names)
	fmt.Printf("distinct resolved packages: %d\n", len(names))
	for _, name := range names {
		fmt.Printf("  %s\n", name)
	}
}
