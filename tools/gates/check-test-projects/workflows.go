package testprojects

// workflows.go -- the CI lists: the places in a workflow that NAME a test
// project to run it.
//
// A workflow file is YAML, and no YAML parser is in the standard library,
// so this reads the shape the repository's own workflows use: two-space job
// headers under a top-level `jobs:` key, folded block scalars for the
// project lists, and a shell `for p in \ ... ; do` list for the coverage
// job.

import (
	"os"
	"os/exec"
	"regexp"
	"strings"
)

// The workflow files the gate reads.
const (
	ciWorkflow      = ".github/workflows/ci.yml"
	osWorkflow      = ".github/workflows/test-os.yml"
	nightlyWorkflow = ".github/workflows/nightly.yml"
	nukeTargetPath  = "build/_build/Targets/TestTarget.cs"
)

// The patterns of the workflow reader.
var (
	foldedKeyRe     = regexp.MustCompile(`^(\s*)([A-Za-z0-9_.\-]+):\s*(?:>|\|)[-+]?\s*$`)
	dashFoldedKeyRe = regexp.MustCompile(`^(\s*)-\s*([A-Za-z0-9_.\-]+):\s*(?:>|\|)[-+]?\s*$`)
	forListRe       = regexp.MustCompile(`^\s*for\s+([A-Za-z0-9_]+)\s+in\s+\\?\s*$`)
	opensKeyRe      = regexp.MustCompile(`^[A-Za-z0-9_.\-]+:`)
	projectInPathRe = regexp.MustCompile(`tests/([A-Za-z0-9_.]+)/bin/Release/net10\.0/([A-Za-z0-9_.]+)\.dll`)
	jobHeaderRe     = regexp.MustCompile(`^  ([A-Za-z0-9_.\-]+):\s*$`)
	jobsKeyRe       = regexp.MustCompile(`^jobs:\s*$`)
	jobIfRe         = regexp.MustCompile(`^    if:\s*(.*)$`)
	includeRe       = regexp.MustCompile(`^\s*include:\s*$`)
	shardRe         = regexp.MustCompile(`^\s*-\s*shard:\s*([A-Za-z0-9_.\-]+)\s*$`)
)

// ciList is one list of test projects.
type ciList struct {
	id                string
	file              string
	job               string
	kind              string
	primary           bool
	projects          []string
	condition         string
	appliesFilter     bool
	projectConditions map[string][]string
}

// indentOf is the indentation of a line.
func indentOf(line string) int {
	return len(line) - len(strings.TrimLeft(line, " \t"))
}

// splitLines splits a text into its lines, the way python's str.splitlines
// does.
func splitLines(text string) []string {
	var lines []string
	start := 0
	runes := []rune(text)
	for index := 0; index < len(runes); index++ {
		boundary := false
		width := 1
		switch runes[index] {
		case '\n':
			boundary = true
		case '\r':
			boundary = true
			if index+1 < len(runes) && runes[index+1] == '\n' {
				width = 2
			}
		case '\v', '\f', '\u0085', '\u2028', '\u2029', '\u001c', '\u001d', '\u001e':
			boundary = true
		}
		if !boundary {
			continue
		}
		lines = append(lines, string(runes[start:index]))
		index += width - 1
		start = index + 1
	}
	if start < len(runes) {
		lines = append(lines, string(runes[start:]))
	}
	return lines
}

// workflowJobs returns the jobs of a workflow, as {name: [body lines]}.
func workflowJobs(text string) map[string][]string {
	jobs := map[string][]string{}
	inJobs := false
	current := ""
	for _, line := range splitLines(text) {
		if jobsKeyRe.MatchString(line) {
			inJobs = true
			continue
		}
		if !inJobs {
			continue
		}
		header := jobHeaderRe.FindStringSubmatch(line)
		if header != nil {
			current = header[1]
			jobs[current] = []string{}
			continue
		}
		if current != "" {
			jobs[current] = append(jobs[current], line)
		}
	}
	return jobs
}

