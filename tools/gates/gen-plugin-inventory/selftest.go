package genplugininventory

// selftest.go -- the generator's own proof that the checker can fail.
//
// The python original had no self-test: the generator writes a committed
// document, and the gate that compares the two is the checker. The port adds
// one, because a generator that stops reading the tree writes a document that
// agrees with itself and says nothing.
//
// Two fixtures: a tree whose document differs from the committed file must be
// reported stale, and a tree whose document matches must be reported clean. A
// checker that cannot say no is a checker that enforces nothing.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// writeFixture writes a tree with one axis, one route and one plugin.
func writeFixture(fixture string) {
	abstractions := filepath.Join(fixture, "src", "Harbor.Plugins.Abstractions")
	os.MkdirAll(abstractions, 0o755)
	os.WriteFile(filepath.Join(abstractions, "PluginAxisRegistry.cs"), []byte(
		"namespace Harbor.Plugins.Abstractions;\n\n"+
			"public static class PluginAxisRegistry\n{\n"+
			"    public static readonly object[] All =\n"+
			"    [\n"+
			"        new(\"IDemoPlugin\", nameof(IPluginLoadHost.RegisterDemo)),\n"+
			"    ];\n}\n"), 0o644)

	hosting := filepath.Join(fixture, "src", "Harbor.Plugins.Hosting")
	os.MkdirAll(hosting, 0o755)
	os.WriteFile(filepath.Join(hosting, "PluginRouteAxisRegistry.cs"), []byte(
		"namespace Harbor.Plugins.Hosting;\n\n"+
			"public enum PluginAxisCarriage\n{\n"+
			"    Carried,\n"+
			"    Refused,\n"+
			"    ClosedSeam,\n}\n\n"+
			"public static class PluginRouteAxisRegistry\n{\n"+
			"    public const string DemoRouteId = \"demo\";\n"+
			"    public const string DemoHostType = \"DemoHost\";\n\n"+
			"    public static readonly object[] All =\n"+
			"    [\n"+
			"        new(\n"+
			"            DemoRouteId,\n"+
			"            nameof(Harbor.Plugins.Abstractions.IPluginLoadHost.RegisterDemo),\n"+
			"            PluginAxisCarriage.Carried,\n"+
			"            \"the demo route carries the demo axis\"),\n"+
			"    ];\n}\n"), 0o644)

	writer := filepath.Join(fixture, "src", "Harbor.Plugins.Hosting", "PluginInventory.cs")
	os.MkdirAll(filepath.Dir(writer), 0o755)
	os.WriteFile(writer, []byte(
		"namespace Harbor.Plugins.Hosting;\n\n"+
			"public sealed class PluginInventory\n{\n"+
			"    public string ToJson()\n"+
			"    {\n"+
			"        writer.WriteNumber(\"schemaVersion\", 1);\n"+
			"        writer.WriteNull(\"route\");\n"+
			"        writer.WriteStartArray(\"axes\");\n"+
			"        writer.WriteStartArray(\"routes\");\n"+
			"        writer.WriteStartArray(\"plugins\");\n"+
			"        WriteStringArray(writer, \"axes\", axes);\n"+
			"        WriteStringArray(writer, \"dependencies\", dependencies);\n"+
			"        WriteStringArray(writer, \"unsatisfiedDependencies\", unsatisfied);\n"+
			"        writer.WriteString(\"name\", name);\n"+
			"        writer.WriteString(\"version\", version);\n"+
			"        writer.WriteString(\"source\", source);\n"+
			"        writer.WriteString(\"phase\", phase);\n"+
			"        writer.WriteBoolean(\"manifest\", manifest);\n"+
			"        writer.WriteStartArray(\"registrations\");\n"+
			"        writer.WriteString(\"door\", door);\n"+
			"        writer.WriteString(\"subject\", subject);\n"+
			"        writer.WriteString(\"outcome\", outcome);\n"+
			"        writer.WriteNull(\"reason\");\n"+
			"        writer.WriteEndArray();\n"+
			"        writer.WriteNull(\"failureReason\");\n"+
			"        writer.WriteString(\"marker\", marker);\n"+
			"        writer.WriteString(\"carriage\", carriage);\n"+
			"        return text;\n"+
			"    }\n}\n"), 0o644)

	samples := filepath.Join(fixture, "samples", "plugins-cs")
	os.MkdirAll(samples, 0o755)
	os.WriteFile(filepath.Join(samples, "DemoPlugin.cs"), []byte(
		"// A demo plugin.\n"+
			"public sealed class DemoPlugin : IDemoPlugin\n"+
			"{\n"+
			"    public string Name => \"demo-plugin\";\n"+
			"    public Version Version => new(1, 2, 3);\n"+
			"}\n"), 0o644)
}

