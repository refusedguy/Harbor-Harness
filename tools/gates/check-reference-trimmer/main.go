// Package checkreferencetrimmer is the compiled port of
// tools/check-reference-trimmer.py.
//
// The gate asks whether the tree carries a reference the compiler never uses:
// a finding the tree produces that the recorded baseline does not carry is
// red, and a baseline row the tree no longer produces is red too.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checkreferencetrimmer

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// baselineFile is the baseline, relative to the repository root.
const baselineFile = "tools/reference-trimmer-baseline.txt"

// unusedRules are the rule ids the analyzer emits. RT0000 and RT9999 are not
// findings of an unused reference — they are statements about the quality of
// the answer — and the gate treats them as failures of their own kind rather
// than as baseline rows, because a baseline row for them would be blessing
// a broken instrument.
var unusedRules = []string{"RT0001", "RT0002", "RT0003"}

// minReasonLength is the floor on a reason. The floor is the same floor the
// sibling gate uses, and for the same reason: a short string is a category,
// not a reason, and a category cannot be reviewed.
const minReasonLength = 40

// candidateRe is MSBuild's canonical warning line. The upstream tool's own
// end-to-end test reads the same shape, so the format is not a guess.
var candidateRe = regexp.MustCompile(
	`(?:warning|error)\s+(RT\d{4})\s*:\s*(.*?)\s*\[([^\]]*)\]\s*$`)

// fallbackRe is the shape of a warning without the project bracket.
var fallbackRe = regexp.MustCompile(
	`^(.+?)\s*:\s*(?:warning|error)\s+(RT\d{4})\s*:\s*(.*?)\s*$`)

// specRe is the analyzer's message format.
var specRe = regexp.MustCompile(
	`^(Reference|ProjectReference|PackageReference)\s+(.+?)\s+can be removed$`)

// finding is one row of the answer: a rule, a subject, and where it came
// from.
type finding struct {
	rule    string
	subject string
	message string
	project string
	lineNo  int
}

// key is the identity of a finding.
func (f finding) key() string { return f.rule + "\x00" + f.subject }

// baselineRow is one row of the baseline file.
type baselineRow struct {
	rule    string
	subject string
	reason  string
	owner   string
	lineNo  int
}

// key is the identity of a baseline row.
func (r baselineRow) key() string { return r.rule + "\x00" + r.subject }

// Run is the gate entry point.
func Run(args []string) int {
	findingsPath := ""
	coveragePath := ""
	minProjects := 100
	baseline := baselineFile
	repoRoot := ""
	printRows := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--findings":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--findings needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			findingsPath = value
		case "--coverage":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--coverage needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			coveragePath = value
		case "--min-projects":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-projects needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-projects needs a number")
				return gate.Broken
			}
			minProjects = parsed
		case "--baseline":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--baseline needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			baseline = value
		case "--repo-root":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--repo-root needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			repoRoot = value
		case "--print-rows":
			printRows = true
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-reference-trimmer --findings LOG [--coverage FILE] [--min-projects N] [--baseline PATH] [--repo-root DIR] [--print-rows] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if findingsPath == "" {
		fmt.Fprintln(os.Stderr, "check-reference-trimmer: error: --findings is required (or --self-test)")
		return gate.Broken
	}

	root := repoRoot
	if root == "" {
		found, err := gate.RepoRoot()
		if err != nil {
			fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
			return gate.Broken
		}
		root = found
	}
	root, _ = filepath.Abs(root)
	baselinePath := baseline
	if !filepath.IsAbs(baselinePath) {
		baselinePath = filepath.Join(root, baselinePath)
	}

	found, stats := scan(root, findingsPath, coveragePath, baselinePath, minProjects)

	if printRows {
		data, err := os.ReadFile(findingsPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "check-reference-trimmer: cannot read the findings file:", err)
			return gate.Broken
		}
		parsed, _ := parsedFindings(root, string(data))
		for _, item := range parsed {
			fmt.Printf("%s | %s | TODO reason for this candidate | @refusedguy\n", item.rule, item.subject)
		}
		return gate.Green
	}

	return report(found, baseline, stats)
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
	negative := false
	body := text
	if body[0] == '+' || body[0] == '-' {
		negative = body[0] == '-'
		body = body[1:]
	}
	if body == "" {
		return 0, false
	}
	value := 0
	for _, r := range body {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	if negative {
		value = -value
	}
	return value, true
}

