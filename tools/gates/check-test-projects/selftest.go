package testprojects

// selftest.go -- the failure path, proved on every run of the gate in CI.
//
// The fixtures cover: a clean tree staying clean; a project that no list and
// no referencing project reaches; a project that no list names but a LISTED
// project references, which is NOT a hole; a project reached only through
// an UNLISTED project's reference, which IS a hole; a stale list entry; a
// project claimed only behind a condition, with that condition quoted; the
// primary list's own job condition not condemning every project that shares
// it; a project whose tests run in more than one context; a reference that
// uses none of the referenced project's types, and one that does use them; a
// reference that costs nothing not being reported; a build-time reference
// duplicating nothing; a helper library with no tests not being a hole, and
// a test project with no tests that NOTHING consumes being one; a treenode
// partition that no list applies, and one that a list does apply; a
// baseline row that matches nothing; a baseline row with no reason; a
// finding the baseline does not carry failing; a scan floor firing on a
// truncated list; a list that names nothing; a workflow with no primary
// list; and a test project missing from the solution.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const csproj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`

const csprojReferences = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\%s\%s.csproj"/>
  </ItemGroup>
</Project>
`

const csprojAnalyzer = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\%s\%s.csproj"
                      OutputItemType="Analyzer" />
  </ItemGroup>
</Project>
`

const csprojNoOutput = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\%s\%s.csproj"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
`

const csprojNotTest = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>false</IsTestProject>
  </PropertyGroup>
</Project>
`

const testSource = `namespace Fixture;

public class Tests
{
    [Test]
    public void Works() { }
}
`

const emptySource = `namespace Fixture;

public class Helpers
{
    public int Value => 1;
}
`

const harnessSource = `namespace Fixture;

public class Helper
{
    public int Value => 1;
}

public class HarnessTests
{
    [Test]
    public void Works() { }
}
`

const usesHelperSource = `namespace Fixture;

public class Consumer
{
    public Helper Helper { get; } = new Helper();
}
`

const usesNothingSource = `namespace Fixture;

public class Consumer
{
}
`

const partitionSource = `namespace Fixture;

public static class E2eShards
{
    public static string FilterFor(string[] names)
    {
        return "/*/*/(" + string.Join(")|(", names) + ")/*";
    }
}
`

const testsPropsFixture = `<Project>
  <PropertyGroup>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>
`

const nukeFixture = `namespace Harbor.Build.Targets;

public static class TestTarget
{
    private static readonly string[] ExcludedSubstrings =
    [
        "E2E",
        "LoadTests",
        "PerfTests",
        "Benchmarks",
        "TestKit",
        "PtyTests",
    ];

    public static void Execute()
    {
        var projects = solution.AllProjects
            .Where(p => p.Name.EndsWith("Tests", StringComparison.Ordinal)
                && !ExcludedSubstrings.Any(s => p.Name.Contains(s, StringComparison.Ordinal)));
    }
}
`

const baselineClean = "# no known holes\n"

const baselineWithHole = "PROJECT-UNCLAIMED | Orphan.Tests | the project is a placeholder that " +
	"carries no tests yet and must not be scheduled | @refusedguy\n"

