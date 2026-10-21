package testpackages

// analyze.go -- the analysis: the scope, the ban set, the resolved graph.
//
// The gate answers one question about every project under `tests/` and
// `samples/`: which packages may it reference, directly or transitively,
// and is a BANNED one among them?
//
// The banned names are read out of BannedSymbols.txt, the file the
// BannedApiAnalyzers already consume -- there is deliberately NO second
// list of banned packages here, because a second list is a copy of the
// answer, and a copy of the answer drifts.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// The scope, the inputs and the constants.
const (
	scopeDirTests   = "tests"
	scopeDirSamples = "samples"

	bannedSymbolsFile = "BannedSymbols.txt"
	exceptionsFile    = "tools/test-package-exceptions.txt"

	lockFileName = "packages.lock.json"
)

// The directories whose projects are NOT restored by CI, and therefore do
// not take part in the all-or-nothing question.
var notRestored = []string{"contrib"}

// The patterns.
var (
	// `<PackageReference Include="X" ...>` — self-closing or with a body,
	// and the attribute may sit on any line of the element.
	packageRefRe = regexp.MustCompile(`(?s)<PackageReference\b[^>]*?Include\s*=\s*"([^"]+)"`)
	// An `N:` entry of BannedSymbols.txt: a banned NAMESPACE.
	namespaceBanRe = regexp.MustCompile(`^N:([^;\s]+)`)
	// The restore inputs, and the property that turns them on.
	wantsLockFileRe = regexp.MustCompile(`(?i)<RestorePackagesWithLockFile>\s*true\s*</RestorePackagesWithLockFile>`)
	lockedModeRe    = regexp.MustCompile(`(?i)<RestoreLockedMode>\s*true\s*</RestoreLockedMode>`)
)

// finding is one thing the gate found.
type finding struct {
	rule    string
	project string
	message string
	subject string
}

// String renders a finding the way the python original did.
func (f finding) String() string {
	return fmt.Sprintf("%s  %s: %s", f.rule, f.project, f.message)
}

// read reads a file, and an unreadable one is an empty one.
func read(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return string(data)
}

// projectException is one line of the allow-list: a name, a scope, a
// reason, an owner.
type projectException struct {
	subject     string
	projectGlob string
	reason      string
	owner       string
	lineNo      int
}

// matches reports whether this exception covers a name in a project.
func (e projectException) matches(name string, projectPath string) bool {
	if !strings.EqualFold(e.subject, name) {
		return false
	}
	glob := e.projectGlob
	if glob == "" || glob == "*" {
		return true
	}
	if fnmatchCase(projectPath, glob) {
		return true
	}
	return fnmatchCase(filepath.Base(projectPath), glob)
}

// fnmatchCase is python's fnmatch.fnmatchcase: `*` matches ANY character,
// the separator included.
func fnmatchCase(name string, pattern string) bool {
	return matchHere(name, pattern)
}

func matchHere(name string, pattern string) bool {
	for len(pattern) > 0 {
		switch pattern[0] {
		case '*':
			// Collapse the stars.
			for len(pattern) > 0 && pattern[0] == '*' {
				pattern = pattern[1:]
			}
			if len(pattern) == 0 {
				return true
			}
			for i := 0; i <= len(name); i++ {
				if matchHere(name[i:], pattern) {
					return true
				}
			}
			return false
		case '?':
			if len(name) == 0 {
				return false
			}
			name = name[1:]
			pattern = pattern[1:]
		case '[':
			if len(name) == 0 {
				return false
			}
			matched, rest := matchClass(name[0], pattern)
			if !matched {
				return false
			}
			name = name[1:]
			pattern = rest
		default:
			if len(name) == 0 || name[0] != pattern[0] {
				return false
			}
			name = name[1:]
			pattern = pattern[1:]
		}
	}
	return len(name) == 0
}

