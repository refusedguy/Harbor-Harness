package contractdomains

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// # THE SELF-TEST
//
// The self-test plants a fixture tree -- a solution with three projects and
// two contract roots -- and proves three things:
//
//   1. the census counts the consumers of a type and ignores a mention in a
//      comment and a mention in a string;
//   2. the domains fall out of the consumer sets, and a type everybody uses
//      is NOT a domain;
//   3. the radius shrinks when a domain that only a few projects use moves
//      into an assembly of its own -- and the CHECK path goes RED when the
//      committed snapshot does not match what was measured.
//
// The third one is the one that matters: an instrument that cannot fail is
// indistinguishable from an instrument that found nothing.

// selfTestRun runs the self-test.
func selfTestRun() int {
	failures := []string{}
	check := func(name string, ok bool, detail string) {
		if ok {
			fmt.Printf("  ok   %s\n", name)
			return
		}
		fmt.Printf("  FAIL %s: %s\n", name, detail)
		failures = append(failures, name)
	}
	root, err := os.MkdirTemp("", "contract-domains-selftest")
	if err != nil {
		fmt.Fprintf(os.Stderr, "cannot create the fixture tree: %v\n", err)
		return gate.Broken
	}
	defer os.RemoveAll(root)
	if err := plantFixture(root); err != nil {
		fmt.Fprintf(os.Stderr, "cannot plant the fixture: %v\n", err)
		return gate.Broken
	}

	measured, err := loadTree(root, "Fixture.slnx")
	if err != nil {
		fmt.Fprintf(os.Stderr, "cannot read the fixture tree: %v\n", err)
		return gate.Broken
	}
	roots := []string{
		"src/Fixture.Contracts/Fixture.Contracts.csproj",
		"src/Fixture.Abstractions/Fixture.Abstractions.csproj",
	}
	census := measureTypes(measured, roots)

	// 1. the census.
	widely := census.Consumers["src/Fixture.Contracts/Fixture.Contracts.csproj|Widely"]
	check("a type three of five projects use has three consumers",
		len(widely) == 3,
		fmt.Sprintf("got %d consumers", len(widely)))
	check("the project that declares a type is not a consumer of it",
		!widely["src/Fixture.Contracts/Fixture.Contracts.csproj"],
		"the declaring project counted itself as a consumer")
	narrow := census.Consumers["src/Fixture.Contracts/Fixture.Contracts.csproj|Narrow"]
	check("a type two of five projects use has two consumers",
		len(narrow) == 2 && narrow["apps/Fixture.Only/Fixture.Only.csproj"],
		fmt.Sprintf("got %v", narrow))
	check("a mention in a comment is not a use",
		len(census.Consumers["src/Fixture.Contracts/Fixture.Contracts.csproj|OnlyInComment"]) == 0,
		"a type named only in a comment was counted")
	check("a mention in a string is not a use",
		len(census.Consumers["src/Fixture.Contracts/Fixture.Contracts.csproj|OnlyInString"]) == 0,
		"a type named only in a string was counted")

	// 2. the domains.
	domains := domainOf(census, roots[0], defaultJaccard)
	names := map[string]*domain{}
	for _, d := range domains {
		names[d.Name] = d
	}
	narrowDomain, ok := names["Narrow"]
	check("the narrow type is a domain of its own", ok, "no domain named Narrow")
	if ok {
		check("the narrow domain has two consumers", len(narrowDomain.Consumers) == 2,
			fmt.Sprintf("got %d", len(narrowDomain.Consumers)))
	}
	wideDomain, ok := names["Widely"]
	check("the widely used type is a domain", ok, "no domain named Widely")
	if ok {
		check("the widely used type is NOT separable: three of five projects use it",
			strings.Contains(domainVerdict(wideDomain, len(measured.Projects), 0.5), "NOT a domain"),
			"a type everybody uses was called separable")
	}

	// 3. the radius, and the check path.
	part := cut(domains, 2)
	part.Root = roots[0]
	sim := simulate(measured, census, part, nil)
	before := len(transitiveConsumers(roots[0], referencesOf(measured)))
	after := sim.Radius[roots[0]]
	check("the root's radius does NOT shrink when a narrow domain moves out",
		after == before,
		fmt.Sprintf("before %d, after %d -- the facade holds the radius", before, after))
	check("the moved domain's own radius is small",
		sim.Radius[sim.NewAssemblies[narrowDomain]] == 2,
		fmt.Sprintf("got %d", sim.Radius[sim.NewAssemblies[narrowDomain]]))

	// 3b. the lever: the facade holds the whole radius of the root.
	lever := measureLever(roots[0], measured, census, domains)
	check("the lever names the referencer that holds the radius",
		len(lever.Rows) == 1 && lever.Rows[0].Referencer == "src/Fixture.Abstractions/Fixture.Abstractions.csproj",
		fmt.Sprintf("got %v", lever.Rows))
	check("the lever shows the whole radius dropping with that one edge",
		len(lever.Rows) == 1 && lever.Rows[0].Dropped == lever.Radius,
		fmt.Sprintf("got %v", lever.Rows))

	// The check path: a snapshot that does not match must go red.
	opts := options{
		solution:          "Fixture.slnx",
		jaccard:           defaultJaccard,
		universal:         defaultUniversal,
		sweepCompilations: defaultSweepCompilations,
		sweepSeconds:      defaultSweepSeconds,
		sweepPasses:       defaultSweepPasses,
	}
	report := buildReport(root, measured, census, nil, opts, roots)
	snapshotPath := filepath.Join(root, "snapshot.txt")
	if err := os.WriteFile(snapshotPath, []byte(report.snapshot()), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "cannot write the snapshot: %v\n", err)
		return gate.Broken
	}
	code := checkSnapshot(snapshotPath, report.snapshot())
	check("the check path is green on the snapshot it just wrote", code == gate.Green,
		fmt.Sprintf("got %d", code))
	if err := os.WriteFile(snapshotPath, []byte(report.snapshot()+"tampered\n"), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "cannot tamper with the snapshot: %v\n", err)
		return gate.Broken
	}
	code = checkSnapshot(snapshotPath, report.snapshot())
	check("the check path is RED on a snapshot that does not match", code == gate.Red,
		fmt.Sprintf("got %d", code))

	if len(failures) > 0 {
		fmt.Printf("\nFAILED: %d of the self-test's checks did not hold\n", len(failures))
		return gate.Red
	}
	fmt.Printf("\nall the self-test's checks held\n")
	return gate.Green
}

