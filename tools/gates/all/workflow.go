package all

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// THE HONESTY CHECK
//
// The list of tree gates is a claim: "these gates need nothing but the
// tree". A claim that is never checked is a claim that decays, and the way
// it decays here is visible: somebody adds a gate that reads a TRX to the
// list, the runner runs it, the runner fails because there is no TRX, and
// the one command that was supposed to check the tree stops working.
//
// So the list is checked against the workflows, which are the place where
// the truth about what a gate needs is written down: a workflow passes a
// gate the arguments it needs, and the arguments name the artifacts.
//
// Two directions:
//
//   - a gate of the list that a workflow runs with an artifact (or with the
//     SDK) on the command line is NOT a tree gate, and does not belong in
//     the list;
//   - a gate of the list that no workflow runs at all is a gate nobody
//     reviews, and does not belong in the list either.
//
// The check reads the workflows, not the table in docs/CI_GATES.md: the
// table is a summary of the workflows, and a summary cannot be the proof of
// the summary.
//
// THE RUNNER RUNS THE LIST
//
// A workflow that calls `harbor-gates all` RUNS the gates of the list that
// are compiled into the binary: that is what the runner is for. A gate that
// no workflow calls directly is still run, and the check has to know it —
// otherwise the check calls a gate that runs on every pull request a gate
// nobody runs, and blocks a pull request that changed nothing.
//
// A gate that is still a python script is NOT run by the runner: the binary
// must not need python. It needs a workflow of its own, and the check says
// so.

// runnerGate is the name of the subcommand that runs the list.
const runnerGate = "all"

// artifactTokens name the things a gate needs that the BUILD produces, and
// the SDK itself.
//
// The tokens are exact enough not to catch a flag: "--min-coverage 0.96"
// is a floor and not a coverage directory, and the token for the coverage
// report carries the slash.
var artifactTokens = []string{
	"TestResults",         // the directory the test job writes its results into
	".trx",                // a test result file
	"rt-warnings.log",     // the reference-trimmer findings
	"rt-coverage.txt",     // the reference-trimmer coverage
	"project.assets.json", // the restore's output
	"out/packages",        // the packed NuGet packages
	".nupkg",              // a packed package
	"Coverage/",           // the coverage report
	"dotnet",              // the SDK
}

// The patterns of an invocation in a workflow.
//
// The compiled form: the binary, then the gate's name.
// The python form: python3, then the script.
var (
	compiledInvokeRe = regexp.MustCompile(`tools/bin/harbor-gates-linux-(?:x64|arm64)\s+([A-Za-z0-9_.\-]+)`)
	pythonInvokeRe   = regexp.MustCompile(`python3(?:\s+-[^\s]+)*\s+tools/([A-Za-z0-9_.\-]+)\.py`)
	jobRe            = regexp.MustCompile(`^  ([A-Za-z0-9_.\-]+):\s*$`)
)

// invocation is one gate invocation in one workflow step.
type invocation struct {
	workflow string
	job      string
	gate     string
	args     string
}

// scanWorkflows reads every workflow and returns the gate invocations.
func scanWorkflows(root string) ([]invocation, error) {
	dir := filepath.Join(root, ".github", "workflows")
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, fmt.Errorf("cannot read %s: %w", dir, err)
	}
	var names []string
	for _, entry := range entries {
		name := entry.Name()
		if strings.HasSuffix(name, ".yml") || strings.HasSuffix(name, ".yaml") {
			names = append(names, name)
		}
	}
	sort.Strings(names)
	var all []invocation
	for _, name := range names {
		found, err := scanWorkflow(filepath.Join(dir, name), name)
		if err != nil {
			return nil, err
		}
		all = append(all, found...)
	}
	return all, nil
}

// scanWorkflow reads one workflow file and returns its gate invocations.
func scanWorkflow(path, name string) ([]invocation, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("cannot read %s: %w", path, err)
	}
	lines := strings.Split(string(data), "\n")
	var found []invocation
	job := ""
	for i := 0; i < len(lines); i++ {
		line := lines[i]
		if m := jobRe.FindStringSubmatch(line); m != nil {
			job = m[1]
			continue
		}
		trimmed := strings.TrimSpace(line)
		if !strings.HasPrefix(trimmed, "run:") {
			continue
		}
		indent := len(line) - len(strings.TrimLeft(line, " "))
		command := strings.TrimSpace(strings.TrimPrefix(trimmed, "run:"))
		if isBlockScalar(command) {
			var block []string
			for j := i + 1; j < len(lines); j++ {
				next := lines[j]
				if strings.TrimSpace(next) == "" {
					block = append(block, "")
					continue
				}
				nextIndent := len(next) - len(strings.TrimLeft(next, " "))
				if nextIndent <= indent {
					break
				}
				block = append(block, strings.TrimSpace(next))
				i = j
			}
			command = strings.Join(block, " ")
		}
		found = append(found, parseCommand(name, job, command)...)
	}
	return found, nil
}

