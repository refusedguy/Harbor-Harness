package testpackages

// selftest.go -- the failure path, proved on every run of the gate in CI.
//
// The fixtures cover: a banned package declared directly by a `.csproj`; a
// banned package that arrives TRANSITIVELY through the resolved graph; a
// banned assembly sitting in a project's `bin/` tree; a named exception
// silencing exactly that finding and not matching anything else; an
// exception that matches nothing (stale); an exception with no reason
// (unjustified); a clean tree staying clean; a project with no resolved
// graph reporting BLIND rather than passing; the same report being waivable
// explicitly; a package whose id merely CONTAINS a banned name not being
// flagged; the ban set being read from the `N:` entries only; an empty ban
// set being reported rather than passed; and, for `--output-only`, a banned
// assembly in the output directory being found, the resolved graph being
// ignored, and a tree with no output at all being reported rather than
// passed.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

const csprojTemplate = `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="%s"/>
  </ItemGroup>
</Project>
`

const bannedSymbolsFixture = `# Banned APIs — fixture
N:Newtonsoft.Json
T:Newtonsoft.Json.JsonConvert
T:System.Xml.Serialization.XmlSerializer
M:System.Threading.Thread.Sleep(System.Int32)
`

// assetsJSON is a minimal `project.assets.json` for a fixture.
//
// Only the two sections the gate reads are filled in: `targets` (what is
// resolved) and `project.frameworks` (what the project declared).
func assetsJSON(packages [][2]string, direct [][2]string) string {
	targets := map[string]map[string]any{}
	for _, package_ := range packages {
		name, version := package_[0], package_[1]
		entry := map[string]any{
			"type":    "package",
			"runtime": map[string]any{"lib/net6.0/" + name + ".dll": map[string]any{}},
			"compile": map[string]any{"lib/net6.0/" + name + ".dll": map[string]any{}},
		}
		targets[name+"/"+version] = entry
	}
	dependencies := map[string]any{}
	for _, package_ := range direct {
		name, version := package_[0], package_[1]
		dependencies[name] = map[string]any{"target": "Package", "version": "[" + version + ", )"}
	}
	payload := map[string]any{
		"version": 4,
		"targets": map[string]any{"net10.0": targets},
		"libraries": func() map[string]any {
			libraries := map[string]any{}
			for _, package_ := range packages {
				libraries[package_[0]+"/"+package_[1]] = map[string]any{"type": "package"}
			}
			return libraries
		}(),
		"project": map[string]any{
			"frameworks": map[string]any{
				"net10.0": map[string]any{"dependencies": dependencies},
			},
		},
	}
	encoded, err := json.Marshal(payload)
	if err != nil {
		return ""
	}
	return string(encoded)
}

