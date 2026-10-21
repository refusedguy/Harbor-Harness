package checkabstractionscontract

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture is a shape this gate must not get wrong. The fixtures are
// the vacuity shapes measured while writing the gate: a declared package
// nothing uses, a used namespace nothing declares, a tree that is genuinely
// clean, a project with no declarations at all (must report BLIND, never
// pass), a package whose only "use" is inside a comment, a source generator
// that has no namespace to match, and the two one-hop reachability halves.

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

// widened is the projects added to the perimeter after the frozen two.
//
// Every fixture gets them as a clean baseline unless the fixture is
// specifically about one of them, because `analyse` walks the WHOLE scope: a
// fixture that built only the frozen two would pick up a CONTRACT-BLIND for
// each missing project and then be asserting against a finding list
// carrying entries that have nothing to do with what it is testing.
var widened = []string{
	"src/Harbor.Extensions",
	"src/Harbor.Application",
	"src/Harbor.PolicyConfig",
}

// csproj is a project file with the given references.
func csproj(refs string) string {
	return strings.Replace(csprojTemplate, "{refs}", refs, 1)
}

// fixture is a throwaway tree: repo-relative path -> contents.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "contract-gate-")
	if err != nil {
		return ""
	}
	merged := map[string]string{}
	for _, project := range widened {
		name := project
		if idx := strings.LastIndexByte(project, '/'); idx >= 0 {
			name = project[idx+1:]
		}
		merged[project+"/"+name+".csproj"] = csproj("    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n")
		merged[project+"/Baseline.cs"] = "using CSharpFunctionalExtensions;\n"
	}
	for path, body := range files {
		merged[path] = body
	}
	for path, body := range merged {
		full := filepath.Join(root, path)
		os.MkdirAll(filepath.Dir(full), 0o755)
		os.WriteFile(full, []byte(body), 0o644)
	}
	return root
}