// matchClass matches one character against a `[...]` class, and returns the
// rest of the pattern.
func matchClass(char byte, pattern string) (bool, string) {
	rest := pattern[1:]
	negated := false
	if len(rest) > 0 && (rest[0] == '!' || rest[0] == '^') {
		negated = true
		rest = rest[1:]
	}
	matched := false
	for len(rest) > 0 && rest[0] != ']' {
		if len(rest) >= 3 && rest[1] == '-' && rest[2] != ']' {
			if char >= rest[0] && char <= rest[2] {
				matched = true
			}
			rest = rest[3:]
			continue
		}
		if rest[0] == char {
			matched = true
		}
		rest = rest[1:]
	}
	if len(rest) > 0 {
		rest = rest[1:]
	}
	if negated {
		matched = !matched
	}
	return matched, rest
}

// bannedNames reads the banned package/assembly names out of
// BannedSymbols.txt.
//
// `T:` and `M:` entries are types and methods: they name no package, so
// they contribute nothing here.
func bannedNames(root string) []string {
	seen := map[string]bool{}
	var names []string
	for _, line := range strings.Split(read(filepath.Join(root, bannedSymbolsFile)), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		match := namespaceBanRe.FindStringSubmatch(line)
		if match == nil {
			continue
		}
		if seen[match[1]] {
			continue
		}
		seen[match[1]] = true
		names = append(names, match[1])
	}
	return names
}

// parseExceptions reads the allow-list.
//
// A line with the wrong number of fields, or with an empty reason or an
// empty owner, is a finding and not an exception: the file is the one place
// in this repository where a permission is granted, and a permission nobody
// can justify is exactly the hole this gate closes.
func parseExceptions(root string) ([]projectException, []finding) {
	var exceptions []projectException
	var findings []finding
	path := filepath.Join(root, exceptionsFile)
	if _, err := os.Stat(path); err != nil {
		findings = append(findings, finding{
			rule:    "EXCEPTION-UNJUSTIFIED",
			project: exceptionsFile,
			message: "the exception file does not exist. An allow-list that is " +
				"absent is indistinguishable from an allow-list that grants " +
				"everything, so its absence is reported rather than read as " +
				"'no exceptions'.",
		})
		return exceptions, findings
	}
	for number, raw := range strings.Split(read(path), "\n") {
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
				rule:    "EXCEPTION-UNJUSTIFIED",
				project: fmt.Sprintf("%s:%d", exceptionsFile, number+1),
				message: fmt.Sprintf(
					"expected 4 fields (<name> | <project glob> | <reason> | <owner>), got %d: %q",
					len(parts), raw),
			})
			continue
		}
		subject, glob, reason, owner := parts[0], parts[1], parts[2], parts[3]
		if subject == "" || reason == "" || owner == "" {
			var missing []string
			if subject == "" {
				missing = append(missing, "name")
			}
			if reason == "" {
				missing = append(missing, "reason")
			}
			if owner == "" {
				missing = append(missing, "owner")
			}
			findings = append(findings, finding{
				rule:    "EXCEPTION-UNJUSTIFIED",
				project: fmt.Sprintf("%s:%d", exceptionsFile, number+1),
				message: fmt.Sprintf(
					"the %s field is empty. An exception without a reason is a silent allowance.",
					strings.Join(missing, ", ")),
			})
			continue
		}
		exceptions = append(exceptions, projectException{
			subject:     subject,
			projectGlob: glob,
			reason:      reason,
			owner:       owner,
			lineNo:      number + 1,
		})
	}
	return exceptions, findings
}

// assetsFile is the resolved package graph of a restored project.
type assetsFile struct {
	Project struct {
		Frameworks map[string]struct {
			Dependencies map[string]struct {
				Target string `json:"target"`
			} `json:"dependencies"`
		} `json:"frameworks"`
	} `json:"project"`
	Targets map[string]map[string]struct {
		Type    string                     `json:"type"`
		Runtime map[string]json.RawMessage `json:"runtime"`
		Compile map[string]json.RawMessage `json:"compile"`
	} `json:"targets"`
}

