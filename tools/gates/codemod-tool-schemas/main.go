// Package codemodschemas is the compiled port of tools/codemod-tool-schemas.py.
//
// The codemod that turned the hand-written JSON parameter schemas into
// declarative [ToolParam] attributes, and the writer of the pin they are
// pinned with.
//
// The schema is the tool's contract with the model, so it may not drift by a
// byte, and two copies of it is exactly how it drifted. The schema now
// lives ONCE, as a typed declaration: a [ToolSchema] marker and one
// [ToolParam] per argument on the tool class.
//
// This tool is the migration (--migrate) and the pin writer (--write-pin).
// The pin is the SHA-256 of the canonical schema of every tool, and the
// generator refuses to compile when a schema differs from its pin.
//
// The gate that says the pin still agrees with the declarations is
// harbor-gates check-tool-schema-pin, and both read the declaration through
// the same model, so the two cannot drift apart.
//
// A TOOL A HUMAN RUNS, and no workflow runs it.
package codemodschemas

import (
	"fmt"
	"os"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/toolschema"
)

// Run is the tool entry point.
func Run(args []string) int {
	migrate := false
	writePin := false
	report := false

	for _, arg := range args {
		switch arg {
		case "--migrate":
			migrate = true
		case "--write-pin":
			writePin = true
		case "--report":
			report = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates codemod-tool-schemas [--migrate] [--write-pin] [--report]")
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

	if migrate {
		migrated := 0
		for _, path := range toolschema.ToolFiles(root) {
			className, _, err := toolschema.MigrateFile(path)
			if err != nil {
				fmt.Fprintln(os.Stderr, "cannot migrate:", err)
				return gate.Red
			}
			if className != "" {
				migrated++
			}
		}
		fmt.Printf("migrated %d tool files\n", migrated)
	}

	entries, err := toolschema.Collect(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the declarations:", err)
		return gate.Red
	}
	if len(entries) == 0 {
		fmt.Fprintln(os.Stderr, "no tool declares a schema")
		return gate.Red
	}

	if writePin {
		if err := toolschema.WritePin(root, entries); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the pin:", err)
			return gate.Broken
		}
		fmt.Printf("wrote %d pins to %s\n", len(entries), toolschema.PinRelative)
	}

	if report {
		for _, entry := range entries {
			fmt.Println(toolschema.Describe(entry))
		}
	}

	return gate.Green
}