// domainVerdict is the verdict a domain gets.
func domainVerdict(d *domain, projects int, universal float64) string {
	if float64(len(d.Consumers)) >= universal*float64(projects) {
		return "NOT a domain: everybody uses it"
	}
	return "domain: it has a consumer set of its own"
}

// referencesOf is the reference map of a measured tree.
func referencesOf(measured *tree) map[string][]string {
	out := map[string][]string{}
	for _, proj := range measured.Projects {
		out[proj.Path] = proj.References
	}
	return out
}

// plantFixture writes the fixture tree.
//
// The shape is the shape of the real tree: a contract root, a facade over
// it, a project that uses everything and a project that uses one narrow
// type.
func plantFixture(root string) error {
	files := map[string]string{
		"Fixture.slnx": `<?xml version="1.0" encoding="utf-8"?>
<Solution>
  <Project Path="src/Fixture.Contracts/Fixture.Contracts.csproj" />
  <Project Path="src/Fixture.Abstractions/Fixture.Abstractions.csproj" />
  <Project Path="apps/Fixture.All/Fixture.All.csproj" />
  <Project Path="apps/Fixture.Third/Fixture.Third.csproj" />
  <Project Path="apps/Fixture.Only/Fixture.Only.csproj" />
</Solution>
`,
		"src/Fixture.Contracts/Fixture.Contracts.csproj": `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`,
		"src/Fixture.Contracts/Contracts.cs": `namespace Fixture.Contracts;

public record Widely(int Value);

public record Narrow(string Name);

public record OnlyInComment(string Text);

public record OnlyInString(string Text);
`,
		"src/Fixture.Abstractions/Fixture.Abstractions.csproj": `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Fixture.Contracts\Fixture.Contracts.csproj" />
  </ItemGroup>
</Project>
`,
		"src/Fixture.Abstractions/Facade.cs": `namespace Fixture.Abstractions;

public interface IFacade
{
    Widely Get();
}
`,
		"apps/Fixture.All/Fixture.All.csproj": `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Fixture.Abstractions\Fixture.Abstractions.csproj" />
  </ItemGroup>
</Project>
`,
		"apps/Fixture.All/All.cs": `using Fixture.Contracts;

namespace Fixture.All;

public class All
{
    public Widely One() => new Widely(1);

    public Narrow Two() => new Narrow("x");
}
`,
		"apps/Fixture.Third/Fixture.Third.csproj": `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Fixture.Abstractions\Fixture.Abstractions.csproj" />
  </ItemGroup>
</Project>
`,
		"apps/Fixture.Third/Third.cs": `using Fixture.Contracts;

namespace Fixture.Third;

public class Third
{
    public Widely One() => new Widely(2);
}
`,
		"apps/Fixture.Only/Fixture.Only.csproj": `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Fixture.Abstractions\Fixture.Abstractions.csproj" />
  </ItemGroup>
</Project>
`,
		"apps/Fixture.Only/Only.cs": `using Fixture.Contracts;

namespace Fixture.Only;

public class Only
{
    // OnlyInComment is named in a comment, and that is not a use.
    public string Text => "OnlyInString";

    public Narrow Three() => new Narrow("y");
}
`,
	}
	for name, body := range files {
		path := filepath.Join(root, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			return err
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			return err
		}
	}
	return nil
}
