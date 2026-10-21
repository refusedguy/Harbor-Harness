package checkcoverageregistry

// selftest.go -- the gate's own proof that it can fail.
//
// The two defects the gate exists for are planted one at a time:
//   - a file that STOPPED BEING COVERED and is not in the registry;
//   - a registry row that is no longer NEEDED because the file is covered.
//
// And the three states around them: an empty measurement, an unreadable
// report, and a partial measurement.

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/cov"
	"harbor.local/gates/internal/gate"
)

// writeFile writes a file, creating the directories it needs.
func writeFile(path string, text string) {
	os.MkdirAll(filepath.Dir(path), 0o755)
	os.WriteFile(path, []byte(text), 0o644)
}

// cobertura writes a minimal cobertura file: one class, the given line hits.
func cobertura(path string, filename string, lines map[int]int) {
	var body strings.Builder
	numbers := make([]int, 0, len(lines))
	for number := range lines {
		numbers = append(numbers, number)
	}
	sort.Ints(numbers)
	for _, number := range numbers {
		fmt.Fprintf(&body, "        <line number=\"%d\" hits=\"%d\" branch=\"false\" />\n",
			number, lines[number])
	}
	writeFile(path, fmt.Sprintf(`<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<coverage line-rate="0.5" branch-rate="0" complexity="1" version="1.9" timestamp="0">
  <sources><source>/_</source></sources>
  <packages>
    <package name="Foo" line-rate="0.5" branch-rate="0" complexity="1">
      <classes>
        <class name="Foo.%s" filename="%s" line-rate="0.5" branch-rate="0" complexity="1">
          <lines>
%s          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
`, strings.TrimSuffix(filepath.Base(filename), ".cs"), filename, body.String()))
}

// fixture is a throwaway tree with three product files and one measurement.
//
// `Covered.cs` ran, `Dead.cs` was measured and never ran a line, and
// `Orphan.cs` belongs to a project no report mentions at all. The registry
// that goes with it accepts the two holes.
type fixture struct {
	root      string
	sourceRoot string
	coverage  string
	registry  string
}

// newFixture writes the fixture into a directory of its own.
func newFixture(root string) fixture {
	sourceRoot := filepath.Join(root, "repo")
	coverageDir := filepath.Join(sourceRoot, "Coverage")
	registry := filepath.Join(root, "registry.txt")
	writeFile(filepath.Join(sourceRoot, "src", "Foo", "Covered.cs"), "// Covered\n")
	writeFile(filepath.Join(sourceRoot, "src", "Foo", "Dead.cs"), "// Dead\n")
	writeFile(filepath.Join(sourceRoot, "src", "Bar", "Orphan.cs"), "// Orphan\n")
	cobertura(filepath.Join(coverageDir, "Harbor.Foo.Tests.cobertura.xml"),
		"src/Foo/Covered.cs", map[int]int{1: 1, 2: 0})
	cobertura(filepath.Join(coverageDir, "Harbor.Foo.Other.Tests.cobertura.xml"),
		"src/Foo/Dead.cs", map[int]int{1: 0})
	writeFile(registry, "# the holes of the fixture\n"+
		"src/Foo/Dead.cs | NEVER-RAN | The measurement saw the file and ran none of its lines. | @refusedguy\n"+
		"src/Bar/Orphan.cs | NOT-MEASURED | The project belongs to is not loaded by any suite of the lane. | @refusedguy\n")
	return fixture{root: root, sourceRoot: sourceRoot, coverage: coverageDir, registry: registry}
}

// modelFor is the model of a fixture, with its registry loaded.
func modelFor(f fixture) *cov.Model {
	model := cov.LoadCoverage(f.coverage, f.sourceRoot)
	rows, err := cov.ParseRegistry(f.registry)
	if err != nil {
		return model
	}
	model.Registry = rows
	return model
}

// codesOf are the codes of the findings.
func codesOf(findings []finding) map[string]bool {
	out := map[string]bool{}
	for _, item := range findings {
		out[item.Code] = true
	}
	return out
}

// whereOf are the places the findings with a code name.
func whereOf(findings []finding, code string) []string {
	var out []string
	for _, item := range findings {
		if item.Code == code {
			out = append(out, item.Where)
		}
	}
	return out
}

