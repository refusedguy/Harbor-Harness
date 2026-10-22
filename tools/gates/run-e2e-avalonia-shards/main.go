// Package rune2eshards is the compiled port of
// tools/run-e2e-avalonia-shards.py.
//
// It runs the headless Avalonia E2E suite as N processes, one per shard, and
// checks three things loudly instead of quietly: that every shard found
// tests, that every class of the plan ran in the shard that names it, and
// that the totals match the assembly's own discovery.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package rune2eshards

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
	"harbor.local/gates/internal/xmltree"
)

// trxNamespace is the TRX schema's namespace.
const trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

// defaultPlan is the shard plan of the repository.
var defaultPlan = filepath.Join("tests", "Harbor.E2E.App.Avalonia", "e2e-shards.json")

// The upstream defect's signature, as it appears in a shard's output.
// Matching it is what turns "shard-1 failed" into "shard-1 was POISONED by
// Avalonia#22021, and here is the upstream issue".
var poisonSignatures = []string{
	"EnsureSharedApplication",
	"EnsureIsolatedApplication",
	"The calling thread cannot access this object because a different thread owns it",
}

const poisonReference = "AvaloniaUI/Avalonia#22021 - headless `HeadlessUnitTestSession.Dispatch` " +
	"intermittently throws a cross-thread `VerifyAccess` in " +
	"`EnsureSharedApplication`, poisoning the process; upstream states the only " +
	"recovery is a new process"

// discoveryTimeout is how long the discovery pass may take.
const discoveryTimeout = 300 * time.Second

