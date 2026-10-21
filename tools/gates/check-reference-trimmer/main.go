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
		parsed, _, _ := parsedFindings(root, string(data))
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

// guardedScope is the scope of the sibling gate.
//
// The list is read from the sibling gate rather than copied, so it cannot
// drift: if that gate widens its scope, this one follows on the next run.
func guardedScope(root string) []string {
	data, err := os.ReadFile(filepath.Join(root, "tools", "gates", "check-abstractions-contract", "main.go"))
	if err != nil {
		return nil
	}
	text := string(data)
	start := strings.Index(text, "var scope = []string{")
	if start < 0 {
		return nil
	}
	rest := text[start+len("var scope = []string{"):]
	end := strings.Index(rest, "}")
	if end < 0 {
		return nil
	}
	var scope []string
	for _, match := range regexp.MustCompile(`"([^"]*)"`).FindAllStringSubmatch(rest[:end], -1) {
		scope = append(scope, match[1])
	}
	return scope
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

// parsedFindings is (findings, quality problems, excluded count) for one
// analyzer log.
//
// The exclusion is the projects whose package set another gate already
// answers for. Their findings are dropped here, not silenced: the count is
// reported, so a reader can see that the gate did not look at them rather
// than being left to assume it did.
func parsedFindings(root string, text string) ([]finding, []finding, int) {
	projects := knownProjects(root)
	parsed, quality := parseFindings(text, root, projects)
	guarded := guardedScope(root)
	excluded := 0
	if len(guarded) > 0 {
		var kept []finding
		for _, item := range parsed {
			directory := item.project
			if idx := strings.LastIndexByte(directory, '/'); idx >= 0 {
				directory = directory[:idx]
			}
			skip := false
			for _, scope := range guarded {
				if directory == scope || strings.HasPrefix(directory, scope+"/") {
					skip = true
					break
				}
			}
			if skip {
				excluded++
				continue
			}
			kept = append(kept, item)
		}
		parsed = kept
	}
	return parsed, quality, excluded
}

// stats is what the scan saw.
type stats struct {
	covered  int
	parsed   int
	excluded int
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

	parsed, quality, excluded := parsedFindings(root, text)
	findings = append(findings, quality...)
	result.parsed = len(parsed)
	result.excluded = excluded

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
	for _, row := range rows {
		if !parsedKeys[row.key()] {
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
		fmt.Printf("  the analyzer ran in %d project(s); %d finding(s) parsed; %d dropped as another gate's scope.\n",
			result.covered, result.parsed, result.excluded)
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
	fmt.Printf("  the analyzer ran in %d project(s); %d finding(s) parsed; %d dropped as another gate's scope.\n",
		result.covered, result.parsed, result.excluded)
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
