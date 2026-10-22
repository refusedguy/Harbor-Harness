// Package codemodtestevents is the compiled port of tools/codemod-test-events.py.
//
// The codemod rewrites the hand-built test events and messages in `tests/`
// into the shared `TestEvents` and `TestMessages` builders, and reports the
// forms it will not touch. It is a tool a human runs: no workflow runs it,
// and the CI_GATES table says so.
//
// The python original was deleted in the same change. A python original and
// a compiled twin of the same tool never coexist, and the twin is how a
// drifted pair is caught.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the tool
// could not run.
package codemodtestevents

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode/utf8"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

// skippedSite is one construction the codemod left alone, and why.
type skippedSite struct {
	offset int
	file   string
	line   int
	typ    string
	family string
	reason string
}

// fileResult is one file the codemod rewrote.
type fileResult struct {
	path        string
	sites       int
	linesBefore int
	linesAfter  int
	charsBefore int
	charsAfter  int
}

// reportData is the whole report, in the order the python original wrote it.
type reportData struct {
	mode              string
	filesChanged      int
	sitesRewritten    int
	eventSites        int
	messageSites      int
	linesBefore       int
	linesAfter        int
	charsBefore       int
	charsAfter        int
	referenceProjects []string
	skippedReasons    []string
	skippedCounts     []int
	skippedSites      []skippedSite
	files             []fileResult
}

// rewriteText rewrites every construction in one file.
//
// The second return value is the number of sites rewritten; the third is the
// sites that were left alone — each with the offset and the reason; and the
// fourth is the type of every site that was rewritten.
func rewriteText(text string) (string, int, []skippedSite, []string) {
	calls := findCalls(text)
	var skipped []skippedSite
	type replacement struct {
		start int
		end   int
		call  string
	}
	var replacements []replacement
	var rewritten []string
	consumedUntil := -1

	for _, c := range calls {
		if c.start < consumedUntil {
			continue // inside a construction that was already rewritten
		}
		if isMessageType(c.typename) {
			messagePlan, reason := planForMessage(c.typename, c.args)
			if messagePlan == nil {
				skipped = append(skipped, skippedSite{offset: c.start, typ: c.typename, reason: reason})
				continue
			}
			call := emitMessage(messagePlan)
			if call == text[c.start:c.end] {
				continue
			}
			replacements = append(replacements, replacement{c.start, c.end, call})
			rewritten = append(rewritten, c.typename)
			consumedUntil = c.end
			continue
		}

		var p *plan
		var reason string
		if c.typename == "MessageUpdateEvent" {
			p, reason = planForUpdate(c.typename, c.args)
			if p == nil {
				generic, genericReason := planForMessageUpdate(c.args)
				if generic != nil {
					p, reason = generic, ""
				} else {
					reason = reason + "; and " + genericReason
				}
			}
		} else {
			p, reason = planFor(c.typename, c.args)
		}
		if p == nil {
			skipped = append(skipped, skippedSite{offset: c.start, typ: c.typename, reason: reason})
			continue
		}
		call := emit(p.method, p.params)
		if call == text[c.start:c.end] {
			continue
		}
		replacements = append(replacements, replacement{c.start, c.end, call})
		rewritten = append(rewritten, c.typename)
		consumedUntil = c.end
	}

	if len(replacements) == 0 {
		return text, 0, skipped, rewritten
	}

	out := text
	for i := len(replacements) - 1; i >= 0; i-- {
		r := replacements[i]
		out = out[:r.start] + r.call + out[r.end:]
	}
	return out, len(replacements), skipped, rewritten
}

// The `using` directive.

var (
	namespacePattern = regexp.MustCompile(`(?m)^namespace\s+([\w.]+)\s*;`)
	usingPattern     = regexp.MustCompile(`(?m)^([ \t]*)using\s+(?:static\s+)?([\w.]+)\s*(?:=\s*[\w.]+)?\s*;`)
	testKitUsingRe   = regexp.MustCompile(`(?m)^[ \t]*using\s+Harbor\.TestKit\s*;`)
)

