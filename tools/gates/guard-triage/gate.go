// Package guardtriage is the compiled port of tools/guard-triage.py.
//
// The tool classifies the architecture-guard firings into defect / style /
// noise / unknown (issue #1726). It reads what a CI run already produced
// (a TRX file, or a job log) and writes a machine-readable report of every
// guard that FIRED, with the evidence that made it fire and the class it
// belongs to.
//
// `unknown` is a first-class answer, not a failure: it says the feedback
// loop has not visited this rule yet. A report where everything is
// `unknown` is a report that says nothing, and the tool prints that fact
// instead of hiding it.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 clean, 1 the input was
// unusable, 2 the self-test failed.
package guardtriage

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The schema names the report and the registry.
const (
	schema          = "harbor.guard-triage/1"
	registrySchema  = "harbor.guard-triage/registry/1"
	fixtureMarker   = "harbor-md-gate-selftest"
	maxEvidenceLine = 120
)

// The four classes, in the order the report prints them.
var classes = []string{"defect", "style", "noise", "unknown"}

// The part of an assertion message that names the thing that broke.
var citedMarkers = []string{
	"Found:",
	"Undeclared:",
	"Stale entries:",
	"Violations:",
	"Offenders:",
	"Actual:",
	"Received:",
}

// pathLine is a `path:line` in an assertion message.
var pathLine = regexp.MustCompile(`((?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.[A-Za-z0-9]+):(\d+)`)

// barePath is a bare repository-relative path in an assertion message.
var barePath = regexp.MustCompile(
	`((?:src|apps|tests|tools|docs|build|samples|contrib|\.github)/[A-Za-z0-9_./+-]+\.[A-Za-z0-9]+)`)

// failedTest is the test platform's failure block.
var failedTest = regexp.MustCompile(`failed\s+([A-Za-z0-9_.\-]+)\s+\(([^)]*)\)`)

// nukePrefix is the build tool's logger prefix: `05:15:38 [DBG] `.
var nukePrefix = regexp.MustCompile(`^(?:\d{2}:\d{2}:\d{2}\s+)?\[(?:DBG|INF|WRN|ERR)\]\s*`)

// stackFrame is the stack frame that names the rule.
var stackFrame = regexp.MustCompile(
	`at\s+([A-Za-z0-9_.]+)\.([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\(\)`)

// projectGroup is the `::group::dotnet exec <project>` marker.
var projectGroup = regexp.MustCompile(`(?:##\[group\]|::group::)dotnet exec\s+([A-Za-z0-9_.]+)`)

// ghPrefix is the prefix a CI log line carries.
var ghPrefix = regexp.MustCompile(`^(?:[^\t]*\t[^\t]*\t)?\d{4}-\d{2}-\d{2}T[\d:.]+Z\s?`)

// ansi is the colour escape, in the caret form and in the raw form.
var ansi = regexp.MustCompile(`(?:\x1b\[|\^\[\[)[0-9;]*[a-zA-Z]`)

// ledgerStale is the assertion that says the allowance is stale.
var ledgerStale = regexp.MustCompile(
	`(?i)(stale entr|retired debt|no longer (an )?orphan|outlived|is stale|stopped being an orphan)`)

// classRe and methodRe find the class and the method of a guard file.
var (
	classRe = regexp.MustCompile(
		`^\s*(?:public|internal|private|protected)?\s*` +
			`(?:sealed\s+|static\s+|abstract\s+|partial\s+|file\s+)*class\s+([A-Za-z0-9_]+)`)
	methodRe = regexp.MustCompile(
		`^\s*(?:public|internal|private|protected)\s+` +
			`(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|partial\s+)*` +
			`(?:Task|void|ValueTask|Task<[^>]+>|bool|int|string|` +
			`IReadOnlyList<[^>]+>|IEnumerable<[^>]+>)\s+([A-Za-z0-9_]+)\s*\(`)
)

// registryEntry is one rule of the human registry.
type registryEntry struct {
	rule           string
	class          string
	reason         string
	ratchet        string
	allowanceFiles []string
}

// registry is the human decisions: which class a rule's firings belong to.
type registry struct {
	rules     map[string]registryEntry
	ruleOrder []string
	firings   []registryFiring
}

// registryFiring is one per-firing decision.
type registryFiring struct {
	rule             string
	class            string
	reason           string
	ratchet          string
	evidenceContains string
}

