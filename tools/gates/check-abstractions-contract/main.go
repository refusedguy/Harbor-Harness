// Package checkabstractionscontract is the compiled port of
// tools/check-abstractions-contract.py.
//
// The gate asks two questions of the guarded projects: is every
// <PackageReference> a declaration the sources actually reach, and is every
// non-BCL namespace the sources use covered by a declaration.
//
// Both sides of every comparison are FILES: the <PackageReference> elements
// of the .csproj files against the `using` directives of the .cs files they
// compile. The gate never reads a document.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checkabstractionscontract

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// scope is the guarded projects, and the one-hop ProjectReference each is
// allowed to reach.
//
// Read from the .csproj files at run time — nothing here is a table of what a
// project "is supposed to" reference. A path is a scope, not a claim about
// the repository, so widening the list is a reviewable diff.
var scope = []string{
	"src/Harbor.Abstractions",
	"src/Harbor.Abstractions.Contracts",
	"src/Harbor.Extensions",
	"src/Harbor.Application",
	"src/Harbor.PolicyConfig",
}

// ownPrefixes are namespaces that are this repository's own, or the
// compiler's. A namespace in one of these needs no PackageReference to
// justify itself.
var ownPrefixes = []string{"Harbor.", "internal"}

// bclRoots are the namespace roots the framework ships.
//
// `Microsoft` is NOT in this list, and that is the whole point of the rule
// rather than an omission: `Microsoft.Extensions.Logging` /
// `.DependencyInjection` / `.Configuration` are NuGet packages.
var bclRoots = []string{"System", "Windows", "Mono"}

// noise is the stripper of comments and literals.
//
// One alternation, matched left to right, so whichever construct starts first
// wins. The raw-string branch is FIRST, because `"""…"""` would otherwise be
// read as three empty strings. The line-comment branch is bounded to its
// line: written as `//.*` it is "the rest of the file" and it silently
// swallows every `using` after the first comment.
var noise = regexp.MustCompile(
	`(?s)"""[\s\S]*?"""` +
		`|//[^\n]*` +
		`|/\*.*?\*/` +
		`|"(?:[^"\\\n]|\\.)*"` +
		`|'(?:[^'\\\n]|\\.)*'`)

// usingRe is a `using` directive.
var usingRe = regexp.MustCompile(
	`(?m)^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?([A-Za-z_][A-Za-z0-9_.]*)[ \t]*;`)

// packageRefRe is a <PackageReference> element, self-closing or not.
var packageRefRe = regexp.MustCompile(
	`(?s)<PackageReference\b[^>]*?/>|<PackageReference\b[^>]*?>.*?</PackageReference>`)

// includeRe is the Include attribute of an element.
var includeRe = regexp.MustCompile(`Include\s*=\s*"([^"]+)"`)

// includeAssetsRe is the IncludeAssets of an element, either as an element
// body or as an attribute.
//
// A source generator / analyzer contributes generated code and no types, so
// the signal that it is one is that its own declaration hands `analyzers` to
// the compiler and does not also hand it `compile`.
var includeAssetsRe = regexp.MustCompile(
	`(?s)<IncludeAssets\b[^>]*?>(.*?)</IncludeAssets>|` +
		`<IncludeAssets\b[^>]*?Include\s*=\s*"([^"]+)"`)

// assetTokenRe are the words of an IncludeAssets value.
var assetTokenRe = regexp.MustCompile(`[A-Za-z]+`)

// projectRefRe is a <ProjectReference> element.
var projectRefRe = regexp.MustCompile(
	`(?s)<ProjectReference\s+[^>]*?Include\s*=\s*"([^"]+)"`)

// finding is one problem of the gate.
type finding struct {
	rule    string
	project string
	message string
	subject string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	return fmt.Sprintf("%s  %s: %s", f.rule, f.project, f.message)
}

// projectCensus is what the gate measured for one project.
type projectCensus struct {
	packages            []string
	generators          []string
	projectRefs         []string
	hopPackages         []string
	csFiles             int
	externalNamespaces  []string
	reachableNamespaces []string
}

// totals are the numbers the floors are compared against.
type totals struct {
	packages   int
	namespaces int
	csFiles    int
}

// census is the derived census of the whole scope.
type census struct {
	projects map[string]*projectCensus
	order    []string
	totals   totals
}

