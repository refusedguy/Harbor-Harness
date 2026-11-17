package guardtriage

// firings.go -- find the firings in the artifacts a CI run already produced.
//
// Two input shapes, because CI produces both: a directory of TRX files and a
// CI job log. A failed UnitTestResult is one firing; so is one failed-test
// block in a log. The evidence is the assertion message, and the stack frame
// names the rule.
//
// The python original used a lookbehind and a lookahead to find a run id in
// an artifact's name: (?<!\d)(\d{8,})(?!\d). Go's regexp engine (RE2) has
// neither. guessRunID below rewrites the LOGIC instead of translating the
// pattern: it scans maximal runs of digits and takes the first whose length
// is at least eight, which is exactly what "a run of 8+ digits bounded by
// non-digits" means.

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"unicode"

	"harbor.local/gates/internal/xmltree"
)

const schema = "harbor.guard-triage/1"

// citedMarkers: only the part of an assertion after one of these names the
// places the rule CITES; the part before it is the rule explaining itself.
var citedMarkers = []string{
	"Found:", "Undeclared:", "Stale entries:", "Violations:", "Offenders:", "Actual:", "Received:",
}

const maxEvidenceLines = 120

var (
	pathLineRE     = regexp.MustCompile(`(?P<path>(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.[A-Za-z0-9]+):(?P<line>\d+)`)
	barePathRE     = regexp.MustCompile(`(?P<path>(?:src|apps|tests|tools|docs|build|samples|contrib|\.github)/[A-Za-z0-9_./+-]+\.[A-Za-z0-9]+)`)
	failedTestRE   = regexp.MustCompile(`failed\s+(?P<name>[A-Za-z0-9_.\-]+)\s+\((?P<dur>[^)]*)\)`)
	nukePrefixRE   = regexp.MustCompile(`^(?:\d{2}:\d{2}:\d{2}\s+)?\[(?:DBG|INF|WRN|ERR)\]\s*`)
	stackFrameRE   = regexp.MustCompile(`at\s+(?P<ns>[A-Za-z0-9_.]+)\.(?P<cls>[A-Za-z0-9_]+)\.(?P<method>[A-Za-z0-9_]+)\(\)`)
	projectGroupRE = regexp.MustCompile(`(?:##\[group\]|::group::)dotnet exec\s+(?P<project>[A-Za-z0-9_.]+)`)
	ghPrefixRE     = regexp.MustCompile(`^(?:[^\t]*\t[^\t]*\t)?\d{4}-\d{2}-\d{2}T[\d:.]+Z\s?`)
	ansiRE         = regexp.MustCompile("(?:\\x{1b}\\[|\\^\\[\\[)[0-9;]*[a-zA-Z]")
	ledgerStaleRE  = regexp.MustCompile(`(?i)(stale entr|retired debt|no longer (an )?orphan|outlived|is stale|stopped being an orphan)`)
)

func group(re *regexp.Regexp, match []string, name string) string {
	index := re.SubexpIndex(name)
	if index < 0 || index >= len(match) {
		return ""
	}
	return match[index]
}

// location is a file:line pair; line is 0 when the citation has no line.
type location struct {
	path string
	line int
}

type firing struct {
	rule           string
	testName       string
	project        string
	evidence       string
	source         string // "trx" or "log"
	origin         string
	runID          string
	guardFile      string
	guardLine      int
	locations      []location
	classification string
	classifiedBy   string
	reason         string
	ratchet        string
	run            *jnode
}

func newFiring(rule, testName, project, evidence, source, origin string, locations []location) *firing {
	return &firing{
		rule:           rule,
		testName:       testName,
		project:        project,
		evidence:       evidence,
		source:         source,
		origin:         origin,
		locations:      locations,
		classification: "unknown",
		classifiedBy:   "none",
		run:            jobject(),
	}
}

func (f *firing) toJSON() *jnode {
	node := jobject()
	node.set("rule", jstring(f.rule))
	node.set("test_name", jstring(f.testName))
	node.set("project", jstring(f.project))
	node.set("class", jstring(f.classification))
	node.set("classified_by", jstring(f.classifiedBy))
	node.set("reason", jstring(f.reason))
	node.set("ratchet", jstring(f.ratchet))
	if f.guardFile == "" {
		node.set("guard_file", jnull())
	} else {
		node.set("guard_file", jstring(f.guardFile))
	}
	if f.guardLine == 0 {
		node.set("guard_line", jnull())
	} else {
		node.set("guard_line", jint(f.guardLine))
	}
	locationsNode := jarray()
	for _, place := range f.locations {
		item := jobject()
		item.set("path", jstring(place.path))
		if place.line == 0 {
			item.set("line", jnull())
		} else {
			item.set("line", jint(place.line))
		}
		locationsNode.add(item)
	}
	node.set("locations", locationsNode)
	node.set("evidence", jstring(f.evidence))
	node.set("source", jstring(f.source))
	node.set("origin", jstring(f.origin))
	node.set("run_id", jstring(f.runID))
	node.set("run", f.run)
	return node
}