// ciWorkflowFixture is a ci.yml fixture with the shape the real one has.
func ciWorkflowFixture(shards map[string][]string, coverage []string, perfGate []string,
	testIf string, coverageIf string, perfIf string, coverageStepIf string, testFilter string) string {
	lines := []string{"name: ci", "on:", "  push:", "jobs:"}
	lines = append(lines, "  test:")
	if testIf != "" {
		lines = append(lines, fmt.Sprintf("    if: %s", testIf))
	}
	lines = append(lines, "    runs-on: ubuntu-latest")
	lines = append(lines, "    strategy:")
	lines = append(lines, "      fail-fast: false")
	lines = append(lines, "      matrix:")
	lines = append(lines, "        shard: [core]")
	lines = append(lines, "        include:")
	for name, tokens := range shards {
		lines = append(lines, fmt.Sprintf("          - shard: %s", name))
		lines = append(lines, "            guard: true")
		lines = append(lines, "            projects: >-")
		for _, token := range tokens {
			lines = append(lines, fmt.Sprintf("              %s", token))
		}
	}
	lines = append(lines, "    steps:")
	lines = append(lines, "      - uses: actions/checkout@v7")
	lines = append(lines, "      - name: Run shard")
	if testFilter != "" {
		lines = append(lines, fmt.Sprintf("        if: %s", testFilter))
	}
	lines = append(lines, "        run: |")
	lines = append(lines, "          for p in ${ matrix.projects }; do")
	if testFilter != "" {
		lines = append(lines, "            dotnet exec \"tests/$p/bin/Release/net10.0/$p.dll\" --treenode-filter")
	} else {
		lines = append(lines, "            dotnet exec \"tests/$p/bin/Release/net10.0/$p.dll\"")
	}
	lines = append(lines, "          done")

	lines = append(lines, "  coverage:")
	if coverageIf != "" {
		lines = append(lines, fmt.Sprintf("    if: %s", coverageIf))
	}
	lines = append(lines, "    runs-on: ubuntu-latest")
	lines = append(lines, "    steps:")
	lines = append(lines, "      - uses: actions/checkout@v7")
	lines = append(lines, "      - name: Collect coverage")
	if coverageStepIf != "" {
		lines = append(lines, fmt.Sprintf("        if: %s", coverageStepIf))
	}
	lines = append(lines, "        run: |")
	lines = append(lines, "          for p in \\")
	tokens := coverage
	if coverage == nil {
		tokens = []string{"Clean.Tests"}
	}
	for _, token := range tokens {
		lines = append(lines, fmt.Sprintf("            %s \\", token))
	}
	lines = append(lines, "            ; do")
	lines = append(lines, "            dotnet exec tests/$p/bin/Release/net10.0/$p.dll")
	lines = append(lines, "          done")

	lines = append(lines, "  perf-gate:")
	if perfIf != "" {
		lines = append(lines, fmt.Sprintf("    if: %s", perfIf))
	}
	lines = append(lines, "    runs-on: ubuntu-latest")
	lines = append(lines, "    steps:")
	lines = append(lines, "      - uses: actions/checkout@v7")
	lines = append(lines, "      - name: Run renderer performance gate")
	lines = append(lines, "        run: |")
	perf := perfGate
	if perfGate == nil {
		perf = []string{"Clean.Tests"}
	}
	for _, token := range perf {
		lines = append(lines, fmt.Sprintf("          taskset -c 1 dotnet exec tests/%s/bin/Release/net10.0/%s.dll --output Detailed", token, token))
	}
	return strings.Join(lines, "\n") + "\n"
}

// osWorkflowFixture is a test-os.yml fixture with the shape the real one has.
func osWorkflowFixture(projects []string) string {
	lines := []string{
		"name: test-os",
		"on:",
		"  push:",
		"jobs:",
		"  test-os:",
		"    if: >-",
		"      needs.gate.outputs.proceed == 'true' &&",
		"      (needs.changes.outputs.dotnet == 'true' ||",
		"       github.event_name == 'schedule')",
		"    runs-on: ${{ matrix.os }}",
		"    strategy:",
		"      fail-fast: false",
		"      matrix:",
		"        os: [windows-latest, macos-latest]",
		"        include:",
		"          - projects: >-",
	}
	for _, token := range projects {
		lines = append(lines, fmt.Sprintf("              %s", token))
	}
	lines = append(lines, "    steps:")
	lines = append(lines, "      - uses: actions/checkout@v7")
	lines = append(lines, "      - name: Test critical suites")
	lines = append(lines, "        shell: pwsh")
	lines = append(lines, "        run: |")
	lines = append(lines, "          $projects = @(\"${{ matrix.projects }}\" -split '\\s+')")
	lines = append(lines, "          foreach ($p in $projects) { }")
	return strings.Join(lines, "\n") + "\n"
}

