// Package benchset is the gate that keeps the benchmark's gate set and the
// CI's gate set from drifting apart.
//
// # WHAT IT CHECKS
//
// The repository measures its own gates: `bench/gates/measure.sh` times the
// set, and `docs/BENCHMARKS.md` quotes what the set costs. A number that
// names a set nobody runs is a number about a different repository, and the
// way it goes wrong is silent: a gate is added to a workflow, the file that
// lists the measured gates is not touched, and the section keeps claiming a
// cost for a set that is smaller than the one a pull request pays.
//
// This gate is the instrument that keeps the instrument honest. It reads
// three sources and fails when they disagree:
//
//	.github/workflows/*.yml   what CI really runs, and with which arguments
//	bench/gates/tools.tsv    the set the benchmark measures
//	bench/gates/excluded.tsv the gates CI runs that the set does NOT measure,
//	                          each with the reason it is out
//
// THE RULES
//
//	BENCH-SET-GATE-MISSING   a gate CI runs is in neither file
//	BENCH-SET-GATE-STALE     a row names a gate no workflow runs
//	BENCH-SET-NO-REASON      an excluded gate has no reason
//	BENCH-SET-README-SILENT  an excluded gate is not named in the README
//	BENCH-SET-ARGS-DIVERGE   the measured row does not pass the arguments CI
//	                         passes, so the benchmark times a different gate
//	BENCH-SET-COMMAND-DEAD   the measured row runs a file that is not in the
//	                         tree
//	BENCH-SET-ROW-SHAPE      a row is not what the format says
//
// # WHY A COMPILED GATE
//
// The gate has to run where there is no SDK, no build of Harbor and no
// python: the same place the other gates run. It is a gate ABOUT the
// workflow files, so it reads them with a file API, and it is written in Go
// because the binary is committed to the tree.
//
// See bench/gates/README.md for what the set is, and docs/CI_GATES.md for
// the policy every gate of this repository obeys.
package benchset

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The files the gate reads, relative to the repository root.
const (
	toolsFile    = "bench/gates/tools.tsv"
	excludedFile = "bench/gates/excluded.tsv"
	readmeFile   = "bench/gates/README.md"
)

// finding is one disagreement between the workflows and the set.
type finding struct {
	code  string
	msg   string
	where string
}

// Run is the gate entry point.
func Run(args []string) int {
	minGates := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-gates":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-gates needs a number")
				return gate.Broken
			}
			i++
			n := 0
			for _, r := range args[i] {
				if r < '0' || r > '9' {
					fmt.Fprintln(os.Stderr, "--min-gates needs a number")
					return gate.Broken
				}
				n = n*10 + int(r-'0')
			}
			minGates = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-gate-bench-set [--min-gates N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	findings, measured, ran := check(root)

	fmt.Printf("the workflows run %d gate(s); the measured set holds %d row(s)\n",
		ran, measured)

	if len(findings) > 0 {
		fmt.Println()
		for _, f := range findings {
			fmt.Printf("%s [%s] %s\n", f.where, f.code, f.msg)
		}
	}

	problems := len(findings)
	if minGates > 0 && measured < minGates {
		fmt.Printf("\nBENCH-SET-BLIND: the measured set holds %d rows, floor is %d — the set cannot shrink without a decision\n",
			measured, minGates)
		problems++
	}

	if problems > 0 {
		fmt.Printf("\nFAIL: %d problem(s)\n", problems)
		return gate.Red
	}
	fmt.Println("\nOK: the measured set is the set CI runs.")
	return gate.Green
}