// resolvedPackage is one package of the resolved graph.
type resolvedPackage struct {
	id         string
	versions   map[string]bool
	frameworks map[string]bool
	direct     bool
	runtime    map[string]bool
	compile    map[string]bool
}

// resolvedPackages reads the resolved package graph of a restored project.
//
// `obj/project.assets.json` carries, per target framework, every package in
// the closure with its version and its asset paths, and marks which of them
// the project declares directly.
func resolvedPackages(assets assetsFile) map[string]*resolvedPackage {
	direct := map[string]bool{}
	for _, framework := range assets.Project.Frameworks {
		for name, spec := range framework.Dependencies {
			if spec.Target == "Package" {
				direct[name] = true
			}
		}
	}
	packages := map[string]*resolvedPackage{}
	for framework, entries := range assets.Targets {
		for key, spec := range entries {
			if spec.Type != "package" {
				continue
			}
			name, version, _ := strings.Cut(key, "/")
			record, ok := packages[name]
			if !ok {
				record = &resolvedPackage{
					id:         name,
					versions:   map[string]bool{},
					frameworks: map[string]bool{},
					runtime:    map[string]bool{},
					compile:    map[string]bool{},
				}
				packages[name] = record
			}
			record.versions[version] = true
			record.frameworks[framework] = true
			record.direct = record.direct || direct[name]
			for asset := range spec.Runtime {
				record.runtime[asset] = true
			}
			for asset := range spec.Compile {
				record.compile[asset] = true
			}
		}
	}
	return packages
}

// declaredPackages reads the package ids a `.csproj` declares itself.
func declaredPackages(csprojXML string) []string {
	seen := map[string]bool{}
	var names []string
	for _, match := range packageRefRe.FindAllStringSubmatch(csprojXML, -1) {
		if seen[match[1]] {
			continue
		}
		seen[match[1]] = true
		names = append(names, match[1])
	}
	sort.Strings(names)
	return names
}

// outputDirs are the build-output roots of a project, if it has been built.
func outputDirs(projectDir string) []string {
	base := filepath.Join(projectDir, "bin")
	if info, err := os.Stat(base); err == nil && info.IsDir() {
		return []string{base}
	}
	return nil
}

// outputDLLs are the DLLs sitting in a project's build output tree.
func outputDLLs(projectDir string) []string {
	base := filepath.Join(projectDir, "bin")
	var found []string
	walkTree(base, func(name string) bool { return name != "ref" },
		func(path string, dirs []string, files []string) {
			for _, name := range files {
				if strings.HasSuffix(strings.ToLower(name), ".dll") {
					found = append(found, filepath.Join(path, name))
				}
			}
		})
	sort.Strings(found)
	return found
}

// projectCensus is what the gate knows about one project.
type projectCensus struct {
	declared   []string
	resolved   []string
	direct     []string
	hasGraph   bool
	outputDirs int
	outputDLLs int
}

// census is everything the gate derived.
type census struct {
	projects   map[string]*projectCensus
	order      []string
	banned     []string
	exceptions []projectException
	totals     map[string]int
}