// fixture builds a throwaway tree. `files` maps repo-relative path to
// contents.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "test-package-gate-")
	if err != nil {
		return ""
	}
	for relative, body := range files {
		path := filepath.Join(root, relative)
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
		bannedSymbolsFile:                      bannedSymbolsFixture,
		exceptionsFile:                         "# no exceptions\n",
		"tests/Clean.Tests/Clean.Tests.csproj": fmt.Sprintf(csprojTemplate, "TUnit.Assertions"),
		"tests/Clean.Tests/obj/project.assets.json": assetsJSON(
			[][2]string{{"TUnit.Assertions", "1.61.0"}},
			[][2]string{{"TUnit.Assertions", "1.61.0"}}),
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

// hasRule reports whether a finding with the rule and the subject exists.
func hasRule(found []finding, rule string, subject string) bool {
	for _, item := range found {
		if item.rule == rule && item.subject == subject {
			return true
		}
	}
	return false
}

// hasRulePrefix reports whether a finding whose rule starts with the prefix
// exists.
func hasRulePrefix(found []finding, prefix string) bool {
	for _, item := range found {
		if strings.HasPrefix(item.rule, prefix) {
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

	// 1. A banned package DECLARED by a .csproj must be found, with no
	//    resolved graph at all.
	root := baseFixture(map[string]string{
		"tests/Bad.Tests/Bad.Tests.csproj": fmt.Sprintf(csprojTemplate, "Newtonsoft.Json"),
	})
	_, found := analyse(root, true, false)
	check("a banned package declared by a .csproj is reported",
		hasRule(found, "BANNED-PACKAGE", "Newtonsoft.Json"), shown(found))
	os.RemoveAll(root)

	// 2. The TRANSITIVE half — the case the issue is actually about.
	root = baseFixture(map[string]string{
		"tests/Transitive.Tests/Transitive.Tests.csproj": fmt.Sprintf(csprojTemplate, "Fabrikam.Arch"),
		"tests/Transitive.Tests/obj/project.assets.json": assetsJSON(
			[][2]string{{"Fabrikam.Arch", "1.0.0"}, {"Newtonsoft.Json", "13.0.4"}},
			[][2]string{{"Fabrikam.Arch", "1.0.0"}}),
	})
	_, found = analyse(root, false, false)
	transitive := false
	for _, item := range found {
		if item.rule == "BANNED-PACKAGE" && item.subject == "Newtonsoft.Json" &&
			strings.Contains(item.message, "transitive") {
			transitive = true
		}
	}
	check("a transitively resolved banned package is reported", transitive, shown(found))
	os.RemoveAll(root)

	// 3. The other mechanism: a banned assembly already sitting in a
	//    project's output directory.
	root = baseFixture(map[string]string{
		"tests/Output.Tests/Output.Tests.csproj": fmt.Sprintf(csprojTemplate, "TUnit.Assertions"),
		"tests/Output.Tests/obj/project.assets.json": assetsJSON(
			[][2]string{{"TUnit.Assertions", "1.61.0"}},
			[][2]string{{"TUnit.Assertions", "1.61.0"}}),
		"tests/Output.Tests/bin/Release/net10.0/Newtonsoft.Json.dll": "MZ",
	})
	_, found = analyse(root, false, false)
	check("a banned assembly in the output directory is reported",
		hasRule(found, "BANNED-ASSEMBLY", "Newtonsoft.Json"), shown(found))
	os.RemoveAll(root)

	// 4. A NAMED exception silences exactly that finding — and nothing else.
	root = baseFixture(map[string]string{
		exceptionsFile: "Newtonsoft.Json | tests/Excused.Tests | the arch library requires it | @someone\n",
		"tests/Excused.Tests/Excused.Tests.csproj": fmt.Sprintf(csprojTemplate, "Newtonsoft.Json"),
		"tests/Excused.Tests/obj/project.assets.json": assetsJSON(
			[][2]string{{"Newtonsoft.Json", "13.0.4"}},
			[][2]string{{"Newtonsoft.Json", "13.0.4"}}),
	})
	_, found = analyse(root, true, false)
	check("a named exception silences the finding it covers",
		!hasRulePrefix(found, "BANNED-PACKAGE") && !hasRulePrefix(found, "EXCEPTION-STALE"),
		shown(found))
	os.RemoveAll(root)

	// 5. An exception that matches NOTHING is stale.
	root = baseFixture(map[string]string{
		exceptionsFile: "Newtonsoft.Json | tests/Nothing.Tests | no longer needed | @someone\n",
	})
	_, found = analyse(root, false, false)
	check("an exception that matches nothing is stale",
		hasRulePrefix(found, "EXCEPTION-STALE"), shown(found))
	os.RemoveAll(root)

	// 6. An exception with no reason (or no owner) is unjustified.
	root = baseFixture(map[string]string{
		exceptionsFile: "Newtonsoft.Json | tests/Excused.Tests |  | \n",
	})
	_, found = analyse(root, false, false)
	check("an exception without a reason is unjustified",
		hasRulePrefix(found, "EXCEPTION-UNJUSTIFIED"), shown(found))
	os.RemoveAll(root)

	// 7. A clean tree stays clean — the false-positive control.
	root = baseFixture(nil)
	_, found = analyse(root, false, false)
	check("a clean tree stays green", len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 7a. The restore inputs are all-or-nothing.
	root = baseFixture(map[string]string{
		"src/One/One.csproj":         "<Project Sdk=\"Microsoft.NET.Sdk\" />",
		"src/One/packages.lock.json": `{"version": 1}`,
		"src/Two/Two.csproj":         "<Project Sdk=\"Microsoft.NET.Sdk\" />",
	})
	_, found = analyse(root, false, false)
	check("a half-migrated tree is reported",
		hasRulePrefix(found, "RESTORE-INPUTS-PARTIAL"), shown(found))
	check("a lock file nobody regenerates is reported",
		hasRulePrefix(found, "RESTORE-INPUTS-UNENFORCED"), shown(found))
	os.RemoveAll(root)

	// 7b. The property set and the lock files committed together is green.
	root = baseFixture(map[string]string{
		"Directory.Build.props": "<Project><PropertyGroup>" +
			"<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>" +
			"</PropertyGroup></Project>",
		"src/One/One.csproj":         "<Project Sdk=\"Microsoft.NET.Sdk\" />",
		"src/One/packages.lock.json": `{"version": 1}`,
		"src/Two/Two.csproj":         "<Project Sdk=\"Microsoft.NET.Sdk\" />",
		"src/Two/packages.lock.json": `{"version": 1}`,
	})
	_, found = analyse(root, false, false)
	check("a fully migrated tree is green",
		!hasRulePrefix(found, "RESTORE-INPUTS") && !hasRulePrefix(found, "RESTORE-LOCKED-WITHOUT-FILES"),
		shown(found))
	os.RemoveAll(root)

	// 8. A project with NO resolved graph is BLIND, never green.
	root = baseFixture(map[string]string{
		"tests/Blind.Tests/Blind.Tests.csproj": fmt.Sprintf(csprojTemplate, "TUnit.Assertions"),
	})
	_, found = analyse(root, false, false)
	check("a project with no resolved graph is BLIND, not green",
		hasRulePrefix(found, "PACKAGE-BLIND"), shown(found))
	os.RemoveAll(root)

	// 9. The same project is NOT reported when the caller says the graph is
	//    optional.
	root = baseFixture(map[string]string{
		"tests/Blind.Tests/Blind.Tests.csproj": fmt.Sprintf(csprojTemplate, "TUnit.Assertions"),
	})
	_, found = analyse(root, true, false)
	check("the blind report can be waived explicitly",
		!hasRulePrefix(found, "PACKAGE-BLIND"), shown(found))
	os.RemoveAll(root)

	// 10. The match is EXACT and case-insensitive, as NuGet ids are.
	root = baseFixture(map[string]string{
		"tests/Near.Tests/Near.Tests.csproj": fmt.Sprintf(csprojTemplate, "Newtonsoft.Json.Schema"),
		"tests/Near.Tests/obj/project.assets.json": assetsJSON(
			[][2]string{{"Newtonsoft.Json.Schema", "3.0.0"}},
			[][2]string{{"Newtonsoft.Json.Schema", "3.0.0"}}),
	})
	_, found = analyse(root, false, false)
	check("a package whose id merely contains a banned name is not flagged",
		!hasRulePrefix(found, "BANNED"), shown(found))
	os.RemoveAll(root)

	// 11. The ban set is read from the `N:` entries and ONLY from them.
	root = fixture(map[string]string{bannedSymbolsFile: bannedSymbolsFixture})
	names := bannedNames(root)
	sort.Strings(names)
	check("the ban set comes from the N: entries only",
		len(names) == 1 && names[0] == "Newtonsoft.Json", fmt.Sprint(names))
	os.RemoveAll(root)

	// 12. `--output-only` asks the issue's question literally.
	root = baseFixture(map[string]string{
		"tests/Output.Tests/Output.Tests.csproj":                     fmt.Sprintf(csprojTemplate, "TUnit.Assertions"),
		"tests/Output.Tests/bin/Release/net10.0/Newtonsoft.Json.dll": "MZ",
	})
	_, found = analyse(root, false, true)
	check("output-only mode finds a banned assembly in the output directory",
		hasRule(found, "BANNED-ASSEMBLY", "Newtonsoft.Json") && !hasRulePrefix(found, "PACKAGE-BLIND"),
		shown(found))
	os.RemoveAll(root)

	// 13. The same mode on a tree that was never built looked at NOTHING.
	root = baseFixture(nil)
	_, found = analyse(root, false, true)
	check("output-only mode on a tree with no output is BLIND, not green",
		hasRulePrefix(found, "PACKAGE-BLIND"), shown(found))
	os.RemoveAll(root)

	// 14. `--output-only` means the OUTPUT DIRECTORY and nothing else.
	root = baseFixture(map[string]string{
		"tests/Graph.Tests/Graph.Tests.csproj": fmt.Sprintf(csprojTemplate, "Fabrikam.Arch"),
		"tests/Graph.Tests/obj/project.assets.json": assetsJSON(
			[][2]string{{"Fabrikam.Arch", "1.0.0"}, {"Newtonsoft.Json", "13.0.4"}},
			[][2]string{{"Fabrikam.Arch", "1.0.0"}}),
	})
	_, found = analyse(root, false, true)
	check("output-only mode ignores the resolved graph",
		!hasRulePrefix(found, "BANNED"), shown(found))
	os.RemoveAll(root)

	// 15. An EMPTY ban set is blindness, not cleanliness.
	root = baseFixture(map[string]string{bannedSymbolsFile: "# nothing banned here\n"})
	_, found = analyse(root, false, false)
	check("an empty ban set is reported, not passed",
		hasRulePrefix(found, "PACKAGE-BLIND"), shown(found))
	os.RemoveAll(root)

	// 16. The gate must not be able to count itself.
	here, err := os.Getwd()
	if err != nil {
		here = ""
	}
	inside := false
	for _, scope := range []string{scopeDirTests, scopeDirSamples} {
		if strings.HasPrefix(here, filepath.Join("..", "..", scope)+string(os.PathSeparator)) {
			inside = true
		}
	}
	check("the gate's own source is outside the scope", !inside, here)

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}
