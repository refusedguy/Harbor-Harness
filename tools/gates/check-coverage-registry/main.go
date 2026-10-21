// Package checkcoverageregistry is the compiled port of
// tools/check-coverage-registry.py.
//
// It compares the registry of consciously uncovered files with the
// measurement, in BOTH directions: a file that stopped being covered and no
// row names it, and a row that is no longer needed because the file is
// covered now.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checkcoverageregistry

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

// finding is one disagreement between the registry and the measurement.
type finding struct {
	Code    string `json:"code"`
	Message string `json:"message"`
	Where   string `json:"where"`
}

// String is the finding the way the python original printed it.
func (f finding) String() string {
	head := f.Code + ": " + f.Message
	if f.Where == "" {
		return head
	}
	return head + " (" + f.Where + ")"
}

// defaultRegistry is the registry of consciously uncovered files.
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

// Run is the gate's entry point.
func Run(args []string) int {
	coverageDir := "Coverage"
	registry := ""
	sourceRoot := "."
	minFiles := 0
	minCovered := 0
	statusPath := ""
	strict := false
	asJSON := false
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
		case "--registry":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --registry needs a path")
				return gate.Broken
			}
			registry = args[index]
		case "--source-root":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --source-root needs a path")
				return gate.Broken
			}
			sourceRoot = args[index]
		case "--status":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --status needs a path")
				return gate.Broken
			}
			statusPath = args[index]
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
		case "--strict":
			strict = true
		case "--json":
			asJSON = true
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
	if _, err := os.Stat(registry); err != nil {
		fmt.Fprintf(os.Stderr, "error: no registry: %s\n", registry)
		return gate.Broken
	}

	model := cov.LoadCoverage(coverageDir, sourceRoot)
	rows, err := cov.ParseRegistry(registry)
	if err != nil {
		fmt.Fprintf(os.Stderr, "error: %v\n", err)
		return gate.Red
	}
	model.Registry = rows

	status := map[string]string{}
	if statusPath != "" {
		status = readSuiteStatus(statusPath)
	}
	var failed []string
	for name, state := range status {
		if state != "pass" {
			failed = append(failed, name)
		}
	}
	sort.Strings(failed)
	partial := len(failed) > 0 || len(model.Unreadable) > 0

	findings := evaluate(model, minFiles, minCovered, partial)

	if partial && strict {
		fmt.Fprintf(os.Stderr,
			"error: the measurement is partial — %d suite(s) did not pass (%s), "+
				"so the registry cannot be compared\n",
			len(failed), strings.Join(failed, ", "))
		return gate.Broken
	}

	if asJSON {
		payload := jsonPayload{
			Suites:            emptyIfNil(model.Suites),
			UnreadableReports: emptyIfNil(model.Unreadable),
			Partial:           partial,
			FailedSuites:      emptyIfNil(failed),
			ProductFiles:      len(model.ProductFiles()),
			CoveredFiles:      len(model.CoveredProductFiles()),
			UncoveredFiles:    len(model.Holes()),
			AcceptedFiles:     len(model.AcceptedFiles()),
			UnacceptedFiles:   len(model.UnacceptedFiles()),
			RegistryRows:      len(model.Registry),
			Findings:          emptyFindings(findings),
		}
		data, err := pycompat.JSONIndent(payload)
		if err != nil {
			fmt.Fprintf(os.Stderr, "error: %v\n", err)
			return gate.Red
		}
		fmt.Println(string(data))
	} else {
		for _, item := range findings {
			fmt.Println(item.String())
		}
		fmt.Printf("product files: %d   covered: %d   uncovered: %d   in the registry: %d   registry rows: %d\n",
			len(model.ProductFiles()), len(model.CoveredProductFiles()),
			len(model.Holes()), len(model.AcceptedFiles()), len(model.Registry))
		if partial {
			fmt.Printf("note: the measurement is PARTIAL — %d suite(s) did not pass (%s), %d report(s) could not be read (%s), so the uncovered files were NOT compared against the registry; the stale rows were\n",
				len(failed), strings.Join(failed, ", "),
				len(model.Unreadable), strings.Join(model.Unreadable, ", "))
		}
		fmt.Printf("%d finding(s)\n", len(findings))
	}

	if len(findings) > 0 {
		return gate.Red
	}
	return gate.Green
}

// jsonPayload is the machine-readable verdict, in the order the python
// original wrote it.
type jsonPayload struct {
	Suites            []string  `json:"suites"`
	UnreadableReports []string  `json:"unreadable_reports"`
	Partial           bool      `json:"partial"`
	FailedSuites      []string  `json:"failed_suites"`
	ProductFiles      int       `json:"product_files"`
	CoveredFiles      int       `json:"covered_files"`
	UncoveredFiles    int       `json:"uncovered_files"`
	AcceptedFiles     int       `json:"accepted_files"`
	UnacceptedFiles   int       `json:"unaccepted_files"`
	RegistryRows      int       `json:"registry_rows"`
	Findings          []finding `json:"findings"`
}

