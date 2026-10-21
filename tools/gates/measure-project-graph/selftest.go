package measureprojectgraph

// selftest.go -- the gate's own proof that it can fail.
//
// The fixture is a graph with a KNOWN shape: a chain of three projects and a
// fourth that depends on the middle one. The depth is 2, the widest level
// holds 2 projects, and the critical path is the chain. A rule that counted
// the edges instead of the levels, or that read the depth from the number of
// projects, would report something else.

import (
	"fmt"

	"harbor.local/gates/internal/gate"
)

// selfTestRun measures a planted graph and asserts its shape.
func selfTestRun() int {
	projects := []string{
		"src/A/A.csproj",
		"src/B/B.csproj",
		"src/C/C.csproj",
		"src/D/D.csproj",
	}
	// A -> B -> C is the chain; D depends on B as well, so level 1 holds
	// both B and D. The references are relative to the project directory,
	// the way a ProjectReference is.
	edges := map[string][]string{
		"src/A/A.csproj": {"../B/B.csproj"},
		"src/B/B.csproj": {"../C/C.csproj"},
		"src/C/C.csproj": {},
		"src/D/D.csproj": {"../B/B.csproj"},
	}
	reader := func(path string) []string {
		for project, refs := range edges {
			if project == path {
				return refs
			}
		}
		return nil
	}

	measured := analyze("fixture.slnx", projects, ".", reader)

	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}
	check(len(measured.projects) == 4, fmt.Sprintf("4 projects, got %d", len(measured.projects)))
	check(measured.edgeCount == 3, fmt.Sprintf("3 edges, got %d", measured.edgeCount))
	check(measured.depth == 2, fmt.Sprintf("the depth is 2, got %d", measured.depth))
	check(measured.width == 2, fmt.Sprintf("the widest level holds 2 projects, got %d", measured.width))
	check(measured.levelsWide4 == 0, fmt.Sprintf("no level is wide enough for 4 compilers, got %d", measured.levelsWide4))
	check(measured.levelsWide2 == 1, fmt.Sprintf("one level is wide enough for 2 compilers, got %d", measured.levelsWide2))
	check(len(measured.critical) == 3, fmt.Sprintf("the critical path is 3 long, got %d", len(measured.critical)))
	if len(measured.critical) == 3 {
		check(measured.critical[0] == "src/A/A.csproj" &&
			measured.critical[1] == "src/B/B.csproj" &&
			measured.critical[2] == "src/C/C.csproj",
			fmt.Sprintf("the critical path is A -> B -> C, got %v", measured.critical))
	}
	check(len(measured.levelNumbers) == 3, fmt.Sprintf("3 levels, got %d", len(measured.levelNumbers)))
	if len(measured.levelNumbers) == 3 {
		check(measured.levelNumbers[0] == 0 && measured.levelNumbers[1] == 1 && measured.levelNumbers[2] == 2,
			fmt.Sprintf("the levels are 0, 1, 2, got %v", measured.levelNumbers))
		check(len(measured.byLevel[2]) == 2, fmt.Sprintf("level 2 holds A and D, got %d", len(measured.byLevel[2])))
	}

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: the planted graph measured to depth 2, width 2 and the chain A -> B -> C.")
	return gate.Green
}