// Run is the runner's entry point.
func Run(args []string) int {
	dll := ""
	resultsDir := ""
	planPath := ""
	guard := ""
	summary := os.Getenv("GITHUB_STEP_SUMMARY")
	selfTest := false

	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--dll":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --dll needs a path")
				return gate.Broken
			}
			dll = args[index]
		case "--results-dir":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --results-dir needs a path")
				return gate.Broken
			}
			resultsDir = args[index]
		case "--plan":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --plan needs a path")
				return gate.Broken
			}
			planPath = args[index]
		case "--guard":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --guard needs arguments")
				return gate.Broken
			}
			guard = args[index]
		case "--summary":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --summary needs a path")
				return gate.Broken
			}
			summary = args[index]
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

	if dll == "" {
		fmt.Fprintln(os.Stderr, "run-e2e-avalonia-shards.py: the following arguments are required: --dll")
		return gate.Broken
	}
	if resultsDir == "" {
		fmt.Fprintln(os.Stderr, "run-e2e-avalonia-shards.py: the following arguments are required: --results-dir")
		return gate.Broken
	}

	root, err := repoRoot(".")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	planFile := planPath
	if planFile == "" {
		planFile = filepath.Join(root, defaultPlan)
	}
	if !filepath.IsAbs(planFile) {
		planFile = filepath.Join(root, planFile)
	}

	if info, err := os.Stat(dll); err != nil || info.IsDir() {
		fmt.Fprintf(os.Stderr, "run-e2e-avalonia-shards.py: no assembly at %s\n", dll)
		return gate.Broken
	}
	if info, err := os.Stat(planFile); err != nil || info.IsDir() {
		fmt.Fprintf(os.Stderr, "run-e2e-avalonia-shards.py: no plan at %s\n", planFile)
		return gate.Broken
	}

	plan, err := loadPlan(planFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "run-e2e-avalonia-shards.py: %v\n", err)
		return gate.Broken
	}

	var guardArgs []string
	if guard != "" {
		guardArgs = strings.Fields(guard)
	}
	// The results directory must be ABSOLUTE: the test platform resolves a
	// relative --results-directory against the ASSEMBLY's directory, not the
	// working directory, so a relative path puts the TRX where the caller
	// cannot find it (and where the CI's upload glob cannot see it).
	absoluteResults, err := filepath.Abs(resultsDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "run-e2e-avalonia-shards.py: %v\n", err)
		return gate.Red
	}
	if err := os.MkdirAll(absoluteResults, 0o755); err != nil {
		fmt.Fprintf(os.Stderr, "run-e2e-avalonia-shards.py: %v\n", err)
		return gate.Red
	}

	discoveredCounts, discoveredTotal, discoveryNote := runDiscovery(dll)
	if discoveryNote != "" {
		fmt.Printf("::warning::%s\n", discoveryNote)
	}

	shards := planShards(plan)
	results := runShards(shards, dll, absoluteResults, guardArgs)

	totalTests := 0
	totalFailed := 0
	for _, result := range results {
		totalTests += result.Tests
		totalFailed += result.Failed
	}

	// ── The guards ────────────────────────────────────────────────────────
	var problems []string

	for _, result := range results {
		if result.ExitCode != 0 {
			problems = append(problems, fmt.Sprintf(
				"shard %s exited %d (%d failed, %d test(s)); classes: %s; log: %s",
				result.ID, result.ExitCode, result.Failed, result.Tests,
				strings.Join(result.Classes, ", "), result.Log))
		}
		if result.Poisoned {
			problems = append(problems, fmt.Sprintf(
				"shard %s was POISONED by the upstream headless dispatcher defect - %s. "+
					"Its classes (%s) failed with it; the other shards were not cancelled. Log: %s",
				result.ID, poisonReference, strings.Join(result.Classes, ", "), result.Log))
		}
	}

	if discoveryNote != "" {
		// The exact check is unavailable; fall back to the plan's measured
		// total as a FLOOR so a dropped shard is still caught.
		floor := int(planTotalsMeasuredTests(plan))
		if floor != 0 && totalTests < floor {
			problems = append(problems, fmt.Sprintf(
				"the shards ran %d test(s) but the plan measured %d - at least one class "+
					"was DROPPED. Note: %s", totalTests, floor, discoveryNote))
		}
	} else {
		if totalTests != discoveredTotal {
			problems = append(problems, fmt.Sprintf(
				"the shards ran %d test(s) but the assembly discovers %d - at least one "+
					"class was DROPPED", totalTests, discoveredTotal))
		}
		for _, result := range results {
			for _, name := range result.Classes {
				expected, ok := discoveredCounts[name]
				if !ok {
					problems = append(problems, fmt.Sprintf(
						"class %s is in shard %s but the assembly discovers no test for it",
						name, result.ID))
					continue
				}
				actual := result.Counts[name]
				if actual != expected {
					problems = append(problems, fmt.Sprintf(
						"class %s ran %d test(s) in shard %s but the assembly discovers %d - "+
							"the shard's filter does not match what it claims",
						name, actual, result.ID, expected))
				}
			}
		}
	}

	// ── The report ────────────────────────────────────────────────────────
	lines := []string{
		"| shard | classes | tests | failed | skipped | seconds | result |",
		"|---|---|--:|--:|--:|--:|---|",
	}
	criticalPath := 0.0
	for _, result := range results {
		status := "pass"
		if result.ExitCode != 0 {
			status = "FAIL"
		}
		if result.Poisoned {
			status += " (poisoned - AvaloniaUI/Avalonia#22021)"
		}
		lines = append(lines, fmt.Sprintf("| %s | %d | %d | %d | %d | %.1f | %s |",
			result.ID, len(result.Classes), result.Tests, result.Failed,
			result.NotExecuted, result.Seconds, status))
		if result.Seconds > criticalPath {
			criticalPath = result.Seconds
		}
	}
	totalClasses := 0
	totalNotExecuted := 0
	for _, result := range results {
		totalClasses += len(result.Classes)
		totalNotExecuted += result.NotExecuted
	}
	lines = append(lines, fmt.Sprintf("| **total** | %d | %d | %d | %d | %.1f (critical path) | |",
		totalClasses, totalTests, totalFailed, totalNotExecuted, criticalPath))
	if discoveredTotal != 0 {
		lines = append(lines, "")
		lines = append(lines, fmt.Sprintf(
			"Discovered by the assembly: **%d** test(s); run across %d process(es). %s",
			discoveredTotal, len(results),
			discoveryNoteText(discoveryNote)))
	} else {
		lines = append(lines, "")
		lines = append(lines, fmt.Sprintf("Discovery pass unavailable: %s", discoveryNote))
	}

	report := strings.Join(lines, "\n")
	fmt.Println(report)
	if summary != "" {
		if err := appendTo(summary, report+"\n\n"); err != nil {
			fmt.Fprintf(os.Stderr, "run-e2e-avalonia-shards.py: cannot write the summary (%v)\n", err)
		}
	}

	if len(problems) > 0 {
		fmt.Println("")
		for _, problem := range problems {
			fmt.Printf("::error::%s\n", problem)
		}
		// The shards' own output goes to their log files, not to the job log,
		// so a failure would otherwise be invisible here. Print the whole log
		// of every shard that failed: the CI log is where a human looks first,
		// and the artifact is where they look second.
		for _, result := range results {
			if result.ExitCode == 0 {
				continue
			}
			fmt.Println("")
			fmt.Printf("::group::%s output (%s)\n", result.ID, result.Log)
			data, err := os.ReadFile(result.Log)
			if err != nil {
				fmt.Printf("(cannot read %s: %v)\n", result.Log, err)
			} else {
				// python's print() adds the newline the file does not
				// have, and the group block is read by a human.
				fmt.Println(string(data))
			}
			fmt.Println("::endgroup::")
		}
		return gate.Red
	}
	return gate.Green
}