// Run is the gate entry point.
func Run(args []string) int {
	root := "."
	report := false
	minPackages := 0
	minNamespaces := 0
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
		case "--report":
			report = true
		case "--min-packages":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-packages needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-packages needs a number")
				return gate.Broken
			}
			minPackages = parsed
		case "--min-namespaces":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-namespaces needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-namespaces needs a number")
				return gate.Broken
			}
			minNamespaces = parsed
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
			fmt.Println("usage: harbor-gates check-abstractions-contract [--root DIR] [--report] [--min-packages N] [--min-namespaces N] [--min-files N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	absolute, err := filepath.Abs(root)
	if err != nil {
		absolute = root
	}
	measured, findings := analyse(absolute)

	if report {
		printReport(measured)
		return gate.Green
	}

	type floor struct {
		name   string
		floor  int
		actual int
	}
	floors := []floor{
		{"packages", minPackages, measured.totals.packages},
		{"namespaces", minNamespaces, measured.totals.namespaces},
		{"files", minFiles, measured.totals.csFiles},
	}
	var breached []floor
	for _, item := range floors {
		if item.floor != 0 && item.actual < item.floor {
			breached = append(breached, item)
		}
	}

	if len(findings) > 0 {
		fmt.Print("Guarded-project dependency gate — findings:\n\n")
		for _, item := range findings {
			fmt.Printf("  %s\n", item)
		}
		fmt.Println()
	}
	if len(breached) > 0 {
		for _, item := range breached {
			fmt.Printf("  CONTRACT-BLIND  scan floor: %s compared %d, floor is %d\n", item.name, item.actual, item.floor)
		}
		fmt.Println()
	}

	if len(breached) > 0 && len(findings) == 0 {
		fmt.Println("Guarded-project dependency gate: clean, but a scan floor was breached.")
		fmt.Println()
	}
	if len(findings) > 0 || len(breached) > 0 {
		return gate.Red
	}
	return gate.Green
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

// namespacePrefixes is the plausible namespace prefixes a NuGet package id
// may own.
//
// One convention is encoded, and it is a property of the id itself: a
// trailing `.Abstractions` is a *flavour* marker, not the namespace root.
func namespacePrefixes(packageID string) []string {
	parts := strings.Split(packageID, ".")
	candidates := []string{packageID}
	if parts[len(parts)-1] == "Abstractions" && len(parts) > 1 {
		candidates = append(candidates, strings.Join(parts[:len(parts)-1], "."))
	}
	return candidates
}

// blank replaces a noise match with spaces, keeping the line count.
func blank(match string) string {
	var out strings.Builder
	for _, ch := range match {
		if ch == '\n' {
			out.WriteByte('\n')
			continue
		}
		out.WriteByte(' ')
	}
	return out.String()
}

// stripNoise blanks comments and literals, preserving line count.
//
// Comments are blanked rather than deleted so a line number computed from the
// result still points at real source. Literals are blanked too: a package id
// quoted inside a string is not a use of that package.
func stripNoise(source string) string {
	return noise.ReplaceAllStringFunc(source, blank)
}

// readFile reads a file, and returns "" when it cannot be read.
func readFile(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return string(data)
}

// csFiles is every .cs under a project, minus build output.
func csFiles(root string, project string) []string {
	base := filepath.Join(root, project)
	var found []string
	var walk func(dir string)
	walk = func(dir string) {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return
		}
		for _, entry := range entries {
			if entry.IsDir() {
				if entry.Name() == "bin" || entry.Name() == "obj" {
					continue
				}
				walk(filepath.Join(dir, entry.Name()))
				continue
			}
			if strings.HasSuffix(entry.Name(), ".cs") {
				found = append(found, filepath.Join(dir, entry.Name()))
			}
		}
	}
	walk(base)
	sort.Strings(found)
	return found
}

// namespacesUsed is the set of namespaces a set of .cs files actually
// imports.
func namespacesUsed(paths []string) map[string]bool {
	used := map[string]bool{}
	for _, path := range paths {
		for _, match := range usingRe.FindAllStringSubmatch(stripNoise(readFile(path)), -1) {
			used[match[1]] = true
		}
	}
	return used
}

// isExternal is whether a namespace needs a NuGet package to justify itself.
func isExternal(namespace string) bool {
	if namespace == "" || namespace == "System" {
		return false
	}
	for _, prefix := range ownPrefixes {
		if strings.HasPrefix(namespace, prefix) {
			return false
		}
	}
	root := strings.Split(namespace, ".")[0]
	for _, candidate := range bclRoots {
		if root == candidate {
			return false
		}
	}
	return true
}

