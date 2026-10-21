// Package checkanalyzerscope is the compiled port of
// tools/check-analyzer-scope.py.
//
// The gate fails when an analyzer package's SCOPE says anything but what the
// tree says: the scope table in tools/analyzer-scope.json against the tree,
// so a family cannot spread back to every project.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checkanalyzerscope

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

// scanSuffixes are the project files in scope: every tracked .csproj /
// .props / .targets.
var scanSuffixes = []string{".csproj", ".props", ".targets"}

// rootProps is the props file that holds the DEFAULT scope, and the tree it
// stands for.
const rootProps = "Directory.Build.props"

// scopeFiles is the scope file of each tree, and the tree it stands for. The
// contrib copies mirror the tree they live in — contrib/ is not built by CI,
// but its props files ARE tracked, and an analyzer wired there is still a
// decision somebody has to make.
var scopeFiles = map[string]string{
	"src/Directory.Build.props":           "src",
	"apps/Directory.Build.props":          "apps",
	"contrib/apps/Directory.Build.props":  "apps",
	"tests/Directory.Build.props":         "tests",
	"contrib/tests/Directory.Build.props": "tests",
}

// scopeFileOrder is the scope file list in the order the python original's
// dict holds it — the order the findings name them in.
var scopeFileOrder = []string{
	"src/Directory.Build.props",
	"apps/Directory.Build.props",
	"contrib/apps/Directory.Build.props",
	"tests/Directory.Build.props",
	"contrib/tests/Directory.Build.props",
}

// excludedPrefixes is the vendored third-party tree (ADR-019): it is a git
// submodule, so its files are not tracked here, and the exclusion is belt and
// braces.
var excludedPrefixes = []string{"contrib/external/"}

// analyzerWords are the words that make a package id look like an analyzer.
// The heuristic is deliberately broad — a false positive costs a table row, a
// false negative lets a new analyzer arrive without a scope decision.
var analyzerWords = []string{"analyzer", "analysers", "analyzers"}

// specialAnalyzerPackages are the two packages in this repository that
// break the shape rule. Both are named in the table, so the table is what
// decides them.
var specialAnalyzerPackages = []string{"AsyncFixer", "ReferenceTrimmer"}

// elementRe is the whole element, so that the attributes on it (the switch,
// in particular) can be read.
var elementRe = regexp.MustCompile(`(?i)<PackageReference\b([^>]*)>`)

// includeRe is the Include attribute of an element.
//
// The closing quote is checked in code: a backreference is not in the regex
// engine of Go, and the python original required the opening and the closing
// quote to be the same character.
var includeRe = regexp.MustCompile(`(?i)\bInclude\s*=\s*(["'])([^"']+)`)

// propertyOpenRe is the opening tag of a Harbor* property.
var propertyOpenRe = regexp.MustCompile(`<(Harbor[A-Za-z]+)\b[^>]*>`)

// versionElementRe is a PackageVersion element.
var versionElementRe = regexp.MustCompile(`(?i)<PackageVersion\b([^>]*)>`)

// versionIncludeRe is the Include attribute of such an element.
var versionIncludeRe = regexp.MustCompile(`(?i)\bInclude\s*=\s*(["'])([^"']+)`)

// finding is one breach of the scope table, with the evidence to act on.
type finding struct {
	rule   string
	path   string
	line   int
	detail string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	where := f.path
	if f.line != 0 {
		where = fmt.Sprintf("%s:%d", f.path, f.line)
	}
	return fmt.Sprintf("%s  %s  %s", f.rule, where, f.detail)
}

// family is one analyzer family of the scope table.
type family struct {
	id         string
	property   string
	declaredIn []string
	scopes     []string
	mode       string
}

