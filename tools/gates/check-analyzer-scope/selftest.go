package checkanalyzerscope

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture is a shape the rules have: a family declared in the wrong
// file, a family whose scope drifted, a family without the switch, an
// unknown analyzer package, a missing pin, an orphan switch, a stale table
// row — and the false-positive controls: a NON-analyzer package that looks
// like one, a comment that names an analyzer package, and a clean tree that
// stays clean.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const (
	csprojTemplate = `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
`
	propsTemplate = `<Project>
  <PropertyGroup>
{props}  </PropertyGroup>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
`
	analyzerPackage = "Roslynator.Analyzers"
	analyzerSwitch  = "HarborAnalyzerRoslynator"
)

// fixture is a throwaway tree: repo-relative path -> contents.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "analyzer-scope-gate-")
	if err != nil {
		return ""
	}
	for relative, body := range files {
		path := filepath.Join(root, relative)
		os.MkdirAll(filepath.Dir(path), 0o755)
		os.WriteFile(path, []byte(body), 0o644)
	}
	return root
}

// props is a props file with the given properties and references.
func props(properties string, refs string) string {
	out := strings.Replace(propsTemplate, "{props}", properties, 1)
	return strings.Replace(out, "{refs}", refs, 1)
}

// newFamily is one family of the fixture table.
func newFamily(identifier string, property string, scopes []string, declaredIn []string, mode string) family {
	if scopes == nil {
		scopes = []string{"root", "src", "apps", "tests"}
	}
	if declaredIn == nil {
		declaredIn = []string{"Directory.Build.props"}
	}
	return family{
		id:         identifier,
		property:   property,
		declaredIn: declaredIn,
		scopes:     scopes,
		mode:       mode,
	}
}

// tableFor is the fixture scope table.
func tableFor(families []family) *scopeTable {
	table := &scopeTable{
		trees:        map[string]any{"root": "r", "src": "s", "apps": "a", "tests": "t"},
		notAnalyzers: map[string]bool{},
	}
	table.families = families
	return table
}

