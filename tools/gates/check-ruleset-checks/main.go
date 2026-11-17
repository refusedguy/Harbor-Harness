// Package checkrulesetchecks is the gate that keeps the branch-protection
// rulesets from demanding a status check that no workflow job publishes.
//
// # WHY THIS GATE EXISTS
//
// A GitHub ruleset can require a status check whose context is a name that no
// job in any workflow ever produces. GitHub does not warn about it. The rule
// simply waits for a check that never arrives, so EVERY pull request into the
// branch is blocked forever, and nothing in the tree says why.
//
// It happened here. PR #2159 removed the jobs `guarded project dependencies`
// and `xml-doc coverage` from the workflows, while the ruleset still required
// those two names. The rule had to be repaired by hand through the API right
// after the merge, or the queue would have stopped. Nothing in CI noticed the
// drift: no gate read `.github/rulesets/**` at all.
//
// # WHAT IT READS
//
//	.github/rulesets/*.json   the required status-check contexts (the
//	                          `context` field of each entry in a
//	                          `required_status_checks` rule)
//	.github/workflows/*.yml   the check-run name every job publishes. GitHub
//	                          uses the job's `name:` when it has one, and its
//	                          id otherwise, so the reader collects both.
//
// A context with no matching job name is the defect. The message names both
// the ruleset and the context, because the reader has to go and fix the rule.
//
// # WHY A COMPILED GATE
//
// The gate has to run where there is no SDK, no build of Harbor and no
// python: the same place the other gates run. It is a gate ABOUT the tree
// (workflow and ruleset files), so it reads them with a file API, and it is
// written in Go because the binary is committed to the tree.
//
// The exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package checkrulesetchecks

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The files the gate reads, relative to the repository root.
const (
	rulesetDir  = ".github/rulesets"
	workflowDir = ".github/workflows"
)

// requiredCheck is one status-check context a ruleset demands.
type requiredCheck struct {
	ruleset string
	context string
}

// jobName is the check-run name one workflow job publishes.
type jobName struct {
	workflow string
	id       string
	name     string // the explicit `name:`, empty when the job has none
	line     int
}

// display is the name GitHub shows for the job: `name:` when it is set, the
// job id otherwise.
func (j jobName) display() string {
	if j.name != "" {
		return j.name
	}
	return j.id
}

// finding is one required check with no job to satisfy it.
type finding struct {
	code    string
	ruleset string
	context string
	msg     string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	return fmt.Sprintf("%s [%s] %s", f.ruleset, f.code, f.msg)
}

// The on-disk shape of a ruleset. Only the required-status-check rule carries
// the contexts this gate cares about; the other rules (deletion,
// non_fast_forward, pull_request) decode to zero values and are ignored.
type rulesetDocument struct {
	Name  string        `json:"name"`
	Rules []rulesetRule `json:"rules"`
}

type rulesetRule struct {
	Type       string            `json:"type"`
	Parameters rulesetParameters `json:"parameters"`
}

type rulesetParameters struct {
	RequiredStatusChecks []statusCheck `json:"required_status_checks"`
}

type statusCheck struct {
	Context string `json:"context"`
}

// A job id: two spaces, an id, a colon, end of line. A job display name: the
// `name:` key at four spaces. A block scalar start: `|`/`>` with optional
// indentation and chomping indicators.
var (
	jobIDRe       = regexp.MustCompile(`^([A-Za-z0-9_][A-Za-z0-9_.-]*):\s*$`)
	jobNameRe     = regexp.MustCompile(`^name:\s*(.+)$`)
	blockScalarRe = regexp.MustCompile(`:\s*[|>][0-9]*[-+]?\s*$`)
)

// parseRuleset returns the required status-check contexts of one ruleset
// document. The file name travels with each context so a finding can name
// the rule that must be repaired.
func parseRuleset(file string, data []byte) ([]requiredCheck, error) {
	var doc rulesetDocument
	if err := json.Unmarshal(data, &doc); err != nil {
		return nil, fmt.Errorf("%s: %w", file, err)
	}
	var out []requiredCheck
	for _, rule := range doc.Rules {
		for _, check := range rule.Parameters.RequiredStatusChecks {
			context := strings.TrimSpace(check.Context)
			if context == "" {
				continue
			}
			out = append(out, requiredCheck{ruleset: file, context: context})
		}
	}
	return out, nil
}

// readRulesets reads every ruleset file and returns its required contexts
// together with the number of files it read.
func readRulesets(root string) ([]requiredCheck, int, error) {
	dir := filepath.Join(root, rulesetDir)
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, 0, fmt.Errorf("cannot read %s: %w", dir, err)
	}
	var out []requiredCheck
	files := 0
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, entry.Name()))
		if err != nil {
			return nil, files, fmt.Errorf("cannot read %s: %w", entry.Name(), err)
		}
		reqs, err := parseRuleset(entry.Name(), data)
		if err != nil {
			return nil, files, err
		}
		out = append(out, reqs...)
		files++
	}
	return out, files, nil
}