// solutionFixture is a Harbor.slnx fixture.
func solutionFixture(paths []string) string {
	lines := []string{"<Solution>", "    <Folder Name=\"/tests/\">"}
	for _, path := range paths {
		lines = append(lines, fmt.Sprintf("        <Project Path=\"%s\"/>", path))
	}
	lines = append(lines, "    </Folder>")
	lines = append(lines, "</Solution>")
	return strings.Join(lines, "\n") + "\n"
}

// fixture builds a throwaway tree. `files` maps repo-relative path to
// contents.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "test-project-gate-")
	if err != nil {
		return ""
	}
	for relative, body := range files {
		path := filepath.Join(root, filepath.FromSlash(relative))
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			continue
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			continue
		}
	}
	return root
}

// baseFixture is the clean baseline every fixture starts from.
func baseFixture(files map[string]string) string {
	merged := map[string]string{
		solutionFile:                           solutionFixture([]string{"tests/Clean.Tests/Clean.Tests.csproj"}),
		testsProps:                             testsPropsFixture,
		ciWorkflow:                             ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests"}}, nil, nil, "", "", "", "", ""),
		osWorkflow:                             osWorkflowFixture([]string{"Clean.Tests"}),
		nukeTargetPath:                         nukeFixture,
		baselineFile:                           baselineClean,
		"tests/Clean.Tests/Clean.Tests.csproj": csproj,
		"tests/Clean.Tests/Clean.Tests.cs":     testSource,
	}
	for name, body := range files {
		merged[name] = body
	}
	return fixture(merged)
}

// shown renders the findings, readably.
func shown(found []finding) string {
	parts := make([]string, 0, len(found))
	for _, item := range found {
		parts = append(parts, item.String())
	}
	return strings.Join(parts, " | ")
}

// hasFinding reports whether a finding with the rule and the subject exists.
func hasFinding(found []finding, rule string, subject string) bool {
	for _, item := range found {
		if item.rule == rule && item.subject == subject {
			return true
		}
	}
	return false
}

// hasRule reports whether a finding with the rule exists.
func hasRule(found []finding, rule string) bool {
	for _, item := range found {
		if item.rule == rule {
			return true
		}
	}
	return false
}

