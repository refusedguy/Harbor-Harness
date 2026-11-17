// Package pinschema is the compiled port of tools/check-tool-schema-pin.py.
//
// The gate compares a builtin tool's parameter schema against the PIN that
// was written from it: the schema is the tool's contract with the model, so
// it may not drift by a byte.
//
// The compiler already enforces this: Harbor.CodeGen.ToolSchemaGenerator
// renders the same declaration and refuses to compile a tool whose schema
// differs from its pin. This gate asks the SAME question in a place that
// needs no SDK, because the generator is the enforcement, and a rule whose
// only reader is the compiler never runs on a pull request that does not
// build. The pin is what the two share, so the two cannot disagree about
// what the schema is.
//
// The failure paths are the point. A tool that checks nothing is green
// forever, so:
//
//   - the pin is missing          -> FAIL
//   - a tool has no pin           -> FAIL
//   - a pin names a tool that is
//     not there                   -> FAIL
//   - a schema hashes to another
//     value than its pin          -> FAIL
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could
// not run.
package pinschema

import (
	"fmt"
	"os"
	"strconv"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/toolschema"
)

// Run is the gate entry point.
func Run(args []string) int {
	minTools := 0
	writePin := false
	report := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--min-tools":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-tools needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-tools needs a number")
				return gate.Broken
			}
			minTools = value
		case len(arg) > 12 && arg[:12] == "--min-tools=":
			value, err := strconv.Atoi(arg[12:])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-tools needs a number")
				return gate.Broken
			}
			minTools = value
		case arg == "--write-pin":
			writePin = true
		case arg == "--report":
			report = true
		case arg == "--selftest" || arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-tool-schema-pin [--min-tools N] [--write-pin] [--report] [--selftest]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	entries, err := toolschema.Collect(root)
	if err != nil {
		// A tree whose declarations do not parse is a tree the pin cannot
		// describe, and the gate says so instead of passing.
		fmt.Fprintf(os.Stderr, "::error::the tool declarations do not parse: %s\n", err.Error())
		return gate.Red
	}

	if minTools != 0 && len(entries) < minTools {
		fmt.Fprintf(os.Stderr,
			"::error::only %d tools declare a schema, and the floor is %d: a scan that measured less than it should cannot say that the tree is fine\n",
			len(entries), minTools)
		return gate.Red
	}

	if writePin {
		if err := toolschema.WritePin(root, entries); err != nil {
			fmt.Fprintln(os.Stderr, "::error::cannot write the pin:", err)
			return gate.Broken
		}
		fmt.Printf("wrote %d pins to %s\n", len(entries), toolschema.PinRelative)
		return gate.Green
	}

	if report {
		for _, entry := range entries {
			fmt.Println(toolschema.Describe(entry))
		}
		return gate.Green
	}

	problems := Analyze(entries, toolschema.ReadPins(root))
	for _, problem := range problems {
		fmt.Fprintf(os.Stderr, "::error::%s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}

	fmt.Printf("the pin agrees with the %d declared tool schemas\n", len(entries))
	return gate.Green
}