// isGeneratorPackage is whether a PackageReference element declares an
// analyzer-only package.
func isGeneratorPackage(element string) bool {
	tokens := map[string]bool{}
	for _, match := range includeAssetsRe.FindAllStringSubmatch(element, -1) {
		body := match[1]
		if body == "" {
			body = match[2]
		}
		for _, token := range assetTokenRe.FindAllString(body, -1) {
			tokens[token] = true
		}
	}
	return tokens["analyzers"] && !tokens["compile"]
}

// declaredPackages is the package ids a .csproj declares, with which of them
// are generators.
//
// One implementation for both the project under test and the project it
// references, so the two sides of the reachability rule cannot drift apart.
func declaredPackages(xml string) ([]string, []string) {
	type declaration struct {
		id        string
		generator bool
	}
	var declarations []declaration
	for _, match := range packageRefRe.FindAllString(xml, -1) {
		element := match
		include := includeRe.FindStringSubmatch(element)
		if include == nil {
			continue
		}
		declarations = append(declarations, declaration{
			id:        include[1],
			generator: isGeneratorPackage(element),
		})
	}
	seen := map[string]bool{}
	var packages []string
	for _, item := range declarations {
		if !seen[item.id] {
			seen[item.id] = true
			packages = append(packages, item.id)
		}
	}
	sort.Strings(packages)
	var generators []string
	seenGenerators := map[string]bool{}
	for _, item := range declarations {
		if item.generator && !seenGenerators[item.id] {
			seenGenerators[item.id] = true
			generators = append(generators, item.id)
		}
	}
	sort.Strings(generators)
	return packages, generators
}

