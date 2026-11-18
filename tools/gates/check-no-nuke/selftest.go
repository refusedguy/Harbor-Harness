package checknonuke

// selftest.go -- the gate's proof that it can fail.
//
// The load-bearing fixture is Nuke.Common planted in a project file: the
// gate MUST go red and name the file:line. The other fixtures cover the
// second rule, the case-insensitive forms, and both sides of the exclusions
// (a pattern in a historical document must stay quiet, and a clean tree
// with words that merely CONTAIN "nuke" must stay green).

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// fixture is a throwaway tree: repo-relative path -> contents.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "no-nuke-gate-")
	if err != nil {
		return ""
	}
	for relative, body := range files {
		path := filepath.Join(root, filepath.FromSlash(relative))
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			os.RemoveAll(root)
			return ""
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			os.RemoveAll(root)
			return ""
		}
	}
	return root
}

// shown is the list of findings, for the detail of a failed fixture.
func shown(found []finding) string {
	parts := make([]string, 0, len(found))
	for _, item := range found {
		parts = append(parts, item.String())
	}
	return strings.Join(parts, " | ")
}

// hasRule is whether a finding carries a rule.
func hasRule(found []finding, rule string) bool {
	for _, item := range found {
		if item.rule == rule {
			return true
		}
	}
	return false
}

// cites is whether a finding names a file:line and a rule.
func cites(found []finding, rule, path string, line int) bool {
	want := fmt.Sprintf("%s:%d", path, line)
	for _, item := range found {
		if item.rule == rule && item.path == path && item.line == line &&
			strings.Contains(item.String(), want) {
			return true
		}
	}
	return false
}