// needsUsing whether the file has to import `Harbor.TestKit` to see the
// builders.
func needsUsing(text string) bool {
	if testKitUsingRe.MatchString(text) {
		return false
	}
	m := namespacePattern.FindStringSubmatch(text)
	if m != nil {
		namespace := m[1]
		if namespace == "Harbor.TestKit" || strings.HasPrefix(namespace, "Harbor.TestKit.") {
			return false
		}
	}
	return true
}

// insertUsing adds `using Harbor.TestKit;` where the file keeps its other
// imports.
func insertUsing(text string) string {
	namespace := namespacePattern.FindStringIndex(text)
	boundary := len(text)
	if namespace != nil {
		boundary = namespace[0]
	}
	var inside, outside [][]int
	for _, m := range usingPattern.FindAllStringIndex(text, -1) {
		if m[0] > boundary {
			inside = append(inside, m)
		} else {
			outside = append(outside, m)
		}
	}

	plain := func(entries [][]int) [][]int {
		var out [][]int
		for _, m := range entries {
			if !strings.Contains(text[m[0]:m[1]], "=") {
				out = append(out, m)
			}
		}
		return out
	}
	firstAlias := func(entries [][]int) []int {
		for _, m := range entries {
			if strings.Contains(text[m[0]:m[1]], "=") {
				return m
			}
		}
		return nil
	}

	for _, scope := range [][][]int{inside, outside} {
		if plainScope := plain(scope); len(plainScope) > 0 {
			last := plainScope[len(plainScope)-1]
			indent := usingIndent(text[last[0]:last[1]])
			return text[:last[1]] + "\n" + indent + "using Harbor.TestKit;" + text[last[1]:]
		}
		if alias := firstAlias(scope); alias != nil {
			indent := usingIndent(text[alias[0]:alias[1]])
			return text[:alias[0]] + indent + "using Harbor.TestKit;\n" + text[alias[0]:]
		}
	}

	if namespace != nil {
		return text[:namespace[0]] + "using Harbor.TestKit;\n\n" + text[namespace[0]:]
	}

	lines := strings.Split(text, "\n")
	index := 0
	for index < len(lines) {
		stripped := strings.TrimSpace(lines[index])
		if stripped != "" && !strings.HasPrefix(stripped, "//") {
			break
		}
		index++
	}
	head := strings.Join(lines[:index], "\n")
	tail := strings.Join(lines[index:], "\n")
	return head + "\nusing Harbor.TestKit;\n" + tail
}

// usingIndent the indentation of a using directive.
func usingIndent(directive string) string {
	i := 0
	for i < len(directive) && (directive[i] == ' ' || directive[i] == '\t') {
		i++
	}
	return directive[:i]
}

// The csproj edge.

const supportReferenceComment = "<!-- #1821: the shared event builders (TestEvents). The edge is\n" +
	"         closure-neutral — that part references only Harbor.Abstractions\n" +
	"         and Harbor.Abstractions.Contracts, and this project already has\n" +
	"         both because it already publishes events. -->"

// projectHasTestkitReference whether the project file already has the edge.
func projectHasTestkitReference(csprj string) bool {
	return strings.Contains(csprj, "Harbor.TestKit.Support.csproj") ||
		strings.Contains(csprj, "Harbor.TestKit.csproj")
}

var itemGroupRe = regexp.MustCompile(`(?s)<ItemGroup>(.*?)</ItemGroup>`)

