package measureprojectgraph

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/buildcost"
	"harbor.local/gates/internal/gate"
)

// selfTestRun measures a planted graph and asserts its shape.
//
// The fixture is a graph with a KNOWN shape: a chain of three projects and a
// fourth that depends on the middle one. The depth is 2, the widest level
// holds 2 projects, and the critical path is the chain. A rule that counted
// the edges instead of the levels, or that read the depth from the number
// of projects, would report something else.
//
// The radius is asserted too: the chain's owner is rebuilt by everything
// that depends on it, and the fourth project, which depends on the middle
// one, is a consumer of the chain's head.
func selfTestRun() int {
	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}

	directory, err := os.MkdirTemp("", "project-graph-selftest")
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: cannot make a temporary directory: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(directory)

	// A -> B -> C is the chain; D depends on B as well, so level 1 holds
	// both B and D. The references are relative to the project directory,
	// the way a ProjectReference is.
	plant := func(path, content string) {
		full := filepath.Join(directory, path)
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			problems = append(problems, fmt.Sprintf("cannot make %s: %v", path, err))
			return
		}
		if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
			problems = append(problems, fmt.Sprintf("cannot write %s: %v", path, err))
		}
	}
	plant("fixture.slnx", `<Solution>
    <Project Path="src/A/A.csproj"/>
    <Project Path="src/B/B.csproj"/>
    <Project Path="src/C/C.csproj"/>
    <Project Path="src/D/D.csproj"/>
</Solution>
`)
	plant("src/A/A.csproj", `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../B/B.csproj"/>
  </ItemGroup>
</Project>
`)
	plant("src/B/B.csproj", `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../C/C.csproj"/>
  </ItemGroup>
</Project>
`)
	plant("src/C/C.csproj", `<Project Sdk="Microsoft.NET.Sdk">
</Project>
`)
	plant("src/D/D.csproj", `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../B/B.csproj"/>
  </ItemGroup>
</Project>
`)

	graph, err := buildcost.ReadGraph(filepath.Join(directory, "fixture.slnx"))
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: the planted graph does not read: %v\n", err)
		return gate.Red
	}
	measured := analyze(graph)

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

	// THE RADIUS. C is the head of the chain: A depends on B, B depends on
	// C, and D depends on B. So C's consumers are B, A and D.
	radius := graph.RadiusOf("src/C/C.csproj")
	check(len(radius.Consumers) == 3,
		fmt.Sprintf("C has 3 transitive consumers, got %d", len(radius.Consumers)))
	check(radius.Direct == 1,
		fmt.Sprintf("C has 1 direct consumer, got %d", radius.Direct))
	aRadius := graph.RadiusOf("src/A/A.csproj")
	check(len(aRadius.Consumers) == 0,
		fmt.Sprintf("A has no consumers, got %d", len(aRadius.Consumers)))
	dRadius := graph.RadiusOf("src/D/D.csproj")
	check(len(dRadius.Consumers) == 0,
		fmt.Sprintf("D has no consumers, got %d", len(dRadius.Consumers)))

	// The price of the radius: C's radius is C itself plus its three
	// consumers (B, A and D), so 4 + 2 + 1 + 8.
	prices := map[string]float64{
		"src/A/A.csproj": 1,
		"src/B/B.csproj": 2,
		"src/C/C.csproj": 4,
		"src/D/D.csproj": 8,
	}
	rows := radiusRows(graph, prices)
	var cRow radiusRow
	for _, row := range rows {
		if row.Project == "src/C/C.csproj" {
			cRow = row
		}
	}
	check(cRow.Seconds == 15,
		fmt.Sprintf("C's radius costs 4 + 2 + 1 + 8 = 15, got %.1f", cRow.Seconds))
	check(cRow.Priced, "the radius of a priced graph says it is priced")

	// A project that references the same project TWICE has one consumer,
	// not two. The tree has two such rows today (Harbor.App.Cli names
	// Harbor.Tui.CellForge.Engine twice, and Harbor.Hosting.Tests names
	// Harbor.Abstractions.Contracts twice), and a graph that counts them
	// twice says a project has a consumer it does not have.
	duplicateDirectory, err := os.MkdirTemp("", "project-graph-duplicate")
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: cannot make a temporary directory: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(duplicateDirectory)
	write := func(path, content string) {
		full := filepath.Join(duplicateDirectory, path)
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			problems = append(problems, fmt.Sprintf("cannot make %s: %v", path, err))
			return
		}
		if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
			problems = append(problems, fmt.Sprintf("cannot write %s: %v", path, err))
		}
	}
	write("dup.slnx", `<Solution>
    <Project Path="src/One/One.csproj"/>
    <Project Path="src/Two/Two.csproj"/>
</Solution>
`)
	write("src/One/One.csproj", `<Project Sdk="Microsoft.NET.Sdk">
</Project>
`)
	write("src/Two/Two.csproj", `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../One/One.csproj"/>
  </ItemGroup>
  <ItemGroup Condition="'$(Configuration)' == 'Release'">
    <ProjectReference Include="..\One\One.csproj"/>
  </ItemGroup>
</Project>
`)
	duplicateGraph, err := buildcost.ReadGraph(filepath.Join(duplicateDirectory, "dup.slnx"))
	if err != nil {
		fmt.Printf("  SELF-TEST FAIL: the duplicate-reference graph does not read: %v\n", err)
		return gate.Red
	}
	duplicateEdges := 0
	for _, references := range duplicateGraph.References {
		duplicateEdges += len(references)
	}
	check(duplicateEdges == 1,
		fmt.Sprintf("a project that references another one twice has 1 edge, got %d", duplicateEdges))
	check(len(duplicateGraph.Consumers["src/One/One.csproj"]) == 1,
		fmt.Sprintf("the referenced project has 1 consumer, got %d",
			len(duplicateGraph.Consumers["src/One/One.csproj"])))

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: the planted graph measured to depth 2, width 2 and the chain A -> B -> C,")
	fmt.Println("             and C's radius held the three projects that depend on it.")
	return gate.Green
}
