package testprojects

// analyze.go -- the analysis: which test projects a CI run discovers, and
// which it does not.

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// The baseline of the known holes.
const (
	baselineFile    = "tools/test-project-baseline.txt"
	minReasonLength = 40
)

// The rules a baseline row may carry.
var baselineRules = []string{
	"PROJECT-UNCLAIMED",
	"PROJECT-EMPTY",
	"TEST-DUPLICATED",
	"TEST-REFERENCE-UNUSED",
	"SHARD-UNWIRED",
	"CLAIM-CONDITIONAL",
	"LIST-STALE",
}

// urlOnly is a reason that is only a URL.
var urlOnlyRe = regexp.MustCompile(`^\W*https?://\S+[.,;]?\W*$`)

// finding is one thing the gate found.
//
// `rule` names the class of the finding, `subject` the thing it is about,
// and the two together are the key a baseline row has to match.
type finding struct {
	rule     string
	subject  string
	message  string
	blocking bool
}

// String renders a finding the way the python original did.
func (f finding) String() string {
	return f.rule + "  " + f.subject + ": " + f.message
}

// key is the baseline key of a finding.
func (f finding) key() string {
	return f.rule + "\x00" + f.subject
}

// baselineRow is one row of the baseline file.
type baselineRow struct {
	rule    string
	subject string
	reason  string
	owner   string
	lineNo  int
}

// key is the baseline key of a row.
func (r baselineRow) key() string {
	return r.rule + "\x00" + r.subject
}

// parseBaseline reads the baseline file.
//
// A row with the wrong number of fields, an unknown rule, an empty reason,
// an empty owner or a reason that is only a URL is a finding and not a row.
func parseBaseline(root string) ([]baselineRow, []finding) {
	var rows []baselineRow
	var findings []finding
	path := filepath.Join(root, filepath.FromSlash(baselineFile))
	if _, err := os.Stat(path); err != nil {
		findings = append(findings, finding{
			rule:    "BASELINE-UNJUSTIFIED",
			subject: baselineFile,
			message: "the baseline file does not exist. A baseline that is absent " +
				"is indistinguishable from a baseline that forgives everything, so " +
				"its absence is reported rather than read as 'no known holes'.",
			blocking: true,
		})
		return rows, findings
	}
	for number, raw := range splitLines(read(path)) {
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
				rule:    "BASELINE-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselineFile, number+1),
				message: fmt.Sprintf(
					"expected 4 '|'-separated fields (<rule> | <subject> | <reason> | <owner>), got %d: %s",
					len(parts), pythonRepr(raw)),
				blocking: true,
			})
			continue
		}
		rule, subject, reason, owner := parts[0], parts[1], parts[2], parts[3]
		if !containsString(baselineRules, rule) {
			findings = append(findings, finding{
				rule:    "BASELINE-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselineFile, number+1),
				message: fmt.Sprintf("%s is not a rule this gate produces. The rules are: %s.",
					pythonRepr(rule), strings.Join(baselineRules, ", ")),
				blocking: true,
			})
			continue
		}
		if subject == "" || reason == "" || owner == "" {
			var missing []string
			if subject == "" {
				missing = append(missing, "subject")
			}
			if reason == "" {
				missing = append(missing, "reason")
			}
			if owner == "" {
				missing = append(missing, "owner")
			}
			findings = append(findings, finding{
				rule:    "BASELINE-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselineFile, number+1),
				message: fmt.Sprintf(
					"the %s field is empty. A row without a reason is a permission nobody can justify.",
					strings.Join(missing, ", ")),
				blocking: true,
			})
			continue
		}
		if len(reason) < minReasonLength {
			findings = append(findings, finding{
				rule:    "BASELINE-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselineFile, number+1),
				message: fmt.Sprintf(
					"the reason is %d characters; the floor is %d. A reason that short says nothing a reviewer can check.",
					len(reason), minReasonLength),
				blocking: true,
			})
			continue
		}
		if urlOnlyRe.MatchString(reason) {
			findings = append(findings, finding{
				rule:     "BASELINE-UNJUSTIFIED",
				subject:  fmt.Sprintf("%s:%d", baselineFile, number+1),
				message:  "the reason is a URL. A URL says where the debt is, not why it is allowed here.",
				blocking: true,
			})
			continue
		}
		if !strings.HasPrefix(owner, "@") {
			findings = append(findings, finding{
				rule:    "BASELINE-UNJUSTIFIED",
				subject: fmt.Sprintf("%s:%d", baselineFile, number+1),
				message: fmt.Sprintf(
					"the owner %s is not a '@handle'. A row without an accountable owner is an allowance with no one to ask.",
					pythonRepr(owner)),
				blocking: true,
			})
			continue
		}
		rows = append(rows, baselineRow{
			rule:    rule,
			subject: subject,
			reason:  reason,
			owner:   owner,
			lineNo:  number + 1,
		})
	}
	return rows, findings
}

