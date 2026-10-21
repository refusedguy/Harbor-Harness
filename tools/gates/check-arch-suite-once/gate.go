// Package archsuiteonce is the compiled port of tools/check-arch-suite-once.py.
//
// The gate counts the architecture suite's executions on the pull-request
// lane: on a pull request the suite must run EXACTLY ONCE, and zero
// executions is a finding too.
//
// It reads two files as text — .github/workflows/ci.yml and
// Directory.Build.props — and asks five questions:
//
//	EXEC-ONCE      the suite is executed exactly once
//	GATE-OFF-PR    every job that compiles the solution switches the
//	               build-time gate off for pull_request events
//	GATE-ON-MERGE  at least one of those jobs still runs the gate for push
//	GATE-DEFAULT   Directory.Build.props defaults the gate to true
//	GATE-CONDITION the target's own condition still asks the property
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package archsuiteonce

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

const (
	// suite is the project the count is about.
	suite = "Harbor.Architecture.Tests"
	// gateProperty is the MSBuild property that runs the suite inside a
	// build.
	gateProperty = "HarborArchGate"
	// flagOff is the switch that turns the build-time gate off on the
	// command line.
	flagOff = "-p:HarborArchGate=false"
	// defaultOn is the default the gate carries when nothing says otherwise.
	defaultOn = `<HarborArchGate Condition="'$(HarborArchGate)' == ''">true</HarborArchGate>`
	// gateCondition is the clause in the target's condition that asks the
	// property before it runs.
	gateCondition = `'$(HarborArchGate)' == 'true'`
)

// blockMarkers are the block markers a `run:` key may carry instead of an
// inline command.
var blockMarkers = map[string]bool{
	"|": true, ">": true, "|-": true, ">-": true, "|+": true, ">+": true,
}

// A workflow job id: two spaces, an id, a colon.
var jobRe = regexp.MustCompile(`^  ([A-Za-z0-9_.\-]+):\s*$`)

// A `run:` key, with or without the list dash in front of it.
var runRe = regexp.MustCompile(`^(\s*)(?:-\s+)?run:\s*(.*)$`)

// An `env:` key of a job.
var envRe = regexp.MustCompile(`^    env:\s*$`)

// An entry of a job's `env:` block.
var envEntryRe = regexp.MustCompile(`^      ([A-Za-z0-9_]+):\s*(.*?)\s*$`)

// The shard matrix of the `test` job. The terminator is CAPTURED rather
// than looked ahead at, because Go's regexp engine is RE2 and RE2 has no
// lookahead: the capture is cut off again right after the match.
var shardRe = regexp.MustCompile(`(?s)- shard: (\S+)\n(.*?)(\n[\t\n\v\f\r ]+- shard: |\n[\t\n\v\f\r ]+steps:)`)

// A literal value of the gate property.
var literalRe = regexp.MustCompile(`^('([A-Za-z]+)'|"([A-Za-z]+)")$`)

// The three parts of the GitHub expression the workflows use.
var expressionRe = regexp.MustCompile(`^(.+?)\s*&&\s*(.+?)\s*\|\|\s*(.+)$`)

// The event comparison inside that expression.
var eventRe = regexp.MustCompile(`^github\.event_name\s*==\s*'([A-Za-z_]+)'$`)

// The word `dotnet`, as a word.
var dotnetRe = regexp.MustCompile(`\bdotnet\b`)

// Run is the gate entry point.
func Run(args []string) int {
	workflow := ""
	props := ""
	minShards := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--workflow":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--workflow needs a path")
				return gate.Broken
			}
			i++
			workflow = args[i]
		case "--props":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--props needs a path")
				return gate.Broken
			}
			i++
			props = args[i]
		case "--min-shards":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-shards needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-shards needs a number")
				return gate.Broken
			}
			minShards = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-arch-suite-once [--workflow PATH] [--props PATH] [--min-shards N] [--self-test]")
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
	if workflow == "" {
		workflow = filepath.Join(root, ".github", "workflows", "ci.yml")
	}
	if props == "" {
		props = filepath.Join(root, "Directory.Build.props")
	}

	workflowText, err := gate.ReadText(workflow)
	if err != nil {
		fmt.Printf("::error::workflow not found: %s\n", workflow)
		return gate.Broken
	}
	propsText, err := gate.ReadText(props)
	if err != nil {
		fmt.Printf("::error::props file not found: %s\n", props)
		return gate.Broken
	}

	findings := evaluate(workflowText, propsText, minShards)
	if len(findings) > 0 {
		fmt.Println("::error::the pull-request lane does not run the architecture suite once:")
		for _, finding := range findings {
			fmt.Printf("  - %s\n", finding)
		}
		return gate.Red
	}

	shards := shardLists(workflowText)
	var carriers []string
	for _, name := range sortedShardNames(shards) {
		if contains(shards[name], suite) {
			carriers = append(carriers, name)
		}
	}
	carrier := "?"
	if len(carriers) > 0 {
		carrier = carriers[0]
	}
	fmt.Printf("ok: %d shards, the architecture suite runs once - in the `%s` shard\n",
		len(shards), carrier)
	return gate.Green
}