// loadRegistry reads the classification registry.
func loadRegistry(path string) (*registry, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	parsed, err := parseJSON(data)
	if err != nil {
		return nil, errors.New(jsonDecodeText(data, err))
	}
	obj, ok := parsed.obj, isObject(parsed)
	if !ok {
		return nil, fmt.Errorf("the registry is not a JSON object")
	}
	if v, ok := objString(parsed, "schema"); !ok || v != registrySchema {
		schema := v
		if !ok {
			schema = "None"
		}
		return nil, fmt.Errorf("%s: unknown schema %s, expected %s",
			path, pythonRepr(schema), pythonRepr(registrySchema))
	}
	reg := &registry{rules: map[string]registryEntry{}}
	for _, member := range obj {
		switch member.key {
		case "rules":
			for _, rule := range member.value.arr {
				name, ok := objString(rule, "rule")
				if !ok || name == "" {
					return nil, fmt.Errorf("%s: a rule entry has no 'rule' key", path)
				}
				class, _ := objString(rule, "class")
				if !containsString(classes, class) {
					return nil, fmt.Errorf("%s: rule %s has class %s, expected one of %s",
						path, name, pythonRepr(class), pythonReprTuple(classes))
				}
				reason, _ := objString(rule, "reason")
				ratchet, _ := objString(rule, "ratchet")
				var files []string
				for _, file := range objList(rule, "allowance_files") {
					if s, ok := file.str, file.kind == 's'; ok {
						files = append(files, s)
					}
				}
				reg.rules[name] = registryEntry{
					rule: name, class: class, reason: reason,
					ratchet: ratchet, allowanceFiles: files,
				}
				reg.ruleOrder = append(reg.ruleOrder, name)
			}
		case "firings":
			for _, firing := range member.value.arr {
				name, _ := objString(firing, "rule")
				class, _ := objString(firing, "class")
				reason, _ := objString(firing, "reason")
				ratchet, _ := objString(firing, "ratchet")
				needle, _ := objString(firing, "evidence_contains")
				reg.firings = append(reg.firings, registryFiring{
					rule: name, class: class, reason: reason,
					ratchet: ratchet, evidenceContains: needle,
				})
			}
		}
	}
	return reg, nil
}

// objString reads a string member of an object.
func objString(value jsonValue, key string) (string, bool) {
	if !isObject(value) {
		return "", false
	}
	for _, member := range value.obj {
		if member.key == key && member.value.kind == 's' {
			return member.value.str, true
		}
	}
	return "", false
}

// objList reads an array member of an object.
func objList(value jsonValue, key string) []jsonValue {
	if !isObject(value) {
		return nil
	}
	for _, member := range value.obj {
		if member.key == key && member.value.kind == 'a' {
			return member.value.arr
		}
	}
	return nil
}

// jsonDecodeText is python's message for a file that is not JSON.
//
// The python decoder says "Expecting value" when the document does not
// start with a value, and it names the position of the character that
// stopped it. The port says the same, at the same position.
func jsonDecodeText(data []byte, err error) string {
	index := 0
	for index < len(data) && isJSONSpace(data[index]) {
		index++
	}
	if index >= len(data) {
		return "Expecting value: line 1 column 1 (char 0)"
	}
	// A literal that does not spell itself stops the decoder at the FIRST
	// character, not at the second: "not json" is not a value, and the
	// value starts at the "n".
	literal := ""
	switch data[index] {
	case 't':
		literal = "true"
	case 'f':
		literal = "false"
	case 'n':
		literal = "null"
	}
	if literal != "" && !strings.HasPrefix(string(data[index:]), literal) {
		line, column := jsonLineAndColumn(data, index)
		return fmt.Sprintf("Expecting value: line %d column %d (char %d)",
			line, column, index)
	}
	switch data[index] {
	case '{', '[', '"', '-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
		't', 'f', 'n':
		var syntax *json.SyntaxError
		if errors.As(err, &syntax) && syntax.Offset > 0 {
			offset := int(syntax.Offset - 1)
			line, column := jsonLineAndColumn(data, offset)
			return fmt.Sprintf("Expecting value: line %d column %d (char %d)",
				line, column, offset)
		}
	}
	line, column := jsonLineAndColumn(data, index)
	return fmt.Sprintf("Expecting value: line %d column %d (char %d)",
		line, column, index)
}

