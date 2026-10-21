package goquality

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture below is a defect the gate exists to catch, planted in a
// throwaway tree. A rule that cannot fire is indistinguishable from a rule
// that found nothing, and that is the failure every gate in this repository
// is tested against.
//
// The fixtures that need no toolchain are the ones the STANDARD LIBRARY
// rules decide: the formatting, the registry in both directions, the
// self-test, and a file that does not parse. The vet fixture needs the Go
// toolchain, and when the toolchain is not on PATH the fixture says it did
// not run instead of pretending it passed -- a self-test that reports green
// for a rule it did not exercise is the same lie as a gate that reports
// green for a rule it did not run.

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The fixtures. The tabs are written as escapes on purpose: a fixture that
// carried a literal tab would be at the mercy of whatever the editor did
// to the file, and a fixture that is not in gofmt's shape would make the
// clean-tree case red for the wrong reason.
const (
	fixtureRegistry = "package main\n" +
		"\n" +
		"import \"fmt\"\n" +
		"\n" +
		"type registered struct {\n" +
		"\tname string\n" +
		"}\n" +
		"\n" +
		"var registry = []registered{\n" +
		"\t{\n" +
		"\t\tname: \"alpha\",\n" +
		"\t},\n" +
		"}\n" +
		"\n" +
		"func main() {\n" +
		"\tfmt.Println(len(registry))\n" +
		"}\n"

	// The same registry with a gate that does not exist.
	fixtureRegistryStale = "package main\n" +
		"\n" +
		"import \"fmt\"\n" +
		"\n" +
		"type registered struct {\n" +
		"\tname string\n" +
		"}\n" +
		"\n" +
		"var registry = []registered{\n" +
		"\t{\n" +
		"\t\tname: \"alpha\",\n" +
		"\t},\n" +
		"\t{\n" +
		"\t\tname: \"delta\",\n" +
		"\t},\n" +
		"}\n" +
		"\n" +
		"func main() {\n" +
		"\tfmt.Println(len(registry))\n" +
		"}\n"

	// The same registry with one gate named twice.
	fixtureRegistryTwice = "package main\n" +
		"\n" +
		"import \"fmt\"\n" +
		"\n" +
		"type registered struct {\n" +
		"\tname string\n" +
		"}\n" +
		"\n" +
		"var registry = []registered{\n" +
		"\t{\n" +
		"\t\tname: \"alpha\",\n" +
		"\t},\n" +
		"\t{\n" +
		"\t\tname: \"alpha\",\n" +
		"\t},\n" +
		"}\n" +
		"\n" +
		"func main() {\n" +
		"\tfmt.Println(len(registry))\n" +
		"}\n"

	fixtureAlphaMain = "package alpha\n" +
		"\n" +
		"// Run is the entry point of the gate.\n" +
		"func Run() string {\n" +
		"\treturn \"alpha\"\n" +
		"}\n"

	// The same file with a space where the tab belongs.
	fixtureUnformatted = "package alpha\n" +
		"\n" +
		"// Run is the entry point of the gate.\n" +
		"func Run() string {\n" +
		"    return \"alpha\"\n" +
		"}\n"

	// A file that does not parse.
	fixtureUnparseable = "package alpha\n" +
		"\n" +
		"// Run is the entry point of the gate.\n" +
		"func Run() string {\n" +
		"\treturn \"alpha\"\n"

	fixtureAlphaSelf = "package alpha\n" +
		"\n" +
		"// selfTestRun is the gate's own proof that it can fail.\n" +
		"func selfTestRun() int {\n" +
		"\treturn 0\n" +
		"}\n"

	fixtureBetaMain = "package beta\n" +
		"\n" +
		"// Run is the entry point of the gate.\n" +
		"func Run() string {\n" +
		"\treturn \"beta\"\n" +
		"}\n"

	fixtureBetaSelf = "package beta\n" +
		"\n" +
		"// selfTestRun is the gate's own proof that it can fail.\n" +
		"func selfTestRun() int {\n" +
		"\treturn 0\n" +
		"}\n"

	// A module with a printf that vet must reject.
	fixtureVetGoMod = "module harbor.local/goqualityfixture\n" +
		"\n" +
		"go 1.21\n"

	fixtureVetMain = "package fixture\n" +
		"\n" +
		"import \"fmt\"\n" +
		"\n" +
		"// Run prints a number that is not a number.\n" +
		"func Run() {\n" +
		"\tfmt.Printf(\"%d\\n\", \"not a number\")\n" +
		"}\n"
)