func selfTestRun() int {
	var failures []string
	ran := 0

	check := func(name string, condition bool, detail string) {
		ran++
		if condition {
			fmt.Printf("  ok    %s\n", name)
			return
		}
		fmt.Printf("  FAIL  %s %s\n", name, detail)
		failures = append(failures, name)
	}

	// 1. A clean tree stays clean. The false-positive control.
	root := baseFixture(nil)
	_, found := analyse(root)
	check("a clean tree stays green", len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 2. A project in the tree and in the solution, but in no list and
	//    referenced by nothing. The hole this gate exists for.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Orphan.Tests/Orphan.Tests.csproj",
		}),
		"tests/Orphan.Tests/Orphan.Tests.csproj": csproj,
		"tests/Orphan.Tests/Orphan.Tests.cs":     testSource,
	})
	_, found = analyse(root)
	check("a project missing from every list is reported",
		hasFinding(found, "PROJECT-UNCLAIMED", "Orphan.Tests"), shown(found))
	check("the unclaimed project is reported as a blocking finding",
		hasBlocking(found, "PROJECT-UNCLAIMED", "Orphan.Tests"), shown(found))
	os.RemoveAll(root)

	// 3. A project that no list names, but that a listed project
	//    REFERENCES.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
		}),
		ciWorkflow:                     ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Harness"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj": csproj,
		"tests/Harness/Harness.cs":     testSource,
	})
	_, found = analyse(root)
	check("a listed project is not reported as unclaimed",
		!hasRule(found, "PROJECT-UNCLAIMED"), shown(found))
	os.RemoveAll(root)

	// 4. A project that no list names and that only an UNLISTED project
	//    references. Nothing runs it.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
			"tests/Runner/Runner.csproj",
		}),
		ciWorkflow:                     ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj": csproj,
		"tests/Harness/Harness.cs":     testSource,
		"tests/Runner/Runner.csproj":   fmt.Sprintf(csprojReferences, "Harness", "Harness"),
		"tests/Runner/Runner.cs":       testSource,
	})
	_, found = analyse(root)
	check("a project reached only through an unlisted reference is reported",
		hasFinding(found, "PROJECT-UNCLAIMED", "Harness"), shown(found))
	os.RemoveAll(root)

	// 5. A stale list entry: a list names a project the tree does not have.
	root = baseFixture(map[string]string{
		ciWorkflow: ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Ghost.Tests"}}, nil, nil, "", "", "", "", ""),
	})
	_, found = analyse(root)
	check("a stale list entry is reported",
		hasSubjectSuffix(found, "LIST-STALE", "Ghost.Tests"), shown(found))
	os.RemoveAll(root)

	// 6. A project claimed only by a list whose job carries a condition.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Skipped.Tests/Skipped.Tests.csproj",
		}),
		ciWorkflow: ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests"}},
			[]string{"Skipped.Tests"}, nil, "", "needs.changes.outputs.dotnet == 'true'", "", "", ""),
		"tests/Skipped.Tests/Skipped.Tests.csproj": csproj,
		"tests/Skipped.Tests/Skipped.Tests.cs":     testSource,
	})
	_, found = analyse(root)
	check("a project claimed only by a conditional list is reported",
		hasFinding(found, "CLAIM-CONDITIONAL", "Skipped.Tests"), shown(found))
	check("the conditional finding quotes the condition",
		containsMessage(found, "needs.changes.outputs.dotnet == 'true'"), shown(found))
	os.RemoveAll(root)

	// 7. The PRIMARY list's own job condition does not condemn every
	//    project that shares it.
	root = baseFixture(map[string]string{
		ciWorkflow: ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests"}}, nil, nil,
			"needs.changes.outputs.dotnet == 'true'", "", "", "", ""),
	})
	_, found = analyse(root)
	check("the primary list's job condition does not condemn its projects",
		!hasRule(found, "CLAIM-CONDITIONAL"), shown(found))
	os.RemoveAll(root)

	// 8. A test project with no [Test] method.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Fixtures/Fixtures.csproj",
		}),
		"tests/Fixtures/Fixtures.csproj": csproj,
		"tests/Fixtures/Fixtures.cs":     emptySource,
	})
	_, found = analyse(root)
	check("a test project with no tests is reported",
		hasFinding(found, "PROJECT-EMPTY", "Fixtures"), shown(found))
	os.RemoveAll(root)

	// 9. A baseline row that matches nothing.
	root = baseFixture(map[string]string{baselineFile: baselineWithHole})
	_, found = analyse(root)
	check("a baseline row that matches nothing is stale",
		hasRule(found, "BASELINE-STALE"), shown(found))
	os.RemoveAll(root)

	// 10. A baseline row with no reason.
	root = baseFixture(map[string]string{
		baselineFile: "PROJECT-UNCLAIMED | Orphan.Tests | too short | \n",
	})
	_, found = analyse(root)
	check("a baseline row without a reason is unjustified",
		hasRule(found, "BASELINE-UNJUSTIFIED"), shown(found))
	os.RemoveAll(root)

	// 11. A row that DOES match its finding silences it.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Orphan.Tests/Orphan.Tests.csproj",
		}),
		"tests/Orphan.Tests/Orphan.Tests.csproj": csproj,
		"tests/Orphan.Tests/Orphan.Tests.cs":     testSource,
		baselineFile:                             baselineWithHole,
	})
	_, found = analyse(root)
	check("a baseline row silences the finding it covers",
		hasNotBlocking(found, "PROJECT-UNCLAIMED", "Orphan.Tests"), shown(found))
	os.RemoveAll(root)

	// 12. A test project on disk that the solution does not carry.
	root = baseFixture(map[string]string{
		"tests/Stray.Tests/Stray.Tests.csproj": csproj,
		"tests/Stray.Tests/Stray.Tests.cs":     testSource,
	})
	_, found = analyse(root)
	check("a test project missing from the solution is reported",
		hasFinding(found, "PROJECT-NOT-IN-SOLUTION", "Stray.Tests"), shown(found))
	os.RemoveAll(root)

	// 13. A project that declares IsTestProject=false is not a test
	//     project.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
		}),
		"tests/Harness/Harness.csproj": csprojNotTest,
		"tests/Harness/Harness.cs":     emptySource,
	})
	_, found = analyse(root)
	check("a project with IsTestProject=false is not a test project",
		!hasSubject(found, "Harness"), shown(found))
	os.RemoveAll(root)

	// 14. A [Test] in a comment or a string is not a test.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Docs.Tests/Docs.Tests.csproj",
		}),
		ciWorkflow:                           ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Docs.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Docs.Tests/Docs.Tests.csproj": csproj,
		"tests/Docs.Tests/Docs.Tests.cs": "namespace F;\n" +
			"// [Test]\n" +
			"/* [Test] */\n" +
			"public class C { public string S = \"[Test]\"; }\n",
	})
	_, found = analyse(root)
	check("a [Test] in a comment or a string is not counted",
		hasFinding(found, "PROJECT-EMPTY", "Docs.Tests"), shown(found))
	os.RemoveAll(root)

	// 15. A test project's classes discovered in more than one context.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
			"tests/Other.Tests/Other.Tests.csproj",
		}),
		ciWorkflow:                             ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Other.Tests", "Harness"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj":         csproj,
		"tests/Harness/Harness.cs":             harnessSource,
		"tests/Other.Tests/Other.Tests.csproj": fmt.Sprintf(csprojReferences, "Harness", "Harness"),
		"tests/Other.Tests/Other.Tests.cs":     usesHelperSource,
	})
	_, found = analyse(root)
	check("a project whose classes run in two contexts is reported",
		hasFinding(found, "TEST-DUPLICATED", "Harness"), shown(found))
	os.RemoveAll(root)

	// 16. A reference that names none of the referenced project's types.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
			"tests/Other.Tests/Other.Tests.csproj",
		}),
		ciWorkflow:                             ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Other.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj":         csproj,
		"tests/Harness/Harness.cs":             harnessSource,
		"tests/Other.Tests/Other.Tests.csproj": fmt.Sprintf(csprojReferences, "Harness", "Harness"),
		"tests/Other.Tests/Other.Tests.cs":     usesNothingSource,
	})
	_, found = analyse(root)
	check("a reference that uses none of the referenced types is reported",
		hasFinding(found, "TEST-REFERENCE-UNUSED", "Other.Tests -> Harness"), shown(found))
	os.RemoveAll(root)

	// 17. A reference that DOES use the referenced project's types is
	//     load-bearing and is not a finding.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
			"tests/Other.Tests/Other.Tests.csproj",
		}),
		ciWorkflow:                             ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Other.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj":         csproj,
		"tests/Harness/Harness.cs":             harnessSource,
		"tests/Other.Tests/Other.Tests.csproj": fmt.Sprintf(csprojReferences, "Harness", "Harness"),
		"tests/Other.Tests/Other.Tests.cs":     usesHelperSource,
	})
	_, found = analyse(root)
	check("a reference that uses the referenced types is not reported",
		!hasRule(found, "TEST-REFERENCE-UNUSED"), shown(found))
	os.RemoveAll(root)

	// 18. A helper library with no tests of its own, consumed by a
	//     project that carries tests. It is a library, not a hole.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Helper/Helper.csproj",
		}),
		"tests/Clean.Tests/Clean.Tests.csproj": fmt.Sprintf(csprojReferences, "Helper", "Helper"),
		"tests/Helper/Helper.csproj":           csproj,
		"tests/Helper/Helper.cs":               emptySource,
	})
	_, found = analyse(root)
	check("a helper library with no tests is not a hole",
		!hasFinding(found, "PROJECT-EMPTY", "Helper"), shown(found))
	os.RemoveAll(root)

	// 19. A test project with no tests that NOTHING consumes.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Lonely/Lonely.csproj",
		}),
		"tests/Lonely/Lonely.csproj": csproj,
		"tests/Lonely/Lonely.cs":     emptySource,
	})
	_, found = analyse(root)
	check("a test project with no tests that nothing consumes is a hole",
		hasFinding(found, "PROJECT-EMPTY", "Lonely"), shown(found))
	os.RemoveAll(root)

	// 20. A reference that costs nothing is not a finding.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Helper/Helper.csproj",
		}),
		"tests/Clean.Tests/Clean.Tests.csproj": fmt.Sprintf(csprojReferences, "Helper", "Helper"),
		"tests/Helper/Helper.csproj":           csproj,
		"tests/Helper/Helper.cs":               emptySource,
	})
	_, found = analyse(root)
	check("a reference that costs nothing is not reported",
		!hasRule(found, "TEST-REFERENCE-UNUSED"), shown(found))
	os.RemoveAll(root)

	// 21. A treenode partition on disk that no CI list applies.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Sharded.Tests/Sharded.Tests.csproj",
		}),
		ciWorkflow: ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Sharded.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Sharded.Tests/Sharded.Tests.csproj": csproj,
		"tests/Sharded.Tests/Sharded.Tests.cs":     partitionSource + "\n" + testSource,
	})
	_, found = analyse(root)
	check("a treenode partition no list applies is reported",
		hasFinding(found, "SHARD-UNWIRED", "Sharded.Tests"), shown(found))
	os.RemoveAll(root)

	// 22. The same partition, applied by the list that runs it.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Sharded.Tests/Sharded.Tests.csproj",
		}),
		ciWorkflow: ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Sharded.Tests"}}, nil, nil, "", "", "", "", "matrix.shard == 'core'"),
		"tests/Sharded.Tests/Sharded.Tests.csproj": csproj,
		"tests/Sharded.Tests/Sharded.Tests.cs":     partitionSource + "\n" + testSource,
	})
	_, found = analyse(root)
	check("a wired treenode partition is not reported",
		!hasRule(found, "SHARD-UNWIRED"), shown(found))
	os.RemoveAll(root)

	// 23. A BUILD-TIME reference drags no test classes into a test process.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
			"tests/Other.Tests/Other.Tests.csproj",
		}),
		ciWorkflow:                             ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Other.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj":         csproj,
		"tests/Harness/Harness.cs":             harnessSource,
		"tests/Other.Tests/Other.Tests.csproj": fmt.Sprintf(csprojAnalyzer, "Harness", "Harness"),
		"tests/Other.Tests/Other.Tests.cs":     usesNothingSource,
	})
	_, found = analyse(root)
	check("an analyzer reference does not duplicate the referenced tests",
		!hasRule(found, "TEST-DUPLICATED"), shown(found))
	check("an analyzer reference is not an unused runtime reference",
		!hasRule(found, "TEST-REFERENCE-UNUSED"), shown(found))
	os.RemoveAll(root)

	// 24. The other build-time shape: a reference whose output assembly is
	//     not referenced at all.
	root = baseFixture(map[string]string{
		solutionFile: solutionFixture([]string{
			"tests/Clean.Tests/Clean.Tests.csproj",
			"tests/Harness/Harness.csproj",
			"tests/Other.Tests/Other.Tests.csproj",
		}),
		ciWorkflow:                             ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests", "Other.Tests"}}, nil, nil, "", "", "", "", ""),
		"tests/Harness/Harness.csproj":         csproj,
		"tests/Harness/Harness.cs":             harnessSource,
		"tests/Other.Tests/Other.Tests.csproj": fmt.Sprintf(csprojNoOutput, "Harness", "Harness"),
		"tests/Other.Tests/Other.Tests.cs":     usesNothingSource,
	})
	_, found = analyse(root)
	check("a reference with no output assembly does not duplicate the tests",
		!hasRule(found, "TEST-DUPLICATED"), shown(found))
	check("a reference with no output assembly is not an unused runtime one",
		!hasRule(found, "TEST-REFERENCE-UNUSED"), shown(found))
	os.RemoveAll(root)

	// 25. A list that names nothing is blindness, not cleanliness.
	root = baseFixture(map[string]string{
		ciWorkflow: ciWorkflowFixture(map[string][]string{"core": {"Clean.Tests"}}, []string{}, nil, "", "", "", "", ""),
	})
	_, found = analyse(root)
	check("a list that names nothing is reported",
		hasFinding(found, "SCAN-BLIND", "ci:coverage"), shown(found))
	os.RemoveAll(root)

	// 26. The scan floors.
	root = baseFixture(nil)
	result, _ := analyse(root)
	os.RemoveAll(root)
	breached := checkFloors(result, []floorFixture{
		{"projects", 40, result.totals["csproj"]},
		{"CI lists", 7, result.totals["lists"]},
		{"claimed projects", 36, result.totals["claims"]},
	})
	check("a scan floor fires on a truncated list", len(breached) == 3, fmt.Sprint(breached))
	check("a scan floor passes on a full list",
		len(checkFloors(result, []floorFixture{
			{"projects", 1, result.totals["csproj"]},
			{"CI lists", 4, result.totals["lists"]},
			{"claimed projects", 1, result.totals["claims"]},
		})) == 0, fmt.Sprint(result.totals))

	// 27. A workflow without the primary list.
	root = baseFixture(map[string]string{
		ciWorkflow: "name: ci\njobs:\n  coverage:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n",
	})
	_, found = analyse(root)
	check("a workflow without the primary list is reported",
		hasFinding(found, "SCAN-BLIND", "ci.yml::test"), shown(found))
	os.RemoveAll(root)

	// 28. The gate must not be able to count itself.
	here, err := os.Getwd()
	if err != nil {
		here = ""
	}
	inside := strings.HasPrefix(here, filepath.Join("..", "..", testsDir)+string(os.PathSeparator))
	check("the gate's own source is outside the scope", !inside, here)

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}

