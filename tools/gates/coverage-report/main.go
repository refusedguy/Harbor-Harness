// Package coveragereport is the compiled port of tools/coverage_report.py.
//
// It writes the per-file coverage report of a run: the per-project table,
// the files that never ran a line, the files in projects the lane does not
// load, and the full per-file table.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
//
// The model it reads is in `internal/coverage`: the python original was one
// module that was both the model and the reporter, and the second coverage
// gate imported it. The port keeps the same split.
package coveragereport

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/cov"
	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

// The default registry: the rows that say which product files are
// consciously not covered.
//
// The python original resolved it from its own path, so the tool worked from
// any working directory. The binary is committed at tools/bin/, so the
// registry is one directory up from the sources and two from the binary.
func defaultRegistry() string {
	root, err := gate.BinaryRoot()
	if err != nil {
		return filepath.Join("tools", "coverage-registry.txt")
	}
	return filepath.Join(root, "tools", "coverage-registry.txt")
}

// Run is the reporter's entry point.
func Run(args []string) int {
	coverageDir := "Coverage"
	sourceRoot := "."
	registry := ""
	summary := ""
	out := ""
	uncoveredOut := ""
	jsonOut := ""
	printMissing := false
	printStale := false
	minFiles := 0
	minCovered := 0
	selfTest := false

	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--coverage-dir":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --coverage-dir needs a path")
				return gate.Broken
			}
			coverageDir = args[index]
		case "--source-root":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --source-root needs a path")
				return gate.Broken
			}
			sourceRoot = args[index]
		case "--registry":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --registry needs a path")
				return gate.Broken
			}
			registry = args[index]
		case "--summary":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --summary needs a path")
				return gate.Broken
			}
			summary = args[index]
		case "--out":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --out needs a path")
				return gate.Broken
			}
			out = args[index]
		case "--uncovered-out":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --uncovered-out needs a path")
				return gate.Broken
			}
			uncoveredOut = args[index]
		case "--json":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --json needs a path")
				return gate.Broken
			}
			jsonOut = args[index]
		case "--print-missing":
			printMissing = true
		case "--print-stale":
			printStale = true
		case "--min-files":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --min-files needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --min-files needs a number: %s\n", args[index])
				return gate.Broken
			}
			minFiles = value
		case "--min-covered":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --min-covered needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --min-covered needs a number: %s\n", args[index])
				return gate.Broken
			}
			minCovered = value
		case "--self-test":
			selfTest = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if registry == "" {
		registry = defaultRegistry()
	}

	if info, err := os.Stat(coverageDir); err != nil || !info.IsDir() {
		fmt.Fprintf(os.Stderr, "error: no coverage directory: %s\n", coverageDir)
		return gate.Broken
	}

	model := cov.LoadCoverage(coverageDir, sourceRoot)
	rows, err := cov.ParseRegistry(registry)
	if err != nil {
		// The python original printed the message and let the exception
		// end the run with exit code 1.
		fmt.Fprintf(os.Stderr, "error: %v\n", err)
		return gate.Red
	}
	model.Registry = rows

	product := model.ProductFiles()
	covered := model.CoveredProductFiles()
	if len(product) < minFiles {
		fmt.Fprintf(os.Stderr, "error: the product tree has %d file(s), the floor is %d\n",
			len(product), minFiles)
		return gate.Red
	}
	if len(covered) < minCovered {
		fmt.Fprintf(os.Stderr, "error: %d product file(s) are covered, the floor is %d\n",
			len(covered), minCovered)
		return gate.Red
	}
	if len(product) == 0 {
		// The python original divided by the number of product files while
		// it rendered the report, and a tree with no product files ended
		// the run with a traceback and exit code 1. The gate's floor says
		// the same thing in one line.
		fmt.Fprintln(os.Stderr, "error: the product tree has no file at all, so there is nothing to report")
		return gate.Red
	}

	report := cov.RenderReport(model, 25)
	if summary != "" {
		if err := appendTo(summary, report+"\n"); err != nil {
			fmt.Fprintf(os.Stderr, "error: %v\n", err)
			return gate.Red
		}
	} else {
		fmt.Println(report)
	}
	if out != "" {
		if err := os.WriteFile(out, []byte(cov.RenderFileTable(model)), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "error: %v\n", err)
			return gate.Red
		}
	}
	if uncoveredOut != "" {
		if err := os.WriteFile(uncoveredOut, []byte(cov.RenderUncoveredLocations(model)), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "error: %v\n", err)
			return gate.Red
		}
	}
	if jsonOut != "" {
		if err := writeJSON(jsonOut, model); err != nil {
			fmt.Fprintf(os.Stderr, "error: %v\n", err)
			return gate.Red
		}
	}

	if printMissing {
		fmt.Println()
		fmt.Println("# the rows the registry still needs (the reason is yours to write)")
		for _, path := range model.UnacceptedFiles() {
			kind := "NOT-MEASURED"
			if model.StatusOf(path) == "never-ran" {
				kind = "NEVER-RAN"
			}
			fmt.Printf("%s | %s | <why is this hole allowed to stay?> | @refusedguy\n", path, kind)
		}
	}
	if printStale {
		fmt.Println()
		fmt.Println("# the rows the registry no longer needs (the file is covered now)")
		for _, row := range model.Registry {
			if row.IsDirectory() {
				still := false
				for _, path := range model.Holes() {
					if strings.HasPrefix(path, row.Path) {
						still = true
						break
					}
				}
				if still {
					continue
				}
				fmt.Printf("%s | %s | <the directory needs no row any more> | %s\n",
					row.Path, row.Kind, row.Owner)
				continue
			}
			if !model.IsCovered(row.Path) {
				continue
			}
			fmt.Printf("%s | %s | <the file is covered now> | %s\n", row.Path, row.Kind, row.Owner)
		}
	}

	fmt.Printf("coverage: %d product file(s), %d covered, %d uncovered (%d in the registry), %d suite(s)\n",
		len(product), len(covered), len(model.Holes()),
		len(model.AcceptedFiles()), len(model.Suites))
	return gate.Green
}