// addSupportReference adds the support reference to the ItemGroup that holds
// the references.
func addSupportReference(csprj string) string {
	groups := itemGroupRe.FindAllStringSubmatchIndex(csprj, -1)
	var chosen []int
	for _, g := range groups {
		if strings.Contains(csprj[g[2]:g[3]], "ProjectReference") {
			chosen = g
			break
		}
	}
	if chosen == nil {
		if len(groups) == 0 {
			return csprj
		}
		chosen = groups[0]
	}

	// Match the separator the file already spells its references with.
	separator := "/"
	if strings.Contains(csprj, `Include="..\`) {
		separator = `\`
	}
	reference := `<ProjectReference Include="..` + separator + `Harbor.TestKit.Support` +
		separator + `Harbor.TestKit.Support.csproj"/>`

	body := csprj[chosen[2]:chosen[3]]
	var newBody string
	if strings.Contains(body, "ProjectReference") {
		newBody = strings.Replace(body, "<ProjectReference",
			supportReferenceComment+"\n    "+reference+"\n    <ProjectReference", 1)
	} else {
		newBody = "\n    " + supportReferenceComment + "\n    " + reference + "\n  " + body
	}
	return csprj[:chosen[2]] + newBody + csprj[chosen[3]:]
}

// iterSourceFiles every .cs file under `tests/`, in the order the python
// original listed them.
//
// The order is python's pathlib order: the paths compare by their PARTS and
// not by their text. A file list in a different order is a different list,
// and the report names the files in it.
func iterSourceFiles(root string) []string {
	var paths []string
	base := filepath.Join(root, filepath.FromSlash(scopeDir))
	_ = filepath.WalkDir(base, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return nil
		}
		if d.IsDir() {
			return nil
		}
		if !strings.HasSuffix(d.Name(), ".cs") {
			return nil
		}
		paths = append(paths, path)
		return nil
	})
	paths = pycompat.SortPaths(paths)

	var out []string
	for _, path := range paths {
		relative, err := filepath.Rel(root, path)
		if err != nil {
			continue
		}
		rel := filepath.ToSlash(relative)
		if strings.Contains(rel, "/obj/") || strings.Contains(rel, "/bin/") {
			continue
		}
		excluded := false
		for _, dir := range excludedDirs {
			if strings.HasPrefix(rel, dir+"/") {
				excluded = true
				break
			}
		}
		if excluded {
			continue
		}
		out = append(out, path)
	}
	return out
}