// scopeTable is the table of tools/analyzer-scope.json.
type scopeTable struct {
	families     []family
	trees        map[string]any
	notAnalyzers map[string]bool
}

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	tablePath := ""
	report := false
	minFiles := 0
	selfTest := false

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
		case "--table":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--table needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			tablePath = value
		case "--report":
			report = true
		case "--min-files":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-files needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = parsed
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-analyzer-scope [--root DIR] [--table PATH] [--report] [--min-files N] [--self-test]")
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
	if tablePath == "" {
		tablePath = filepath.Join(root, "tools", "analyzer-scope.json")
	}
	absolute, err := filepath.Abs(root)
	if err != nil {
		absolute = root
	}
	table, err := loadTable(tablePath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the scope table:", err)
		return gate.Broken
	}

	census, findings := analyse(absolute, table)

	if report {
		fmt.Printf("project files scanned: %d\n", census.files)
		fmt.Printf("families in the table: %d\n", census.families)
		fmt.Printf("trees in the table: %d\n", census.scopes)
		if len(findings) > 0 {
			fmt.Println("\nfindings:")
			for _, item := range findings {
				fmt.Printf("  %s\n", item)
			}
		}
		return gate.Green
	}

	breached := false
	if minFiles != 0 && census.files < minFiles {
		fmt.Printf("  SCAN-BLIND  scan floor: project files compared %d, floor is %d. The gate saw less than the tree holds, so it cannot report that the tree is clean.\n",
			census.files, minFiles)
		breached = true
	}

	if len(findings) > 0 {
		fmt.Print("Analyzer-scope gate — findings:\n\n")
		for _, item := range findings {
			fmt.Printf("  %s\n", item)
		}
		fmt.Println()
		fmt.Println("  Every analyzer package runs inside the compiler of every project that")
		fmt.Println("  references it, and a severity table cannot switch that off: a rule at")
		fmt.Println("  'severity = none' is silenced while its analyzer keeps running. The")
		fmt.Println("  scope is the only lever that removes the cost.")
		fmt.Println()
		fmt.Println("  Fix: change tools/analyzer-scope.json AND the props file that declares")
		fmt.Println("  the family, so the two agree. A family that should not run in a tree")
		fmt.Println("  is switched off there:")
		fmt.Println()
		fmt.Println("    <PropertyGroup>")
		fmt.Println("      <HarborAnalyzerTheFamily>off</HarborAnalyzerTheFamily>")
		fmt.Println("    </PropertyGroup>")
		fmt.Println()
		fmt.Println("  before the <Import Project=\"../Directory.Build.props\"/>.")
		fmt.Println()
	}

	if breached {
		fmt.Println()
	}

	if len(findings) > 0 || breached {
		return gate.Red
	}

	fmt.Printf("OK: %d project file(s) scanned, %d analyzer families, every scope agrees with tools/analyzer-scope.json\n",
		census.files, census.families)
	return gate.Green
}