// discoveryNoteText is the sentence the report carries after the counts.
func discoveryNoteText(note string) string {
	if note == "" {
		return "The per-class and total counts match."
	}
	return note
}

// shardResult is what one shard did.
type shardResult struct {
	ID          string
	Classes     []string
	Filter      string
	ExitCode    int
	Seconds     float64
	Tests       int
	Failed      int
	NotExecuted int
	Counts      map[string]int
	Trx         string
	Log         string
	Poisoned    bool
}

// runShards runs the shards, one process each, and keeps the order of the
// plan.
//
// The python original used a thread pool with one worker per shard and
// `pool.map`, which keeps the order of the input; the report reads in that
// order, so the order is part of the output.
func runShards(shards []shard, dll string, resultsDir string, guard []string) []shardResult {
	results := make([]shardResult, len(shards))
	var group sync.WaitGroup
	for index := range shards {
		group.Add(1)
		go func(index int) {
			defer group.Done()
			results[index] = runShard(shards[index], dll, resultsDir, guard)
		}(index)
	}
	group.Wait()
	return results
}

// runShard runs one shard in its own process and collects what it did.
func runShard(shard shard, dll string, resultsDir string, guard []string) shardResult {
	shardDir := filepath.Join(resultsDir, shard.ID)
	screenshotDir := filepath.Join(shardDir, "screenshots")
	os.MkdirAll(shardDir, 0o755)
	os.MkdirAll(screenshotDir, 0o755)

	expression := filterFor(shard.Classes)
	command := []string{
		"dotnet", "exec", dll,
		"--treenode-filter", expression,
	}
	command = append(command, guard...)
	command = append(command,
		// The same zero-test policy the `test` job gives every other project:
		// a shard that discovered nothing must fail, not pass.
		"--zero-tests-policy", "strict",
		"--report-trx",
		"--results-directory", shardDir,
		"--report-trx-filename", shard.ID+".trx",
	)

	environment := os.Environ()
	// Each shard writes its screenshots into its OWN directory. Two processes
	// sharing one directory is a race: `AvaloniaUiTests` deletes the stale
	// `??-*.png` files at init, so one shard's init would delete the frames
	// another shard had just written.
	environment = append(environment,
		"HARBOR_E2E_SHARD_DIR="+screenshotDir,
		"HARBOR_E2E_SHARD_ID="+shard.ID,
	)

	logPath := filepath.Join(shardDir, "shard.log")
	started := time.Now()
	var output bytes.Buffer
	command2 := exec.Command(command[0], command[1:]...)
	command2.Env = environment
	command2.Stdout = &output
	command2.Stderr = &output
	exitCode := 0
	if err := command2.Run(); err != nil {
		if exitError, ok := err.(*exec.ExitError); ok {
			exitCode = exitError.ExitCode()
		} else {
			exitCode = 1
		}
	}
	elapsed := time.Since(started).Seconds()

	log, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return shardResult{
			ID: shard.ID, Classes: shard.Classes, Filter: expression,
			ExitCode: exitCode, Seconds: elapsed, Counts: map[string]int{},
			Log: logPath, Trx: "",
		}
	}
	fmt.Fprintf(log, "# %s\n# filter: %s\n", shard.ID, expression)
	log.Write(output.Bytes())
	log.Close()

	trxPath := filepath.Join(shardDir, shard.ID+".trx")
	counts, total, failed, notExecuted := readTrx(trxPath)

	text := output.String()
	poisoned := false
	for _, signature := range poisonSignatures {
		if strings.Contains(text, signature) {
			poisoned = true
			break
		}
	}

	trx := ""
	if info, err := os.Stat(trxPath); err == nil && !info.IsDir() {
		trx = trxPath
	}

	return shardResult{
		ID:          shard.ID,
		Classes:     shard.Classes,
		Filter:      expression,
		ExitCode:    exitCode,
		Seconds:     elapsed,
		Tests:       total,
		Failed:      failed,
		NotExecuted: notExecuted,
		Counts:      counts,
		Trx:         trx,
		Log:         logPath,
		Poisoned:    poisoned,
	}
}