// jobCondition returns the job-level `if:` of a job, as written, or "" when
// it has none.
func jobCondition(jobLines []string) string {
	for index, line := range jobLines {
		match := jobIfRe.FindStringSubmatch(line)
		if match == nil {
			continue
		}
		value := strings.TrimSpace(match[1])
		if value == ">-" || value == ">" || value == "|" || value == "|-" || value == "|+" {
			var parts []string
			baseIndent := indentOf(line)
			for _, follow := range jobLines[index+1:] {
				if strings.TrimSpace(follow) == "" {
					continue
				}
				if indentOf(follow) <= baseIndent {
					break
				}
				parts = append(parts, strings.TrimSpace(follow))
			}
			return strings.Join(parts, " ")
		}
		return value
	}
	return ""
}

// jobAppliesFilter reports whether a job passes a treenode filter to the
// projects it runs.
func jobAppliesFilter(jobLines []string) bool {
	for _, line := range jobLines {
		if strings.Contains(line, "--treenode-filter") {
			return true
		}
	}
	return false
}

// foldedBlock returns the tokens of the folded block scalar that starts at
// `lines[index]`.
//
// The block runs until a line at the opening line's own indentation, or a
// line that opens a new key.
func foldedBlock(lines []string, index int) []string {
	match := foldedKeyRe.FindStringSubmatch(lines[index])
	if match == nil {
		match = dashFoldedKeyRe.FindStringSubmatch(lines[index])
	}
	if match == nil {
		return nil
	}
	baseIndent := len(match[1])
	var tokens []string
	for _, follow := range lines[index+1:] {
		if strings.TrimSpace(follow) == "" {
			continue
		}
		if indentOf(follow) <= baseIndent {
			break
		}
		stripped := strings.TrimSpace(follow)
		if opensKeyRe.MatchString(stripped) {
			break
		}
		tokens = append(tokens, stripped)
	}
	return tokens
}

// shellList returns the tokens of the `for p in \ ... ; do` list at
// `lines[index]`.
func shellList(lines []string, index int) []string {
	var tokens []string
	for _, follow := range lines[index+1:] {
		stripped := strings.TrimSpace(follow)
		if strings.HasSuffix(stripped, "; do") {
			tokens = append(tokens, strings.TrimSpace(strings.TrimSuffix(stripped, "; do")))
			break
		}
		if strings.HasSuffix(stripped, "\\") {
			stripped = strings.TrimSpace(strings.TrimSuffix(stripped, "\\"))
		}
		if stripped != "" {
			tokens = append(tokens, stripped)
		}
	}
	var kept []string
	for _, token := range tokens {
		if token != "" {
			kept = append(kept, token)
		}
	}
	return kept
}

// matrixProjects returns the `projects:` lists of a job's matrix `include:`
// block, with the shard label of each.
func matrixProjects(jobLines []string) []struct {
	Label  string
	Tokens []string
	Index  int
} {
	includeAt := -1
	for index, line := range jobLines {
		if includeRe.MatchString(line) {
			includeAt = index
			break
		}
	}
	if includeAt < 0 {
		return nil
	}

	type entry = struct {
		Label  string
		Tokens []string
		Index  int
	}
	var results []entry
	label := ""
	for index := includeAt + 1; index < len(jobLines); index++ {
		line := jobLines[index]
		if strings.TrimSpace(line) == "" {
			continue
		}
		if indentOf(line) <= indentOf(jobLines[includeAt]) {
			break
		}
		shard := shardRe.FindStringSubmatch(line)
		if shard != nil {
			label = shard[1]
			continue
		}
		opened := foldedKeyRe.FindStringSubmatch(line)
		key := ""
		if opened != nil {
			key = opened[2]
		} else {
			dash := dashFoldedKeyRe.FindStringSubmatch(line)
			if dash != nil {
				key = dash[2]
			}
		}
		if key == "" {
			continue
		}
		if key != "projects" {
			continue
		}
		tokens := foldedBlock(jobLines, index)
		if len(tokens) > 0 {
			results = append(results, entry{Label: label, Tokens: tokens, Index: index})
		}
		label = ""
	}
	return results
}

// coverageList returns the `for p in \ ... ; do` list of the coverage job.
func coverageList(jobLines []string) []string {
	for index, line := range jobLines {
		if forListRe.MatchString(line) {
			tokens := shellList(jobLines, index)
			if len(tokens) > 0 {
				return tokens
			}
		}
	}
	return nil
}

