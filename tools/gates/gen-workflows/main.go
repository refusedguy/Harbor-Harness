// Package genworkflows generates the workflow files of this repository from
// Go code, and refuses to let a workflow file be edited by hand.
//
// # WHAT THIS IS
//
// The workflow files under .github/workflows/ are what GitHub reads, so they
// stay in the repository. What changes is who may write them: the model in
// tools/gates/gen-workflows/workflows/ is the source of truth, this gate
// writes the YAML from it, and the check mode refuses a committed file that
// the model does not produce.
//
// # WHY A FILE PER WORKFLOW
//
// The complaint this answers is not that the files are large. It is that
// two agents that edit two workflow files collide. A merge conflict is a
// disagreement about TEXT, and a YAML file of 3 500 lines is a lot of text
// to disagree about: two agents that each add a step to the same job
// disagree about the same region, and whoever merges second has to read
// three thousand lines to work out what the other one meant.
//
// The model is one Go file per workflow, so two agents that edit two
// workflows edit two Go files, and their diffs are two small diffs in two
// different files. The YAML becomes an artifact: nobody merges it, and
// nobody has to read it to resolve a conflict.
//
// # THE THREE MODES
//
//	--write     regenerate the workflow files from the models
//	--check     fail when a committed file is not what the model produces
//	--report    print the numbers
//	--self-test prove that the checker can fail
//
// The exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package genworkflows

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/gen-workflows/workflows"
	"harbor.local/gates/gen-workflows/yamlout"
	"harbor.local/gates/internal/gate"
)

// generated is one workflow file this gate owns.
type generated struct {
	// path is the path of the file, relative to the repository root.
	path string
	// build returns the model of the file.
	build func() *yamlout.Doc
}

// registry is the list of the workflow files this gate generates.
//
// It is the one place that says which files are artifacts and which are
// still written by hand, and it grows one line per file that moves over.
var registry = []generated{
	{path: ".github/workflows/meas-jit-aot.yml", build: workflows.MeasJitAot},
}