func selfTestRun(repo string) int {
	var problems []string

	fixture, err := os.MkdirTemp("", "harbor-plugin-inventory-")
	if err != nil {
		fmt.Printf("FAIL: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(fixture)
	writeFixture(fixture)

	doc, productMembers := collect(fixture)
	axes := doc.Axes
	routes := doc.Routes
	plugins := doc.Plugins

	if len(axes) != 1 || axes[0].Marker != "IDemoPlugin" {
		problems = append(problems, fmt.Sprintf("the fixture's axis was not read: %v", axes))
	}
	if len(routes) != 1 {
		problems = append(problems, fmt.Sprintf("the fixture's route verdicts were not read: %v", routes))
	} else if routes[0].Carriage != "Carried" {
		problems = append(problems, fmt.Sprintf("the fixture's verdict was not read: %v", routes[0]))
	} else if routes[0].Reason != "the demo route carries the demo axis" {
		problems = append(problems, fmt.Sprintf("the fixture's reason was not read: %q", routes[0].Reason))
	}
	if len(plugins) != 1 {
		problems = append(problems, fmt.Sprintf("the fixture's plugin was not read: %v", plugins))
	} else if plugins[0].Name != "demo-plugin" || len(plugins[0].Axes) != 1 || plugins[0].Axes[0] != "IDemoPlugin" {
		problems = append(problems, fmt.Sprintf("the fixture's plugin was misread: %v", plugins[0]))
	} else if plugins[0].Phase != declaredPhase {
		problems = append(problems, fmt.Sprintf("the fixture's phase was misread: %s", plugins[0].Phase))
	}

	// The schema check: a product writer that renames a member has to be
	// reported, because the document would carry a member nobody writes.
	if len(productMembers) == 0 {
		problems = append(problems, "the fixture's writer members were not read")
	}

	// The stale-file half: the committed file must NOT match the tree.
	staleFile := filepath.Join(fixture, "STALE.json")
	os.WriteFile(staleFile, []byte("{}\n"), 0o644)
	stale, staleText := checkOutputCaptured(fixture, staleFile, 0, 0, 0)
	if stale != 1 {
		problems = append(problems, fmt.Sprintf("the stale check did not fail: exit %d", stale))
	}
	if !strings.Contains(staleText, "stale") {
		problems = append(problems, "the stale check did not say WHAT was stale: "+staleText)
	}

	// The clean half: a file the generator produced must pass.
	fresh := filepath.Join(fixture, "FRESH.json")
	os.WriteFile(fresh, []byte(renderDocument(doc)), 0o644)
	clean, _ := checkOutputCaptured(fixture, fresh, 0, 0, 0)
	if clean != 0 {
		problems = append(problems, fmt.Sprintf("the fresh check did not pass: exit %d", clean))
	}

	// The schema half: a writer that writes a member the document does not
	// carry has to be reported.
	writer := filepath.Join(fixture, "src", "Harbor.Plugins.Hosting", "PluginInventory.cs")
	original, _ := os.ReadFile(writer)
	planted := strings.Replace(string(original),
		"        writer.WriteNull(\"failureReason\");\n",
		"        writer.WriteNull(\"failureReason\");\n"+
			"        writer.WriteString(\"invented\", \"x\");\n", 1)
	os.WriteFile(writer, []byte(planted), 0o644)
	diverged, divergedText := checkOutputCaptured(fixture, fresh, 0, 0, 0)
	if diverged != 1 {
		problems = append(problems, fmt.Sprintf("the schema check did not fail: exit %d", diverged))
	}
	if !strings.Contains(divergedText, "schema") && !strings.Contains(divergedText, "member") {
		problems = append(problems, "the schema check did not say WHAT diverged: "+divergedText)
	}
	os.WriteFile(writer, original, 0o644)

	if len(problems) > 0 {
		for _, problem := range problems {
			fmt.Printf("FAIL: %s\n", problem)
		}
		return gate.Red
	}
	fmt.Println("OK: the self-test passed: the scanner reads the fixture's axes, route " +
		"verdicts and plugins, the stale file is reported stale, the fresh file passes, " +
		"and a schema divergence is reported")
	return gate.Green
}