// parseInt reads a non-negative integer.
func parseInt(s string) (int, bool) {
	if s == "" {
		return 0, false
	}
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, false
		}
		n = n*10 + int(r-'0')
	}
	return n, true
}

// splitJobs returns the workflow's jobs, as job id -> the job's own text.
func splitJobs(text string) map[string]string {
	jobs := map[string]string{}
	inJobs := false
	name := ""
	var body []string
	lines := splitLines(text)
	for _, line := range lines {
		if line != "" && !isSpace(line[0]) {
			inJobs = strings.TrimSpace(line) == "jobs:"
			if name != "" {
				jobs[name] = strings.Join(body, "\n")
				name = ""
				body = nil
			}
			continue
		}
		if !inJobs {
			continue
		}
		if m := jobRe.FindStringSubmatch(line); m != nil {
			if name != "" {
				jobs[name] = strings.Join(body, "\n")
			}
			name = m[1]
			body = nil
			continue
		}
		if name != "" {
			body = append(body, line)
		}
	}
	if name != "" {
		jobs[name] = strings.Join(body, "\n")
	}
	return jobs
}

// jobEnv returns the job's own `env:` block, as name -> value.
func jobEnv(jobText string) map[string]string {
	lines := splitLines(jobText)
	for index, line := range lines {
		if !envRe.MatchString(line) {
			continue
		}
		values := map[string]string{}
		for _, entry := range lines[index+1:] {
			if strings.TrimSpace(entry) != "" && !strings.HasPrefix(entry, "     ") {
				break
			}
			if m := envEntryRe.FindStringSubmatch(entry); m != nil {
				values[m[1]] = m[2]
			}
		}
		return values
	}
	return map[string]string{}
}

// runBlocks returns the text of every `run:` block in the job.
func runBlocks(jobText string) []string {
	var blocks []string
	lines := splitLines(jobText)
	index := 0
	for index < len(lines) {
		m := runRe.FindStringSubmatch(lines[index])
		if m == nil {
			index++
			continue
		}
		indent := len(m[1])
		rest := strings.TrimSpace(m[2])
		var body []string
		index++
		if rest != "" && !blockMarkers[rest] {
			body = append(body, rest)
		} else {
			for index < len(lines) {
				entry := lines[index]
				if strings.TrimSpace(entry) != "" && len(entry)-len(strings.TrimLeft(entry, " \t")) <= indent {
					break
				}
				body = append(body, entry)
				index++
			}
		}
		blocks = append(blocks, strings.Join(body, "\n"))
	}
	return blocks
}

// shardLists returns the `test` job's shard matrix: shard name -> the
// projects it runs.
//
// The terminator is CAPTURED rather than looked ahead at, because Go's
// regexp engine is RE2 and RE2 has no lookahead. The capture is cut off
// again right after the match: the next search starts where the terminator
// starts, not where the match ended, so the "- shard: " the terminator
// carries is still available to the next match. Consuming it would drop
// every second shard.
func shardLists(text string) map[string][]string {
	shards := map[string][]string{}
	pos := 0
	for pos <= len(text) {
		loc := shardRe.FindStringSubmatchIndex(text[pos:])
		if loc == nil {
			break
		}
		shard := text[pos+loc[2] : pos+loc[3]]
		block := text[pos+loc[4] : pos+loc[5]]
		terminator := pos + loc[6]

		lines := splitLines(block)
		start := -1
		for i, ln := range lines {
			if strings.HasPrefix(strings.TrimSpace(ln), "projects:") {
				start = i
				break
			}
		}
		if start >= 0 {
			var projects []string
			for _, line := range lines[start+1:] {
				if strings.TrimSpace(line) != "" && strings.HasPrefix(line, strings.Repeat(" ", 10)) {
					projects = append(projects, strings.TrimSpace(line))
				} else if len(projects) > 0 {
					break
				}
			}
			if len(projects) > 0 {
				shards[shard] = projects
			}
		}
		pos = terminator
	}
	return shards
}

// sortedShardNames returns the shard names, sorted.
func sortedShardNames(shards map[string][]string) []string {
	out := make([]string, 0, len(shards))
	for name := range shards {
		out = append(out, name)
	}
	sort.Strings(out)
	return out
}