// check compares the workflows with the two files, and returns the findings,
// the number of measured rows and the number of gates the workflows run.
func check(root string) ([]finding, int, int) {
	var findings []finding

	invocations, err := readWorkflows(root)
	if err != nil {
		return []finding{{
			code:  "BENCH-SET-WORKFLOWS-UNREADABLE",
			msg:   err.Error(),
			where: ".github/workflows",
		}}, 0, 0
	}

	measured, err := readMeasured(root)
	if err != nil {
		return []finding{{
			code:  "BENCH-SET-ROW-SHAPE",
			msg:   err.Error(),
			where: toolsFile,
		}}, 0, 0
	}

	excluded, err := readExcluded(root)
	if err != nil {
		return []finding{{
			code:  "BENCH-SET-ROW-SHAPE",
			msg:   err.Error(),
			where: excludedFile,
		}}, 0, 0
	}

	// The gates the workflows run, and the arguments each of them is run
	// with. A self-test invocation is not a run of the gate: it proves the
	// gate can fail, and it costs nothing like the gate.
	invoked := map[string][]invocation{}
	ran := map[string][]invocation{}
	for _, inv := range invocations {
		invoked[inv.name] = append(invoked[inv.name], inv)
		if inv.selfTest() {
			continue
		}
		ran[inv.name] = append(ran[inv.name], inv)
	}

	// Every gate CI runs is measured, or excluded with a reason.
	measuredNames := map[string]bool{}
	for _, row := range measured.rows {
		measuredNames[row.name] = true
	}
	excludedNames := map[string]bool{}
	for _, row := range excluded.rows {
		excludedNames[row.name] = true
	}
	for name := range invoked {
		if measuredNames[name] || excludedNames[name] {
			continue
		}
		how := "CI runs it"
		if len(ran[name]) == 0 {
			how = "the only thing CI does with it is run its self-test"
		}
		findings = append(findings, finding{
			code: "BENCH-SET-GATE-MISSING",
			msg: fmt.Sprintf("%s, and it is in neither %s nor %s — the measured set is not the set a pull request pays",
				how, toolsFile, excludedFile),
			where: name,
		})
	}

	// Every row names a gate CI runs, and runs it the way CI runs it.
	for _, row := range measured.rows {
		if _, ok := invoked[row.name]; !ok {
			findings = append(findings, finding{
				code: "BENCH-SET-GATE-STALE",
				msg: fmt.Sprintf("%s is in the measured set, and no workflow runs it — the set names a gate this repository does not have",
					row.name),
				where: toolsFile,
			})
			continue
		}
		if len(ran[row.name]) == 0 {
			// CI runs it only as a self-test. That is not a run of the gate,
			// and a row that claims to measure it is measuring nothing.
			findings = append(findings, finding{
				code: "BENCH-SET-GATE-STALE",
				msg: fmt.Sprintf("%s is in the measured set, and the only thing CI does with it is run its self-test",
					row.name),
				where: toolsFile,
			})
			continue
		}
		findings = append(findings, argsFindings(row, ran[row.name])...)
		findings = append(findings, commandFindings(root, row)...)
	}

	// Every excluded gate says why it is out, and the README says it too.
	readme, err := os.ReadFile(filepath.Join(root, readmeFile))
	if err != nil {
		findings = append(findings, finding{
			code:  "BENCH-SET-README-MISSING",
			msg:   fmt.Sprintf("%s is not in the tree, and it is where the exclusions are written down", readmeFile),
			where: readmeFile,
		})
	}
	for _, row := range excluded.rows {
		if _, ok := invoked[row.name]; !ok {
			findings = append(findings, finding{
				code: "BENCH-SET-GATE-STALE",
				msg: fmt.Sprintf("%s is excluded from the measured set, and no workflow runs it — the exclusion list is a list of gates that are not gates",
					row.name),
				where: excludedFile,
			})
			continue
		}
		if strings.TrimSpace(row.reason) == "" {
			findings = append(findings, finding{
				code:  "BENCH-SET-NO-REASON",
				msg:   fmt.Sprintf("%s is excluded from the measured set and carries no reason", row.name),
				where: excludedFile,
			})
		}
		if len(readme) > 0 && !strings.Contains(string(readme), row.name) {
			findings = append(findings, finding{
				code: "BENCH-SET-README-SILENT",
				msg: fmt.Sprintf("%s is excluded from the measured set, and %s does not name it — a reason that is only in a data file is a reason nobody reads",
					row.name, readmeFile),
				where: readmeFile,
			})
		}
	}

	sort.Slice(findings, func(i, j int) bool {
		if findings[i].code != findings[j].code {
			return findings[i].code < findings[j].code
		}
		return findings[i].where < findings[j].where
	})
	return findings, len(measured.rows), len(ran)
}