func selfTestRun() int {
	var failures []string
	fail := func(message string) {
		failures = append(failures, message)
	}

	root, err := os.MkdirTemp("", "harbor-coverage-registry-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	// The clean fixture: the registry matches the measurement.
	clean := newFixture(filepath.Join(root, "clean"))
	findings := evaluate(modelFor(clean), 3, 1, false)
	if len(findings) > 0 {
		messages := make([]string, 0, len(findings))
		for _, item := range findings {
			messages = append(messages, item.String())
		}
		fail("the clean fixture is not clean: " + strings.Join(messages, "; "))
	}

	// (a) A file that stopped being covered: the registry still says
	//     nothing about it, and the measurement now says it never ran.
	stopped := newFixture(filepath.Join(root, "stopped"))
	cobertura(filepath.Join(stopped.coverage, "Harbor.Foo.Tests.cobertura.xml"),
		"src/Foo/Covered.cs", map[int]int{1: 0, 2: 0})
	findings = evaluate(modelFor(stopped), 3, 0, false)
	codes := codesOf(findings)
	if !codes["COV-UNCOVERED"] {
		fail(fmt.Sprintf("a file that stopped being covered did not raise COV-UNCOVERED; got %v", codes))
	} else {
		where := whereOf(findings, "COV-UNCOVERED")
		if !contains(where, "src/Foo/Covered.cs") {
			fail("the finding did not name the file that stopped being covered; it named " + fmt.Sprint(where))
		}
	}

	// (b) A registry row that is no longer needed: the file it accepts
	//     is covered now.
	stale := newFixture(filepath.Join(root, "stale"))
	writeFile(stale.registry, readFile(stale.registry)+
		"src/Foo/Covered.cs | NEVER-RAN | A row for a file that is covered, so the registry is stale in the planted fixture. | @refusedguy\n")
	findings = evaluate(modelFor(stale), 3, 1, false)
	codes = codesOf(findings)
	if !codes["COV-ROW-STALE"] {
		fail(fmt.Sprintf("a registry row that is no longer needed did not raise COV-ROW-STALE; got %v", codes))
	} else {
		where := whereOf(findings, "COV-ROW-STALE")
		if !contains(where, "src/Foo/Covered.cs") {
			fail("the stale-row finding did not name the row; it named " + fmt.Sprint(where))
		}
	}

	// (c) An empty measurement: the gate must not call a tree with no
	//     data at all clean.
	empty := newFixture(filepath.Join(root, "empty"))
	entries, _ := os.ReadDir(empty.coverage)
	for _, entry := range entries {
		os.Remove(filepath.Join(empty.coverage, entry.Name()))
	}
	findings = evaluate(modelFor(empty), 3, 0, false)
	codes = codesOf(findings)
	if !codes["COV-DATA-EMPTY"] {
		fail(fmt.Sprintf("an empty measurement did not raise COV-DATA-EMPTY; got %v", codes))
	}

	// (c2) A report that cannot be READ is a measurement that did not
	//      happen: the files it would have covered look uncovered, so the
	//      comparison has to be skipped.
	unreadable := newFixture(filepath.Join(root, "unreadable"))
	writeFile(filepath.Join(unreadable.coverage, "Harbor.Foo.Tests.cobertura.xml"),
		"<coverage><packages>truncated")
	model := modelFor(unreadable)
	if len(model.Unreadable) == 0 {
		fail("the unreadable report was not reported as unreadable")
	}
	findings = evaluate(model, 3, 0, true)
	codes = codesOf(findings)
	if codes["COV-UNCOVERED"] {
		fail("an unreadable report still produced hole findings, so a broken report " +
			"would be reported as a hole in the tree")
	}

	// (d) A row that names a file the tree does not have.
	missing := newFixture(filepath.Join(root, "missing"))
	writeFile(missing.registry, readFile(missing.registry)+
		"src/Foo/Gone.cs | NEVER-RAN | A row for a file that is not in the tree any more, so the registry is stale. | @refusedguy\n")
	findings = evaluate(modelFor(missing), 3, 1, false)
	codes = codesOf(findings)
	if !codes["COV-ROW-MISSING"] {
		fail(fmt.Sprintf("a row for a file that is not in the tree did not raise COV-ROW-MISSING; got %v", codes))
	}

	// (e) A PARTIAL measurement: the direction that a partial
	//     measurement cannot support is skipped, the other one is not.
	partial := newFixture(filepath.Join(root, "partial"))
	cobertura(filepath.Join(partial.coverage, "Harbor.Foo.Tests.cobertura.xml"),
		"src/Foo/Covered.cs", map[int]int{1: 0, 2: 0})
	findings = evaluate(modelFor(partial), 3, 0, true)
	codes = codesOf(findings)
	if codes["COV-UNCOVERED"] {
		fail("a partial measurement still reported the uncovered files, so a flaky " +
			"suite would make the gate fire on a flake")
	}
	partialStale := newFixture(filepath.Join(root, "partial-stale"))
	writeFile(partialStale.registry, readFile(partialStale.registry)+
		"src/Foo/Covered.cs | NEVER-RAN | A row for a file that is covered, so the registry is stale in the planted fixture. | @refusedguy\n")
	findings = evaluate(modelFor(partialStale), 3, 1, true)
	codes = codesOf(findings)
	if !codes["COV-ROW-STALE"] {
		fail("a partial measurement stopped reporting the stale rows, so a flake would " +
			"hide a row that has to go")
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the gate reports a file that stopped being covered, a " +
		"registry row that is no longer needed, an empty measurement and a row " +
		"for a file that is gone — the clean fixture stays clean, and a " +
		"partial measurement (a failed suite, an unreadable report) compares " +
		"only the direction it supports")
	return gate.Green
}

// readFile reads a file, or returns the empty string.
func readFile(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return string(data)
}

// contains reports whether the list holds the value.
func contains(values []string, value string) bool {
	for _, item := range values {
		if item == value {
			return true
		}
	}
	return false
}