// cleanTree is a tree that agrees with the table: one family, everywhere.
func cleanTree() map[string]string {
	files := map[string]string{}
	files["Directory.Build.props"] = props(
		"    <"+analyzerSwitch+">on</"+analyzerSwitch+">",
		"    <PackageReference Include=\""+analyzerPackage+"\" Condition=\"'$(HarborAnalyzerRoslynator)' != 'off'\" />")
	files["src/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">", "")
	files["apps/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">", "")
	files["tests/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">", "")
	files["Directory.Packages.props"] = props("",
		"    <PackageVersion Include=\""+analyzerPackage+"\" Version=\"5.0.1\" />")
	return files
}

// rules is the set of rules a finding list carries.
func rules(found []finding) map[string]bool {
	out := map[string]bool{}
	for _, item := range found {
		out[item.rule] = true
	}
	return out
}

// shown is the detail of a failed fixture.
func shown(found []finding) string {
	parts := make([]string, 0, len(found))
	for _, item := range found {
		parts = append(parts, item.String())
	}
	return strings.Join(parts, " | ")
}

// selfTestRun builds throwaway trees and asserts the failure path on every
// shape the rules have.
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

	// 0. The control: a tree that agrees with the table is clean.
	root := fixture(cleanTree())
	_, found := analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a clean tree stays green", len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 1. SCOPE-DRIFT: the scope file switches the family off, the table
	//    still says it runs everywhere.
	files := cleanTree()
	files["tests/Directory.Build.props"] = props("    <"+analyzerSwitch+">off</"+analyzerSwitch+">", "")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a family switched off in a scope file is reported", rules(found)["SCOPE-DRIFT"], shown(found))
	os.RemoveAll(root)

	// 2. DECLARATION-SITE: the family is declared in a file the table does
	//    not name.
	files = cleanTree()
	files["src/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">",
		"    <PackageReference Include=\""+analyzerPackage+"\" Condition=\"'$(HarborAnalyzerRoslynator)' != 'off'\" />")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a family declared in an unnamed file is reported", rules(found)["DECLARATION-SITE"], shown(found))
	os.RemoveAll(root)

	// 3. NO-CONDITION: the family is declared without the switch.
	files = cleanTree()
	files["Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">",
		"    <PackageReference Include=\""+analyzerPackage+"\" />")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a family without the switch is reported", rules(found)["NO-CONDITION"], shown(found))
	os.RemoveAll(root)

	// 4. UNDECLARED-ANALYZER: a package that looks like an analyzer is
	//    referenced and is not in the table.
	files = cleanTree()
	files["src/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">",
		"    <PackageReference Include=\"Brand.New.Analyzers\" />")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("an analyzer package that is not in the table is reported", rules(found)["UNDECLARED-ANALYZER"], shown(found))
	os.RemoveAll(root)

	// 5. MISSING-PIN: the family has no PackageVersion.
	files = cleanTree()
	files["Directory.Packages.props"] = props("", "")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a family without a version pin is reported", rules(found)["MISSING-PIN"], shown(found))
	os.RemoveAll(root)

	// 6. ORPHAN-SWITCH: a scope file switches a property no family owns.
	files = cleanTree()
	files["src/Directory.Build.props"] = props("    <HarborAnalyzerNobody>off</HarborAnalyzerNobody>", "")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a switch that no family owns is reported", rules(found)["ORPHAN-SWITCH"], shown(found))
	os.RemoveAll(root)

	// 7. STALE-ROW: the table names a family that is not in the tree.
	root = fixture(cleanTree())
	_, found = analyse(root, tableFor([]family{
		newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope"),
		newFamily("Absent.Analyzers", "HarborAnalyzerAbsent", nil, nil, "scope"),
	}))
	check("a table row with no declaration is reported", rules(found)["STALE-ROW"], shown(found))
	os.RemoveAll(root)

	// 8. THE FALSE-POSITIVE CONTROL: a NON-analyzer package that mentions an
	//    analyzer in its id is not an analyzer.
	files = cleanTree()
	files["src/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">",
		"    <PackageReference Include=\"Newtonsoft.Json\" />")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("a package that is not an analyzer is not reported", !rules(found)["UNDECLARED-ANALYZER"], shown(found))
	os.RemoveAll(root)

	// 9. THE FALSE-POSITIVE CONTROL: a comment that NAMES an analyzer
	//    package is not a declaration.
	files = cleanTree()
	files["src/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">",
		"    <!-- Roslynator.Analyzers is switched off here on purpose. -->\n")
	root = fixture(files)
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	check("an analyzer package named in a comment is not reported", !rules(found)["UNDECLARED-ANALYZER"], shown(found))
	os.RemoveAll(root)

	// 10. The finding carries the line number of the FILE, not of the
	//     comment-masked copy.
	body := "    <!-- Roslynator.Analyzers -->\n" +
		"    <PackageReference Include=\"Brand.New.Analyzers\" />\n"
	files = cleanTree()
	files["src/Directory.Build.props"] = props("    <"+analyzerSwitch+">on</"+analyzerSwitch+">", body)
	root = fixture(files)
	expectedLine := 0
	for index, line := range strings.Split(props("", body), "\n") {
		if line == "    <PackageReference Include=\"Brand.New.Analyzers\" />" {
			expectedLine = index + 1
			break
		}
	}
	_, found = analyse(root, tableFor([]family{newFamily(analyzerPackage, analyzerSwitch, nil, nil, "scope")}))
	lineFired := false
	for _, item := range found {
		if item.rule == "UNDECLARED-ANALYZER" && item.line == expectedLine {
			lineFired = true
		}
	}
	check("the finding carries the file's line number", lineFired,
		shown(found)+fmt.Sprintf(" (expected line %d)", expectedLine))
	os.RemoveAll(root)

	// 11. The gate must not be able to count itself: this gate lives under
	//     tools/, and the scope is the project files.
	here, err := os.Executable()
	if err != nil {
		here = ""
	}
	check("the gate's own source is outside the scope",
		!strings.HasSuffix(here, ".csproj"), here)

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}