// selfTestRun plants every defect the gate exists to catch and requires it
// to be named.
func selfTestRun() int {
	ok := true
	fixtures := []struct {
		name  string
		files map[string]string
		want  string
	}{
		{
			name:  "a clean tree is clean",
			files: cleanTree(),
			want:  "",
		},
		{
			name: "an unformatted file is caught",
			files: cleanTreeWith(map[string]string{
				"tools/gates/alpha/main.go": fixtureUnformatted,
			}),
			want: ruleFormat,
		},
		{
			name: "a file that does not parse is caught",
			files: cleanTreeWith(map[string]string{
				"tools/gates/alpha/main.go": fixtureUnparseable,
			}),
			want: ruleParse,
		},
		{
			name: "a package the registry does not name is caught",
			files: cleanTreeWith(map[string]string{
				"tools/gates/beta/main.go":     fixtureBetaMain,
				"tools/gates/beta/selftest.go": fixtureBetaSelf,
			}),
			want: ruleNoEntry,
		},
		{
			name: "a package without a self-test is caught",
			files: map[string]string{
				"tools/gates/main.go":       fixtureRegistry,
				"tools/gates/alpha/main.go": fixtureAlphaMain,
			},
			want: ruleNoSelf,
		},
		{
			name: "a registry row without a package is caught",
			files: map[string]string{
				"tools/gates/main.go":           fixtureRegistryStale,
				"tools/gates/alpha/main.go":     fixtureAlphaMain,
				"tools/gates/alpha/selftest.go": fixtureAlphaSelf,
			},
			want: ruleStaleRow,
		},
		{
			name: "a gate named twice in the registry is caught",
			files: map[string]string{
				"tools/gates/main.go":           fixtureRegistryTwice,
				"tools/gates/alpha/main.go":     fixtureAlphaMain,
				"tools/gates/alpha/selftest.go": fixtureAlphaSelf,
			},
			want: ruleDuplicate,
		},
	}

	fmt.Printf("fixtures: %d\n", len(fixtures))
	for _, fixture := range fixtures {
		fired := runFixture(fixture.files)
		good := fired == fixture.want
		ok = ok && good
		mark := "ok"
		if !good {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-42s] fired %-22s expected %-22s %s\n",
			fixture.name, verdict(fired), verdict(fixture.want), mark)
		if !good {
			fmt.Printf("      the gate did not name the defect it exists for\n")
		}
	}

	// The toolchain rule. Without the toolchain the fixture did not run,
	// and the self-test says so instead of counting it as a pass.
	vetFired, vetRan, err := vetFixture()
	switch {
	case err != nil:
		fmt.Printf("  [%-42s] << MISMATCH: %v\n", "a printf vet rejects is caught", err)
		ok = false
	case !vetRan:
		fmt.Printf("  [%-42s] did not run: no Go toolchain on PATH\n", "a printf vet rejects is caught")
	default:
		good := vetFired == ruleVet
		ok = ok && good
		mark := "ok"
		if !good {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-42s] fired %-22s expected %-22s %s\n",
			"a printf vet rejects is caught", verdict(vetFired), verdict(ruleVet), mark)
	}

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}

// cleanTree is the tree every fixture starts from: one registered gate,
// formatted, with a self-test.
func cleanTree() map[string]string {
	return map[string]string{
		"tools/gates/main.go":           fixtureRegistry,
		"tools/gates/alpha/main.go":     fixtureAlphaMain,
		"tools/gates/alpha/selftest.go": fixtureAlphaSelf,
	}
}

// cleanTreeWith is the clean tree with some files replaced.
func cleanTreeWith(overrides map[string]string) map[string]string {
	files := cleanTree()
	for name, content := range overrides {
		files[name] = content
	}
	return files
}

// runFixture writes a tree into a temporary directory and returns the rule
// the source rules raised, or an empty string when they raised nothing.
func runFixture(files map[string]string) string {
	dir, err := os.MkdirTemp("", "harbor-go-quality-")
	if err != nil {
		return "(the fixture could not be written)"
	}
	defer os.RemoveAll(dir)

	for name, content := range files {
		path := filepath.Join(dir, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			return "(the fixture could not be written)"
		}
		if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
			return "(the fixture could not be written)"
		}
	}

	t := tree{root: dir, gatesDir: filepath.Join(dir, "tools", "gates")}
	findings, _, _, err := checkSources(t)
	if err != nil {
		return "(the registry could not be read)"
	}
	if len(findings) == 0 {
		return ""
	}
	rules := map[string]bool{}
	for _, f := range findings {
		rules[f.rule] = true
	}
	for _, rule := range []string{ruleParse, ruleFormat, ruleNoSelf, ruleNoEntry, ruleStaleRow, ruleDuplicate} {
		if rules[rule] {
			return rule
		}
	}
	return findings[0].rule
}

// verdict is how a rule code is written in the report: the empty string is
// "the gate found nothing", and that is a verdict of its own.
func verdict(rule string) string {
	if rule == "" {
		return "(no finding)"
	}
	return rule
}

// vetFixture plants a printf that `go vet` must reject, and asks the gate
// whether it names it.
//
// The second return value says whether the fixture RAN: a self-test that
// cannot run must say so, not report a pass it did not earn.
func vetFixture() (string, bool, error) {
	if _, err := exec.LookPath("go"); err != nil {
		return "", false, nil
	}
	dir, err := os.MkdirTemp("", "harbor-go-quality-vet-")
	if err != nil {
		return "", false, err
	}
	defer os.RemoveAll(dir)

	gatesDir := filepath.Join(dir, "tools", "gates")
	if err := os.MkdirAll(filepath.Join(gatesDir, "fixture"), 0o755); err != nil {
		return "", false, err
	}
	if err := os.WriteFile(filepath.Join(gatesDir, "go.mod"), []byte(fixtureVetGoMod), 0o644); err != nil {
		return "", false, err
	}
	if err := os.WriteFile(filepath.Join(gatesDir, "fixture", "main.go"), []byte(fixtureVetMain), 0o644); err != nil {
		return "", false, err
	}

	t := tree{root: dir, gatesDir: gatesDir}
	findings, err := checkToolchain(t)
	if err != nil {
		return "", true, err
	}
	if len(findings) == 0 {
		return "", true, nil
	}
	rules := make([]string, 0, len(findings))
	for _, f := range findings {
		rules = append(rules, f.rule)
	}
	sort.Strings(rules)
	return strings.Join(rules, ","), true, nil
}