// analyse returns the census and the findings.
func analyse(root string, allowMissingAssets bool, outputOnly bool) (census, []finding) {
	var findings []finding
	result := census{projects: map[string]*projectCensus{}}

	banned := bannedNames(root)
	sort.Strings(banned)
	result.banned = banned
	if len(banned) == 0 {
		// The gate with an empty ban set reports a clean tree for the same
		// reason a gate whose matcher stopped matching does: it found
		// nothing because it was looking for nothing.
		findings = append(findings, finding{
			rule:    "PACKAGE-BLIND",
			project: bannedSymbolsFile,
			message: "no banned name was read. The ban set is empty, so every " +
				"package in every test project is allowed by definition - a " +
				"gate that bans nothing cannot report a banned package. " +
				"Check that the file exists and carries at least one `N:` entry.",
		})
	}
	exceptions, exceptionFindings := parseExceptions(root)
	result.exceptions = exceptions
	findings = append(findings, exceptionFindings...)

	// The restore inputs are what produce the package set this gate reads,
	// so the question "is the tree half-migrated to lock files" belongs here.
	findings = append(findings, restoreInputFindings(root)...)

	projects := scopeProjects(root)
	if len(projects) == 0 {
		findings = append(findings, finding{
			rule:    "PACKAGE-BLIND",
			project: scopeDirTests + "/" + scopeDirSamples,
			message: "no project was found in scope. A gate that examined " +
				"nothing cannot report that everything is fine.",
		})
	}

	usedExceptions := map[int]bool{}
	totalDeclared := 0
	totalResolved := 0
	totalOutputDirs := 0
	totalOutputDLLs := 0

	bannedLower := map[string]bool{}
	for _, name := range banned {
		bannedLower[strings.ToLower(name)] = true
	}

	for _, relative := range projects {
		projectDir := filepath.Dir(relative)
		csproj := filepath.Join(root, relative)
		var declared []string
		if !outputOnly {
			declared = declaredPackages(read(csproj))
		}
		totalDeclared += len(declared)

		assetsPath := filepath.Join(root, projectDir, "obj", "project.assets.json")
		packages := map[string]*resolvedPackage{}
		hasGraph := false
		if _, err := os.Stat(assetsPath); err == nil {
			hasGraph = true
		}
		if outputOnly {
			hasGraph = false
		} else if hasGraph {
			data, err := os.ReadFile(assetsPath)
			if err != nil {
				hasGraph = false
				if !allowMissingAssets {
					findings = append(findings, finding{
						rule:    "PACKAGE-BLIND",
						project: relative,
						message: fmt.Sprintf(
							"the resolved package graph could not be read (%s). Re-run `dotnet restore`.",
							err.Error()),
					})
				}
			} else {
				var assets assetsFile
				if err := json.Unmarshal(data, &assets); err != nil {
					hasGraph = false
					if !allowMissingAssets {
						findings = append(findings, finding{
							rule:    "PACKAGE-BLIND",
							project: relative,
							message: fmt.Sprintf(
								"the resolved package graph could not be read (%s). Re-run `dotnet restore`.",
								err.Error()),
						})
					}
				} else {
					packages = resolvedPackages(assets)
				}
			}
		} else if !allowMissingAssets && !outputOnly {
			findings = append(findings, finding{
				rule:    "PACKAGE-BLIND",
				project: relative,
				message: "no resolved package graph (obj/project.assets.json). The " +
					"transitive package set of this project is unverified, so run " +
					"`dotnet restore` before the gate. An unverified project is not " +
					"a clean project.",
			})
		}

		resolved := make([]string, 0, len(packages))
		for name := range packages {
			resolved = append(resolved, name)
		}
		sort.Strings(resolved)
		totalResolved += len(resolved)

		direct := []string{}
		for _, name := range resolved {
			if packages[name].direct {
				direct = append(direct, name)
			}
		}

		entry := &projectCensus{
			declared: declared,
			resolved: resolved,
			direct:   direct,
			hasGraph: hasGraph,
		}
		result.projects[relative] = entry
		result.order = append(result.order, relative)

		// One finding per (project, banned name), with every piece of
		// evidence collected first.
		type evidence struct {
			name    string
			kinds   map[string]bool
			details []string
		}
		evidenceByName := map[string]*evidence{}
		note := func(name string, kind string, detail string) {
			key := strings.ToLower(name)
			entry, ok := evidenceByName[key]
			if !ok {
				entry = &evidence{name: name, kinds: map[string]bool{}}
				evidenceByName[key] = entry
			}
			entry.kinds[kind] = true
			entry.details = append(entry.details, detail)
		}

		for _, name := range declared {
			if bannedLower[strings.ToLower(name)] {
				note(name, "package", fmt.Sprintf("declared by %s", filepath.Base(csproj)))
			}
		}

		for _, name := range resolved {
			spec := packages[name]
			if bannedLower[strings.ToLower(name)] {
				how := "direct"
				if !spec.direct {
					how = "transitive"
				}
				versions := make([]string, 0, len(spec.versions))
				for version := range spec.versions {
					versions = append(versions, version)
				}
				sort.Strings(versions)
				note(name, "package", fmt.Sprintf("%s in the resolved package graph (%s)",
					strings.Join(versions, ", "), how))
			}
			assets := make([]string, 0, len(spec.runtime))
			for asset := range spec.runtime {
				assets = append(assets, asset)
			}
			sort.Strings(assets)
			for _, asset := range assets {
				stem := filepath.Base(asset)
				if strings.HasSuffix(strings.ToLower(stem), ".dll") {
					stem = stem[:len(stem)-4]
				}
				if !bannedLower[strings.ToLower(stem)] {
					continue
				}
				if strings.EqualFold(stem, name) {
					note(name, "package", fmt.Sprintf("runtime asset %s", asset))
				} else {
					note(stem, "assembly", fmt.Sprintf("runtime asset %s", asset))
				}
			}
		}

		// The output directory, asked directly.
		roots := outputDirs(filepath.Join(root, projectDir))
		totalOutputDirs += len(roots)
		dlls := outputDLLs(filepath.Join(root, projectDir))
		entry.outputDirs = len(roots)
		entry.outputDLLs = len(dlls)
		totalOutputDLLs += len(dlls)
		for _, dll := range dlls {
			stem := filepath.Base(dll)
			stem = stem[:len(stem)-4]
			if !bannedLower[strings.ToLower(stem)] {
				continue
			}
			note(stem, "assembly", "present in the output directory at "+relativeTo(root, dll))
		}

		keys := make([]string, 0, len(evidenceByName))
		for key := range evidenceByName {
			keys = append(keys, key)
		}
		sort.Strings(keys)
		for _, key := range keys {
			entry := evidenceByName[key]
			name := entry.name
			if index := coveringException(exceptions, name, projectDir); index >= 0 {
				usedExceptions[index] = true
				continue
			}
			rule := "BANNED-ASSEMBLY"
			if entry.kinds["package"] {
				rule = "BANNED-PACKAGE"
			}
			findings = append(findings, finding{
				rule:    rule,
				project: relative,
				message: fmt.Sprintf(
					"%s is banned by %s (`N:%s`) and appears in this project: %s. "+
						"Either drop the dependency that drags it in, or record a named "+
						"exception in %s saying why this test project cannot do without it.",
					name, bannedSymbolsFile, name, strings.Join(entry.details, "; "), exceptionsFile),
				subject: name,
			})
		}
	}
	if outputOnly && totalOutputDirs == 0 {
		findings = append(findings, finding{
			rule:    "PACKAGE-BLIND",
			project: scopeDirTests + "/" + scopeDirSamples,
			message: "no build output was found for any project in scope, so the " +
				"output directories were not looked at. A gate that examined " +
				"nothing cannot report that everything is fine - build the tree first.",
		})
	}

	for index, exception := range exceptions {
		if usedExceptions[index] {
			continue
		}
		findings = append(findings, finding{
			rule:    "EXCEPTION-STALE",
			project: fmt.Sprintf("%s:%d", exceptionsFile, exception.lineNo),
			message: fmt.Sprintf(
				"the exception for %s (projects: %s) matches no package in any project "+
					"in scope. An exception that outlives the dependency that needed it is "+
					"a permission with no referent — delete it.",
				exception.subject, exception.projectGlob),
			subject: exception.subject,
		})
	}

	result.totals = map[string]int{
		"projects":    len(projects),
		"declared":    totalDeclared,
		"resolved":    totalResolved,
		"output_dirs": totalOutputDirs,
		"output_dlls": totalOutputDLLs,
	}
	return result, findings
}