// filterFor is the `--treenode-filter` expression that selects exactly these
// classes.
//
// The shape is `/*/*/(ClassA)|(ClassB)/*`: the assembly and namespace
// segments are wildcards (the classes live in two namespaces - this one and
// `ComponentTests`), the class segment is the OR of the requested names, and
// the test segment is a wildcard.
func filterFor(classNames []string) string {
	if len(classNames) == 0 {
		return ""
	}
	return "/*/*/(" + strings.Join(classNames, ")|(") + ")/*"
}

// shard is one shard of the plan.
type shard struct {
	ID      string
	Classes []string
}

// loadPlan reads the plan and checks that it is a plan this runner can run.
func loadPlan(path string) (map[string]any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var document map[string]any
	if err := json.Unmarshal(data, &document); err != nil {
		return nil, err
	}
	version, ok := document["schemaVersion"]
	if !ok || !isOne(version) {
		return nil, fmt.Errorf("%s: unsupported schemaVersion %s", path, jsonRepr(version))
	}
	shards, _ := document["shards"].([]any)
	if len(shards) == 0 {
		return nil, fmt.Errorf("%s: no shards", path)
	}
	seen := map[string]string{}
	for _, entry := range shards {
		item, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		id, _ := item["id"].(string)
		if id == "" {
			return nil, fmt.Errorf("%s: a shard has no id", path)
		}
		classes := stringList(item["classes"])
		if len(classes) == 0 {
			return nil, fmt.Errorf("%s: shard %s names no classes", path, id)
		}
		for _, name := range classes {
			if previous, seenAlready := seen[name]; seenAlready {
				return nil, fmt.Errorf("%s: class %s is in both %s and %s",
					path, name, previous, id)
			}
			seen[name] = id
		}
	}
	return document, nil
}

// isOne reports whether the JSON value is the number 1, the way python's
// `!= 1` does: 1.0 is 1.
func isOne(value any) bool {
	switch typed := value.(type) {
	case float64:
		return typed == 1
	case int:
		return typed == 1
	}
	return false
}

