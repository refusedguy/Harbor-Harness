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
// WHAT A "USED EXCEPTION" IS
//
// One rule, and every path of this gate counts the same way (#2122): an
// exception is USED when a reflection finding matched THAT exception -- the
// finding's path is the exception's path, and the exception's text appears
// in the call the finding names. An exception that no finding matched is
// STALE, and a stale exception fails the gate.
//
// The python original had two counters and said so nowhere: its plain path
// marked every exception used as soon as one of them was used, and its
// report path counted per exception. On a tree where one exception was used
// and another was stale, the plain path said green and the report path said
// red. The rule here is the report path's -- the one the exceptions file
// states -- and the plain path, `--report` and `--json-out` answer with the
// same verdict on the same tree.
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

	return gateVerdict(os.Stderr, findings, loaded)
}