// evaluate compares the registry with the measurement.
//
// The findings are the whole answer: an empty list means the two agree.
// `partial` says the measurement is incomplete (a suite did not pass), so
// the direction that a partial measurement cannot support — the uncovered
// files — is left out.
func evaluate(model *cov.Model, minFiles int, minCovered int, partial bool) []finding {
	var findings []finding
	product := model.ProductFiles()
	covered := model.CoveredProductFiles()

	// 1. The measurement itself.
	if len(model.Suites) == 0 {
		findings = append(findings, finding{
			Code: "COV-DATA-EMPTY",
			Message: "the coverage directory holds no report at all, so there is " +
				"nothing to compare the registry against — an empty measurement " +
				"is not a clean tree",
			Where: "coverage",
		})
	} else if len(model.Files) == 0 {
		findings = append(findings, finding{
			Code: "COV-DATA-EMPTY",
			Message: fmt.Sprintf("the %d report(s) name no source file at all, so "+
				"the measurement is empty — an empty measurement is not a clean tree",
				len(model.Suites)),
			Where: "coverage",
		})
	} else if len(covered) == 0 {
		findings = append(findings, finding{
			Code: "COV-DATA-EMPTY",
			Message: "no product file has a single covered line, so the measurement " +
				"reached nothing — an empty measurement is not a clean tree",
			Where: "coverage",
		})
	}

	// 2. The files that are not covered and nobody accepted.
	for _, path := range model.UnacceptedFiles() {
		if partial {
			// The measurement is incomplete: a suite that died reports
			// nothing, and the files only it covers look uncovered. Naming
			// them would be a finding about a flake, not about the tree.
			continue
		}
		what := "no suite in this lane measured it"
		if model.StatusOf(path) == "never-ran" {
			what = "the measurement saw it and not one line ran"
		}
		findings = append(findings, finding{
			Code: "COV-UNCOVERED",
			Message: "the file is not covered (" + what + ") and no registry row names it — " +
				"add a row with a reason, or cover the file",
			Where: path,
		})
	}

	// 3. The rows that are not needed any more.
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
			message := "the directory row covers no uncovered file any more"
			if !isDir(row.Path) {
				message += " — and the directory is not in the tree"
			}
			findings = append(findings, finding{
				Code:    "COV-ROW-STALE",
				Message: message,
				Where:   row.Path,
			})
		} else {
			if !model.IsCovered(row.Path) {
				continue
			}
			findings = append(findings, finding{
				Code: "COV-ROW-STALE",
				Message: "the file is covered now, so the row has outlived its reason " +
					"and has to go",
				Where: row.Path,
			})
		}
	}

	// 4. The rows that name a path the tree does not have.
	for _, row := range model.Registry {
		if row.IsDirectory() {
			continue
		}
		found := false
		for _, path := range product {
			if path == row.Path {
				found = true
				break
			}
		}
		if !found {
			findings = append(findings, finding{
				Code: "COV-ROW-MISSING",
				Message: "the row names a file the product tree does not have — the " +
					"file was deleted or moved, so the row has to go",
				Where: row.Path,
			})
		}
	}

	// 5. The floors: a gate that scanned nothing must not say "clean".
	if len(product) < minFiles {
		findings = append(findings, finding{
			Code:    "COV-FLOOR",
			Message: fmt.Sprintf("the product tree has %d file(s), the floor is %d", len(product), minFiles),
			Where:   "product tree",
		})
	}
	if len(covered) < minCovered {
		findings = append(findings, finding{
			Code:    "COV-FLOOR",
			Message: fmt.Sprintf("%d product file(s) are covered, the floor is %d", len(covered), minCovered),
			Where:   "measurement",
		})
	}
	return findings
}

// readSuiteStatus reads the per-suite outcome the collect step writes:
// `name<TAB>status`.
func readSuiteStatus(path string) map[string]string {
	status := map[string]string{}
	if path == "" {
		return status
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return status
	}
	for _, raw := range strings.Split(string(data), "\n") {
		line := strings.TrimSuffix(raw, "\n")
		name, state, _ := strings.Cut(line, "\t")
		if strings.TrimSpace(name) == "" {
			continue
		}
		status[strings.TrimSpace(name)] = strings.TrimSpace(state)
	}
	return status
}

// isDir reports whether the path is a directory.
func isDir(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

// emptyIfNil is python's `[]` for an empty list.
func emptyIfNil(values []string) []string {
	if values == nil {
		return []string{}
	}
	return values
}

// emptyFindings is python's `[]` for an empty list of findings.
func emptyFindings(values []finding) []finding {
	if values == nil {
		return []finding{}
	}
	return values
}