// appendTo appends to a file, creating it when it is not there.
func appendTo(path string, text string) error {
	handle, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer handle.Close()
	_, err = handle.WriteString(text)
	return err
}

// jsonFile is one file's entry in the machine-readable model.
type jsonFile struct {
	Covered        int      `json:"covered"`
	Total          int      `json:"total"`
	Suites         []string `json:"suites"`
	UncoveredLines []int    `json:"uncovered_lines"`
}

// jsonPayload is the model as the python original wrote it: the fields in
// the order it wrote them.
type jsonPayload struct {
	Suites         []string             `json:"suites"`
	ProductFiles   int                  `json:"product_files"`
	CoveredFiles   int                  `json:"covered_files"`
	UncoveredFiles int                  `json:"uncovered_files"`
	AcceptedFiles  int                  `json:"accepted_files"`
	UnacceptedFile int                  `json:"unaccepted_files"`
	UnmappedPaths  []string             `json:"unmapped_paths"`
	Files          map[string]jsonFile `json:"files"`
}

// writeJSON writes the model the way python's json.dump wrote it.
func writeJSON(path string, model *cov.Model) error {
	payload := jsonPayload{
		Suites:         emptyIfNil(model.Suites),
		ProductFiles:   len(model.ProductFiles()),
		CoveredFiles:   len(model.CoveredProductFiles()),
		UncoveredFiles: len(model.Holes()),
		AcceptedFiles:  len(model.AcceptedFiles()),
		UnacceptedFile: len(model.UnacceptedFiles()),
		UnmappedPaths:  emptyIfNil(model.Unmapped),
		Files:          map[string]jsonFile{},
	}
	for path, entry := range model.Files {
		suites := make([]string, 0, len(entry.Suites))
		for suite := range entry.Suites {
			suites = append(suites, suite)
		}
		sort.Strings(suites)
		payload.Files[path] = jsonFile{
			Covered:        entry.Covered(),
			Total:          entry.Total(),
			Suites:         suites,
			UncoveredLines: emptyInts(entry.UncoveredLines()),
		}
	}
	data, err := pycompat.JSONIndent(payload)
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o644)
}

// emptyIfNil is python's `[]` for an empty list.
func emptyIfNil(values []string) []string {
	if values == nil {
		return []string{}
	}
	return values
}

// emptyInts is python's `[]` for an empty list.
func emptyInts(values []int) []int {
	if values == nil {
		return []int{}
	}
	return values
}
