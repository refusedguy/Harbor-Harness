package benchset

// selftest.go -- the gate's own proof that it can fail.
//
// One fixture per rule: a defect is planted in a synthetic tree, the gate is
// run against it, and the rule has to fire. A rule that cannot fire is
// indistinguishable from a rule that found nothing, and that is the failure
// every gate in this repository is tested against.
//
// The synthetic tree is four gates in three shapes, because the shapes are
// what the gate has to tell apart:
//
//	check-alpha.py   a python gate, run with a floor
//	check-beta       a COMPILED gate, run with a floor and a switch
//	check-gamma.py   a gate CI runs only as its self-test
//	check-delta.py   not run by any workflow at all (the stale case)

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

const syntheticWorkflow = `name: synthetic
on: [push]
jobs:
  gates:
    runs-on: ubuntu-latest
    steps:
      - name: the python gate
        run: python3 tools/check-alpha.py --min-files 10
      - name: the compiled gate
        run: tools/bin/harbor-gates-linux-x64 check-beta --check --min-files 5
      - name: the self-test only
        run: python3 tools/check-gamma.py --self-test
`

const syntheticTools = `check-alpha.py	python3 tools/check-alpha.py --min-files 10
check-beta	tools/bin/harbor-gates-linux-x64 check-beta --check --min-files 5
`

const syntheticExcluded = `check-gamma.py	CI runs only its self-test
`

const syntheticReadme = `# The gate benchmark

The measured set holds check-alpha.py and check-beta.
The excluded set holds check-gamma.py.
`

// syntheticTree writes the fixture tree and returns its root.
func syntheticTree() (string, error) {
	root, err := os.MkdirTemp("", "bench-set-selftest-")
	if err != nil {
		return "", err
	}
	files := map[string]string{
		".github/workflows/ci.yml":         syntheticWorkflow,
		"bench/gates/tools.tsv":            syntheticTools,
		"bench/gates/excluded.tsv":         syntheticExcluded,
		"bench/gates/README.md":            syntheticReadme,
		"tools/check-alpha.py":             "",
		"tools/check-gamma.py":             "",
		"tools/check-delta.py":             "",
		"tools/bin/harbor-gates-linux-x64": "",
	}
	for name, body := range files {
		path := filepath.Join(root, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			return "", err
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			return "", err
		}
	}
	if err := os.MkdirAll(filepath.Join(root, "tools", "gates", "check-beta"), 0o755); err != nil {
		return "", err
	}
	return root, nil
}

// codes returns the codes of the findings, sorted.
func codes(findings []finding) []string {
	out := make([]string, 0, len(findings))
	for _, f := range findings {
		out = append(out, f.code)
	}
	sort.Strings(out)
	return out
}

func hasCode(findings []finding, code string) bool {
	for _, f := range findings {
		if f.code == code {
			return true
		}
	}
	return false
}

// write replaces the body of a file in the fixture tree.
func write(root, name, body string) {
	_ = os.MkdirAll(filepath.Dir(filepath.Join(root, name)), 0o755)
	_ = os.WriteFile(filepath.Join(root, name), []byte(body), 0o644)
}

func selfTestRun() int {
	root, err := syntheticTree()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot build the fixture tree:", err)
		return gate.Broken
	}
	defer os.RemoveAll(root)

	ok := true

	// The clean tree must be clean. A gate that fires on a tree it should
	// pass is a gate nobody trusts.
	clean, _, _ := check(root)
	if len(clean) > 0 {
		fmt.Printf("  [%-38s] %s\n", "the clean tree is clean", strings.Join(codes(clean), " "))
		ok = false
	} else {
		fmt.Printf("  [%-38s] ok\n", "the clean tree is clean")
	}

	type fixture struct {
		name  string
		code  string
		plant func()
	}
	fixtures := []fixture{
		{
			name: "a gate CI runs is in the measured set",
			code: "BENCH-SET-GATE-MISSING",
			plant: func() {
				write(root, "bench/gates/tools.tsv",
					"check-beta\ttools/bin/harbor-gates-linux-x64 check-beta --check --min-files 5\n")
			},
		},
		{
			name: "a row names a gate CI runs",
			code: "BENCH-SET-GATE-STALE",
			plant: func() {
				write(root, "bench/gates/tools.tsv", syntheticTools+
					"check-delta.py\tpython3 tools/check-delta.py\n")
			},
		},
		{
			name: "an exclusion carries a reason",
			code: "BENCH-SET-NO-REASON",
			plant: func() {
				write(root, "bench/gates/excluded.tsv", "check-gamma.py\t\n")
			},
		},
		{
			name: "an exclusion is named in the README",
			code: "BENCH-SET-README-SILENT",
			plant: func() {
				write(root, "bench/gates/README.md",
					"# The gate benchmark\n\nThe measured set holds check-alpha.py and check-beta.\n")
			},
		},
		{
			name: "the row passes the arguments CI passes",
			code: "BENCH-SET-ARGS-DIVERGE",
			plant: func() {
				write(root, "bench/gates/tools.tsv",
					"check-alpha.py\tpython3 tools/check-alpha.py --min-files 20\n"+
						"check-beta\ttools/bin/harbor-gates-linux-x64 check-beta --check --min-files 5\n")
			},
		},
		{
			name: "the row runs a file in the tree",
			code: "BENCH-SET-COMMAND-DEAD",
			plant: func() {
				write(root, "bench/gates/tools.tsv",
					"check-alpha.py\tpython3 tools/check-absent.py --min-files 10\n"+
						"check-beta\ttools/bin/harbor-gates-linux-x64 check-beta --check --min-files 5\n")
			},
		},
		{
			name: "a row is the name and the command",
			code: "BENCH-SET-ROW-SHAPE",
			plant: func() {
				write(root, "bench/gates/tools.tsv", "check-alpha.py python3 tools/check-alpha.py --min-files 10\n")
			},
		},
	}

	for _, f := range fixtures {
		// Every fixture starts from the clean tree.
		write(root, "bench/gates/tools.tsv", syntheticTools)
		write(root, "bench/gates/excluded.tsv", syntheticExcluded)
		write(root, "bench/gates/README.md", syntheticReadme)
		f.plant()
		findings, _, _ := check(root)
		fired := hasCode(findings, f.code)
		mark := "ok"
		if !fired {
			mark = "<< MISMATCH"
			ok = false
		}
		fmt.Printf("  [%-38s] %-28s %s\n", f.name, f.code, mark)
		if !fired {
			fmt.Printf("      the rule did not fire; the findings were %s\n", strings.Join(codes(findings), " "))
		}
	}

	// The tree is put back, and the gate is green on it again.
	write(root, "bench/gates/tools.tsv", syntheticTools)
	write(root, "bench/gates/excluded.tsv", syntheticExcluded)
	write(root, "bench/gates/README.md", syntheticReadme)
	restored, _, _ := check(root)
	if len(restored) > 0 {
		fmt.Printf("  [%-38s] %s\n", "the tree is clean again", strings.Join(codes(restored), " "))
		ok = false
	} else {
		fmt.Printf("  [%-38s] ok\n", "the tree is clean again")
	}

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}