// Run is the gate entry point. The dispatcher hands it the arguments that
// follow the gate name.
func Run(args []string) int {
	root := ""
	check := false
	write := false
	report := false
	selfTest := false
	minFiles := 0

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--repo":
			value, ok := nextValue(args, &i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--repo needs a path")
				return gate.Broken
			}
			root = value
		case "--check":
			check = true
		case "--write":
			write = true
		case "--report":
			report = true
		case "--self-test":
			selfTest = true
		case "--min-files":
			value, ok := nextValue(args, &i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = parsed
		case "--help", "-h":
			usage()
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if root == "" {
		found, err := gate.RepoRoot()
		if err != nil {
			fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
			return gate.Broken
		}
		root = found
	}

	switch {
	case write:
		return writeOutput(root)
	case check:
		return checkOutput(root, minFiles)
	case report:
		return reportOutput(root)
	default:
		usage()
		return gate.Broken
	}
}

func usage() {
	fmt.Println("usage: harbor-gates gen-workflows [--check | --write | --report | --self-test]")
	fmt.Println("       [--repo PATH] [--min-files N]")
	fmt.Println()
	fmt.Println("  --check     fail when a committed workflow file is not what the model produces")
	fmt.Println("  --write     regenerate the workflow files from the models")
	fmt.Println("  --report    print the numbers")
	fmt.Println("  --self-test prove that the checker can fail")
	fmt.Println("  --min-files N  floor on the number of generated workflow files")
}

// writeOutput regenerates every workflow file the registry names.
func writeOutput(root string) int {
	for _, entry := range registry {
		text := entry.build().Render()
		path := filepath.Join(root, entry.path)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		fmt.Println("wrote", entry.path)
	}
	return gate.Green
}

// finding is one divergence between a committed file and its model.
type finding struct {
	// path is the file that diverged.
	path string
	// reason says what is wrong.
	reason string
	// line is the first line that differs, or 0 when the whole file is
	// absent or shorter than the model.
	line int
	// want is what the model produced at that line.
	want string
	// got is what the committed file holds at that line.
	got string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	if f.line == 0 {
		return fmt.Sprintf("%s  %s", f.path, f.reason)
	}
	return fmt.Sprintf("%s:%d  %s\n    model:     %s\n    committed: %s",
		f.path, f.line, f.reason, f.want, f.got)
}

// checkTree compares every generated file in the tree with its model.
func checkTree(root string, minFiles int) ([]finding, int) {
	var findings []finding
	if len(registry) < minFiles {
		findings = append(findings, finding{
			path: "tools/gates/gen-workflows",
			reason: fmt.Sprintf("the registry names %d generated workflow file(s), the floor is %d — the check would agree with itself and say nothing",
				len(registry), minFiles),
		})
	}
	checked := 0
	for _, entry := range registry {
		path := filepath.Join(root, entry.path)
		actual, err := os.ReadFile(path)
		if err != nil {
			findings = append(findings, finding{
				path:   entry.path,
				reason: "the file is not in the tree — run --write",
			})
			continue
		}
		checked++
		expected := entry.build().Render()
		if string(actual) == expected {
			continue
		}
		findings = append(findings, firstDifference(entry.path, expected, string(actual)))
	}
	return findings, checked
}

// firstDifference reports the first line at which the model and the
// committed file disagree.
func firstDifference(path, expected, actual string) finding {
	wantLines := strings.Split(expected, "\n")
	gotLines := strings.Split(actual, "\n")
	limit := len(wantLines)
	if len(gotLines) < limit {
		limit = len(gotLines)
	}
	for i := 0; i < limit; i++ {
		if wantLines[i] != gotLines[i] {
			return finding{
				path:   path,
				reason: "the committed file is not what the model produces — it was edited by hand",
				line:   i + 1,
				want:   wantLines[i],
				got:    gotLines[i],
			}
		}
	}
	return finding{
		path:   path,
		reason: fmt.Sprintf("the committed file has %d line(s), the model produces %d", len(gotLines), len(wantLines)),
		line:   limit + 1,
		want:   "",
		got:    "",
	}
}

// checkOutput is the gate the workflows run.
func checkOutput(root string, minFiles int) int {
	findings, checked := checkTree(root, minFiles)
	if len(findings) > 0 {
		fmt.Printf("Generated-workflow gate — findings:\n\n")
		for _, item := range findings {
			fmt.Printf("  %s\n", item)
		}
		fmt.Print()
		fmt.Println("  A workflow file under .github/workflows/ that this gate generates is")
		fmt.Println("  an ARTIFACT. Its source of truth is the Go model next to it, and")
		fmt.Println("  editing the YAML by hand makes the two disagree — which is a red")
		fmt.Println("  build, not a preference.")
		fmt.Print()
		fmt.Println("  Fix: edit the model, then run `harbor-gates gen-workflows --write`.")
		fmt.Print()
		return gate.Red
	}
	fmt.Printf("OK: %d generated workflow file(s) match their models\n", checked)
	return gate.Green
}

// reportOutput prints the numbers.
func reportOutput(root string) int {
	fmt.Printf("generated workflow files: %d\n", len(registry))
	totalLines := 0
	totalJobs := 0
	for _, entry := range registry {
		document := entry.build()
		text := document.Render()
		lines := strings.Count(text, "\n")
		jobs := countJobs(document)
		totalLines += lines
		totalJobs += jobs
		fmt.Printf("  %-40s %5d line(s), %d job(s)\n", entry.path, lines, jobs)
	}
	fmt.Printf("total: %d line(s), %d job(s)\n", totalLines, totalJobs)
	return gate.Green
}

// countJobs counts the job keys of a document: the keys of the root
// mapping's `jobs` mapping.
func countJobs(document *yamlout.Doc) int {
	if document == nil || document.Root == nil {
		return 0
	}
	for _, pair := range document.Root.Pairs {
		if pair.Key != "jobs" || pair.Value == nil || pair.Value.Kind != yamlout.Mapping {
			continue
		}
		return len(pair.Value.Pairs)
	}
	return 0
}

func nextValue(args []string, i *int) (string, bool) {
	if *i+1 >= len(args) {
		return "", false
	}
	*i++
	return args[*i], true
}

func parseInt(text string) (int, bool) {
	if text == "" {
		return 0, false
	}
	value := 0
	for _, r := range text {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	return value, true
}