// extractLocations returns the file:line pairs an assertion message points
// at, counting only the part after a citation marker.
func extractLocations(evidence string) []location {
	cited := evidence
	for _, marker := range citedMarkers {
		if index := strings.Index(evidence, marker); index >= 0 {
			cited = evidence[index+len(marker):]
			break
		}
	}
	var found []location
	seen := map[location]bool{}
	for _, match := range pathLineRE.FindAllStringSubmatch(cited, -1) {
		path := group(pathLineRE, match, "path")
		if !strings.Contains(path, "/") && !strings.HasSuffix(path, ".cs") {
			continue
		}
		line, err := strconv.Atoi(group(pathLineRE, match, "line"))
		if err != nil {
			continue
		}
		item := location{path: path, line: line}
		if !seen[item] {
			seen[item] = true
			found = append(found, item)
		}
	}
	for _, match := range barePathRE.FindAllStringSubmatch(cited, -1) {
		path := strings.TrimRight(group(barePathRE, match, "path"), ".,;:)")
		item := location{path: path, line: 0}
		if !seen[item] {
			seen[item] = true
			found = append(found, item)
		}
	}
	return found
}

func ruleFromFrame(ns string, cls string, method string) string {
	if strings.HasSuffix(ns, "Architecture.Tests") || strings.HasSuffix(ns, ".Tests") {
		return cls + "." + method
	}
	return ns + "." + cls + "." + method
}

// trxText is the text of the first descendant whose local name is tag, with
// the surrounding whitespace removed.
func trxText(node *xmltree.Node, tag string) string {
	if node == nil {
		return ""
	}
	var walk func(*xmltree.Node) string
	walk = func(current *xmltree.Node) string {
		if current == nil {
			return ""
		}
		if current.Name.Local == tag {
			return current.TrimmedText()
		}
		for _, child := range current.Children {
			if text := walk(child); text != "" {
				return text
			}
		}
		return ""
	}
	return walk(node)
}

func splitTestName(testName string, className string) (string, string) {
	if strings.Contains(testName, ".") {
		index := strings.LastIndex(testName, ".")
		cls := testName[:index]
		parts := strings.Split(cls, ".")
		return parts[len(parts)-1], testName[index+1:]
	}
	if className != "" {
		parts := strings.Split(className, ".")
		return parts[len(parts)-1], testName
	}
	return "", testName
}

func projectOf(className string, clsName string) string {
	if className == "" {
		return ""
	}
	if clsName != "" && strings.HasSuffix(className, "."+clsName) {
		return className[:len(className)-len(clsName)-1]
	}
	if index := strings.LastIndex(className, "."); index >= 0 {
		return className[:index]
	}
	return className
}

// firingsFromTRX returns one firing per failed test in a TRX file.
func firingsFromTRX(path string) []*firing {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	root, err := xmltree.Parse(data)
	if err != nil {
		return nil
	}
	results := root.IterNS("http://microsoft.com/schemas/VisualStudio/TeamTest/2010", "UnitTestResult")
	if len(results) == 0 {
		results = root.Iter("UnitTestResult")
	}
	var out []*firing
	for _, result := range results {
		if strings.ToLower(result.Attr("outcome")) != "failed" {
			continue
		}
		testName := result.Attr("testName")
		className := result.Attr("className")
		message := trxText(result, "Message")
		// The python original asked for "Output/StdOut" as a local name, which
		// no element has: stdout is always empty. Keep the same answer.
		stdout := trxText(result, "Output/StdOut")
		var parts []string
		if message != "" {
			parts = append(parts, message)
		}
		if stdout != "" {
			parts = append(parts, stdout)
		}
		evidence := strings.TrimSpace(strings.Join(parts, "\n"))
		clsName, method := splitTestName(testName, className)
		rule := testName
		if clsName != "" {
			rule = clsName + "." + method
		}
		stack := trxText(result, "StackTrace")
		if match := stackFrameRE.FindStringSubmatch(stack); match != nil {
			rule = ruleFromFrame(group(stackFrameRE, match, "ns"),
				group(stackFrameRE, match, "cls"), group(stackFrameRE, match, "method"))
		}
		out = append(out, newFiring(rule, testName, projectOf(className, clsName),
			evidence, "trx", path, extractLocations(evidence)))
	}
	return out
}

// cleanLine removes colour codes, the log prefix and the logger prefix.
func cleanLine(line string) string {
	text := ansiRE.ReplaceAllString(line, "")
	text = ghPrefixRE.ReplaceAllString(text, "")
	text = nukePrefixRE.ReplaceAllString(text, "")
	return strings.TrimRightFunc(text, unicode.IsSpace)
}

func splitLines(text string) []string {
	if text == "" {
		return nil
	}
	lines := strings.Split(text, "\n")
	if len(lines) > 0 && lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	for i, line := range lines {
		lines[i] = strings.TrimSuffix(line, "\r")
	}
	return lines
}