// jsonRepr renders a JSON value the way python's repr() would.
func jsonRepr(value any) string {
	switch typed := value.(type) {
	case nil:
		return "None"
	case bool:
		if typed {
			return "True"
		}
		return "False"
	case string:
		return pycompat.Repr(typed)
	case float64:
		if typed == float64(int64(typed)) {
			return strconv.FormatInt(int64(typed), 10)
		}
		return strconv.FormatFloat(typed, 'g', -1, 64)
	}
	return fmt.Sprintf("%v", value)
}

// stringList reads a JSON array of strings.
func stringList(value any) []string {
	items, ok := value.([]any)
	if !ok {
		return nil
	}
	var out []string
	for _, item := range items {
		if text, ok := item.(string); ok {
			out = append(out, text)
		} else {
			out = append(out, fmt.Sprintf("%v", item))
		}
	}
	return out
}

// planShards are the shards of the plan, in the plan's order.
func planShards(plan map[string]any) []shard {
	entries, _ := plan["shards"].([]any)
	var out []shard
	for _, entry := range entries {
		item, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		id, _ := item["id"].(string)
		out = append(out, shard{ID: id, Classes: stringList(item["classes"])})
	}
	return out
}

// planTotalsMeasuredTests is the plan's measured test count.
func planTotalsMeasuredTests(plan map[string]any) float64 {
	totals, ok := plan["totals"].(map[string]any)
	if !ok {
		return 0
	}
	switch typed := totals["measuredTests"].(type) {
	case float64:
		return typed
	case int:
		return float64(typed)
	}
	return 0
}

// runDiscovery enumerates every test the assembly discovers, keyed by class
// name.
//
// Returns (per-class counts, total, note). `note` is empty when the
// discovery pass worked, and explains itself when it did not - the caller
// reports that note, because a guard that silently degrades is worse than no
// guard.
func runDiscovery(dll string) (map[string]int, int, string) {
	ctx, cancel := context.WithTimeout(context.Background(), discoveryTimeout)
	defer cancel()
	command := exec.CommandContext(ctx, "dotnet", "exec", dll, "--list-tests", "json")
	var stdout, stderr bytes.Buffer
	command.Stdout = &stdout
	command.Stderr = &stderr
	err := command.Run()
	if ctx.Err() != nil {
		return map[string]int{}, 0,
			fmt.Sprintf("discovery pass could not run (it did not finish in %d seconds)",
				int(discoveryTimeout.Seconds()))
	}
	if err != nil {
		exitCode := 1
		if exitError, ok := err.(*exec.ExitError); ok {
			exitCode = exitError.ExitCode()
		}
		tail := strings.TrimSpace(stderr.String())
		if tail == "" {
			tail = strings.TrimSpace(stdout.String())
		}
		detail := "no output"
		if tail != "" {
			lines := strings.Split(tail, "\n")
			detail = lines[len(lines)-1]
		}
		return map[string]int{}, 0, fmt.Sprintf(
			"discovery pass exited %d (%s); the exact per-class check was SKIPPED and "+
				"the plan's measured total was used as a floor instead", exitCode, detail)
	}

	document := parseJSONDocument(stdout.String())
	if document == nil {
		return map[string]int{}, 0,
			"discovery pass printed no JSON; the exact per-class check was SKIPPED and " +
				"the plan's measured total was used as a floor instead"
	}
	tests, ok := document["tests"].([]any)
	if !ok {
		return map[string]int{}, 0,
			"discovery pass printed JSON without a `tests` array; the exact " +
				"per-class check was SKIPPED and the plan's measured total was used as a floor instead"
	}

	counts := map[string]int{}
	for _, entry := range tests {
		item, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		if name := classOf(item); name != "" {
			counts[name]++
		}
	}
	return counts, len(tests), ""
}