// normalizePath turns a path out of the build log into a repository-relative
// one.
//
// The log carries whatever MSBuild had: an absolute path on a CI runner, a
// relative one locally, and backslashes on Windows. All three have to land on
// the same string, or the baseline would be a different file on every
// machine.
func normalizePath(raw string, repoRoot string, knownProjects []string) string {
	path := strings.TrimSpace(raw)
	path = strings.Trim(path, "\"")
	path = strings.ReplaceAll(path, "\\", "/")
	if path == "" {
		return ""
	}
	if strings.HasPrefix(path, "/") {
		if relative, err := filepath.Rel(repoRoot, path); err == nil {
			relative = filepath.ToSlash(relative)
			if !strings.HasPrefix(relative, "..") {
				return relative
			}
		}
		// The path is outside the repository root — a CI runner's workspace
		// path, or a checkout somewhere else. The repository's own directory
		// name is the next best anchor.
		rootName := filepath.Base(repoRoot)
		if rootName != "" {
			parts := strings.Split(path, "/")
			for index := len(parts) - 1; index >= 0; index-- {
				if parts[index] == rootName {
					candidate := strings.Join(parts[index+1:], "/")
					if candidate != "" {
						return candidate
					}
				}
			}
		}
	}
	// A path that is not under the repository root (a package outside the
	// checkout, a path from another machine) is matched against the projects
	// this repository knows about, by suffix.
	for _, candidate := range knownProjects {
		if strings.HasSuffix(path, candidate) {
			return candidate
		}
	}
	return path
}

// parseFindings is (findings, quality problems) read out of the analyzer's
// log.
func parseFindings(text string, repoRoot string, knownProjects []string) ([]finding, []finding) {
	var findings []finding
	var quality []finding
	seen := map[string]bool{}
	for _, rawLine := range strings.Split(text, "\n") {
		line := strings.TrimRight(rawLine, "\r")
		if !strings.Contains(line, "RT") {
			continue
		}
		var code, message, project string
		match := candidateRe.FindStringSubmatch(line)
		if match != nil {
			code, message, project = match[1], match[2], match[3]
		} else {
			fallback := fallbackRe.FindStringSubmatch(line)
			if fallback == nil {
				continue
			}
			// No bracket: the location in front of the code carries the
			// project, and the analyzer reports project-level diagnostics
			// without a source location.
			code, message, project = fallback[2], fallback[3], fallback[1]
		}
		message = strings.TrimSpace(message)
		project = normalizePath(project, repoRoot, knownProjects)

		switch code {
		case "RT0000":
			subject := project
			if subject == "" {
				subject = "<unknown>"
			}
			quality = append(quality, finding{rule: "RT-DOC-OFF", subject: subject, message: message, project: project})
			continue
		case "RT9999":
			subject := project
			if subject == "" {
				subject = "<unknown>"
			}
			quality = append(quality, finding{rule: "RT-ANALYZER-ERROR", subject: subject, message: message, project: project})
			continue
		}
		if !containsString(unusedRules, code) {
			continue
		}

		specMatch := specRe.FindStringSubmatch(message)
		if specMatch == nil {
			// A message this gate cannot read is a message this gate cannot
			// judge. It is reported as a quality problem rather than dropped.
			subject := project
			if subject == "" {
				subject = "<unknown>"
			}
			quality = append(quality, finding{rule: "RT-UNREADABLE", subject: subject, message: message, project: project})
			continue
		}

		subject := project + " :: " + specMatch[2]
		key := code + "\x00" + subject
		if seen[key] {
			continue
		}
		seen[key] = true
		findings = append(findings, finding{rule: code, subject: subject, message: message, project: project})
	}
	sort.SliceStable(findings, func(i, j int) bool {
		if findings[i].rule != findings[j].rule {
			return findings[i].rule < findings[j].rule
		}
		return findings[i].subject < findings[j].subject
	})
	sort.SliceStable(quality, func(i, j int) bool {
		if quality[i].rule != quality[j].rule {
			return quality[i].rule < quality[j].rule
		}
		return quality[i].subject < quality[j].subject
	})
	return findings, quality
}