// pythonRepr renders a string the way python's repr does.
func pythonRepr(value string) string {
	var out strings.Builder
	out.WriteByte('\'')
	for _, r := range value {
		switch r {
		case '\'':
			out.WriteString(`\'`)
		case '\\':
			out.WriteString(`\\`)
		case '\n':
			out.WriteString(`\n`)
		case '\r':
			out.WriteString(`\r`)
		case '\t':
			out.WriteString(`\t`)
		default:
			if r < 0x20 {
				fmt.Fprintf(&out, `\x%02x`, r)
			} else {
				out.WriteRune(r)
			}
		}
	}
	out.WriteByte('\'')
	return out.String()
}

// census is everything the gate derived.
type census struct {
	projects      map[string]*project
	universe      map[string]*project
	lists         []ciList
	claims        map[string][]int
	contexts      map[string][]string
	incoming      map[string][]string
	notes         []string
	inherited     bool
	nukeTarget    *nukeTarget
	nukeRuns      []string
	solutionTests []string
	totals        map[string]int
}

// analyse returns the census and the findings.
func analyse(root string) (census, []finding) {
	var findings []finding
	result := census{projects: map[string]*project{}}

	projects, inherited, hasDefault := collectProjects(root)
	result.projects = projects
	result.inherited = inherited

	solution := solutionProjects(root)
	solutionTests := map[string]bool{}
	for _, path := range solution {
		if strings.HasPrefix(path, testsDir+"/") && strings.HasSuffix(path, ".csproj") {
			solutionTests[strings.TrimSuffix(filepath.Base(path), ".csproj")] = true
		}
	}

	lists, listNotes := collectLists(root)
	result.lists = lists
	result.notes = append(result.notes, listNotes...)

	workflowText := map[string]string{}
	for index := range lists {
		path := lists[index].file
		if _, ok := workflowText[path]; !ok {
			workflowText[path] = read(filepath.Join(root, filepath.FromSlash(path)))
		}
		lists[index].projectConditions = perProjectConditions(workflowText[path], lists[index].projects)
	}

	if !hasDefault {
		findings = append(findings, finding{
			rule:    "SCAN-BLIND",
			subject: testsProps,
			message: fmt.Sprintf(
				"%s does not say what IsTestProject inherits, so the gate cannot tell a test project "+
					"from a fixture library. The props file is where that default lives; read it there "+
					"rather than assuming it.", testsProps),
			blocking: true,
		})
	}

	// The universe: a project under tests/ that the solution carries.
	universe := map[string]*project{}
	for name, project := range projects {
		if !project.isTestProject {
			continue
		}
		if !solutionTests[name] {
			findings = append(findings, finding{
				rule:    "PROJECT-NOT-IN-SOLUTION",
				subject: name,
				message: fmt.Sprintf(
					"%s is a test project but %s does not carry it, so no CI job builds it and no CI job "+
						"can run it. Add it to the solution, or delete it.", project.path, solutionFile),
				blocking: true,
			})
			continue
		}
		universe[name] = project
	}
	result.universe = universe

	names := make([]string, 0, len(solutionTests))
	for name := range solutionTests {
		names = append(names, name)
	}
	sortStrings(names)
	for _, name := range names {
		if _, ok := projects[name]; !ok {
			findings = append(findings, finding{
				rule:    "SOLUTION-DRIFT",
				subject: name,
				message: fmt.Sprintf(
					"%s carries %s, but there is no such project under tests/. The solution names a "+
						"project the tree does not have.", solutionFile, name),
				blocking: true,
			})
		}
	}

	// The claims.
	claims := map[string][]int{}
	for name := range universe {
		claims[name] = []int{}
	}
	for index, entry := range lists {
		if len(entry.projects) == 0 {
			findings = append(findings, finding{
				rule:    "SCAN-BLIND",
				subject: entry.id,
				message: fmt.Sprintf(
					"the list %s in %s parsed to zero entries. A list that names nothing was either "+
						"emptied on purpose or stopped matching, and the gate cannot tell the two apart.",
					entry.id, entry.file),
				blocking: true,
			})
		}
		for _, token := range entry.projects {
			if _, ok := claims[token]; ok {
				claims[token] = append(claims[token], index)
				continue
			}
			if project, ok := projects[token]; ok && !project.isTestProject {
				result.notes = append(result.notes, fmt.Sprintf(
					"%s names %s, which is not a test project (IsTestProject is false there). It is not "+
						"counted as a claim.", entry.id, token))
				continue
			}
			findings = append(findings, finding{
				rule:    "LIST-STALE",
				subject: entry.id + ":" + token,
				message: fmt.Sprintf(
					"the list %s in %s names %s, and there is no such test project in the tree. A stale "+
						"entry is a list that lies about what it runs.", entry.id, entry.file, token),
				blocking: true,
			})
		}
	}
	result.claims = claims

	// The reference graph between test projects.
	graph := projectGraph(root)
	incoming := map[string][]string{}
	for name := range universe {
		incoming[name] = []string{}
	}
	universeNames := make([]string, 0, len(universe))
	for name := range universe {
		universeNames = append(universeNames, name)
	}
	sortStrings(universeNames)
	for _, name := range universeNames {
		for _, item := range graph[name] {
			if _, ok := universe[item.name]; !ok {
				continue
			}
			if !isRuntimeReference(item.attributes) {
				continue
			}
			incoming[item.name] = append(incoming[item.name], name)
		}
	}
	result.incoming = incoming

	// The execution contexts each test project's classes are discovered in.
	contexts := map[string][]string{}
	for _, name := range universeNames {
		var found []string
		if len(claims[name]) > 0 {
			found = append(found, name)
		}
		for _, referrer := range incoming[name] {
			if len(claims[referrer]) > 0 {
				found = append(found, referrer)
			}
		}
		contexts[name] = found
	}
	result.contexts = contexts

	// A project named by two lists is executed by two jobs. That is waste,
	// not a hole, so it is reported and does not fail the gate.
	var repeated []string
	for name, entries := range claims {
		if len(entries) > 1 {
			repeated = append(repeated, name)
		}
	}
	sortStrings(repeated)
	if len(repeated) > 0 {
		result.notes = append(result.notes, fmt.Sprintf(
			"%d project(s) are named by more than one list, so they are executed by more than one job: %s",
			len(repeated), strings.Join(repeated, ", ")))
	}

	// The projects that carry tests and that a CI run discovers.
	var exercisers []string
	for _, name := range universeNames {
		if universe[name].tests > 0 && len(contexts[name]) > 0 {
			exercisers = append(exercisers, name)
		}
	}

	// The helper libraries: the test projects with no tests of their own
	// that a project carrying tests consumes.
	consumedByTests := map[string]bool{}
	for _, name := range exercisers {
		seen := map[string]bool{}
		stack := []string{name}
		for len(stack) > 0 {
			current := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			for _, item := range graph[current] {
				if _, ok := universe[item.name]; !ok {
					continue
				}
				if seen[item.name] {
					continue
				}
				if !isRuntimeReference(item.attributes) {
					continue
				}
				seen[item.name] = true
				consumedByTests[item.name] = true
				stack = append(stack, item.name)
			}
		}
	}

	// The holes.
	for _, name := range universeNames {
		project := universe[name]
		entries := claims[name]
		foundIn := contexts[name]
		if project.tests == 0 {
			if !consumedByTests[name] {
				findings = append(findings, finding{
					rule:    "PROJECT-EMPTY",
					subject: name,
					message: fmt.Sprintf(
						"%s is a test project (IsTestProject is not false there) but carries no [Test] "+
							"method, and no project that carries tests consumes it. It is in the solution, "+
							"so it is compiled on every PR, and nothing runs it and nothing uses it. Either "+
							"give it tests, or make it a helper library that a test project consumes.", name),
					blocking: true,
				})
			}
			continue
		}
		if len(foundIn) == 0 {
			findings = append(findings, finding{
				rule:    "PROJECT-UNCLAIMED",
				subject: name,
				message: fmt.Sprintf(
					"no CI list names %s and no project that CI runs references it, so TUnit never "+
						"discovers its %d [Test] method(s). CI would stay green. Add it to a shard list "+
						"in %s, or record why it may stay out.", name, project.tests, ciWorkflow),
				blocking: true,
			})
			continue
		}
		if len(foundIn) > 1 {
			closure := assemblyClosure(graph, name, nil)
			for key := range universe {
				delete(closure, key)
			}
			product := sortedSet(closure)
			productNote := ""
			if len(product) > 0 {
				productNote = fmt.Sprintf(
					" The referenced project's own closure carries %d product assembl%s (%s), and every "+
						"project that references it inherits them.",
					len(product), plural(len(product)), strings.Join(product, ", "))
			}
			findings = append(findings, finding{
				rule:    "TEST-DUPLICATED",
				subject: name,
				message: fmt.Sprintf(
					"TUnit discovers a referenced test project's classes in every project that "+
						"references it, so %s's %d [Test] method(s) run %d times per CI run (%d "+
						"executions) instead of once. The contexts are: %s.%s Reference the project "+
						"from one project only, or apply a treenode filter that names its classes in "+
						"one of them.",
					name, project.tests, len(foundIn), project.tests*len(foundIn),
					strings.Join(foundIn, ", "), productNote),
				blocking: true,
			})
		}
		var unconditional []int
		for _, index := range entries {
			entry := lists[index]
			if entry.primary || (entry.condition == "" && len(entry.projectConditions[name]) == 0) {
				unconditional = append(unconditional, index)
			}
		}
		if len(entries) > 0 && len(unconditional) == 0 {
			var conditions []string
			for _, index := range entries {
				entry := lists[index]
				condition := fmt.Sprintf("%s: %s", entry.id, entry.condition)
				if len(entry.projectConditions[name]) > 0 {
					condition += "; " + strings.Join(entry.projectConditions[name], "; ")
				}
				conditions = append(conditions, condition)
			}
			findings = append(findings, finding{
				rule:    "CLAIM-CONDITIONAL",
				subject: name,
				message: fmt.Sprintf(
					"the only CI list that names %s runs only under a condition, so the project may not "+
						"execute at all. %s. Claim it from a list that always runs, or record the "+
						"deliberate scoping in %s.", name, strings.Join(conditions, "; "), baselineFile),
				blocking: true,
			})
		}
	}

	// A reference from one test project to another that uses none of its
	// types.
	for _, name := range universeNames {
		for _, item := range graph[name] {
			target, ok := universe[item.name]
			if !ok || item.name == name {
				continue
			}
			if !isRuntimeReference(item.attributes) {
				continue
			}
			types := declaredTypes(root, target.dir)
			if usesTypes(root, universe[name].dir, types) {
				continue
			}
			without := assemblyClosure(graph, name, map[string]bool{item.name: true})
			draggedSet := assemblyClosure(graph, item.name, nil)
			for key := range without {
				delete(draggedSet, key)
			}
			dragged := sortedSet(draggedSet)
			var product []string
			for _, entry := range dragged {
				if !strings.HasSuffix(entry, "Tests") {
					product = append(product, entry)
				}
			}
			duplicated := target.tests > 0

			// A reference that costs nothing is not a finding.
			if len(product) == 0 && !duplicated {
				result.notes = append(result.notes, fmt.Sprintf(
					"%s references %s and names none of its types, but the reference costs nothing: "+
						"the project carries no tests and adds no assembly to the closure. That is what "+
						"a deliberate re-export looks like; the gate cannot tell it from a pointless edge.",
					name, item.name))
				continue
			}

			var costs []string
			if duplicated {
				costs = append(costs, fmt.Sprintf("it makes %s's %d [Test] method(s) run inside %s as well",
					item.name, target.tests, name))
			}
			if len(product) > 0 {
				costs = append(costs, fmt.Sprintf("it drags %d product assembl%s into %s's closure (%s)",
					len(product), plural(len(product)), name, strings.Join(product, ", ")))
			}
			findings = append(findings, finding{
				rule:    "TEST-REFERENCE-UNUSED",
				subject: name + " -> " + item.name,
				message: fmt.Sprintf(
					"%s references the test project %s and names none of its types, so the reference "+
						"buys nothing, and it is not free: %s. Drop the reference, after checking that "+
						"it carries no package or analyzer the project still needs.",
					name, item.name, strings.Join(costs, "; ")),
				blocking: true,
			})
		}
	}

	// A treenode partition on disk that no CI list applies.
	for _, name := range universeNames {
		if !definesPartition(root, universe[name].dir) {
			continue
		}
		for _, index := range claims[name] {
			entry := lists[index]
			if entry.appliesFilter {
				continue
			}
			findings = append(findings, finding{
				rule:    "SHARD-UNWIRED",
				subject: name,
				message: fmt.Sprintf(
					"%s defines a treenode-filter partition of itself, but the list %s in %s runs it "+
						"without a filter, so the partition is dead code: the classes it separates run in "+
						"one process, and the tests it would have moved out of the other processes run "+
						"here too. Wire the partition, or delete it.", name, entry.id, entry.file),
				blocking: true,
			})
			break
		}
	}

	// The census of the local NUKE gate, which is derived and therefore
	// reported rather than enforced.
	target := nukeTestTarget(root)
	result.nukeTarget = target
	if target == nil {
		result.notes = append(result.notes, nukeTargetPath+" could not be read, so the local NUKE `Test` target's coverage was not seen.")
	} else {
		result.nukeRuns = nukeSelected(universe, target)
		var missed []string
		for _, name := range universeNames {
			if universe[name].tests > 0 && !containsString(result.nukeRuns, name) {
				missed = append(missed, name)
			}
		}
		if len(missed) > 0 {
			result.notes = append(result.notes, fmt.Sprintf(
				"the local NUKE `Test` target does not run %d project(s) that carry tests (%s); it "+
					"selects by name suffix minus %s.", len(missed), strings.Join(missed, ", "),
				strings.Join(target.excluded, ", ")))
		}
	}

	// The baseline decides what is allowed and what is a new hole.
	rows, baselineFindings := parseBaseline(root)
	findings = append(findings, baselineFindings...)
	allowed := map[string]bool{}
	for _, row := range rows {
		allowed[row.key()] = true
	}

	current := map[string]bool{}
	for _, item := range findings {
		current[item.key()] = true
	}
	for index := range findings {
		if findings[index].rule == "BASELINE-STALE" || findings[index].rule == "BASELINE-UNJUSTIFIED" {
			continue
		}
		if allowed[findings[index].key()] {
			findings[index].blocking = false
			result.notes = append(result.notes, fmt.Sprintf(
				"%s %s is carried by %s, so it is a known hole and not a new one.",
				findings[index].rule, findings[index].subject, baselineFile))
		}
	}

	for _, row := range rows {
		if current[row.key()] {
			continue
		}
		findings = append(findings, finding{
			rule:    "BASELINE-STALE",
			subject: row.rule + ":" + row.subject,
			message: fmt.Sprintf(
				"the baseline row on line %d carries %s for %s, and the tree produces no such finding "+
					"any more. A row that outlives its finding is a permission with no referent. Delete "+
					"the row in the same diff that closed the hole.", row.lineNo, row.rule, row.subject),
			blocking: true,
		})
	}

	withTests := 0
	for _, project := range universe {
		if project.tests > 0 {
			withTests++
		}
	}
	claimed := 0
	for _, entries := range claims {
		if len(entries) > 0 {
			claimed++
		}
	}
	contextsFound := 0
	for _, found := range contexts {
		if len(found) > 0 {
			contextsFound++
		}
	}
	result.solutionTests = names
	result.totals = map[string]int{
		"csproj":        len(projects),
		"test_projects": len(universe),
		"with_tests":    withTests,
		"lists":         len(lists),
		"claims":        claimed,
		"contexts":      contextsFound,
		"nuke_runs":     len(result.nukeRuns),
	}
	return result, findings
}

// plural is the "y"/"ies" of the messages.
func plural(count int) string {
	if count == 1 {
		return "y"
	}
	return "ies"
}

// sortedSet returns the sorted keys of a set.
func sortedSet(set map[string]bool) []string {
	keys := make([]string, 0, len(set))
	for key := range set {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}