// Run is the tool entry point.
func Run(args []string) int {
	check := false
	apply := false
	root := ""
	reportPath := ""
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--check":
			check = true
		case "--apply":
			apply = true
		case "--root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--root needs a path")
				return gate.Broken
			}
			i++
			root = args[i]
		case "--report":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--report needs a path")
				return gate.Broken
			}
			i++
			reportPath = args[i]
		case "--help", "-h":
			fmt.Println("usage: harbor-gates codemod-test-events [--check|--apply] [--root PATH] [--report PATH]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "codemod-test-events: error: unrecognized argument: %s\n", args[i])
			return gate.Broken
		}
	}
	if !check && !apply {
		fmt.Fprintln(os.Stderr, "codemod-test-events: error: one of --check or --apply is required")
		return gate.Broken
	}

	if root == "" {
		resolved, err := gate.BinaryRoot()
		if err != nil {
			resolved, err = gate.RepoRoot()
			if err != nil {
				fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
				return gate.Broken
			}
		}
		root = resolved
	}
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}

	report := reportData{mode: "check"}
	if apply {
		report.mode = "apply"
	}

	// The skipped sites, counted by reason, in the order the reasons were
	// first met — the same order python's Counter keeps.
	reasonOrder := []string{}
	reasonCount := map[string]int{}

	projectsNeedingReference := map[string]bool{}

	for _, path := range iterSourceFiles(root) {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		text := string(data)
		newText, sites, skipped, rewritten := rewriteText(text)
		relative, err := filepath.Rel(root, path)
		if err != nil {
			continue
		}
		rel := filepath.ToSlash(relative)
		for _, s := range skipped {
			family := "events"
			if isMessageType(s.typ) {
				family = "messages"
			}
			labelled := family + ": " + s.reason
			if _, seen := reasonCount[labelled]; !seen {
				reasonOrder = append(reasonOrder, labelled)
			}
			reasonCount[labelled]++
			report.skippedSites = append(report.skippedSites, skippedSite{
				file:   rel,
				line:   strings.Count(text[:s.offset], "\n") + 1,
				typ:    s.typ,
				family: family,
				reason: s.reason,
			})
		}
		if sites == 0 {
			continue
		}
		if needsUsing(text) {
			newText = insertUsing(newText)
		}
		report.files = append(report.files, fileResult{
			path:        rel,
			sites:       sites,
			linesBefore: strings.Count(text, "\n") + 1,
			linesAfter:  strings.Count(newText, "\n") + 1,
			charsBefore: utf8.RuneCountInString(text),
			charsAfter:  utf8.RuneCountInString(newText),
		})
		report.sitesRewritten += sites
		for _, typename := range rewritten {
			if isMessageType(typename) {
				report.messageSites++
			} else {
				report.eventSites++
			}
		}
		report.linesBefore += strings.Count(text, "\n") + 1
		report.linesAfter += strings.Count(newText, "\n") + 1
		report.charsBefore += utf8.RuneCountInString(text)
		report.charsAfter += utf8.RuneCountInString(newText)
		projectsNeedingReference[filepath.ToSlash(filepath.Dir(rel))] = true
		if apply {
			if err := os.WriteFile(path, []byte(newText), 0o644); err != nil {
				fmt.Fprintln(os.Stderr, "cannot write", path+":", err)
				return gate.Broken
			}
		}
	}

	// The csproj edges.
	var referenceProjects []string
	var projectNames []string
	for project := range projectsNeedingReference {
		projectNames = append(projectNames, project)
	}
	sort.Strings(projectNames)
	for _, project := range projectNames {
		name := filepath.Base(project)
		csprj := filepath.Join(root, filepath.FromSlash(project), name+".csproj")
		if _, err := os.Stat(csprj); err != nil {
			continue
		}
		data, err := os.ReadFile(csprj)
		if err != nil {
			continue
		}
		text := string(data)
		if projectHasTestkitReference(text) {
			continue
		}
		referenceProjects = append(referenceProjects, project)
		if apply {
			if err := os.WriteFile(csprj, []byte(addSupportReference(text)), 0o644); err != nil {
				fmt.Fprintln(os.Stderr, "cannot write", csprj+":", err)
				return gate.Broken
			}
		}
	}
	report.referenceProjects = referenceProjects

	// The reasons, most common first — python's Counter.most_common() is a
	// stable sort, so the reasons with the same count keep the order they
	// were first met in.
	report.skippedReasons = append([]string{}, reasonOrder...)
	report.skippedCounts = make([]int, len(reasonOrder))
	for i, reason := range reasonOrder {
		report.skippedCounts[i] = reasonCount[reason]
	}
	order := make([]int, len(reasonOrder))
	for i := range order {
		order[i] = i
	}
	sort.SliceStable(order, func(a, b int) bool {
		return report.skippedCounts[order[a]] > report.skippedCounts[order[b]]
	})
	sortedReasons := make([]string, len(order))
	sortedCounts := make([]int, len(order))
	for i, idx := range order {
		sortedReasons[i] = report.skippedReasons[idx]
		sortedCounts[i] = report.skippedCounts[idx]
	}
	report.skippedReasons = sortedReasons
	report.skippedCounts = sortedCounts
	report.filesChanged = len(report.files)

	if reportPath != "" {
		if err := os.WriteFile(reportPath, []byte(renderReport(report)), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write", reportPath+":", err)
			return gate.Broken
		}
	}

	fmt.Printf("mode:            %s\n", report.mode)
	fmt.Printf("files changed:   %d\n", report.filesChanged)
	fmt.Printf("sites rewritten: %d\n", report.sitesRewritten)
	fmt.Printf("  events:        %d\n", report.eventSites)
	fmt.Printf("  messages:      %d\n", report.messageSites)
	fmt.Printf("lines:           %d -> %d (%+d)\n",
		report.linesBefore, report.linesAfter, report.linesAfter-report.linesBefore)
	fmt.Printf("characters:      %d -> %d (%+d)\n",
		report.charsBefore, report.charsAfter, report.charsAfter-report.charsBefore)
	leftAlone := 0
	for _, c := range report.skippedCounts {
		leftAlone += c
	}
	fmt.Printf("left alone:      %d\n", leftAlone)
	if len(report.referenceProjects) > 0 {
		fmt.Printf("projects gaining a Harbor.TestKit.Support reference: %d\n", len(report.referenceProjects))
		for _, project := range report.referenceProjects {
			fmt.Printf("  %s\n", project)
		}
	}
	if len(report.skippedReasons) > 0 {
		fmt.Println("left alone (form -> count):")
		for i, reason := range report.skippedReasons {
			fmt.Printf("  %5d  %s\n", report.skippedCounts[i], reason)
		}
	}

	return gate.Green
}