// parseJSONDocument parses the discovery output, tolerating a banner in
// front of the JSON.
func parseJSONDocument(text string) map[string]any {
	text = strings.TrimSpace(text)
	if text == "" {
		return nil
	}
	var document map[string]any
	if err := json.Unmarshal([]byte(text), &document); err == nil {
		return document
	}
	start := strings.Index(text, "{")
	for start >= 0 {
		if err := json.Unmarshal([]byte(text[start:]), &document); err == nil {
			return document
		}
		next := strings.Index(text[start+1:], "{")
		if next < 0 {
			break
		}
		start = start + 1 + next
	}
	return nil
}

// classOf is the class a discovered test belongs to.
//
// `type` carries the namespace and type name when the host asked for the
// full identifier. The `uid` is the fallback: it is the test's tree path,
// whose class segment is the third one.
func classOf(entry map[string]any) string {
	if identifier, ok := entry["type"].(map[string]any); ok {
		if typeName, ok := identifier["typeName"].(string); ok && typeName != "" {
			return lastSegment(typeName)
		}
	}
	if uid, ok := entry["uid"].(string); ok {
		var segments []string
		for _, segment := range strings.Split(uid, "/") {
			if segment != "" {
				segments = append(segments, segment)
			}
		}
		if len(segments) >= 3 {
			return segments[2]
		}
	}
	return ""
}

// readTrx maps class name -> test count, plus (total, failed, not-executed).
func readTrx(path string) (map[string]int, int, int, int) {
	counts := map[string]int{}
	data, err := os.ReadFile(path)
	if err != nil {
		return counts, 0, 0, 0
	}
	root, err := xmltree.Parse(data)
	if err != nil {
		return counts, 0, 0, 0
	}
	methods := map[string]string{}
	for _, unitTest := range root.IterNS(trxNamespace, "UnitTest") {
		method := unitTest.FindChildNS(trxNamespace, "TestMethod")
		if method == nil {
			continue
		}
		methods[unitTest.Attr("id")] = lastSegment(method.Attr("className"))
	}
	total, failed, notExecuted := 0, 0, 0
	for _, result := range root.IterNS(trxNamespace, "UnitTestResult") {
		className, ok := methods[result.Attr("testId")]
		if !ok {
			className = "?"
		}
		counts[className]++
		total++
		outcome := strings.ToLower(result.Attr("outcome"))
		switch outcome {
		case "failed", "error", "timeout", "aborted":
			failed++
		case "notexecuted":
			notExecuted++
		}
	}
	return counts, total, failed, notExecuted
}

// lastSegment is the last dot-separated segment of a name.
func lastSegment(name string) string {
	if index := strings.LastIndex(name, "."); index >= 0 {
		return name[index+1:]
	}
	return name
}

// trxDuration reads a TRX duration: `hh:mm:ss.fffffff`.
func trxDuration(value string) float64 {
	if value == "" {
		return 0
	}
	parts := strings.Split(value, ":")
	switch len(parts) {
	case 3:
		hours, _ := pycompat.Atoi(parts[0])
		minutes, _ := pycompat.Atoi(parts[1])
		seconds, err := strconv.ParseFloat(parts[2], 64)
		if err != nil {
			return 0
		}
		return float64(hours)*3600 + float64(minutes)*60 + seconds
	case 2:
		minutes, _ := pycompat.Atoi(parts[0])
		seconds, err := strconv.ParseFloat(parts[1], 64)
		if err != nil {
			return 0
		}
		return float64(minutes)*60 + seconds
	default:
		seconds, err := strconv.ParseFloat(parts[0], 64)
		if err != nil {
			return 0
		}
		return seconds
	}
}

// repoRoot walks up from `start` until a `Harbor.slnx` marks the repository
// root.
func repoRoot(start string) (string, error) {
	directory, err := filepath.Abs(start)
	if err != nil {
		return "", err
	}
	for {
		if info, err := os.Stat(filepath.Join(directory, "Harbor.slnx")); err == nil && !info.IsDir() {
			return directory, nil
		}
		parent := filepath.Dir(directory)
		if parent == directory {
			return filepath.Abs(start)
		}
		directory = parent
	}
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