// parseWorkflowJobs returns the jobs of one workflow file.
//
// It reads the block structure by indentation, not YAML: a job id is a key at
// two spaces under `jobs:`, and its display name is a `name:` key at four
// spaces. The body of a block scalar (`run: |`) is content, not structure, so
// it is skipped; a line of shell that happens to read `name: foo` is not a
// job name.
func parseWorkflowJobs(workflow string, text string) []jobName {
	var out []jobName
	inJobs := false
	current := -1
	blockIndent := -1

	for i, raw := range strings.Split(text, "\n") {
		lineNo := i + 1
		raw = strings.TrimSuffix(raw, "\r")
		trimmed := strings.TrimSpace(raw)
		if trimmed == "" || strings.HasPrefix(trimmed, "#") {
			continue
		}
		indent := len(raw) - len(strings.TrimLeft(raw, " "))

		if blockIndent >= 0 && indent > blockIndent {
			continue
		}
		blockIndent = -1

		if indent == 0 {
			inJobs = trimmed == "jobs:"
			if !inJobs {
				current = -1
			}
			continue
		}
		if !inJobs {
			continue
		}

		if indent == 2 {
			if match := jobIDRe.FindStringSubmatch(trimmed); match != nil {
				out = append(out, jobName{workflow: workflow, id: match[1], line: lineNo})
				current = len(out) - 1
			}
		}
		if indent == 4 && current >= 0 {
			if match := jobNameRe.FindStringSubmatch(trimmed); match != nil && out[current].name == "" {
				out[current].name = cleanName(match[1])
			}
		}
		if blockScalarRe.MatchString(trimmed) {
			blockIndent = indent
		}
	}
	return out
}

// cleanName strips the quotes a job name may be written with.
func cleanName(value string) string {
	value = strings.TrimSpace(value)
	if len(value) >= 2 && ((value[0] == '"' && value[len(value)-1] == '"') ||
		(value[0] == '\'' && value[len(value)-1] == '\'')) {
		value = strings.TrimSpace(value[1 : len(value)-1])
	}
	return value
}

// readWorkflows reads every workflow file and returns all of its job names.
func readWorkflows(root string) ([]jobName, int, error) {
	dir := filepath.Join(root, workflowDir)
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, 0, fmt.Errorf("cannot read %s: %w", dir, err)
	}
	var out []jobName
	files := 0
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || (!strings.HasSuffix(name, ".yml") && !strings.HasSuffix(name, ".yaml")) {
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			return nil, files, fmt.Errorf("cannot read %s: %w", name, err)
		}
		out = append(out, parseWorkflowJobs(name, string(data))...)
		files++
	}
	return out, files, nil
}

// audit returns one finding for each required context that no job publishes.
// The comparison is case-insensitive: a check name that differs only in case
// still names the same check in GitHub, and a false positive here would block
// every pull request for no reason.
func audit(required []requiredCheck, jobs []jobName) []finding {
	names := map[string]bool{}
	for _, job := range jobs {
		name := strings.ToLower(strings.TrimSpace(job.display()))
		if name != "" {
			names[name] = true
		}
	}

	var out []finding
	for _, req := range required {
		key := strings.ToLower(strings.TrimSpace(req.context))
		if key == "" || names[key] {
			continue
		}
		out = append(out, finding{
			code:    "RULESET-CHECK-NO-JOB",
			ruleset: req.ruleset,
			context: req.context,
			msg: fmt.Sprintf("requires status check %q, and no workflow job publishes that name — every pull request into this branch is blocked with no job able to satisfy the rule",
				req.context),
		})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].ruleset != out[j].ruleset {
			return out[i].ruleset < out[j].ruleset
		}
		return out[i].context < out[j].context
	})
	return out
}

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	minRulesets, minChecks := 0, 0
	selfTest := false
	report := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--root":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--root needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			root = value
		case "--min-rulesets":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-rulesets needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-rulesets needs a number")
				return gate.Broken
			}
			minRulesets = parsed
		case "--min-checks":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-checks needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-checks needs a number")
				return gate.Broken
			}
			minChecks = parsed
		case "--self-test", "--selftest":
			selfTest = true
		case "--report":
			report = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-ruleset-checks [--root DIR] [--min-rulesets N] [--min-checks N] [--report] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
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

	required, rulesetFiles, err := readRulesets(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "RULESETS-UNREADABLE:", err)
		return gate.Broken
	}
	jobs, workflowFiles, err := readWorkflows(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "WORKFLOWS-UNREADABLE:", err)
		return gate.Broken
	}
	findings := audit(required, jobs)

	fmt.Printf("ruleset files read: %d; required status checks: %d; workflow files read: %d; jobs: %d\n",
		rulesetFiles, len(required), workflowFiles, len(jobs))

	if report {
		fmt.Println("\nrequired checks:")
		for _, req := range required {
			fmt.Printf("  %s: %s\n", req.ruleset, req.context)
		}
		fmt.Println("\njob names:")
		for _, job := range jobs {
			fmt.Printf("  %s · %s (line %d)\n", job.workflow, job.display(), job.line)
		}
	}

	if len(findings) > 0 {
		fmt.Println("\nRuleset gate — findings:")
		for _, item := range findings {
			fmt.Printf("  %s\n", item)
		}
	}

	problems := len(findings)
	if minRulesets > 0 && rulesetFiles < minRulesets {
		fmt.Printf("\nRULESET-FLOOR-RULESETS: read %d ruleset files, floor is %d — a scan that saw fewer rulesets cannot say the tree is clean\n",
			rulesetFiles, minRulesets)
		problems++
	}
	if minChecks > 0 && len(required) < minChecks {
		fmt.Printf("\nRULESET-FLOOR-CHECKS: read %d required checks, floor is %d — a scan that matched fewer checks cannot say the tree is clean\n",
			len(required), minChecks)
		problems++
	}

	if problems > 0 {
		fmt.Printf("\nFAIL: %d problem(s)\n", problems)
		return gate.Red
	}
	fmt.Println("\nOK: every required status check has a workflow job able to publish it.")
	return gate.Green
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// parseInt reads a decimal integer.
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
