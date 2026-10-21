// Package tunitanalyzer is the compiled port of
// tools/check-tunit-analyzer-gate.py.
//
// The control for the TUnit analyzer gate: the analyzer must report a
// PLANTED offender, and a waiver must suppress exactly its own site.
//
// TUnit ships its own compiler analyzers, and the test tree promotes two of
// their diagnostics to errors. A promotion nobody watches is a decoration: if
// the analyzer stopped shipping, or the promotion stopped applying, the build
// would go quietly green and the gate would protect nothing.
//
// So the gate is proved by a planted offender. tests/TUnitAnalyzerCanary is a
// project that is deliberately NOT in Harbor.slnx and carries two offences:
// one that the analyzer must report, and one that a scoped waiver
// suppresses.
//
// This tool builds the canary and asserts exactly that outcome. It is a
// CONTROL, not a report: it exits non-zero unless it saw the one diagnostic
// it planted.
//
// A tool that checks nothing is green forever, so the failure paths are the
// point:
//
//   - the build SUCCEEDED             -> the planted offender was not an
//     error -> the gate is not armed
//   - the build failed for another
//     reason                           -> the control did not run
//   - the diagnostic is at another
//     line                             -> the waiver or the fixture drifted
//   - the waived site was reported     -> a waiver grew past its own site
//   - more than one diagnostic         -> the canary is not the fixture it
//     claims to be
//
// It shells out to `dotnet build` because that is the only thing that can
// observe what the analyzer does. The text this prints is the text the
// python original printed, and the exit codes are the contract of the
// binary: 0 green, 1 red, 2 the gate could not run.
package tunitanalyzer

import (
	"fmt"
	"os"
	"os/exec"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The files the control reads.
const (
	canaryProject = "tests/TUnitAnalyzerCanary/TUnitAnalyzerCanary.csproj"
	canarySource  = "tests/TUnitAnalyzerCanary/PlantedOffender.cs"
)

// The methods of the canary: the one the analyzer must report, and the one
// a scoped waiver suppresses.
const (
	plantedMethod = "Planted_Offender_MustBeReported"
	waivedMethod  = "Waived_Offender_MustNotBeReported"
)

// canarySourceName is the canary source's file name: a diagnostic reported
// anywhere else is not the diagnostic this control planted.
const canarySourceName = "PlantedOffender.cs"

// Run is the gate entry point.
func Run(args []string) int {
	configuration := "Release"
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--configuration":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--configuration needs a name")
				return gate.Broken
			}
			i++
			configuration = args[i]
		case arg == "--selftest" || arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-tunit-analyzer-gate [--configuration Release] [--selftest]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	if selfTest {
		return selfTestRun(root)
	}

	source, err := os.ReadFile(root + "/" + canarySource)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the canary source:", err)
		return gate.Broken
	}
	planted, err := methodLine(string(source), plantedMethod)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	waived, err := methodLine(string(source), waivedMethod)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}

	fmt.Printf("building the canary: %s\n", canaryProject)
	fmt.Printf("  planted offender: line %d\n", planted)
	fmt.Printf("  waived offender:  line %d\n", waived)

	code, log, err := buildCanary(root, configuration)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot run dotnet build:", err)
		return gate.Broken
	}

	if code == 0 {
		fmt.Println()
		fmt.Println("FAIL the canary build SUCCEEDED.")
		fmt.Println("     The planted offender was not an error, so the gate is not armed:")
		fmt.Println("     either TUnit's analyzer stopped shipping, or the promotion to")
		fmt.Println("     an error in tests/Directory.Build.props stopped applying.")
		fmt.Println("     (this is the failure mode a control exists to catch)")
		return gate.Red
	}

	problems := Analyse(log, planted, waived)
	if len(problems) > 0 {
		fmt.Println()
		fmt.Println("FAIL the canary build did not produce the expected diagnostic.")
		for _, problem := range problems {
			fmt.Printf("  - %s\n", problem)
		}
		fmt.Println()
		fmt.Println("build output (last 60 lines):")
		for _, line := range lastLines(log, 60) {
			fmt.Printf("  %s\n", line)
		}
		return gate.Red
	}

	fmt.Println()
	fmt.Println("OK: the analyzer reported the planted offender as an error, and the")
	fmt.Println("    waiver suppressed exactly its own site.")
	return gate.Green
}

// buildCanary builds the canary. It returns the exit code and the combined
// output.
func buildCanary(root string, configuration string) (int, string, error) {
	command := exec.Command("dotnet", "build", canaryProject,
		"-c", configuration, "--nologo", "-v:m")
	command.Dir = root
	var stdout, stderr strings.Builder
	command.Stdout = &stdout
	command.Stderr = &stderr
	err := command.Run()
	code := 0
	if err != nil {
		exitError, ok := err.(*exec.ExitError)
		if !ok {
			return 0, "", err
		}
		code = exitError.ExitCode()
	}
	return code, stdout.String() + stderr.String(), nil
}

// lastLines returns the last count lines of a text.
func lastLines(text string, count int) []string {
	lines := splitLines(text)
	if len(lines) <= count {
		return lines
	}
	return lines[len(lines)-count:]
}