// containsString is the python `in` over a tuple.
func containsString(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// parseBaseline is (rows, findings) read from the baseline file.
func parseBaseline(root string, baselinePath string) ([]baselineRow, []finding) {
	var rows []baselineRow
	var findings []finding
	path := baselinePath
	if !filepath.IsAbs(path) {
		path = filepath.Join(root, path)
	}
	if _, err := os.Stat(path); err != nil {
		findings = append(findings, finding{
			rule:    "RT-UNJUSTIFIED",
			subject: baselinePath,
			message: "the baseline file does not exist. A baseline that is absent is indistinguishable from a baseline that forgives everything.",
		})
		return rows, findings
	}
	data, err := os.ReadFile(path)
	if err != nil {
		findings = append(findings, finding{
			rule:    "RT-UNJUSTIFIED",
			subject: baselinePath,
			message: "the baseline file does not exist. A baseline that is absent is indistinguishable from a baseline that forgives everything.",
		})
		return rows, findings
	}
	for index, raw := range strings.Split(string(data), "\n") {
		lineNo := index + 1
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.Split(line, "|")
		for i := range parts {
			parts[i] = strings.TrimSpace(parts[i])
		}
		if len(parts) != 4 {
			findings = append(findings, finding{
				rule:    "RT-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselinePath, lineNo),
				message: fmt.Sprintf("the row has %d fields; the format is <rule> | <subject> | <reason> | <owner>.", len(parts)),
			})
			continue
		}
		rule, subject, reason, owner := parts[0], parts[1], parts[2], parts[3]
		if !containsString(unusedRules, rule) {
			findings = append(findings, finding{
				rule:    "RT-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselinePath, lineNo),
				message: fmt.Sprintf("the rule %s is not one of %s.", rule, strings.Join(unusedRules, ", ")),
			})
			continue
		}
		if len(reason) < minReasonLength {
			findings = append(findings, finding{
				rule:    "RT-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselinePath, lineNo),
				message: fmt.Sprintf("the reason is %d characters; a reason has to be at least %d, and a URL is not a reason.", len(reason), minReasonLength),
			})
			continue
		}
		if !strings.HasPrefix(owner, "@") {
			findings = append(findings, finding{
				rule:    "RT-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselinePath, lineNo),
				message: fmt.Sprintf("the owner %s is not a '@handle' somebody can be asked about.", gatekit.PyRepr(owner)),
			})
			continue
		}
		rows = append(rows, baselineRow{rule: rule, subject: subject, reason: reason, owner: owner, lineNo: lineNo})
	}
	return rows, findings
}

// knownProjects is every project file in the tree, as repository-relative
// paths.
func knownProjects(root string) []string {
	var found []string
	walkProjects(root, root, &found)
	sort.Strings(found)
	return found
}

// walkProjects collects the project files of a tree.
func walkProjects(root string, dir string, found *[]string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	for _, entry := range entries {
		name := entry.Name()
		full := filepath.Join(dir, name)
		if entry.IsDir() {
			if name == "obj" || name == "bin" || name == ".git" || name == "node_modules" {
				continue
			}
			walkProjects(root, full, found)
			continue
		}
		if !strings.HasSuffix(name, ".csproj") {
			continue
		}
		relative, err := filepath.Rel(root, full)
		if err != nil {
			continue
		}
		*found = append(*found, filepath.ToSlash(relative))
	}
}

// parsedFindings is (findings, quality problems) for one analyzer log.
//
// The scope is the whole solution, including the projects whose package set
// the sibling check-abstractions-contract gate also answers for. The two
// gates ask different questions of those projects — that gate asks whether
// a non-BCL using has a declaration, this one asks whether the compiler binds
// a type from a reference — so dropping either answer would leave a blind
// spot. A project that appears for the first time in the log is in scope too:
// it has no baseline row, so a dead reference in it is a new finding.
func parsedFindings(root string, text string) ([]finding, []finding) {
	projects := knownProjects(root)
	return parseFindings(text, root, projects)
}

// stats is what the scan saw.
type stats struct {
	covered      int
	parsed       int
	baselineRows int
	partial      bool
}

// subjectProject is the project half of a finding's subject. The subject is
// "<project> :: <reference>", and a baseline row about a project that is no
// longer in the tree is stale in any answer, partial or complete.
func subjectProject(subject string) string {
	if index := strings.Index(subject, " :: "); index >= 0 {
		return subject[:index]
	}
	return subject
}

// scan is the gate.
func scan(root string, findingsPath string, coveragePath string, baselinePath string, minProjects int) ([]finding, stats) {
	var findings []finding
	var result stats
	if _, err := os.Stat(findingsPath); err != nil {
		findings = append(findings, finding{
			rule:    "RT-SCAN-BLIND",
			subject: findingsPath,
			message: "the findings file does not exist. The analyzer did not run, or its output was not kept; an absent answer is not a clean answer.",
		})
		return findings, result
	}
	data, err := os.ReadFile(findingsPath)
	if err != nil {
		findings = append(findings, finding{
			rule:    "RT-SCAN-BLIND",
			subject: findingsPath,
			message: "the findings file does not exist. The analyzer did not run, or its output was not kept; an absent answer is not a clean answer.",
		})
		return findings, result
	}
	text := string(data)
	if strings.TrimSpace(text) == "" {
		findings = append(findings, finding{
			rule:    "RT-SCAN-BLIND",
			subject: findingsPath,
			message: "the findings file is empty. The analyzer produced no output at all, which is not the same as producing no findings.",
		})
		return findings, result
	}

	parsed, quality := parsedFindings(root, text)
	findings = append(findings, quality...)
	result.parsed = len(parsed)

	// The coverage file is the answer to "how many projects did the analyzer
	// actually run in".
	if coveragePath != "" {
		if _, err := os.Stat(coveragePath); err == nil {
			coverage, err := os.ReadFile(coveragePath)
			if err == nil {
				covered := 0
				for _, line := range strings.Split(string(coverage), "\n") {
					if strings.TrimSpace(line) != "" {
						covered++
					}
				}
				result.covered = covered
				if covered < minProjects {
					findings = append(findings, finding{
						rule:    "RT-SCAN-BLIND",
						subject: coveragePath,
						message: fmt.Sprintf("the analyzer ran in %d projects; the floor is %d. A scan that silently narrowed must go red, not quiet.", covered, minProjects),
					})
				}
			}
		} else if minProjects > 0 {
			findings = append(findings, finding{
				rule:    "RT-SCAN-BLIND",
				subject: coveragePath,
				message: fmt.Sprintf("no coverage file was given, so the number of projects the analyzer ran in is unknown and the floor of %d cannot be checked.", minProjects),
			})
		}
	} else if minProjects > 0 {
		subject := coveragePath
		if subject == "" {
			subject = "<no coverage file>"
		}
		findings = append(findings, finding{
			rule:    "RT-SCAN-BLIND",
			subject: subject,
			message: fmt.Sprintf("no coverage file was given, so the number of projects the analyzer ran in is unknown and the floor of %d cannot be checked.", minProjects),
		})
	}

	rows, baselineFindings := parseBaseline(root, baselinePath)
	findings = append(findings, baselineFindings...)

	parsedKeys := map[string]bool{}
	for _, item := range parsed {
		parsedKeys[item.key()] = true
	}
	carried := map[string]bool{}
	for _, row := range rows {
		carried[row.key()] = true
	}
	result.baselineRows = len(rows)

	// A cached build restores obj/ and bin/, and MSBuild recompiles only the
	// projects whose sources changed. The analyzer emits RT0002 only for the
	// projects it actually compiled, so that run answers with a STRICT SUBSET
	// of the baseline: every finding it holds is carried, and rows are missing
	// only because their projects were not recompiled. The extreme case is
	// ZERO findings: a change to a .txt or a Go source recompiles no C# at
	// all, so the analyzer emits nothing. Reading either as "the tree no
	// longer produces" is the defect #2129 reverted: run 44035722975 parsed
	// three findings and named 48 rows stale the tree still carries, and
	// run 39736944740 parsed zero. An answer holding at most HALF of the
	// baseline rows, all of the ones it holds carried, is that partial
	// shape. It suppresses the stale finding for rows whose projects are
	// still in the tree; a row about a project that is GONE is stale in any
	// answer, a nearly complete answer still flags its missing row, and a
	// new finding still fails below. An empty findings file is a separate
	// RT-SCAN-BLIND failure above, so "no RT line" is never mistaken for
	// "no answer at all".
	partial := len(parsed)*2 <= len(rows)
	if partial {
		for _, item := range parsed {
			if !carried[item.key()] {
				partial = false
				break
			}
		}
	}
	result.partial = partial
	projects := map[string]bool{}
	for _, project := range knownProjects(root) {
		projects[project] = true
	}
	for _, row := range rows {
		if !parsedKeys[row.key()] {
			if partial && projects[subjectProject(row.subject)] {
				continue
			}
			findings = append(findings, finding{
				rule:    "RT-STALE-BASELINE",
				subject: row.subject,
				message: fmt.Sprintf("the baseline row on line %d carries %s for a finding the tree no longer produces. Delete the row in the same diff that closed the finding.", row.lineNo, row.rule),
			})
		}
	}
	for _, item := range parsed {
		if carried[item.key()] {
			continue
		}
		findings = append(findings, finding{
			rule:    "RT-NEW-FINDING",
			subject: item.subject,
			message: fmt.Sprintf("%s says %s. The candidate has not been looked at: it is either a dead reference, which this gate exists to find, or a false positive, which has to be written into the baseline with the reason it is one.", item.rule, item.message),
		})
	}

	sort.SliceStable(findings, func(i, j int) bool {
		if findings[i].rule != findings[j].rule {
			return findings[i].rule < findings[j].rule
		}
		return findings[i].subject < findings[j].subject
	})
	return findings, result
}

// report is the gate's output.
func report(findings []finding, baselinePath string, result stats) int {
	if len(findings) == 0 {
		fmt.Println("OK: no unused reference outside the baseline.")
		fmt.Printf("  the analyzer ran in %d project(s); %d finding(s) parsed.\n",
			result.covered, result.parsed)
		if result.partial {
			fmt.Printf("  the answer is PARTIAL: %d of %d baseline row(s) re-emitted by this incremental build; missing rows are not called stale.\n",
				result.parsed, result.baselineRows)
		}
		return gate.Green
	}
	byRule := map[string][]finding{}
	var ruleOrder []string
	for _, item := range findings {
		if _, ok := byRule[item.rule]; !ok {
			ruleOrder = append(ruleOrder, item.rule)
		}
		byRule[item.rule] = append(byRule[item.rule], item)
	}
	fmt.Printf("ReferenceTrimmer gate: %d finding(s).\n", len(findings))
	fmt.Printf("  the analyzer ran in %d project(s); %d finding(s) parsed.\n",
		result.covered, result.parsed)
	sort.Strings(ruleOrder)
	for _, rule := range ruleOrder {
		fmt.Printf("\n%s (%d):\n", rule, len(byRule[rule]))
		for _, item := range byRule[rule] {
			fmt.Printf("  - %s\n", item.subject)
			fmt.Printf("      %s\n", item.message)
		}
	}
	fmt.Printf("\nThe baseline is %s.\n", baselinePath)
	return gate.Red
}
