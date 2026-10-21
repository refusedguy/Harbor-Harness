package jsonreflection

// selftest.go -- prove the gate still fails on a planted reflection call.
//
// The fixture is a throwaway tree with four files:
//
//	Sample.cs    a reflection call
//	Hole.cs      a reflection call INSIDE the interpolation hole of a raw
//	             interpolated string -- a scan that blanks the literal whole
//	             would miss it, and that is exactly the bug this fixture
//	             exists for
//	Good.cs      a call that names a source-generated context
//	Arch.cs      the arch-guard tree, which the rule excludes
//
// The gate must see three calls, of which two are reflection calls, and the
// two must be the planted ones.

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"

	"harbor.local/gates/internal/gate"
)

const sampleSource = "namespace Harbor.Sample.Tests;\n" +
	"public class Sample\n" +
	"{\n" +
	"    // JsonSerializer.Serialize(payload) in a comment is not a call.\n" +
	"    public string Reflect() => JsonSerializer.Serialize(payload);\n" +
	"}\n"

const holeSource = "namespace Harbor.Sample.Tests;\n" +
	"public class Hole\n" +
	"{\n" +
	"    public string Reflect(string path) =>\n" +
	"        $\"\"\"{{\\\"path\\\":{{JsonSerializer.Serialize(path)}}}}\"\"\";\n" +
	"}\n"

const goodSource = "namespace Harbor.Sample.Tests;\n" +
	"public class Good\n" +
	"{\n" +
	"    public string Ok() => JsonSerializer.Serialize(payload, SampleContext.Default.Payload);\n" +
	"}\n"

const archSource = "namespace Harbor.Architecture.Tests;\n" +
	"public class Arch\n" +
	"{\n" +
	"    public string Reflect() => JsonSerializer.Serialize(payload);\n" +
	"}\n"

func selfTestRun() int {
	tmp, err := os.MkdirTemp("", "test-json-reflection-gate-")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	defer os.RemoveAll(tmp)

	sample := filepath.Join(tmp, "tests", "Harbor.Sample.Tests")
	if err := os.MkdirAll(sample, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	files := map[string]string{
		filepath.Join(sample, "Sample.cs"): sampleSource,
		filepath.Join(sample, "Hole.cs"):   holeSource,
		filepath.Join(sample, "Good.cs"):   goodSource,
	}
	arch := filepath.Join(tmp, "tests", "Harbor.Architecture.Tests")
	if err := os.MkdirAll(arch, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	files[filepath.Join(arch, "Arch.cs")] = archSource
	for path, body := range files {
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
	}

	findings := scan(filepath.Join(tmp, "tests"), tmp)
	var violations []finding
	for _, found := range findings {
		if !found.sourceGenerated {
			violations = append(violations, found)
		}
	}
	if len(findings) != 3 {
		fmt.Printf("self-test FAILED: expected 3 calls, saw %d\n", len(findings))
		return gate.Red
	}
	if len(violations) != 2 {
		fmt.Printf("self-test FAILED: expected exactly 2 reflection calls, saw %d\n", len(violations))
		return gate.Red
	}
	names := make([]string, 0, len(violations))
	for _, violation := range violations {
		parts := splitLast(violation.path, "/")
		names = append(names, parts)
	}
	sort.Strings(names)
	if len(names) != 2 || names[0] != "Hole.cs" || names[1] != "Sample.cs" {
		fmt.Printf("self-test FAILED: the wrong calls were flagged: %v\n", names)
		return gate.Red
	}

	fmt.Println("self-test ok: the gate still fails on a planted reflection call.")
	return gate.Green
}

// splitLast returns the text after the last separator.
func splitLast(text string, separator string) string {
	for i := len(text) - 1; i >= 0; i-- {
		if text[i:i+1] == separator {
			return text[i+1:]
		}
	}
	return text
}