// hasFinding is whether one finding carries a rule and a subject.
func hasFinding(found []finding, rule string, subject string) bool {
	for _, item := range found {
		if item.rule == rule && item.subject == subject {
			return true
		}
	}
	return false
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

// shown is the detail of a failed fixture.
func shown(found []finding) string {
	parts := make([]string, 0, len(found))
	for _, item := range found {
		parts = append(parts, item.String())
	}
	return strings.Join(parts, ", ")
}

// selfTestRun runs the gate against known-broken fixtures.
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

	// 1. A declared package nothing uses MUST be found.
	root := fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found := analyse(root)
	check("unused-package is reported",
		hasFinding(found, "CONTRACT-UNUSED-PACKAGE", "Contoso.Numerics"), shown(found))
	os.RemoveAll(root)

	// 2. A used namespace nothing declares MUST be found.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\nusing Fabrikam.Text;\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("undeclared-package is reported",
		hasFinding(found, "CONTRACT-UNDECLARED-PACKAGE", "Fabrikam.Text"), shown(found))
	os.RemoveAll(root)

	// 3. A genuinely clean tree MUST stay green — the false-positive control.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\nusing Microsoft.Extensions.Logging;\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("a clean tree stays green", len(found) == 0, shown(found))
	os.RemoveAll(root)

	// 4. A project with NO declarations MUST report BLIND, never pass.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(""),
		"src/Harbor.Abstractions/A.cs":                       "using CSharpFunctionalExtensions;\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("an empty declaration list is BLIND, not green", hasRule(found, "CONTRACT-BLIND"), shown(found))
	os.RemoveAll(root)

	// 5. A package named only inside an XML doc comment MUST still be
	//    reported phantom.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\n" +
			"/// <see cref=\"Contoso.Numerics.Big\" /> is what we would use.\n" +
			"// using Contoso.Numerics;\n" +
			"var s = \"using Contoso.Numerics;\";\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("a comment- and string-only mention does not count as a use",
		hasFinding(found, "CONTRACT-UNUSED-PACKAGE", "Contoso.Numerics"), shown(found))
	os.RemoveAll(root)

	// 6. The convention must answer for a package it has never seen.
	prefixes := namespacePrefixes("Fabrikam.Widgets.Abstractions")
	check("namespace_prefixes generalises to an unseen package",
		len(prefixes) == 2 &&
			containsString(prefixes, "Fabrikam.Widgets") &&
			containsString(prefixes, "Fabrikam.Widgets.Abstractions") &&
			len(namespacePrefixes("Fabrikam")) == 1,
		strings.Join(prefixes, ", "))

	// 7. A source generator has no namespace to match and must not be
	//    reported phantom.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Fabrikam\"/>\n" +
				"    <PackageReference Include=\"Fabrikam.DropIn\">\n" +
				"      <PrivateAssets>all</PrivateAssets>\n" +
				"      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>\n" +
				"    </PackageReference>\n"),
		"src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\nusing Fabrikam;\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("a source generator is not reported phantom", !hasRule(found, "CONTRACT-UNUSED-PACKAGE"), shown(found))
	os.RemoveAll(root)

	// 8. `Microsoft.Extensions.*` is a PACKAGE, not framework.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "using Microsoft.Extensions.Logging;\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	measured, found := analyse(root)
	external := measured.projects["src/Harbor.Abstractions"].externalNamespaces
	check("Microsoft.Extensions.* counts as external and is covered by the package",
		len(found) == 0 && containsString(external, "Microsoft.Extensions.Logging"),
		shown(found)+strings.Join(external, ", "))
	os.RemoveAll(root)

	// 9. A facade's re-declared package, used only one ProjectReference hop
	//    away, is NOT phantom.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"MemoryPack\"/>\n" +
				"    <ProjectReference Include=\"..\\Harbor.Abstractions.Contracts\\Harbor.Abstractions.Contracts.csproj\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "public interface I { }\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"MemoryPack\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using MemoryPack;\n",
	})
	_, found = analyse(root)
	check("a facade re-export one ProjectReference hop away is not phantom",
		!hasRule(found, "CONTRACT-UNUSED-PACKAGE"), shown(found))
	os.RemoveAll(root)

	// 10. A block comment and a multi-line raw string literal can each
	//     contain a line that LOOKS like a `using` directive.
	root = fixture(map[string]string{
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\n" +
			"/*\nusing Contoso.Numerics;\n*/\n" +
			"internal const string Sample = \"\"\"\nusing Contoso.Numerics;\n\"\"\";\n",
		"src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("a using-shaped line inside a block comment or raw string is not a use",
		hasFinding(found, "CONTRACT-UNUSED-PACKAGE", "Contoso.Numerics"), shown(found))
	os.RemoveAll(root)

	// 11. A CARRIER: declared here, used only by the project this one
	//     references.
	root = fixture(map[string]string{
		"src/Harbor.Application/Harbor.Application.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Contoso.Numerics\"/>\n" +
				"    <ProjectReference Include=\"..\\Harbor.Abstractions\\Harbor.Abstractions.csproj\"/>\n"),
		"src/Harbor.Application/A.cs": "using CSharpFunctionalExtensions;\n",
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions/B.cs": "using CSharpFunctionalExtensions;\nusing Contoso.Numerics;\n",
	})
	_, found = analyse(root)
	check("a carrier used one hop away is not a phantom",
		!hasFinding(found, "CONTRACT-UNUSED-PACKAGE", "Contoso.Numerics"), shown(found))
	os.RemoveAll(root)

	// 12. The other side of the hop.
	root = fixture(map[string]string{
		"src/Harbor.Application/Harbor.Application.csproj": csproj(
			"    <PackageReference Include=\"ZLinq\"/>\n" +
				"    <ProjectReference Include=\"..\\Harbor.Abstractions\\Harbor.Abstractions.csproj\"/>\n"),
		"src/Harbor.Application/A.cs": "using CSharpFunctionalExtensions;\nusing ZLinq;\n",
		"src/Harbor.Abstractions/Harbor.Abstractions.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n"),
		"src/Harbor.Abstractions/B.cs": "using CSharpFunctionalExtensions;\n",
	})
	_, found = analyse(root)
	check("a namespace declared one hop away is not undeclared",
		!hasFinding(found, "CONTRACT-UNDECLARED-PACKAGE", "CSharpFunctionalExtensions"), shown(found))
	os.RemoveAll(root)

	// 13. A PEER is not a hop.
	root = fixture(map[string]string{
		"src/Harbor.Extensions/Harbor.Extensions.csproj": csproj(
			"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Extensions/A.cs": "public static class Empty { }\n",
		"src/Harbor.Application/Harbor.Application.csproj": csproj(
			"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Application/B.cs": "using Contoso.Numerics;\n",
	})
	_, found = analyse(root)
	peerPhantom := false
	peerClean := true
	for _, item := range found {
		if item.rule == "CONTRACT-UNUSED-PACKAGE" && item.subject == "Contoso.Numerics" &&
			item.project == "src/Harbor.Extensions" {
			peerPhantom = true
		}
		if item.rule == "CONTRACT-UNUSED-PACKAGE" && item.project == "src/Harbor.Application" {
			peerClean = false
		}
	}
	check("a peer's use does not vouch for a sibling's declaration",
		peerPhantom && peerClean, shown(found))
	os.RemoveAll(root)

	// 14. A URL-bearing string literal and a URL-bearing line comment, both
	//     followed by the `using` directives the fixture is about.
	root = fixture(map[string]string{
		"src/Harbor.Application/Harbor.Application.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Application/A.cs": "namespace Harbor.Application\n{\n" +
			"    internal static string Docs = \"https://example.invalid/api\";\n" +
			"    // and the mirror at http://example.invalid/api2\n" +
			"    internal static class Probe { }\n\n" +
			"    using CSharpFunctionalExtensions;\n" +
			"    using Contoso.Numerics;\n}\n",
	})
	_, found = analyse(root)
	check("a URL literal and a URL comment do not swallow the usings after them",
		!hasFinding(found, "CONTRACT-UNUSED-PACKAGE", "Contoso.Numerics") &&
			!hasFinding(found, "CONTRACT-UNDECLARED-PACKAGE", "Contoso.Numerics"), shown(found))
	os.RemoveAll(root)

	// 15. The convention is strict on purpose and this pins it.
	root = fixture(map[string]string{
		"src/Harbor.Application/Harbor.Application.csproj": csproj(
			"    <PackageReference Include=\"CSharpFunctionalExtensions\"/>\n" +
				"    <PackageReference Include=\"Contoso.Numerics\"/>\n"),
		"src/Harbor.Application/A.cs": "using CSharpFunctionalExtensions;\nusing Contoso.SomethingElse;\n",
	})
	_, found = analyse(root)
	check("a package does not claim a sibling package under the same vendor",
		hasFinding(found, "CONTRACT-UNUSED-PACKAGE", "Contoso.Numerics"), shown(found))
	os.RemoveAll(root)

	// 16. The gate must not be able to count itself.
	here, err := os.Executable()
	if err != nil {
		here = ""
	}
	absolute, err := filepath.Abs(here)
	if err == nil {
		here = absolute
	}
	var containing []string
	for _, project := range scope {
		dir, err := filepath.Abs(project)
		if err != nil {
			continue
		}
		if strings.HasPrefix(here, dir+string(filepath.Separator)) {
			containing = append(containing, project)
		}
	}
	check("the gate's own source is outside every guarded project",
		len(containing) == 0, strings.Join(containing, ", "))

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}