// selfTestRun plants each shape and asserts the verdict on it.
func selfTestRun() int {
	var failures []string
	ran := 0
	check := func(name string, ok bool, detail string) {
		ran++
		if ok {
			fmt.Printf("  ok    %s\n", name)
			return
		}
		fmt.Printf("  FAIL  %s %s\n", name, detail)
		failures = append(failures, name)
	}

	csproj := `<Project>
  <ItemGroup>
    <PackageReference Include="Nuke.Common" Version="10.1.0" />
  </ItemGroup>
</Project>
`
	// 1. THE LOAD-BEARING FIXTURE. A NuGet reference in a project file is
	//    the exact shape #2183 removed, and the gate must go red on it and
	//    name the file and the line.
	root := fixture(map[string]string{"build/_build.csproj": csproj})
	code, found := runScan(root)
	check("a Nuke.Common PackageReference goes red with a file:line",
		code == gate.Red && cites(found, ruleNuGet, "build/_build.csproj", 3),
		fmt.Sprintf("code=%d %s", code, shown(found)))
	os.RemoveAll(root)

	// 2. A central pin is the first half of a reintroduction.
	root = fixture(map[string]string{
		"Directory.Packages.props": `<Project>
  <ItemGroup>
    <PackageVersion Include="Nuke.Common.IO" Version="10.1.0" />
  </ItemGroup>
</Project>
`,
	})
	code, found = runScan(root)
	check("a Nuke.* PackageVersion pin goes red",
		code == gate.Red && hasRule(found, ruleNuGet), shown(found))
	os.RemoveAll(root)

	// 3. using Nuke.* in a source file.
	root = fixture(map[string]string{
		"src/Harbor.Foo/Bar.cs": "namespace Harbor.Foo;\nusing Nuke.Common;\n",
	})
	code, found = runScan(root)
	check("a using Nuke.Common directive goes red with a file:line",
		code == gate.Red && cites(found, ruleNuGet, "src/Harbor.Foo/Bar.cs", 2),
		fmt.Sprintf("code=%d %s", code, shown(found)))
	os.RemoveAll(root)

	// 4. A subnamespace, and a comment above it: the line must be the
	//    line of the FILE, not of a stripped copy.
	root = fixture(map[string]string{
		"tests/Harbor.Foo.Tests/BarTests.cs": "// was NUKE, now fallout\nusing Nuke.Common.IO;\n",
	})
	code, found = runScan(root)
	check("a using Nuke.* subnamespace keeps the file's line number",
		code == gate.Red && cites(found, ruleNuGet, "tests/Harbor.Foo.Tests/BarTests.cs", 2),
		fmt.Sprintf("code=%d %s", code, shown(found)))
	os.RemoveAll(root)

	// 5. The old entry point in a workflow.
	root = fixture(map[string]string{
		".github/workflows/ci.yml": "jobs:\n  build:\n    steps:\n      - run: dotnet nuke Compile\n",
	})
	code, found = runScan(root)
	check("dotnet nuke in a workflow goes red",
		code == gate.Red && hasRule(found, ruleLegacy), shown(found))
	os.RemoveAll(root)

	// 6. The telemetry opt-out in a bootstrap script.
	root = fixture(map[string]string{
		"build/build.sh": "#!/usr/bin/env bash\nexport NUKE_TELEMETRY_OPTOUT=1\n",
	})
	code, found = runScan(root)
	check("NUKE_TELEMETRY_OPTOUT in a bootstrap script goes red",
		code == gate.Red && hasRule(found, ruleLegacy), shown(found))
	os.RemoveAll(root)

	// 7. The temp directory in a workflow.
	root = fixture(map[string]string{
		".github/workflows/build.yml": "run: rm -rf .nuke/temp\n",
	})
	code, found = runScan(root)
	check(".nuke/temp in a workflow goes red",
		code == gate.Red && hasRule(found, ruleLegacy), shown(found))
	os.RemoveAll(root)

	// 8. The telemetry version property in a build config.
	root = fixture(map[string]string{
		"build/Directory.Build.props": "<Project>\n  <NukeTelemetryVersion>1</NukeTelemetryVersion>\n</Project>\n",
	})
	code, found = runScan(root)
	check("NukeTelemetryVersion in a build config goes red",
		code == gate.Red && hasRule(found, ruleLegacy), shown(found))
	os.RemoveAll(root)

	// 9. A different casing is still a reintroduction.
	root = fixture(map[string]string{
		".github/workflows/build.yml": "run: dotnet NUKE Compile\n",
		"apps/Harbor.App.Cli/X.cs":    "USING nuke.common;\n",
	})
	code, found = runScan(root)
	check("a different casing of the forbidden forms goes red",
		code == gate.Red && hasRule(found, ruleLegacy) && hasRule(found, ruleNuGet), shown(found))
	os.RemoveAll(root)

	// 10. THE HISTORICAL-DOCUMENT EXCLUSIONS. These files quote the old
	//     build on purpose. The rule is baked into the gate, not a config.
	root = fixture(map[string]string{
		"docs/BUILDSYSTEM_STUDY.md":                    "using Nuke.Common; dotnet nuke .nuke/temp\n",
		"docs/.kilo-docs/notes.md":                     "using Nuke.Common;\n",
		"docs/audit-archive/old.md":                    "NUKE_TELEMETRY_OPTOUT\n",
		"docs/adr/ADR-023-build-orchestrator-moves.md": "NukeTelemetryVersion\n",
	})
	code, found = runScan(root)
	check("the four historical documents stay quiet",
		code == gate.Green && len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 11. The gate's own rule text and the compiled binaries are excluded
	//     as well, or a detector that names what it forbids would flag
	//     itself.
	root = fixture(map[string]string{
		"tools/gates/check-no-nuke/main.go": "dotnet nuke; using Nuke.Common;\n",
		"tools/bin/harbor-gates-linux-x64":  "NUKE_TELEMETRY_OPTOUT\n",
	})
	code, found = runScan(root)
	check("the gate's own source and tools/bin are excluded",
		code == gate.Green && len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 12. THE FALSE-POSITIVE CONTROL. A word that merely CONTAINS "nuke"
	//     and a bare Nuke.Common mention in a clean doc are not the
	//     forbidden forms.
	root = fixture(map[string]string{
		"tests/Harbor.Architecture.Tests/ConfigSetSingleTableTests.cs": "private static IReadOnlyList<string> SubmenuKeys(string text)\n",
		"tests/Harbor.Tools.Builtin.Tests/XTests.cs":                   `["operation"] = "nuke"` + "\n",
		"changelog/fragments/2186.md":                                  "the old Nuke.Common package had more downloads\n",
	})
	code, found = runScan(root)
	check("a word containing nuke and a bare mention stay green",
		code == gate.Green && len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 13. A clean tree stays clean.
	root = fixture(map[string]string{
		"src/Harbor.Foo/Bar.cs": "using Fallout.Common;\n",
		"build/build.sh":        "#!/usr/bin/env bash\nexec dotnet run --project build/_build.fallout\n",
	})
	code, found = runScan(root)
	check("a clean tree stays green", code == gate.Green && len(found) == 0, shown(found))
	os.RemoveAll(root)

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}
