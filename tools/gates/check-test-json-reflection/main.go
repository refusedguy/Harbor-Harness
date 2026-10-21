// Package jsonreflection is the compiled port of
// tools/check-test-json-reflection.py.
//
// The gate over the SERIALIZER the test tree uses: does every
// `JsonSerializer` call under `tests/` name a source-generated type info, or
// does it take the reflection resolver?
//
// The product side of System.Text.Json already rides source generation. The
// TEST side did not, and a reflection call in a test is a test that can stay
// green while the product is broken: a misspelled member name compiles,
// serializes to a DIFFERENT key, and the test then asserts on the wrong
// field.
//
// The exceptions file (`tools/test-json-reflection-exceptions.txt`) carries
// the calls that cannot be moved yet, one per line:
//
//	<path relative to the repo root> | <text that must appear in the call> | <reason>
//
// It is a RATCHET, not a target: an exception the tree no longer produces is
// a failure. A gate whose exception list only grows is a gate that stops
// being a gate.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package jsonreflection

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
)

// The scope, the exceptions file and the tree they are read from.
const (
	testsDir       = "tests"
	exceptionsFile = "tools/test-json-reflection-exceptions.txt"
)

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	exceptions := ""
	reportMode := false
	jsonOut := false
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
		case arg == "--exceptions":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--exceptions needs a path")
				return gate.Broken
			}
			i++
			exceptions = args[i]
		case arg == "--report":
			reportMode = true
		case arg == "--json-out":
			jsonOut = true
		case arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-test-json-reflection [--root PATH] [--exceptions PATH] [--report] [--json-out] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	repoRoot, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	if selfTest {
		return selfTestRun()
	}

	scope := root
	if scope == "" {
		scope = filepath.Join(repoRoot, testsDir)
	}
	absolute, err := filepath.Abs(scope)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	info, err := os.Stat(absolute)
	if err != nil || !info.IsDir() {
		fmt.Fprintf(os.Stderr, "no tests tree at %s\n", absolute)
		return gate.Broken
	}

	exceptionPath := exceptions
	if exceptionPath == "" {
		exceptionPath = filepath.Join(repoRoot, exceptionsFile)
	}

	findings := scan(absolute, repoRoot)
	loaded, err := loadExceptions(exceptionPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}

	if reportMode || jsonOut {
		return report(os.Stdout, findings, loaded, jsonOut)
	}

	violations, _, _ := partition(findings, loaded)
	used := gateUsed(findings, loaded)
	var stale []exception
	for _, item := range loaded {
		if !used[item.line] {
			stale = append(stale, item)
		}
	}
	if len(violations) > 0 {
		fmt.Fprintf(os.Stderr,
			"reflection JSON in tests/: %d call(s) do not name a source-generated type info.\n",
			len(violations))
		for _, violation := range violations {
			fmt.Fprintf(os.Stderr, "  %s:%d  %s\n", violation.path, violation.line, truncate(violation.call, 110))
		}
		fmt.Fprintf(os.Stderr,
			"\nPass the type info from a source-generated context (JsonSerializer.Serialize(value, SomeContext.Default.SomeType)), or add a documented exception to %s.\n",
			exceptionsFile)
	}
	if len(stale) > 0 {
		fmt.Fprintf(os.Stderr,
			"reflection JSON in tests/: %d exception(s) no longer apply — delete them.\n",
			len(stale))
		for _, item := range stale {
			fmt.Fprintf(os.Stderr, "  %s (%s:%d)\n", item.path, exceptionsFile, item.line)
		}
	}

	if len(violations) > 0 || len(stale) > 0 {
		return gate.Red
	}
	return gate.Green
}