// firingsFromLog returns one firing per failed-test block in a CI job log.
func firingsFromLog(path string) []*firing {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	rawLines := splitLines(string(data))
	lines := make([]string, len(rawLines))
	for i, line := range rawLines {
		lines[i] = cleanLine(line)
	}
	var out []*firing
	project := ""
	index := 0
	for index < len(lines) {
		line := lines[index]
		if match := projectGroupRE.FindStringSubmatch(line); match != nil {
			project = group(projectGroupRE, match, "project")
		}
		var match []string
		if strings.Contains(line, "failed") {
			match = failedTestRE.FindStringSubmatch(line)
		}
		if match == nil || strings.Contains(line, "failed:") {
			index++
			continue
		}
		testName := group(failedTestRE, match, "name")
		var block []string
		index++
		for index < len(lines) && len(block) < maxEvidenceLines {
			next := lines[index]
			if strings.Contains(next, "failed") {
				if nextMatch := failedTestRE.FindStringSubmatch(next); nextMatch != nil && !strings.Contains(next, "failed:") {
					break
				}
			}
			if strings.HasPrefix(next, "[slow]") || strings.HasPrefix(next, "In process file artifacts") {
				break
			}
			if strings.TrimSpace(next) != "" {
				block = append(block, next)
			}
			index++
		}
		evidence := strings.TrimSpace(strings.Join(block, "\n"))
		var frame []string
		for _, candidate := range block {
			if match := stackFrameRE.FindStringSubmatch(candidate); match != nil {
				frame = match
				break
			}
		}
		clsName, method := "", testName
		if dot := strings.LastIndex(testName, "."); dot >= 0 {
			parts := strings.Split(testName[:dot], ".")
			clsName = parts[len(parts)-1]
			method = testName[dot+1:]
		}
		rule := testName
		if clsName != "" {
			rule = clsName + "." + method
		}
		if frame != nil {
			ns := group(stackFrameRE, frame, "ns")
			rule = ruleFromFrame(ns, group(stackFrameRE, frame, "cls"), group(stackFrameRE, frame, "method"))
			if project == "" && strings.HasSuffix(ns, ".Tests") {
				project = ns
			}
		}
		out = append(out, newFiring(rule, testName, project, evidence, "log", path, extractLocations(evidence)))
	}
	return out
}

// expandInputs turns a path, a directory or a glob into the files to read.
func expandInputs(raw string, pattern string) []string {
	if strings.ContainsAny(raw, "*?[") {
		return globPaths(raw)
	}
	info, err := os.Stat(raw)
	if err != nil {
		return nil
	}
	if !info.IsDir() {
		return []string{raw}
	}
	var out []string
	_ = filepath.Walk(raw, func(path string, info os.FileInfo, err error) error {
		if err == nil && !info.IsDir() {
			if ok, _ := filepath.Match(pattern, filepath.Base(path)); ok {
				out = append(out, path)
			}
		}
		return nil
	})
	sort.Strings(out)
	return out
}

// globPaths expands a glob, including Path.glob's recursive `**`.
func globPaths(pattern string) []string {
	volume := ""
	rest := pattern
	if filepath.IsAbs(pattern) {
		volume = string(filepath.Separator)
		rest = strings.TrimLeft(rest, string(filepath.Separator))
	} else if len(pattern) > 1 && pattern[1] == ':' {
		volume = pattern[:2]
		rest = strings.TrimLeft(rest[2:], string(filepath.Separator))
	}
	segments := strings.Split(filepath.ToSlash(rest), "/")
	var walk func(dir string, index int) []string
	walk = func(dir string, index int) []string {
		if index == len(segments) {
			return nil
		}
		segment := segments[index]
		if segment == "**" {
			var out []string
			out = append(out, walk(dir, index+1)...)
			if entries, err := os.ReadDir(dir); err == nil {
				for _, entry := range entries {
					if entry.IsDir() {
						out = append(out, walk(filepath.Join(dir, entry.Name()), index)...)
					}
				}
			}
			return out
		}
		matches, err := filepath.Glob(filepath.Join(dir, segment))
		if err != nil {
			return nil
		}
		var out []string
		for _, match := range matches {
			info, err := os.Stat(match)
			if err != nil {
				continue
			}
			if index == len(segments)-1 {
				if !info.IsDir() {
					out = append(out, match)
				}
			} else if info.IsDir() {
				out = append(out, walk(match, index+1)...)
			}
		}
		return out
	}
	out := walk(volume, 0)
	sort.Strings(out)
	return out
}

// guessRunID rewrites the python lookbehind/lookahead with a scan of maximal
// digit runs: the first run of eight or more digits, bounded by non-digits.
func guessRunID(origin string) string {
	name := filepath.Base(origin)
	start := -1
	for i := 0; i <= len(name); i++ {
		digit := i < len(name) && name[i] >= '0' && name[i] <= '9'
		if digit && start < 0 {
			start = i
		} else if !digit && start >= 0 {
			if i-start >= 8 {
				return name[start:i]
			}
			start = -1
		}
	}
	return ""
}