// census is what the scan saw.
type census struct {
	files    int
	families int
	scopes   int
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

// loadTable reads the scope table.
func loadTable(path string) (*scopeTable, error) {
	object, err := gatekit.ReadJSONObject(path)
	if err != nil {
		return nil, err
	}
	table := &scopeTable{trees: gatekit.JSONMap(object, "trees"), notAnalyzers: map[string]bool{}}
	for _, item := range gatekit.JSONList(object, "not_analyzers") {
		switch typed := item.(type) {
		case string:
			table.notAnalyzers[typed] = true
		case map[string]any:
			if id, ok := gatekit.JSONText(typed, "id"); ok {
				table.notAnalyzers[id] = true
			}
		}
	}
	for _, item := range gatekit.JSONList(object, "families") {
		entry, ok := item.(map[string]any)
		if !ok {
			continue
		}
		f := family{}
		f.id, _ = gatekit.JSONText(entry, "id")
		f.property, _ = gatekit.JSONText(entry, "property")
		f.mode, _ = gatekit.JSONText(entry, "mode")
		for _, value := range gatekit.JSONList(entry, "declared_in") {
			if text, ok := value.(string); ok {
				f.declaredIn = append(f.declaredIn, text)
			}
		}
		for _, value := range gatekit.JSONList(entry, "scopes") {
			if text, ok := value.(string); ok {
				f.scopes = append(f.scopes, text)
			}
		}
		table.families = append(table.families, f)
	}
	return table, nil
}

// looksLikeAnalyzer is true when a package id is shaped like an analyzer
// package.
func looksLikeAnalyzer(packageID string) bool {
	lowered := strings.ToLower(packageID)
	for _, word := range analyzerWords {
		if strings.Contains(lowered, word) {
			return true
		}
	}
	for _, special := range specialAnalyzerPackages {
		if packageID == special {
			return true
		}
	}
	return false
}

// treeOf is the tree a project file belongs to.
func treeOf(path string) string {
	if path == rootProps {
		return "root"
	}
	if tree, ok := scopeFiles[path]; ok {
		return tree
	}
	parts := strings.Split(path, "/")
	switch parts[0] {
	case "src", "apps", "tests":
		return parts[0]
	case "contrib":
		if len(parts) > 1 && (parts[1] == "apps" || parts[1] == "tests") {
			return parts[1]
		}
	}
	return "root"
}

// packageReference is one PackageReference element.
type packageReference struct {
	id      string
	element string
	line    int
}

// packageReferences is every PackageReference element in one file.
func packageReferences(text string) []packageReference {
	masked := gatekit.MaskComments(text)
	var found []packageReference
	for _, match := range elementRe.FindAllStringSubmatchIndex(masked, -1) {
		attrs := masked[match[2]:match[3]]
		id, ok := includeValue(attrs)
		if !ok {
			continue
		}
		line := strings.Count(masked[:match[0]], "\n") + 1
		found = append(found, packageReference{id: id, element: masked[match[0]:match[1]], line: line})
	}
	return found
}

// includeValue is the id of an `Include` attribute.
//
// The closing quote must be the opening one: the python original used a
// backreference.
func includeValue(attrs string) (string, bool) {
	for _, match := range includeRe.FindAllStringSubmatchIndex(attrs, -1) {
		quote := attrs[match[2]:match[3]]
		id := attrs[match[4]:match[5]]
		if match[5] >= len(attrs) || string(attrs[match[5]]) != quote {
			continue
		}
		return strings.TrimSpace(id), true
	}
	return "", false
}

// propertyAssignment is one Harbor* property assignment.
type propertyAssignment struct {
	name  string
	value string
	line  int
}

// properties is every Harbor* property assignment in one file.
func properties(text string) []propertyAssignment {
	masked := gatekit.MaskComments(text)
	var found []propertyAssignment
	for _, match := range propertyOpenRe.FindAllStringSubmatchIndex(masked, -1) {
		name := masked[match[2]:match[3]]
		rest := masked[match[1]:]
		// The value ends at the next tag; the closing tag must carry the
		// same name.
		end := strings.IndexByte(rest, '<')
		if end < 0 {
			continue
		}
		value := strings.TrimSpace(rest[:end])
		closing := rest[end:]
		closeEnd := strings.IndexByte(closing, '>')
		if closeEnd < 0 {
			continue
		}
		closingTag := strings.TrimSpace(closing[:closeEnd])
		if closingTag != "</"+name {
			continue
		}
		line := strings.Count(masked[:match[0]], "\n") + 1
		found = append(found, propertyAssignment{name: name, value: value, line: line})
	}
	return found
}

// analyse is the census and the findings. Both are derived from the tree;
// neither is declared.
func analyse(root string, table *scopeTable) (census, []finding) {
	files := gatekit.ProjectFiles(root, scanSuffixes, excludedPrefixes)
	var findings []finding

	families := map[string]family{}
	var familyOrder []string
	knownProperties := map[string]bool{}
	for _, item := range table.families {
		families[item.id] = item
		familyOrder = append(familyOrder, item.id)
		knownProperties[item.property] = true
	}

	// What the tree says: where each family is declared, what each scope
	// file switches, and which packages are referenced that the table does
	// not know.
	declarations := map[string]map[string]packageReference{}
	for _, id := range familyOrder {
		declarations[id] = map[string]packageReference{}
	}
	switches := map[string]map[string]propertyAssignment{}
	referenced := map[string][]string{}
	pins := map[string]bool{}

	for _, relative := range files {
		path := filepath.Join(root, relative)
		data, err := os.ReadFile(path)
		if err != nil {
			findings = append(findings, finding{
				rule:   "SCAN-BLIND",
				path:   relative,
				detail: fmt.Sprintf("the file could not be read (%v)", err),
			})
			continue
		}
		text := string(data)

		for _, reference := range packageReferences(text) {
			referenced[reference.id] = append(referenced[reference.id], relative)
			if _, ok := families[reference.id]; ok {
				declarations[reference.id][relative] = reference
				continue
			}
			if table.notAnalyzers[reference.id] {
				continue
			}
			if looksLikeAnalyzer(reference.id) {
				findings = append(findings, finding{
					rule: "UNDECLARED-ANALYZER",
					path: relative,
					line: reference.line,
					detail: fmt.Sprintf("%s is referenced and is not in tools/analyzer-scope.json — an analyzer package arrives with a scope decision, not by accident",
						reference.id),
				})
			}
		}

		if relative == "Directory.Packages.props" {
			masked := gatekit.MaskComments(text)
			for _, match := range versionElementRe.FindAllStringSubmatchIndex(masked, -1) {
				attrs := masked[match[2]:match[3]]
				for _, attribute := range versionIncludeRe.FindAllStringSubmatchIndex(attrs, -1) {
					quote := attrs[attribute[2]:attribute[3]]
					id := attrs[attribute[4]:attribute[5]]
					if attribute[5] >= len(attrs) || string(attrs[attribute[5]]) != quote {
						continue
					}
					pins[strings.TrimSpace(id)] = true
					break
				}
			}
		}

		if relative == rootProps || isScopeFile(relative) {
			for _, assignment := range properties(text) {
				if !strings.HasPrefix(assignment.name, "HarborAnalyzer") {
					continue
				}
				if switches[relative] == nil {
					switches[relative] = map[string]propertyAssignment{}
				}
				switches[relative][assignment.name] = assignment
				if !knownProperties[assignment.name] {
					findings = append(findings, finding{
						rule: "ORPHAN-SWITCH",
						path: relative,
						line: assignment.line,
						detail: fmt.Sprintf("%s switches a family that tools/analyzer-scope.json does not declare",
							assignment.name),
					})
				}
			}
		}
	}

	// ── per family ──────────────────────────────────────────────────────────
	for _, id := range familyOrder {
		entry := families[id]
		declaredIn := append([]string{}, entry.declaredIn...)
		sort.Strings(declaredIn)
		var actualIn []string
		for site := range declarations[id] {
			actualIn = append(actualIn, site)
		}
		sort.Strings(actualIn)

		if len(actualIn) == 0 {
			findings = append(findings, finding{
				rule:   "STALE-ROW",
				path:   "tools/analyzer-scope.json",
				detail: fmt.Sprintf("%s is declared in the table but is not referenced anywhere in the tree", id),
			})
			continue
		}

		if !sameStrings(actualIn, declaredIn) {
			findings = append(findings, finding{
				rule:   "DECLARATION-SITE",
				path:   strings.Join(actualIn, ", "),
				detail: fmt.Sprintf("%s is declared in %v but the table says %v", id, actualIn, declaredIn),
			})
		}

		for _, relative := range actualIn {
			reference := declarations[id][relative]
			if entry.mode == "job" {
				if !strings.Contains(reference.element, "Condition=") {
					findings = append(findings, finding{
						rule:   "NO-CONDITION",
						path:   relative,
						line:   reference.line,
						detail: fmt.Sprintf("%s carries no switch of its own; a job-scoped analyzer must say when it runs", id),
					})
				}
				continue
			}
			expected := fmt.Sprintf("Condition=\"'$(%s)' != 'off'\"", entry.property)
			if !strings.Contains(reference.element, expected) {
				findings = append(findings, finding{
					rule: "NO-CONDITION",
					path: relative,
					line: reference.line,
					detail: fmt.Sprintf("%s must carry %s — without the switch the scope cannot be expressed and the measurement harness cannot address one family",
						id, expected),
				})
			}
		}

		if !pins[id] {
			findings = append(findings, finding{
				rule:   "MISSING-PIN",
				path:   "Directory.Packages.props",
				detail: fmt.Sprintf("%s has no <PackageVersion> pin", id),
			})
		}

		if entry.mode != "scope" {
			continue
		}

		derived := deriveScopes(entry, declarations[id], switches, &findings)
		declaredScopes := append([]string{}, entry.scopes...)
		sort.Strings(declaredScopes)
		var derivedSorted []string
		for tree := range derived {
			derivedSorted = append(derivedSorted, tree)
		}
		sort.Strings(derivedSorted)
		if !sameStrings(derivedSorted, declaredScopes) {
			findings = append(findings, finding{
				rule:   "SCOPE-DRIFT",
				path:   "tools/analyzer-scope.json",
				detail: fmt.Sprintf("%s runs in %v but the table says %v", id, derivedSorted, declaredScopes),
			})
		}
	}

	return census{
		files:    len(files),
		families: len(families),
		scopes:   len(table.trees),
	}, findings
}

// sameStrings is the python list equality.
func sameStrings(left []string, right []string) bool {
	if len(left) != len(right) {
		return false
	}
	for index := range left {
		if left[index] != right[index] {
			return false
		}
	}
	return true
}

// isScopeFile is whether a path is one of the scope files.
func isScopeFile(path string) bool {
	_, ok := scopeFiles[path]
	return ok
}

// deriveScopes is the trees a family actually runs in, read out of the tree.
func deriveScopes(entry family, declarationSites map[string]packageReference,
	switches map[string]map[string]propertyAssignment, findings *[]finding) map[string]bool {
	var sites []string
	for site := range declarationSites {
		sites = append(sites, site)
	}
	sort.Strings(sites)
	tableTrees := []string{"root", "src", "apps", "tests"}

	// A family declared in a .csproj runs in that project's tree.
	for _, site := range sites {
		if !strings.HasSuffix(site, ".props") {
			enabled := map[string]bool{}
			for _, item := range sites {
				enabled[treeOf(item)] = true
			}
			return enabled
		}
	}

	// A family declared in a scope file runs in that scope's tree, and
	// nowhere else: it is not referenced by the root file at all.
	if !containsString(sites, rootProps) {
		enabled := map[string]bool{}
		for _, item := range sites {
			enabled[treeOf(item)] = true
		}
		return enabled
	}

	// A family declared in the root file runs in a tree unless that tree's
	// scope file switches it off. The root's own value is the default for
	// the trees that have no scope file of their own.
	rootValue := "on"
	if assignment, ok := switches[rootProps][entry.property]; ok {
		rootValue = assignment.value
	}
	enabled := map[string]bool{}
	for _, tree := range tableTrees {
		var statedPaths []string
		var statedValues []propertyAssignment
		for _, path := range scopeFileOrder {
			if scopeFiles[path] != tree {
				continue
			}
			if assignment, ok := switches[path][entry.property]; ok {
				statedPaths = append(statedPaths, path)
				statedValues = append(statedValues, assignment)
			}
		}
		values := map[string]bool{}
		for _, assignment := range statedValues {
			values[assignment.value] = true
		}
		if len(values) > 1 {
			var sortedValues []string
			for value := range values {
				sortedValues = append(sortedValues, value)
			}
			sort.Strings(sortedValues)
			*findings = append(*findings, finding{
				rule: "SCOPE-DRIFT",
				path: strings.Join(statedPaths, ", "),
				detail: fmt.Sprintf("%s is switched differently in the copies of the %s tree: %v",
					entry.property, tree, sortedValues),
			})
		}
		value := rootValue
		if len(statedValues) > 0 {
			value = statedValues[0].value
		}
		if value == "on" {
			enabled[tree] = true
		}
	}
	return enabled
}

// containsString is the python `in` over a list.
func containsString(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}