// argsFindings compares the arguments of a measured row with the arguments
// CI passes.
//
// The comparison is over the FLAGS, not over the values: the value of an
// artifact path is the harness's business (an empty directory of the right
// shape), and the flag is the gate's business. A row that drops a flag CI
// passes measures a weaker gate; a row that adds a flag CI does not pass
// measures a gate that does not exist.
func argsFindings(row measuredRow, invs []invocation) []finding {
	got := flagsOf(row.command)
	for _, inv := range invs {
		if sameFlags(got, flagsOf(inv.args)) {
			return nil
		}
	}
	var expected []string
	for _, inv := range invs {
		expected = append(expected, fmt.Sprintf("%s: %s", inv.place(), strings.Join(flagsOf(inv.args), " ")))
	}
	return []finding{{
		code: "BENCH-SET-ARGS-DIVERGE",
		msg: fmt.Sprintf("the measured row passes [%s], and CI passes [%s] — the benchmark times a gate CI does not run",
			strings.Join(got, " "), strings.Join(expected, "] [")),
		where: toolsFile,
	}}
}

// commandFindings checks that the row runs a file that is in the tree.
func commandFindings(root string, row measuredRow) []finding {
	fields := strings.Fields(row.command)
	if len(fields) == 0 {
		return []finding{{
			code:  "BENCH-SET-COMMAND-DEAD",
			msg:   fmt.Sprintf("the row for %s carries no command", row.name),
			where: toolsFile,
		}}
	}
	// The python gates: `python3 tools/<name>.py ...`.
	if strings.HasPrefix(fields[0], "python3") {
		for _, f := range fields[1:] {
			if !strings.HasSuffix(f, ".py") {
				continue
			}
			if _, err := os.Stat(filepath.Join(root, f)); err != nil {
				return []finding{{
					code:  "BENCH-SET-COMMAND-DEAD",
					msg:   fmt.Sprintf("the row for %s runs %s, which is not in the tree", row.name, f),
					where: toolsFile,
				}}
			}
			return nil
		}
		return []finding{{
			code:  "BENCH-SET-COMMAND-DEAD",
			msg:   fmt.Sprintf("the row for %s names no script to run", row.name),
			where: toolsFile,
		}}
	}
	// The compiled gates: `tools/bin/harbor-gates-linux-x64 <gate> ...`.
	if len(fields) >= 2 && strings.HasPrefix(fields[0], "tools/bin/harbor-gates-linux-") {
		if _, err := os.Stat(filepath.Join(root, fields[0])); err != nil {
			return []finding{{
				code:  "BENCH-SET-COMMAND-DEAD",
				msg:   fmt.Sprintf("the row for %s runs %s, which is not in the tree", row.name, fields[0]),
				where: toolsFile,
			}}
		}
		gate := fields[1]
		if _, err := os.Stat(filepath.Join(root, "tools", "gates", gate)); err != nil {
			return []finding{{
				code:  "BENCH-SET-COMMAND-DEAD",
				msg:   fmt.Sprintf("the row for %s runs the gate %s, and tools/gates/%s is not in the tree", row.name, gate, gate),
				where: toolsFile,
			}}
		}
		return nil
	}
	return []finding{{
		code: "BENCH-SET-COMMAND-DEAD",
		msg: fmt.Sprintf("the row for %s runs %q, which is neither a python gate nor a compiled one",
			row.name, fields[0]),
		where: toolsFile,
	}}
}

// sameFlags compares two flag lists as multisets: the order does not matter,
// the count does.
func sameFlags(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	count := map[string]int{}
	for _, f := range a {
		count[f]++
	}
	for _, f := range b {
		count[f]--
		if count[f] < 0 {
			return false
		}
	}
	return true
}