// isBlockScalar says whether a `run:` value opens a block of lines.
func isBlockScalar(value string) bool {
	switch value {
	case "", "|", ">", "|-", ">-", "|+", ">+":
		return true
	}
	return false
}

// parseCommand returns the gate invocations of one step's command.
func parseCommand(workflow, job, command string) []invocation {
	type match struct {
		gate string
		end  int
	}
	var matches []match
	for _, m := range compiledInvokeRe.FindAllStringSubmatchIndex(command, -1) {
		matches = append(matches, match{gate: command[m[2]:m[3]], end: m[1]})
	}
	for _, m := range pythonInvokeRe.FindAllStringSubmatchIndex(command, -1) {
		matches = append(matches, match{gate: command[m[2]:m[3]], end: m[1]})
	}
	sort.Slice(matches, func(i, j int) bool { return matches[i].end < matches[j].end })
	var found []invocation
	for index, m := range matches {
		end := len(command)
		if index+1 < len(matches) {
			end = matches[index+1].end
		}
		found = append(found, invocation{
			workflow: workflow,
			job:      job,
			gate:     m.gate,
			args:     strings.TrimSpace(command[m.end:end]),
		})
	}
	return found
}

// checkList checks the list of tree gates against the workflows.
//
// The findings are sentences a reader can act on; an empty list means the
// list is honest.
func checkList(root string) []string {
	var findings []string

	// The list is sorted and free of duplicates: the report's order is the
	// list's order, and a list that is not sorted makes two runs differ
	// for a reason that has nothing to do with the tree.
	seen := map[string]bool{}
	previous := ""
	for _, g := range treeGates {
		if seen[g.name] {
			findings = append(findings, fmt.Sprintf("%s is in the list twice", g.name))
		}
		seen[g.name] = true
		if previous != "" && g.name < previous {
			findings = append(findings, fmt.Sprintf("the list is not sorted: %s comes after %s", g.name, previous))
		}
		previous = g.name
	}

	invs, err := scanWorkflows(root)
	if err != nil {
		return append(findings, fmt.Sprintf("cannot read the workflows: %v", err))
	}

	return append(findings, checkInvocations(root, treeGates, invs)...)
}

// checkInvocations checks the gates of the list against the invocations.
//
// The two directions are the two ways the list starts to lie: a gate that
// needs what the build produces, and a gate nobody runs.
func checkInvocations(root string, gates []treeGate, invs []invocation) []string {
	var findings []string
	covered := runnerCovers(root, gates, invs)
	for _, g := range gates {
		used := false
		for _, inv := range invs {
			if inv.gate != g.name {
				continue
			}
			used = true
			for _, token := range artifactTokens {
				if strings.Contains(inv.args, token) {
					findings = append(findings, fmt.Sprintf(
						"%s is in the list of tree gates, and %s · %s runs it with %q on the command line: that is what the build produces, so the gate is not a tree gate",
						g.name, inv.workflow, inv.job, token))
				}
			}
		}
		if !used && covered[g.name] {
			used = true
		}
		if !used {
			findings = append(findings, fmt.Sprintf(
				"%s is in the list of tree gates and no workflow runs it: a gate nobody runs is a gate nobody reviews",
				g.name))
		}
	}
	return findings
}

// runnerCovers returns the gates of the list that the tree runner runs.
//
// The runner runs a gate that is compiled into the binary, and it runs it
// in every workflow that calls the runner. A gate that is still a python
// script is not one of them: the binary must not need python, and the
// local runner (tools/check-local.sh) runs those with python3.
func runnerCovers(root string, gates []treeGate, invs []invocation) map[string]bool {
	covered := map[string]bool{}
	called := false
	for _, inv := range invs {
		if inv.gate == runnerGate && !strings.Contains(inv.args, "--self-test") {
			called = true
			break
		}
	}
	if !called {
		return covered
	}
	for _, g := range gates {
		state, _ := resolve(root, g)
		if state == stateCompiled {
			covered[g.name] = true
		}
	}
	return covered
}
