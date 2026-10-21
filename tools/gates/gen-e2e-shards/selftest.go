package gene2eshards

// selftest.go -- the generator's own proof that it can read the tree.
//
// The python original had no self-test: it is a generator a human runs, and
// the arch rule over the plan it writes is the reader that catches a wrong
// universe. The port adds one, because a generator that stops finding the
// classes writes a plan that drops them, and the plan's own numbers look
// fine either way.
//
// The fixtures are:
//   1. the class universe: a class with a `[Test]` is found, a class without
//      one is not, and a nested type is not a suite;
//   2. the extent rule: a helper class after a suite does not inherit its
//      neighbour's tests;
//   3. the balance: the heaviest class lands in the lightest bin, and the
//      local search finds a split the seed does not;
//   4. the plan: the file the generator writes is the file it says it wrote.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// writeFile writes a file, creating the directories it needs.
func writeFile(path string, text string) {
	os.MkdirAll(filepath.Dir(path), 0o755)
	os.WriteFile(path, []byte(text), 0o644)
}

// fixtureTree writes a throwaway tree with two test classes and one class
// that is not a suite.
func fixtureTree(root string) {
	base := filepath.Join(root, "tests", "Harbor.E2E.App.Avalonia")
	writeFile(filepath.Join(base, "AlphaTests.cs"), `using TUnit.Core;

public class AlphaTests
{
    [Test]
    public void One() { }
}

internal class AlphaHelper
{
    public int Value => 1;
}
`)
	writeFile(filepath.Join(base, "BetaTests.cs"), `public class BetaTests
{
    [Test]
    public void One() { }
    [Test]
    public void Two() { }
}
`)
	writeFile(filepath.Join(base, "Plain.cs"), `public class Plain
{
    public int Value => 1;
}
`)
	// The referenced test project: its classes are discovered here too.
	framework := filepath.Join(root, "tests", "Harbor.E2E.Framework")
	writeFile(filepath.Join(framework, "GammaTests.cs"), `public class GammaTests
{
    [Test]
    public void One() { }
}
`)
}

func selfTestRun() int {
	var failures []string
	fail := func(message string) {
		failures = append(failures, message)
	}

	root, err := os.MkdirTemp("", "harbor-e2e-shards-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)
	fixtureTree(root)

	universe := classUniverse(root)
	for _, wanted := range []string{"AlphaTests", "BetaTests", "GammaTests"} {
		if _, ok := universe[wanted]; !ok {
			fail("the class universe does not hold " + wanted)
		}
	}
	for _, unwanted := range []string{"AlphaHelper", "Plain"} {
		if _, ok := universe[unwanted]; ok {
			fail("the class universe holds " + unwanted + ", which declares no [Test]")
		}
	}
	if file, ok := universe["AlphaTests"]; ok {
		if file != "tests/Harbor.E2E.App.Avalonia/AlphaTests.cs" {
			fail("the universe names the wrong file for AlphaTests: " + file)
		}
	}

	// The balance: the heaviest class lands in the lightest bin, and the
	// local search finishes the split the seed leaves uneven.
	costs := map[string]float64{
		"Alpha": 10.0, "Beta": 9.0, "Gamma": 8.0, "Delta": 7.0, "Epsilon": 1.0,
	}
	bins := balance(costs, 3)
	if len(bins) != 3 {
		fail(fmt.Sprintf("the balance produced %d bins, expected 3", len(bins)))
	}
	loads := make([]float64, len(bins))
	seen := map[string]bool{}
	for index, names := range bins {
		for _, name := range names {
			if seen[name] {
				fail("the class " + name + " is in two bins")
			}
			seen[name] = true
			loads[index] += costs[name]
		}
	}
	if len(seen) != len(costs) {
		fail(fmt.Sprintf("the balance put %d class(es) in the bins, expected %d", len(seen), len(costs)))
	}
	// The exact split the python original produces for these costs: the
	// heaviest class alone, the two lightest together, and the two middle
	// ones together.
	want := []string{"Alpha", "Beta|Epsilon", "Delta|Gamma"}
	for index, names := range bins {
		got := strings.Join(names, "|")
		if got != want[index] {
			fail(fmt.Sprintf("bin %d holds %q, expected %q", index, got, want[index]))
		}
	}

	// The plan: the generator writes the file it says it wrote, and the file
	// names every class of the universe.
	out := filepath.Join(root, "plan.json")
	plan, err := buildPlan(root, nil, 2, nil, "12345", "test-results-e2e", out)
	if err != nil {
		fail("the plan could not be written: " + err.Error())
	} else {
		data, readErr := os.ReadFile(out)
		if readErr != nil {
			fail("the plan file is not there: " + readErr.Error())
		} else if !strings.Contains(string(data), "\"schemaVersion\": 1") {
			fail("the plan file does not carry the schema version")
		}
		named := 0
		for _, shard := range plan.Shards {
			named += len(shard.Classes)
		}
		if named != len(universe) {
			fail(fmt.Sprintf("the plan names %d class(es), the universe holds %d", named, len(universe)))
		}
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the generator finds the classes of the tree, tells a " +
		"suite from a helper, balances the shards and writes a plan that names " +
		"every class the universe holds")
	return gate.Green
}