// compilesTheSolution reports whether the command compiles the SOLUTION
// (and therefore the suite).
func compilesTheSolution(block string) bool {
	if strings.Contains(block, "dotnet build") && strings.Contains(block, "Harbor.slnx") {
		return true
	}
	return strings.Contains(block, "build/build.sh") && strings.Contains(block, "Compile")
}

// gateValue returns the value of a `HarborArchGate` expression for one
// event, or "" when the gate cannot tell.
//
// The shape the workflows use is the GitHub expression
//
//	${{ github.event_name == 'pull_request' && 'false' || 'true' }}
//
// which is read here as "the condition, the value when it holds, the value
// when it does not". A literal is a literal. Anything else is unknown: the
// gate cannot tell, and a gate that cannot tell treats that as a finding
// rather than as a pass.
func gateValue(expression string, event string) string {
	text := strings.TrimSpace(expression)
	if strings.HasPrefix(text, "${{") && strings.HasSuffix(text, "}}") {
		text = strings.TrimSpace(text[3 : len(text)-2])
	}
	if m := literalRe.FindStringSubmatch(text); m != nil {
		if m[2] != "" {
			return m[2]
		}
		return m[3]
	}
	m := expressionRe.FindStringSubmatch(text)
	if m == nil {
		return ""
	}
	condition := strings.TrimSpace(m[1])
	whenTrue := strings.TrimSpace(m[2])
	whenFalse := strings.TrimSpace(m[3])
	e := eventRe.FindStringSubmatch(condition)
	if e == nil {
		return ""
	}
	chosen := whenFalse
	if e[1] == event {
		chosen = whenTrue
	}
	return strings.Trim(chosen, "'\"")
}

// runsTheSuite reports whether the `run:` block RUNS the architecture
// suite.
//
// NAMING the suite is not running it. The guard-triage reporter (#1726)
// passes the project's name to another tool — `--project
// Harbor.Architecture.Tests` — so the name is in a `run:` block on a lane
// where the suite runs exactly once. Counting that as an execution made
// this gate red on a tree that was right.
//
// So a block counts when it names the suite AND invokes dotnet.
func runsTheSuite(block string) bool {
	if !strings.Contains(block, suite) {
		return false
	}
	return dotnetRe.MatchString(block)
}

// checkOnce is EXEC-ONCE: the suite is executed exactly once.
func checkOnce(workflow string, shards map[string][]string) []string {
	var executions []string
	for _, shard := range sortedShardNames(shards) {
		if contains(shards[shard], suite) {
			executions = append(executions, fmt.Sprintf("the `%s` shard's project list", shard))
		}
	}
	jobs := splitJobs(workflow)
	for _, job := range sortedKeys(jobs) {
		for _, block := range runBlocks(jobs[job]) {
			if runsTheSuite(block) {
				executions = append(executions, fmt.Sprintf("the `%s` job's command", job))
			}
		}
	}
	if len(executions) == 1 {
		return nil
	}
	if len(executions) == 0 {
		return []string{
			fmt.Sprintf("EXEC-ONCE: the pull-request lane runs `%s` NOWHERE - the "+
				"architecture gate would not run at all on a pull request", suite),
		}
	}
	return []string{
		fmt.Sprintf("EXEC-ONCE: the pull-request lane runs `%s` %d times - it must "+
			"run exactly once: %s", suite, len(executions), strings.Join(executions, "; ")),
	}
}

