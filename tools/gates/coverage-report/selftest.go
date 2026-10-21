package coveragereport

// selftest.go -- the reporter's own proof that it can read a report.
//
// The fixture is a throwaway tree with three product files and two coverage
// reports: a file that ran, a file the engine saw and never ran, and a file
// the engine said nothing about. The three are three different things — a
// covered file, a hole, and a file with no code to cover — and a reader that
// cannot tell them apart produces a number nobody can act on.

import (
	"fmt"
	"os"
	"path/filepath"
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
	sortInts(numbers)
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

// sortInts sorts in place.
func sortInts(values []int) {
	for i := 1; i < len(values); i++ {
		for j := i; j > 0 && values[j] < values[j-1]; j-- {
			values[j], values[j-1] = values[j-1], values[j]
		}
	}
}

func selfTestRun() int {
	var failures []string
	fail := func(message string) {
		failures = append(failures, message)
	}

	root, err := os.MkdirTemp("", "harbor-coverage-report-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	tree := filepath.Join(root, "repo")
	coverageDir := filepath.Join(tree, "Coverage")
	writeFile(filepath.Join(tree, "src", "Foo", "Covered.cs"), "// Covered\n")
	writeFile(filepath.Join(tree, "src", "Foo", "Dead.cs"), "// Dead\n")
	writeFile(filepath.Join(tree, "src", "Foo", "Contract.cs"),
		"namespace Foo;\npublic interface IContract\n{\n    void Run();\n}\n")
	cobertura(filepath.Join(coverageDir, "Harbor.Foo.Tests.cobertura.xml"),
		"src/Foo/Covered.cs", map[int]int{1: 1, 2: 0})
	cobertura(filepath.Join(coverageDir, "Harbor.Foo.Other.Tests.cobertura.xml"),
		"src/Foo/Dead.cs", map[int]int{1: 0})

	model := cov.LoadCoverage(coverageDir, tree)
	if len(model.ProductFiles()) != 3 {
		fail(fmt.Sprintf("the product tree has %d file(s), expected 3", len(model.ProductFiles())))
	}
	if status := model.StatusOf("src/Foo/Covered.cs"); status != "covered" {
		fail("the file that ran is not reported as covered: " + status)
	}
	if status := model.StatusOf("src/Foo/Dead.cs"); status != "never-ran" {
		fail("the file the engine saw and never ran is not reported as never-ran: " + status)
	}
	if status := model.StatusOf("src/Foo/Contract.cs"); status != "not-instrumented" {
		fail("the file the engine said nothing about is not reported as not-instrumented: " + status)
	}
	holes := model.Holes()
	if len(holes) != 1 || holes[0] != "src/Foo/Dead.cs" {
		fail(fmt.Sprintf("the holes are %v, expected only src/Foo/Dead.cs", holes))
	}
	report := cov.RenderReport(model, 25)
	for _, wanted := range []string{"src/Foo/Dead.cs", "never ran a line"} {
		if !strings.Contains(report, wanted) {
			fail(fmt.Sprintf("the report does not mention %q", wanted))
		}
	}
	if strings.Contains(report, "100.0%") {
		fail("the report carries a global percentage")
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the reader tells a covered file, a hole and a file " +
		"with no code to cover apart, and the report carries no global percentage")
	return gate.Green
}
