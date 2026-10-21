package checknotestsdk

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture below is a shape the rule has: a reference in a project
// file, a pin in a props file, a reference with a version of its own, a
// reference in single quotes, a reference with a body, a download, and a
// reference in a different casing — plus the false-positive controls, because
// a gate that flagged its own explanation would be switched off within a
// week.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const csprojTemplate = `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
`

const propsTemplate = `<Project>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
`

const (
	packageRef        = `    <PackageReference Include="{pkg}"/>`
	packageRefVersion = `    <PackageReference Include="{pkg}" Version="18.8.1"/>`
	packageRefSingle  = `    <PackageReference Include='{pkg}'/>`
	packageRefBody    = "    <PackageReference Include=\"{pkg}\">\n      <PrivateAssets>all</PrivateAssets>\n    </PackageReference>"
	packageDownload   = `    <PackageDownload Include="{pkg}"/>`
	packageVersion    = `    <PackageVersion Include="{pkg}" Version="18.8.1" />`
)

// fixture is a throwaway tree: repo-relative path -> contents.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "no-test-sdk-gate-")
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

// csproj is a project file with the given references.
func csproj(refs ...string) string {
	return strings.Replace(csprojTemplate, "{refs}", joinRefs(refs), 1)
}

// props is a props file with the given references.
func props(refs ...string) string {
	return strings.Replace(propsTemplate, "{refs}", joinRefs(refs), 1)
}

// joinRefs is the python "".join(f"{ref}\n" for ref in refs).
func joinRefs(refs []string) string {
	var out strings.Builder
	for _, ref := range refs {
		out.WriteString(ref)
		out.WriteString("\n")
	}
	return out.String()
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
// shape the rule has.
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

	// 1. The shape the issue is about: a PackageReference in a .csproj.
	root := fixture(map[string]string{
		"tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
			strings.Replace(packageRef, "{pkg}", forbiddenID, 1)),
	})
	_, found := analyse(root)
	check("a PackageReference in a .csproj is reported",
		hasRule(found, "VSTEST-SDK-REFERENCE"), shown(found))
	os.RemoveAll(root)

	// 2. The central pin. A pin declares no dependency, but it is the first
	//    half of the re-introduction, so it is reported on its own.
	root = fixture(map[string]string{
		"Directory.Packages.props": props(
			strings.Replace(packageVersion, "{pkg}", forbiddenID, 1)),
	})
	_, found = analyse(root)
	check("a PackageVersion pin in a props file is reported",
		hasRule(found, "VSTEST-SDK-PIN"), shown(found))
	os.RemoveAll(root)

	// 3. The same reference with a version of its own.
	root = fixture(map[string]string{
		"contrib/tests/Harbor.E2E.Cli/Harbor.E2E.Cli.csproj": csproj(
			strings.Replace(packageRefVersion, "{pkg}", forbiddenID, 1)),
	})
	_, found = analyse(root)
	check("a reference carrying its own Version is reported",
		hasRule(found, "VSTEST-SDK-REFERENCE"), shown(found))
	os.RemoveAll(root)

	// 4. The same reference in single quotes.
	root = fixture(map[string]string{
		"tests/Harbor.Ipc.Tests/Harbor.Ipc.Tests.csproj": csproj(
			strings.Replace(packageRefSingle, "{pkg}", forbiddenID, 1)),
	})
	_, found = analyse(root)
	check("a reference in single quotes is reported",
		hasRule(found, "VSTEST-SDK-REFERENCE"), shown(found))
	os.RemoveAll(root)

	// 5. The same reference with a body instead of a self-closing tag.
	root = fixture(map[string]string{
		"tests/Harbor.LoadTests/Harbor.LoadTests.csproj": csproj(
			strings.Replace(packageRefBody, "{pkg}", forbiddenID, 1)),
	})
	_, found = analyse(root)
	check("a reference with a body is reported",
		hasRule(found, "VSTEST-SDK-REFERENCE"), shown(found))
	os.RemoveAll(root)

	// 6. A PackageDownload fetches the package without referencing it.
	root = fixture(map[string]string{
		"tests/Harbor.Ui.Framework.Tests/Harbor.Ui.Framework.Tests.csproj": csproj(
			strings.Replace(packageDownload, "{pkg}", forbiddenID, 1)),
	})
	_, found = analyse(root)
	check("a PackageDownload is reported",
		hasRule(found, "VSTEST-SDK-REFERENCE"), shown(found))
	os.RemoveAll(root)

	// 7. The casing. NuGet ids are case-insensitive.
	root = fixture(map[string]string{
		"tests/Harbor.Tui.CellForge.Tests/Harbor.Tui.CellForge.Tests.csproj": csproj(
			strings.Replace(packageRef, "{pkg}", "microsoft.net.test.sdk", 1)),
	})
	_, found = analyse(root)
	check("a reference in a different casing is reported",
		hasRule(found, "VSTEST-SDK-REFERENCE"), shown(found))
	os.RemoveAll(root)

	// 8. THE FALSE-POSITIVE CONTROL. A project file that NAMES the package
	//    in a comment — which several do, to explain why it is absent — is
	//    clean.
	root = fixture(map[string]string{
		"tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
			"    <!-- Microsoft.NET.Test.Sdk is deliberately absent:\n         TUnit documents it as breaking discovery. -->",
			`    <PackageReference Include="TUnit"/>`),
	})
	_, found = analyse(root)
	check("a package named in a comment is not reported", len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 9. A package whose id merely CONTAINS the forbidden id is a different
	//    package. The match is exact, as NuGet ids are.
	root = fixture(map[string]string{
		"tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
			strings.Replace(packageRef, "{pkg}", "Microsoft.NET.Test.Sdk.Extensions", 1)),
	})
	_, found = analyse(root)
	check("a package whose id merely contains the forbidden id is not reported",
		len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 10. A clean tree stays clean.
	root = fixture(map[string]string{
		"tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
			strings.Replace(packageRef, "{pkg}", "TUnit", 1)),
		"Directory.Packages.props": props(
			strings.Replace(packageVersion, "{pkg}", "TUnit", 1)),
	})
	_, found = analyse(root)
	check("a clean tree stays green", len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 11. The finding carries the line number of the FILE, not of the
	//     comment-masked copy. A comment above the element must not shift it.
	root = fixture(map[string]string{
		"tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
			"    <!-- Microsoft.NET.Test.Sdk -->",
			strings.Replace(packageRef, "{pkg}", forbiddenID, 1)),
	})
	_, found = analyse(root)
	check("the finding carries the file's line number",
		hasRuleAtLine(found, "VSTEST-SDK-REFERENCE", 4), shown(found))
	os.RemoveAll(root)

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}

// hasRule is whether one finding carries a rule.
func hasRule(found []finding, rule string) bool {
	for _, item := range found {
		if item.rule == rule {
			return true
		}
	}
	return false
}

// hasRuleAtLine is whether one finding carries a rule at a line.
func hasRuleAtLine(found []finding, rule string, line int) bool {
	for _, item := range found {
		if item.rule == rule && item.line == line {
			return true
		}
	}
	return false
}