// checkGate is GATE-OFF-PR and GATE-ON-MERGE: the gate's placement.
func checkGate(workflow string) []string {
	var problems []string
	var compiling []string
	jobs := splitJobs(workflow)
	for _, job := range sortedKeys(jobs) {
		blocks := runBlocks(jobs[job])
		any := false
		for _, block := range blocks {
			if compilesTheSolution(block) {
				any = true
				break
			}
		}
		if any {
			compiling = append(compiling, job)
		}
	}

	if len(compiling) == 0 {
		return []string{
			"GATE-OFF-PR: no job in this workflow compiles the solution - the " +
				"checker cannot see the build-time gate it guards",
		}
	}

	for _, job := range compiling {
		blocks := runBlocks(jobs[job])
		if anyContains(blocks, flagOff) {
			// The command line says so, for every event. That is a job that
			// never runs the gate (the reference-trimmer job), which is
			// allowed: the build job is the one that has to keep it.
			continue
		}
		value, ok := jobEnv(jobs[job])[gateProperty]
		if !ok {
			problems = append(problems, fmt.Sprintf(
				"GATE-OFF-PR: the `%s` job compiles the solution and says nothing "+
					"about `%s` - the suite would run inside that build as well as "+
					"in the shard", job, gateProperty))
			continue
		}
		onPullRequest := gateValue(value, "pull_request")
		switch onPullRequest {
		case "false":
			continue
		case "true":
			problems = append(problems, fmt.Sprintf(
				"GATE-OFF-PR: the `%s` job's `%s` is `%s` - the suite runs inside "+
					"that build AND in the shard", job, gateProperty, value))
		default:
			problems = append(problems, fmt.Sprintf(
				"GATE-OFF-PR: the `%s` job's `%s` is `%s`, and this checker cannot "+
					"tell what it yields for a pull_request event - expected "+
					"`${{ github.event_name == 'pull_request' && 'false' || 'true' }}",
				job, gateProperty, value))
		}
	}

	// The merge path keeps the gate.
	keepsIt := false
	for _, job := range compiling {
		blocks := runBlocks(jobs[job])
		if anyContains(blocks, flagOff) {
			continue
		}
		value, ok := jobEnv(jobs[job])[gateProperty]
		if !ok {
			keepsIt = true
			break
		}
		if gateValue(value, "push") == "true" {
			keepsIt = true
			break
		}
	}
	if !keepsIt {
		problems = append(problems,
			"GATE-ON-MERGE: no job in this workflow runs the architecture gate "+
				"for a push event - a merge into dev/master would no longer fail "+
				"the BUILD on a layering regression")
	}
	return problems
}

// checkDefault is GATE-DEFAULT: the gate is on unless a workflow says
// otherwise.
func checkDefault(props string) []string {
	if strings.Contains(props, defaultOn) {
		return nil
	}
	return []string{
		fmt.Sprintf("GATE-DEFAULT: `Directory.Build.props` does not default `%s` "+
			"to `true` - the gate is off everywhere unless every job asks for it",
			gateProperty),
	}
}

// checkCondition is GATE-CONDITION: the target still asks the property
// before it runs.
func checkCondition(props string) []string {
	if !strings.Contains(props, "HarborArchitectureGate") {
		return []string{
			"GATE-CONDITION: `Directory.Build.props` has no `HarborArchitectureGate` " +
				"target - the architecture gate does not exist",
		}
	}
	if !strings.Contains(props, gateCondition) {
		return []string{
			"GATE-CONDITION: the `HarborArchitectureGate` target's condition no " +
				"longer requires `HarborArchGate` to be `true` - it would run inside " +
				"every Release build, whatever the workflow says",
		}
	}
	return nil
}

// evaluate returns every finding the tree has, in the order the rules are
// stated.
func evaluate(workflow string, props string, minShards int) []string {
	shards := shardLists(workflow)
	var problems []string
	if len(shards) < minShards {
		problems = append(problems, fmt.Sprintf(
			"the workflow declares %d shard(s), the floor is %d - the checker "+
				"cannot see what it guards", len(shards), minShards))
		return problems
	}
	problems = append(problems, checkOnce(workflow, shards)...)
	problems = append(problems, checkGate(workflow)...)
	problems = append(problems, checkDefault(props)...)
	problems = append(problems, checkCondition(props)...)
	return problems
}

// contains reports whether the list holds the value.
func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// anyContains reports whether one of the blocks holds the text.
func anyContains(blocks []string, text string) bool {
	for _, block := range blocks {
		if strings.Contains(block, text) {
			return true
		}
	}
	return false
}

// sortedKeys returns the map's keys, sorted.
func sortedKeys(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// splitLines splits text into lines the way python's `str.splitlines()`
// does, for the LF-only text this gate reads.
//
// The difference that matters: python's splitlines() also splits on \r,
// \v, \f, U+2028 and U+2029, and returns a trailing empty line only for a
// text that ends with a newline... Actually python's splitlines() does NOT
// return a trailing empty string for a text ending in "\n" (unlike
// str.split("\n")). This helper reproduces that.
func splitLines(text string) []string {
	if text == "" {
		return []string{}
	}
	var out []string
	start := 0
	for i := 0; i < len(text); i++ {
		switch text[i] {
		case '\n':
			out = append(out, text[start:i])
			start = i + 1
		case '\r':
			out = append(out, text[start:i])
			if i+1 < len(text) && text[i+1] == '\n' {
				i++
			}
			start = i + 1
		}
	}
	if start < len(text) {
		out = append(out, text[start:])
	}
	return out
}

// isSpace reports whether the byte is whitespace, the way python's
// `str.isspace()` decides for the ASCII range.
func isSpace(b byte) bool {
	switch b {
	case ' ', '\t', '\n', '\v', '\f', '\r':
		return true
	}
	return false
}