// perfGateProjects returns the projects the perf-gate job runs, read out of
// its dll paths.
func perfGateProjects(jobLines []string) []string {
	var found []string
	for _, line := range jobLines {
		for _, match := range projectInPathRe.FindAllStringSubmatch(line, -1) {
			if match[1] == match[2] && !containsString(found, match[1]) {
				found = append(found, match[1])
			}
		}
	}
	return found
}

// projectConditions returns every `if:` in a workflow whose text names this
// project.
//
// A job-level `if:` applies to a whole list. This is the narrower shape: a
// condition that mentions one project by name.
func projectConditions(text string, project string) []string {
	var found []string
	for _, line := range splitLines(text) {
		stripped := strings.TrimSpace(line)
		if !strings.HasPrefix(stripped, "if:") {
			continue
		}
		value := strings.TrimSpace(stripped[3:])
		if strings.Contains(value, project) {
			found = append(found, value)
		}
	}
	return found
}

// perProjectConditions returns the conditions that name one project, keyed
// by project name.
func perProjectConditions(text string, projects []string) map[string][]string {
	conditions := map[string][]string{}
	for _, project := range projects {
		conditions[project] = projectConditions(text, project)
	}
	return conditions
}

// containsString reports whether a list holds a value.
func containsString(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// collectLists returns every list in the tree that names test projects to
// run, and the notes the reader produced.
//
// The four shard lists are PRIMARY. They are the repository's definition of
// "a test project runs in CI". Two more lists exist and are reported but do
// not count as CI claims, because neither is a gate.
func collectLists(root string) ([]ciList, []string) {
	var lists []ciList
	var notes []string

	add := func(id string, path string, job string, projects []string, condition string, primary bool, appliesFilter bool) {
		lists = append(lists, ciList{
			id:            id,
			file:          path,
			job:           job,
			kind:          "ci",
			primary:       primary,
			projects:      projects,
			condition:     condition,
			appliesFilter: appliesFilter,
		})
	}

	ciText := read(filepathJoin(root, ciWorkflow))
	if ciText == "" {
		notes = append(notes, ciWorkflow+" could not be read, so the shard, coverage and perf-gate lists were not seen at all. A gate that examined nothing cannot report that everything is fine.")
	} else {
		jobs := workflowJobs(ciText)
		testJob, hasTest := jobs["test"]
		if !hasTest {
			notes = append(notes, ciWorkflow+" has no `test` job, so the shard lists were not seen. A list that moves to another workflow file has to be added to the table in this file.")
			add("ci.yml::test", ciWorkflow, "test", nil, "", true, false)
		} else {
			condition := jobCondition(testJob)
			applies := jobAppliesFilter(testJob)
			shards := matrixProjects(testJob)
			if len(shards) == 0 {
				notes = append(notes, ciWorkflow+"'s `test` job carries no `projects:` list, so no shard list was seen.")
				add("ci.yml::test", ciWorkflow, "test", nil, condition, true, applies)
			}
			for _, shard := range shards {
				label := shard.Label
				if label == "" {
					label = "matrix"
				}
				add("ci:test:"+label, ciWorkflow, "test", shard.Tokens, condition, true, applies)
			}
		}

		coverageJob, hasCoverage := jobs["coverage"]
		coverageFile := ciWorkflow
		if !hasCoverage {
			// The coverage LIST can live in another workflow file.
			nightlyText := read(filepathJoin(root, nightlyWorkflow))
			if nightlyText != "" {
				if nightlyJob, ok := workflowJobs(nightlyText)["coverage"]; ok {
					coverageJob = nightlyJob
					coverageFile = nightlyWorkflow
					hasCoverage = true
				}
			}
		}
		if !hasCoverage {
			notes = append(notes, ciWorkflow+" and "+nightlyWorkflow+" have no `coverage` job. A list that moves to another workflow file has to be added to the table in this file.")
		} else {
			// The list is a shell `for p in` loop today. A matrix-shaped
			// list is accepted too.
			tokens := coverageList(coverageJob)
			if len(tokens) == 0 {
				for _, block := range matrixProjects(coverageJob) {
					tokens = append(tokens, block.Tokens...)
				}
			}
			add("ci:coverage", coverageFile, "coverage", tokens, jobCondition(coverageJob), false, jobAppliesFilter(coverageJob))
		}

		perfJob, hasPerf := jobs["perf-gate"]
		if !hasPerf {
			notes = append(notes, ciWorkflow+" has no `perf-gate` job.")
		} else {
			add("ci:perf-gate", ciWorkflow, "perf-gate", perfGateProjects(perfJob), jobCondition(perfJob), false, jobAppliesFilter(perfJob))
		}
	}

	osText := read(filepathJoin(root, osWorkflow))
	if osText == "" {
		notes = append(notes, osWorkflow+" could not be read, so the OS matrix list was not seen.")
	} else {
		jobs := workflowJobs(osText)
		osJob, hasOS := jobs["test-os"]
		if !hasOS {
			notes = append(notes, osWorkflow+" has no `test-os` job. A list that moves to another workflow file has to be added to the table in this file.")
		} else {
			var tokens []string
			for _, block := range matrixProjects(osJob) {
				tokens = append(tokens, block.Tokens...)
			}
			add("os:test-os", osWorkflow, "test-os", tokens, jobCondition(osJob), false, jobAppliesFilter(osJob))
		}
	}

	return lists, notes
}

// nukeTarget is what the NUKE `Test` target would run, and what it
// excludes.
type nukeTarget struct {
	excluded []string
	suffix   string
}

// nukeTestTarget reads the NUKE `Test` target.
//
// The target is DERIVED from the solution: every project whose name ends in
// `Tests`, minus the ones whose name contains one of the excluded
// substrings. The substrings are read out of the source rather than
// restated here.
func nukeTestTarget(root string) *nukeTarget {
	text := read(filepathJoin(root, nukeTargetPath))
	if text == "" {
		return nil
	}
	target := &nukeTarget{suffix: "Tests"}
	if match := regexp.MustCompile(`ExcludedSubstrings\s*=\s*\[(.*?)\]`).FindStringSubmatch(text); match != nil {
		for _, name := range regexp.MustCompile(`"([^"]+)"`).FindAllStringSubmatch(match[1], -1) {
			target.excluded = append(target.excluded, name[1])
		}
	}
	if match := regexp.MustCompile(`Name\.EndsWith\("([^"]+)"`).FindStringSubmatch(text); match != nil {
		target.suffix = match[1]
	}
	return target
}

// nukeSelected returns the projects the NUKE `Test` target would run.
func nukeSelected(projects map[string]*project, target *nukeTarget) []string {
	if target == nil {
		return nil
	}
	names := make([]string, 0, len(projects))
	for name := range projects {
		names = append(names, name)
	}
	sortStrings(names)
	var selected []string
	for _, name := range names {
		if !strings.HasSuffix(name, target.suffix) {
			continue
		}
		if containsString(target.excluded, "") {
			continue
		}
		excluded := false
		for _, part := range target.excluded {
			if strings.Contains(name, part) {
				excluded = true
				break
			}
		}
		if excluded {
			continue
		}
		selected = append(selected, name)
	}
	return selected
}

// sortStrings sorts a list of names.
func sortStrings(names []string) {
	for i := 1; i < len(names); i++ {
		for j := i; j > 0 && names[j] < names[j-1]; j-- {
			names[j], names[j-1] = names[j-1], names[j]
		}
	}
}

// filepathJoin joins a path the way python's os.path.join does.
func filepathJoin(root string, relative string) string {
	if strings.HasPrefix(relative, "/") {
		return relative
	}
	return root + "/" + relative
}

// gitLsFiles lists the tracked paths, and reports whether git answered.
func gitLsFiles(root string, paths ...string) ([]string, bool) {
	arguments := append([]string{"ls-files", "--"}, paths...)
	command := exec.Command("git", arguments...)
	command.Dir = root
	command.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	out, err := command.Output()
	if err != nil {
		return nil, false
	}
	var tracked []string
	for _, line := range splitLines(string(out)) {
		if line == "" {
			continue
		}
		if strings.HasSuffix(line, ".csproj") {
			tracked = append(tracked, line)
		}
	}
	sortStrings(tracked)
	return tracked, true
}