// coveringException is the index of the exception covering (name, project),
// or -1.
func coveringException(exceptions []projectException, name string, project string) int {
	for index, exception := range exceptions {
		if exception.matches(name, project) {
			return index
		}
	}
	return -1
}

// restoreInputFindings asks whether the tree's restore input is
// all-or-nothing.
func restoreInputFindings(root string) []finding {
	var findings []finding
	projects := allProjects(root)
	owners := 0
	for _, project := range projects {
		if _, err := os.Stat(filepath.Join(root, filepath.Dir(project), lockFileName)); err == nil {
			owners++
		}
	}

	wantsLockFile := false
	lockedMode := false
	walkTree(root, func(name string) bool {
		return name != "bin" && name != "obj" && name != ".git" && !contains(notRestored, name)
	}, func(path string, dirs []string, files []string) {
		for _, name := range files {
			if !strings.HasSuffix(name, ".props") &&
				!strings.HasSuffix(name, ".targets") &&
				!strings.HasSuffix(name, ".csproj") {
				continue
			}
			body := read(filepath.Join(path, name))
			if wantsLockFileRe.MatchString(body) {
				wantsLockFile = true
			}
			if lockedModeRe.MatchString(body) {
				lockedMode = true
			}
		}
	})

	if owners > 0 && !wantsLockFile {
		findings = append(findings, finding{
			rule:    "RESTORE-INPUTS-UNENFORCED",
			project: lockFileName,
			message: fmt.Sprintf(
				"%d project(s) carry a %s but nothing sets RestorePackagesWithLockFile, "+
					"so NuGet never writes it, never checks it, and the restore ignores it. "+
					"The file rots silently until somebody sets the property.",
				owners, lockFileName),
		})
	}
	if wantsLockFile && owners == 0 {
		findings = append(findings, finding{
			rule:    "RESTORE-INPUTS-UNWRITTEN",
			project: "RestorePackagesWithLockFile",
			message: "RestorePackagesWithLockFile is set but no project has a " +
				lockFileName + ": every restore writes one, so the tree is dirty after " +
				"every restore and the lock file is not a record of anything.",
		})
	}
	if lockedMode && !wantsLockFile {
		findings = append(findings, finding{
			rule:    "RESTORE-LOCKED-WITHOUT-FILES",
			project: "RestoreLockedMode",
			message: "RestoreLockedMode is set but RestorePackagesWithLockFile is not: " +
				"the restore demands a " + lockFileName + " that is never written, and fails.",
		})
	}

	// Half-migrated, per scope.
	scopes := map[string][]string{}
	for _, project := range projects {
		scope := strings.Split(project, "/")[0]
		scopes[scope] = append(scopes[scope], project)
	}
	names := make([]string, 0, len(scopes))
	for name := range scopes {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, scope := range names {
		members := scopes[scope]
		withLock := []string{}
		for _, member := range members {
			if _, err := os.Stat(filepath.Join(root, filepath.Dir(member), lockFileName)); err == nil {
				withLock = append(withLock, member)
			}
		}
		if len(withLock) == 0 || len(withLock) == len(members) {
			continue
		}
		missing := []string{}
		for _, member := range members {
			found := false
			for _, one := range withLock {
				if one == member {
					found = true
					break
				}
			}
			if !found {
				missing = append(missing, member)
			}
		}
		sort.Strings(missing)
		shown := missing
		suffix := ""
		if len(missing) > 5 {
			shown = missing[:5]
			suffix = " ..."
		}
		findings = append(findings, finding{
			rule:    "RESTORE-INPUTS-PARTIAL",
			project: scope + "/",
			message: fmt.Sprintf(
				"%d of %d project(s) under %s/ carry a %s and %d do not: the tree is "+
					"half-migrated, so two projects in the same scope restore differently. "+
					"Without: %s%s",
				len(withLock), len(members), scope, lockFileName, len(missing),
				strings.Join(shown, ", "), suffix),
		})
	}
	return findings
}

// contains reports whether a list holds a value.
func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}