// isJSONSpace reports whether the byte is JSON whitespace.
func isJSONSpace(b byte) bool {
	switch b {
	case ' ', '\t', '\n', '\r':
		return true
	}
	return false
}

// jsonLineAndColumn is the 1-based line and column of a byte offset.
func jsonLineAndColumn(data []byte, offset int) (int, int) {
	if offset > len(data) {
		offset = len(data)
	}
	line := 1
	column := 1
	for i := 0; i < offset; i++ {
		if data[i] == '\n' {
			line++
			column = 1
			continue
		}
		column++
	}
	return line, column
}

// pythonRepr is python's `repr` of a string.
func pythonRepr(s string) string {
	return "'" + strings.ReplaceAll(s, "'", "\\'") + "'"
}

// pythonReprTuple is python's `repr` of a tuple of strings.
func pythonReprTuple(items []string) string {
	parts := make([]string, 0, len(items))
	for _, item := range items {
		parts = append(parts, pythonRepr(item))
	}
	return "(" + strings.Join(parts, ", ") + ")"
}

// isObject reports whether the value is a JSON object.
func isObject(value jsonValue) bool {
	return value.kind == 'o' || value.kind == 0
}

// containsString reports whether the list holds the value.
func containsString(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// Run is the gate entry point.
func Run(args []string) int {
	var trxPaths []string
	var logPaths []string
	classesPath := "tools/guard-triage-classes.json"
	guardDir := "tests/Harbor.Architecture.Tests"
	repoRoot := "."
	outPath := ""
	markdownPath := ""
	summaryPath := ""
	var projects []string
	minGuardFiles := 0
	runsPath := ""
	quiet := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--trx":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--trx needs a path")
				return gate.Broken
			}
			i++
			trxPaths = append(trxPaths, args[i])
		case "--log":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--log needs a path")
				return gate.Broken
			}
			i++
			logPaths = append(logPaths, args[i])
		case "--classes":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--classes needs a path")
				return gate.Broken
			}
			i++
			classesPath = args[i]
		case "--guard-dir":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--guard-dir needs a path")
				return gate.Broken
			}
			i++
			guardDir = args[i]
		case "--repo-root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--repo-root needs a path")
				return gate.Broken
			}
			i++
			repoRoot = args[i]
		case "--out":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--out needs a path")
				return gate.Broken
			}
			i++
			outPath = args[i]
		case "--markdown":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--markdown needs a path")
				return gate.Broken
			}
			i++
			markdownPath = args[i]
		case "--summary":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--summary needs a path")
				return gate.Broken
			}
			i++
			summaryPath = args[i]
		case "--project":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--project needs a name")
				return gate.Broken
			}
			i++
			projects = append(projects, args[i])
		case "--min-guard-files":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-guard-files needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-guard-files needs a number")
				return gate.Broken
			}
			minGuardFiles = n
		case "--runs":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--runs needs a path")
				return gate.Broken
			}
			i++
			runsPath = args[i]
		case "--quiet":
			quiet = true
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates guard-triage [--trx PATH] [--log PATH] [--classes FILE] [--guard-dir DIR] [--repo-root DIR] [--out FILE] [--markdown FILE] [--summary FILE] [--project NAME] [--min-guard-files N] [--runs FILE] [--quiet] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := filepath.Abs(repoRoot)
	if err != nil {
		fmt.Fprintln(os.Stderr, "guard-triage: ", err)
		return gate.Red
	}

	registryFile := classesPath
	if !filepath.IsAbs(registryFile) {
		registryFile = filepath.Join(root, registryFile)
	}
	if _, err := os.Stat(registryFile); err != nil {
		fmt.Fprintf(os.Stderr, "guard-triage: no registry at %s\n", registryFile)
		return gate.Red
	}
	reg, err := loadRegistry(registryFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "guard-triage: %v\n", err)
		return gate.Red
	}

	guardPath := guardDir
	if !filepath.IsAbs(guardPath) {
		guardPath = filepath.Join(root, guardPath)
	}
	index := buildGuardIndex(guardPath, root)
	if minGuardFiles > 0 && len(index.source) < minGuardFiles {
		fmt.Fprintf(os.Stderr,
			"guard-triage: indexed %d guard file(s) under %s, the floor is %d. A "+
				"guard project that moved would otherwise make this tool report an "+
				"empty tree, which reads as 'no guard fired'.\n",
			len(index.source), guardPath, minGuardFiles)
		return gate.Red
	}

	firings, trxUsed := collectTRX(trxPaths)
	logFirings, logsUsed := collectLogs(logPaths)
	firings = append(firings, logFirings...)

	if len(projects) > 0 {
		var kept []firing
		for _, f := range firings {
			if containsString(projects, f.project) {
				kept = append(kept, f)
			}
		}
		firings = kept
	}
	firings = dedupe(firings)

	runIndex := map[string]jsonValue{}
	if runsPath != "" {
		runsFile := runsPath
		if !filepath.IsAbs(runsFile) {
			runsFile = filepath.Join(root, runsFile)
		}
		data, err := os.ReadFile(runsFile)
		if err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %v\n", err)
			return gate.Red
		}
		parsed, err := parseJSON(data)
		if err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %v\n", err)
			return gate.Red
		}
		if isObject(parsed) {
			for _, member := range parsed.obj {
				runIndex[member.key] = member.value
			}
		} else {
			for _, entry := range parsed.arr {
				key := ""
				if v, ok := objString(entry, "run_id"); ok {
					key = v
				} else if v, ok := objString(entry, "id"); ok {
					key = v
				}
				if key != "" {
					runIndex[key] = entry
				}
			}
		}
	}

	for i := range firings {
		if firings[i].runID == "" {
			firings[i].runID = guessRunID(firings[i].origin)
		}
		if firings[i].runID != "" {
			if run, ok := runIndex[firings[i].runID]; ok {
				firings[i].run = run
			}
		}
		clsName := firings[i].rule
		if i := strings.Index(clsName, "."); i >= 0 {
			clsName = clsName[:i]
		}
		method := firings[i].rule
		if i := strings.LastIndex(method, "."); i >= 0 {
			method = method[i+1:]
		}
		path, line, found := index.locate(clsName, method)
		if found {
			firings[i].guardFile = path
			firings[i].hasGuardFile = true
			firings[i].guardLine = line
			firings[i].hasGuardLine = true
		}
		classifyFiring(&firings[i], reg, index, reg.allowanceText(root, firings[i].rule))
	}

	sort.SliceStable(firings, func(i, j int) bool {
		if firings[i].rule != firings[j].rule {
			return firings[i].rule < firings[j].rule
		}
		return firings[i].runID < firings[j].runID
	})

	seenRuns := map[string]bool{}
	for _, f := range firings {
		seenRuns[f.runID] = true
	}
	var stamps []string
	for _, f := range firings {
		if v, ok := objString(f.run, "created_at"); ok && v != "" {
			stamps = append(stamps, v)
		}
	}
	sort.Strings(stamps)
	window := map[string]string{
		"from": "",
		"to":   "",
		"runs": strconv.Itoa(len(seenRuns)),
	}
	if len(stamps) > 0 {
		window["from"] = stamps[0]
		window["to"] = stamps[len(stamps)-1]
	}

	report := buildReport(firings, reg, index,
		map[string][]string{"trx": trxUsed, "logs": logsUsed}, window)

	payload := renderJSON(report) + "\n"
	if outPath != "" {
		if err := os.WriteFile(outPath, []byte(payload), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %v\n", err)
			return gate.Red
		}
	} else {
		fmt.Print(payload)
	}
	if markdownPath != "" {
		if err := os.WriteFile(markdownPath, []byte(renderMarkdown(report)+"\n"), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %v\n", err)
			return gate.Red
		}
	}
	if summaryPath != "" {
		fh, err := os.OpenFile(summaryPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
		if err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %v\n", err)
			return gate.Red
		}
		fmt.Fprint(fh, renderSummary(report)+"\n")
		fh.Close()
	}
	if !quiet && outPath != "" {
		totals := reportTotals(report)
		fmt.Fprintf(os.Stderr,
			"guard-triage: %d firings, %d rules, noise %d, unknown %d -> %s\n",
			totalsInt(totals, "firings"), totalsInt(totals, "rules"),
			totalsClass(totals, "noise"), totalsClass(totals, "unknown"), outPath)
	}
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