// analyse is the census and the findings. Both are derived; neither is
// declared.
func analyse(root string) (*census, []finding) {
	result := &census{projects: map[string]*projectCensus{}}
	var findings []finding
	totalNamespaces := map[string]bool{}

	for _, project := range scope {
		name := project
		if idx := strings.LastIndexByte(project, '/'); idx >= 0 {
			name = project[idx+1:]
		}
		csproj := filepath.Join(root, project, name+".csproj")
		if _, err := os.Stat(csproj); err != nil {
			findings = append(findings, finding{
				rule:    "CONTRACT-BLIND",
				project: project,
				message: "no .csproj at the guarded path — the gate is guarding nothing if the project is not where the gate says it is",
			})
			continue
		}

		xml := readFile(csproj)
		packages, generators := declaredPackages(xml)
		projectRefs := projectRefRe.FindAllStringSubmatch(xml, -1)
		var refPaths []string
		for _, ref := range projectRefs {
			refPaths = append(refPaths, ref[1])
		}

		own := csFiles(root, project)
		// One hop of ProjectReference: a facade legitimately re-declares what
		// the contract project it fronts declares. The hop is taken to the
		// referenced file's DIRECTORY.
		reachable := append([]string{}, own...)
		var hopDirs []string
		for _, ref := range refPaths {
			target := filepath.Clean(filepath.Join(root, project, strings.ReplaceAll(ref, "\\", "/")))
			hopDir := filepath.Dir(target)
			hopDirs = append(hopDirs, hopDir)
			relative, err := filepath.Rel(root, hopDir)
			if err != nil {
				continue
			}
			reachable = append(reachable, csFiles(root, relative)...)
		}

		// The same hop, on the other rule: what a project this one references
		// DECLARES counts toward covering a namespace this project's own
		// sources use.
		var hopPackages []string
		for _, hopDir := range hopDirs {
			hopName := filepath.Base(hopDir)
			hopXML := readFile(filepath.Join(hopDir, hopName+".csproj"))
			if hopXML != "" {
				hopDeclared, _ := declaredPackages(hopXML)
				hopPackages = append(hopPackages, hopDeclared...)
			}
		}

		ownNS := namespacesUsed(own)
		reachableNS := namespacesUsed(reachable)
		var external []string
		for namespace := range ownNS {
			if isExternal(namespace) {
				external = append(external, namespace)
			}
		}
		sort.Strings(external)
		var reachableExternal []string
		for namespace := range reachableNS {
			if isExternal(namespace) {
				reachableExternal = append(reachableExternal, namespace)
			}
		}
		sort.Strings(reachableExternal)

		result.totals.csFiles += len(own)
		result.totals.packages += len(packages)
		for _, namespace := range external {
			totalNamespaces[namespace] = true
		}

		hopUnique := map[string]bool{}
		var hopSorted []string
		for _, item := range hopPackages {
			if !hopUnique[item] {
				hopUnique[item] = true
				hopSorted = append(hopSorted, item)
			}
		}
		sort.Strings(hopSorted)

		result.projects[project] = &projectCensus{
			packages:            packages,
			generators:          generators,
			projectRefs:         refPaths,
			hopPackages:         hopSorted,
			csFiles:             len(own),
			externalNamespaces:  external,
			reachableNamespaces: reachableExternal,
		}
		result.order = append(result.order, project)

		// --- rule 1: declared but nothing reaches it -----------------------
		for _, packageID := range packages {
			if containsString(generators, packageID) {
				continue
			}
			prefixes := namespacePrefixes(packageID)
			hit := false
			for _, namespace := range reachableExternal {
				for _, prefix := range prefixes {
					if namespace == prefix || strings.HasPrefix(namespace, prefix+".") {
						hit = true
						break
					}
				}
				if hit {
					break
				}
			}
			if hit {
				continue
			}
			sortedPrefixes := append([]string{}, prefixes...)
			sort.Strings(sortedPrefixes)
			findings = append(findings, finding{
				rule:    "CONTRACT-UNUSED-PACKAGE",
				project: project,
				subject: packageID,
				message: fmt.Sprintf("PackageReference %s is declared but no .cs file in this project or in a project it ProjectReferences has a `using` under one of its namespace prefixes (%s). A dependency nobody reaches is a promise with no referent — and a package reference here is one every assembly inheriting this one also inherits.",
					packageID, strings.Join(sortedPrefixes, ", ")),
			})
		}

		// --- rule 2: used but nothing declares it --------------------------
		ownPrefixSet := map[string]bool{}
		for _, packageID := range packages {
			for _, prefix := range namespacePrefixes(packageID) {
				ownPrefixSet[prefix] = true
			}
		}
		hopPrefixSet := map[string]bool{}
		for _, packageID := range hopPackages {
			for _, prefix := range namespacePrefixes(packageID) {
				hopPrefixSet[prefix] = true
			}
		}
		for _, namespace := range external {
			covered := false
			for prefix := range ownPrefixSet {
				if namespace == prefix || strings.HasPrefix(namespace, prefix+".") {
					covered = true
					break
				}
			}
			if !covered {
				for prefix := range hopPrefixSet {
					if namespace == prefix || strings.HasPrefix(namespace, prefix+".") {
						covered = true
						break
					}
				}
			}
			if covered {
				continue
			}
			findings = append(findings, finding{
				rule:    "CONTRACT-UNDECLARED-PACKAGE",
				project: project,
				subject: namespace,
				message: fmt.Sprintf("`using %s` is covered by no PackageReference in %s.csproj or in a project it ProjectReferences. The dependency surface grew without a declaration to justify it.",
					namespace, name),
			})
		}

		// --- rule 3: blindness --------------------------------------------
		// Deliberately NOT relaxed to "no packages here AND none one hop
		// away". This dimension is the project's OWN declaration list.
		if len(packages) == 0 {
			findings = append(findings, finding{
				rule:    "CONTRACT-BLIND",
				project: project,
				message: "no PackageReference at all in this project's own .csproj — either the project really is declaration-free, or this gate stopped seeing the element. Reporting rather than passing: those are not the same tree.",
			})
		}
		if len(own) == 0 {
			findings = append(findings, finding{
				rule:    "CONTRACT-BLIND",
				project: project,
				message: "no .cs file found under the guarded project path.",
			})
		}
	}

	result.totals.namespaces = len(totalNamespaces)
	return result, findings
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

// printReport is the derived dependency census.
func printReport(measured *census) {
	for _, project := range measured.order {
		data := measured.projects[project]
		fmt.Printf("\n%s\n", project)
		fmt.Printf("  .cs files                     %d\n", data.csFiles)
		fmt.Printf("  ProjectReference              %s\n", orDash(strings.Join(data.projectRefs, ", ")))
		fmt.Printf("  declared by those refs        %s\n", orDash(strings.Join(data.hopPackages, ", ")))
		fmt.Printf("  PackageReference              %s\n", orDash(strings.Join(data.packages, ", ")))
		fmt.Printf("  of which source generators    %s\n", orDash(strings.Join(data.generators, ", ")))
		fmt.Println("  external namespaces used      " + strings.Join(data.externalNamespaces, ", "))
		fmt.Println("  reachable via ProjectReference " + strings.Join(data.reachableNamespaces, ", "))
	}
	fmt.Printf("\ntotals: %d packages declared, %d external namespaces, %d .cs files\n",
		measured.totals.packages, measured.totals.namespaces, measured.totals.csFiles)
}

// orDash is the python `or "-"`.
func orDash(text string) string {
	if text == "" {
		return "-"
	}
	return text
}
