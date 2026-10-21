package genseamgraph

// selftest.go -- the gate's own proof that it can fail.
//
// A checker that cannot say no is a checker that enforces nothing. The
// python original built a fixture tree in a temporary directory, read the
// graph out of it, and then checked three things:
//
//  1. the scanner finds the fixture's interface, its implementation, its
//     consumer and its registration;
//  2. the route grader reads the DECLARED verdict and OBSERVES the door,
//     and the cross-check catches a route that declares "Carried" while its
//     door refuses the axis;
//  3. the checker says a stale document is stale and says a fresh document
//     is fresh.
//
// The fixture is the same one, byte for byte, so a port that changed the
// scanner's reading of the fixture would fail here.

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

type fixtureFile struct {
	rel     string
	content string
}

var fixtureFiles = []fixtureFile{
	{"src/Demo/Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n"},
	{"src/Demo/ISeam.cs", "namespace Demo;\n\npublic interface ISeam\n{\n    string Name { get; }\n}\n"},
	{"src/Demo/Seam.cs", "namespace Demo;\n\npublic sealed class Seam : ISeam\n{\n    public string Name => \"seam\";\n}\n"},
	{"src/Demo/Consumer.cs", "namespace Demo;\n\npublic sealed class Consumer\n{\n    public string Describe(ISeam seam) => seam.Name;\n}\n"},
	{"src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs",
		"namespace Harbor.Plugins.Abstractions;\n\n" +
			"public static class PluginAxisRegistry\n{\n" +
			"    public static readonly object[] All =\n" +
			"    [\n" +
			"        new(\"IDemoPlugin\", nameof(IPluginLoadHost.RegisterDemo)),\n" +
			"    ];\n}\n"},
	{"src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs",
		"namespace Harbor.Plugins.Abstractions;\n\n" +
			"public interface IPluginLoadHost\n{\n" +
			"    public Result RegisterDemo(string id);\n}\n"},
	{"src/Demo/Hosts.cs",
		"namespace Demo;\n\n" +
			"public sealed class CarryingHost : Harbor.Plugins.Abstractions." +
			"IPluginLoadHost\n{\n" +
			"    private readonly Dictionary<string, string> _items = new();\n" +
			"    public Result RegisterDemo(string id)\n" +
			"    {\n" +
			"        _items[id] = id;\n" +
			"        return Result.Success();\n" +
			"    }\n}\n" +
			"\n" +
			"public sealed class LyingHost : Harbor.Plugins.Abstractions." +
			"IPluginLoadHost\n{\n" +
			"    public Result RegisterDemo(string id) => Result.Failure(\"no\");\n}\n"},
	{"src/Harbor.Plugins.Hosting/PluginRouteAxisRegistry.cs",
		"namespace Harbor.Plugins.Hosting;\n\n" +
			"public enum PluginAxisCarriage\n{\n" +
			"    Carried,\n" +
			"    Refused,\n" +
			"    ClosedSeam,\n}\n" +
			"\n" +
			"public static class PluginRouteAxisRegistry\n{\n" +
			"    public const string DemoRouteId = \"demo\";\n" +
			"    public const string DemoHostType = \"CarryingHost\";\n" +
			"\n" +
			"    public const string LyingRouteId = \"lying\";\n" +
			"    public const string LyingHostType = \"LyingHost\";\n" +
			"\n" +
			"    public static readonly object[] All =\n" +
			"    [\n" +
			"        new(\n" +
			"            DemoRouteId,\n" +
			"            nameof(Harbor.Plugins.Abstractions.IPluginLoadHost.RegisterDemo),\n" +
			"            PluginAxisCarriage.Carried,\n" +
			"            \"the demo route carries the demo axis\"),\n" +
			"        new(\n" +
			"            LyingRouteId,\n" +
			"            nameof(Harbor.Plugins.Abstractions.IPluginLoadHost.RegisterDemo),\n" +
			"            PluginAxisCarriage.Carried,\n" +
			"            \"this row claims the axis and the door refuses it\"),\n" +
			"    ];\n}\n"},
}

