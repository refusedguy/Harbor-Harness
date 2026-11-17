package jsonreflection

// selftest.go -- prove the gate still fails on a planted reflection call,
// and that every path counts a used exception the same way.
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
//
// The second fixture is the COUNTER (#2122): the same tree with an
// exceptions file that carries one row the tree still produces and one row
// it no longer produces. The plain path, the report path and the JSON path
// must answer the SAME verdict on it, and they must name the same stale
// exception. Before the fix the plain path answered green and the report
// path answered red.

import (
	"bytes"
	"encoding/json"
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

// The rows of the counter fixture: two the tree produces, one it does not.
const divergenceRows = "tests/Harbor.Sample.Tests/Sample.cs | Serialize(payload) | " +
	"the row the tree still produces\n" +
	"tests/Harbor.Sample.Tests/Hole.cs | Serialize(path) | " +
	"the other row the tree still produces\n" +
	"tests/Harbor.Sample.Tests/Gone.cs | Serialize(payload) | " +
	"the row the tree no longer produces\n"

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
		names = append(names, splitLast(violation.path, "/"))
	}
	sort.Strings(names)
	if len(names) != 2 || names[0] != "Hole.cs" || names[1] != "Sample.cs" {
		fmt.Printf("self-test FAILED: the wrong calls were flagged: %v\n", names)
		return gate.Red
	}

	// The counter. The tree produces TWO reflection calls, so TWO
	// exceptions are used and a THIRD one -- a row for a file the tree does
	// not have -- is stale. Before the fix the plain path marked all three
	// used and answered green while the report path answered red.
	divergence := filepath.Join(tmp, "divergence.txt")
	if err := os.WriteFile(divergence, []byte(divergenceRows), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	exceptions, err := loadExceptions(divergence)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	var plainText bytes.Buffer
	plain := gateVerdict(&plainText, findings, exceptions)
	var reportText bytes.Buffer
	reported := report(&reportText, findings, exceptions, false)
	var jsonText bytes.Buffer
	asJSON := report(&jsonText, findings, exceptions, true)
	if plain != reported || plain != asJSON {
		fmt.Printf("self-test FAILED: the paths disagree on the same tree (plain %d, report %d, json %d)\n",
			plain, reported, asJSON)
		return gate.Red
	}
	if plain != 1 {
		fmt.Printf("self-test FAILED: a stale exception must fail the gate, the paths answered %d\n", plain)
		return gate.Red
	}
	const stalePath = "tests/Harbor.Sample.Tests/Gone.cs"
	if !bytes.Contains(plainText.Bytes(), []byte(stalePath)) {
		fmt.Println("self-test FAILED: the plain path did not name the stale exception")
		return gate.Red
	}
	if !bytes.Contains(reportText.Bytes(), []byte(stalePath)) {
		fmt.Println("self-test FAILED: the report path did not name the stale exception")
		return gate.Red
	}
	var shape struct {
		Violations []struct {
			Path string `json:"path"`
		} `json:"violations"`
		Stale []struct {
			Path string `json:"path"`
		} `json:"stale_exceptions"`
	}
	if err := json.Unmarshal(jsonText.Bytes(), &shape); err != nil {
		fmt.Println("self-test FAILED: the json path did not print a census:", err.Error())
		return gate.Red
	}
	if len(shape.Violations) != 0 {
		fmt.Println("self-test FAILED: the json path named a violation")
		return gate.Red
	}
	if len(shape.Stale) != 1 || shape.Stale[0].Path != stalePath {
		fmt.Printf("self-test FAILED: the json path named the stale exceptions %v\n", shape.Stale)
		return gate.Red
	}

	fmt.Println("self-test ok: the gate still fails on a planted reflection call, " +
		"and every path counts a used exception the same way.")
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