// floorFixture is one scan floor of the self-test.
type floorFixture struct {
	name   string
	floor  int
	actual int
}

// checkFloors returns the floors this tree breached.
func checkFloors(result census, floors []floorFixture) []floorFixture {
	var breached []floorFixture
	for _, one := range floors {
		if one.floor != 0 && one.actual < one.floor {
			breached = append(breached, one)
		}
	}
	return breached
}

// hasBlocking reports whether a blocking finding with the rule and the
// subject exists.
func hasBlocking(found []finding, rule string, subject string) bool {
	for _, item := range found {
		if item.rule == rule && item.subject == subject && item.blocking {
			return true
		}
	}
	return false
}

// hasNotBlocking reports whether a finding with the rule and the subject
// exists and is NOT blocking.
func hasNotBlocking(found []finding, rule string, subject string) bool {
	for _, item := range found {
		if item.rule == rule && item.subject == subject && !item.blocking {
			return true
		}
	}
	return false
}

// hasSubject reports whether a finding with the subject exists.
func hasSubject(found []finding, subject string) bool {
	for _, item := range found {
		if item.subject == subject {
			return true
		}
	}
	return false
}

// hasSubjectSuffix reports whether a finding with the rule and a subject
// that ENDS with the given text exists.
func hasSubjectSuffix(found []finding, rule string, suffix string) bool {
	for _, item := range found {
		if item.rule == rule && strings.HasSuffix(item.subject, suffix) {
			return true
		}
	}
	return false
}

// containsMessage reports whether a finding's message holds the text.
func containsMessage(found []finding, text string) bool {
	for _, item := range found {
		if strings.Contains(item.message, text) {
			return true
		}
	}
	return false
}