func selfTestRun(_ string) int {
	tmp, err := os.MkdirTemp("", "harbor-seamgraph-selftest")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	defer os.RemoveAll(tmp)

	fixture := filepath.Join(tmp, "fixture")
	for _, f := range fixtureFiles {
		full := filepath.Join(fixture, f.rel)
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		if err := os.WriteFile(full, []byte(f.content), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}
	// The stale document: the fixture's graph does not match it.
	staleDoc := filepath.Join(fixture, "AXIS.md")
	if err := os.WriteFile(staleDoc, []byte("stale\n"), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}

	var problems []string

	g := newGraph(fixture)
	names := make([]string, 0, len(g.decls))
	for _, d := range g.decls {
		names = append(names, d.name)
	}
	sort.Strings(names)
	if !contains(names, "ISeam") {
		problems = append(problems, fmt.Sprintf("the fixture's interface was not declared: %v", names))
	}
	impls := g.implementers("ISeam")
	implNames := make([]string, 0, len(impls))
	for _, d := range impls {
		implNames = append(implNames, d.name)
	}
	if len(implNames) != 1 || implNames[0] != "Seam" {
		problems = append(problems, fmt.Sprintf("the fixture's implementation was not linked: %v", implNames))
	}
	consumers := g.consumers("ISeam")
	foundConsumer := false
	for rel := range consumers {
		if strings.Contains(rel, "Consumer.cs") {
			foundConsumer = true
		}
	}
	if !foundConsumer {
		problems = append(problems, fmt.Sprintf("the fixture's consumer was not found: %v", sortedConsumerKeys(consumers)))
	}
	if len(g.registrationsFor("ISeam")) != 0 {
		problems = append(problems, "the fixture registered nothing, but a registration was found")
	}

	axes := g.axes()
	if len(axes) != 1 || axes[0].marker != "IDemoPlugin" {
		markers := make([]string, 0, len(axes))
		for _, a := range axes {
			markers = append(markers, fmt.Sprintf("(%s, %s)", a.marker, a.door))
		}
		problems = append(problems, fmt.Sprintf("the fixture's axis was not read: %v", markers))
	} else {
		rows := map[string]*routeAxisRow{}
		for _, row := range g.routeRows {
			rows[row.route] = row
		}
		if len(rows) != 2 || !contains(sortedRouteKeys(rows), "demo") || !contains(sortedRouteKeys(rows), "lying") {
			problems = append(problems, fmt.Sprintf("the fixture's route rows were not read: %v", sortedRouteKeys(rows)))
		} else {
			row := rows["demo"]
			if row.hostType != "CarryingHost" {
				problems = append(problems, fmt.Sprintf("the fixture's host type was not read: %s", row.hostType))
			}
			if row.declared != "Carried" {
				problems = append(problems, fmt.Sprintf("the fixture's declared verdict was not read: %s", row.declared))
			}
			if row.reason != "the demo route carries the demo axis" {
				problems = append(problems, fmt.Sprintf("the fixture's reason was not read: %q", row.reason))
			}
			if row.observed != "REGISTERS" {
				problems = append(problems, fmt.Sprintf("the fixture's carrying route was not observed registering: %s", row.observed))
			}
			if !row.agrees() {
				problems = append(problems, "the fixture's declared verdict and its door disagree")
			}
			// The load-bearing fixture: a route that CLAIMS the axis and
			// whose door refuses it. The cross-check has to say so.
			lying := rows["lying"]
			if lying.observed != "REFUSES" {
				problems = append(problems, fmt.Sprintf("the fixture's refusing route was not observed refusing: %s", lying.observed))
			}
			if lying.agrees() {
				problems = append(problems,
					"the cross-check accepted a route that declares Carried and whose door refuses the axis")
			}
		}
	}

	// The stale-file half: the committed file must NOT match the tree.
	staleOut, staleText := capture(func() int { return checkOutput(fixture, staleDoc, 0, 0) })
	if staleOut != 1 {
		problems = append(problems, fmt.Sprintf("the stale check did not fail: exit %d", staleOut))
	}
	if !strings.Contains(staleText, "stale") {
		problems = append(problems, fmt.Sprintf("the stale check did not say WHAT was stale: %q", staleText))
	}

	// The clean half: a file the generator produced must pass.
	freshDoc := filepath.Join(fixture, "FRESH.md")
	if err := os.WriteFile(freshDoc, []byte(renderDocument(fixture)), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	freshOut, _ := capture(func() int { return checkOutput(fixture, freshDoc, 0, 0) })
	if freshOut != 0 {
		problems = append(problems, fmt.Sprintf("the fresh check did not pass: exit %d", freshOut))
	}

	if len(problems) > 0 {
		for _, problem := range problems {
			fmt.Printf("FAIL: %s\n", problem)
		}
		return gate.Red
	}
	fmt.Println(
		"OK: the self-test passed: the scanner reads the fixture, the " +
			"route grader reads the declaration and observes the door, the " +
			"cross-check catches a route that lies about its axis, and the " +
			"checker fails on a stale file")
	return gate.Green
}

func renderDocument(repo string) string {
	g := newGraph(repo)
	return render(g)
}

func contains(list []string, want string) bool {
	for _, item := range list {
		if item == want {
			return true
		}
	}
	return false
}

func sortedRouteKeys(rows map[string]*routeAxisRow) []string {
	out := make([]string, 0, len(rows))
	for k := range rows {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

func sortedConsumerKeys(consumers map[string][]site) []string {
	out := make([]string, 0, len(consumers))
	for k := range consumers {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// capture runs fn with the standard output redirected into a string, so a
// self-test can assert on WHAT the gate said, not only on its exit code.
func capture(fn func() int) (int, string) {
	r, w, err := os.Pipe()
	if err != nil {
		return gate.Broken, ""
	}
	saved := os.Stdout
	os.Stdout = w
	done := make(chan string, 1)
	go func() {
		buf := make([]byte, 0, 4096)
		chunk := make([]byte, 4096)
		for {
			n, err := r.Read(chunk)
			if n > 0 {
				buf = append(buf, chunk[:n]...)
			}
			if err != nil {
				break
			}
		}
		done <- string(buf)
	}()
	code := fn()
	os.Stdout = saved
	w.Close()
	text := <-done
	r.Close()
	return code, text
}
