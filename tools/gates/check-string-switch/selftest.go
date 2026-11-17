package stringswitch

// selftest.go -- the gate's own proof that it can fail.
//
// The python original planted a switch in a throwaway tree and asserted
// that the census finds it, that the ratchet goes red on an unlisted
// switch and on a stale row and on a floor, that the proposal names a
// value per label, and that every reviewed rewrite is in step with the
// tree it names.
//
// The port plants the same source and asserts the same things.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

func selfTestRun() int {
	var failures []string
	check := func(name string, condition bool, detail string) {
		if condition {
			fmt.Printf("  ok    %s\n", name)
			return
		}
		fmt.Printf("  FAIL  %s %s\n", name, detail)
		failures = append(failures, name)
	}

	sites := findStringSwitches(selfTestSource, "Sample.cs")
	var statements, expressions []site
	for _, s := range sites {
		if s.kind == "statement" {
			statements = append(statements, s)
		} else {
			expressions = append(expressions, s)
		}
	}

	check("the statement-form switch is found",
		len(statements) == 1 && statements[0].selector == "kind",
		fmt.Sprintf("%v", sites))
	check("its two labels are read",
		len(statements) == 1 && strings.Join(statements[0].labels, ",") == "udp,tcp",
		fmt.Sprintf("%v", statements))
	check("its default arm is seen",
		len(statements) == 1 && statements[0].hasDefault,
		fmt.Sprintf("%v", statements))
	check("the switch EXPRESSION is found too",
		len(expressions) == 1 && strings.Join(expressions[0].labels, ",") == "udp",
		fmt.Sprintf("%v", expressions))
	check("a `case` in a line comment is not a switch",
		!hasLabel(sites, "not-a-switch"),
		fmt.Sprintf("%v", sites))
	check("a `case` in a block comment is not a switch",
		!hasLabel(sites, "also-not"),
		fmt.Sprintf("%v", sites))
	check("a `case` in a raw string is not a switch",
		!hasLabel(sites, "emitted"),
		fmt.Sprintf("%v", sites))
	check("a char-literal arm is not a string switch",
		!hasLabel(sites, "a"),
		fmt.Sprintf("%v", sites))

	// The ratchet must go red on an unlisted switch, and on a stale row.
	tmp, err := os.MkdirTemp("", "harbor-stringswitch-selftest")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	defer os.RemoveAll(tmp)
	src := filepath.Join(tmp, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	if err := os.WriteFile(filepath.Join(src, "Sample.cs"), []byte(selfTestSource), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	found := census([]string{src}, tmp)
	var findings []site
	for _, s := range found {
		if s.kind == "statement" {
			findings = append(findings, s)
		}
	}
	check("an unlisted string switch is a finding", len(findings) == 1,
		fmt.Sprintf("%v", found))
	check("a listed exception matches the switch it names",
		hasKey(found, "src/Sample.cs | kind"),
		fmt.Sprintf("%v", found))
	check("an exception for a switch the tree does not have is stale",
		!hasKey(found, "src/Sample.cs | not-a-switch"),
		fmt.Sprintf("%v", found))

	// The gate itself, end to end, on the throwaway tree.
	exceptionsFile := filepath.Join(tmp, "exceptions.txt")
	verdict := func(text string, minLabels int) int {
		if err := os.WriteFile(exceptionsFile, []byte(text), 0o644); err != nil {
			return -1
		}
		exceptions, err := loadExceptions(exceptionsFile)
		if err != nil {
			return -1
		}
		return runGateQuiet(found, exceptions, minLabels)
	}
	check("the gate is green when every switch is listed",
		verdict("src/Sample.cs | kind | listed\n", 0) == 0, "")
	check("the gate is red when a row names a switch the tree does not have",
		verdict("src/Sample.cs | gone | stale row\n", 0) == 1, "")
	check("the gate is red when a switch is not listed",
		verdict("", 0) == 1, "")
	check("the gate is red when the census finds fewer labels than the floor",
		verdict("src/Sample.cs | kind | listed\n", 10000) == 1, "")

	// The proposal must produce a table for the mechanical shape.
	sampleSite := statements[0]
	table, reason := proposeTable(sampleSite, selfTestSource)
	check("the proposal names a value per label",
		table != "" && strings.Contains(table, `["udp"] = "a",`) &&
			strings.Contains(table, `["tcp"] = "b",`),
		fmt.Sprintf("%q %q", table, reason))

	// The reviewed rewrites must describe the tree as it stands: every
	// pair's NEW text has to be in the file it names.
	check("the rewrites module carries the reviewed rewrites", len(rewrites) > 0, "")
	root, err := gate.RepoRoot()
	if err == nil {
		var drifted []string
		for _, r := range rewrites {
			data, readErr := os.ReadFile(filepath.Join(root, r.path))
			if readErr != nil {
				drifted = append(drifted, r.path)
				continue
			}
			source := string(data)
			for _, pair := range r.pairs {
				if !strings.Contains(source, pair[1]) {
					drifted = append(drifted, r.path)
					break
				}
			}
		}
		check("every reviewed rewrite is in step with the tree it names",
			len(drifted) == 0,
			fmt.Sprintf("%v", drifted))
	}

	if len(failures) > 0 {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %d check(s)\n", len(failures))
		return gate.Red
	}
	fmt.Println("self-test ok: the census still finds a planted string switch.")
	return gate.Green
}

// hasLabel reports whether one of the sites carries the label.
func hasLabel(sites []site, label string) bool {
	for _, s := range sites {
		for _, item := range s.labels {
			if item == label {
				return true
			}
		}
	}
	return false
}

// hasKey reports whether one of the sites carries the key.
func hasKey(sites []site, key string) bool {
	for _, s := range sites {
		if s.key() == key {
			return true
		}
	}
	return false
}
