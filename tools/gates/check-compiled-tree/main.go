// Package checkcompiledtree is the gate that proves the lane compiled the
// tree it was given.
//
// # THE FACT THIS GATE CHECKS
//
// The build-output cache (#1927) restores obj/ and bin/ from a commit, and
// the mtime normalisation stamps the restored outputs so that MSBuild skips
// the projects that did not change. That is what makes the compile
// incremental, and it is also what can make a COMPILE ERROR invisible: a
// project that is skipped is never handed to the compiler, so an error in it
// never reaches the build.
//
// Issue #2100 is exactly that: a broken file sat in the tree, the linux lane
// came from the cache, and the lane was green. A lane must be able to PROVE
// that it compiled the tree it was handed, and a tree that does not compile
// must not be able to pass.
//
// # HOW THE GATE KNOWS
//
// Two facts, one from git and one from the clock.
//
//   - the CHANGED set: the files whose CONTENT differs between the tree the
//     restored outputs were built from (--base) and the tree under test. The
//     tree under test is the WORKING TREE, not HEAD: the working tree is
//     what the compiler read, and a lane that patched the tree after the
//     checkout compiled something other than the commit.
//
//   - the BUILD START: the moment the compile began, taken from a marker file
//     the workflow touches right before it compiles.
//
// A project whose sources changed MUST have been compiled during this run,
// so its output must be NEWER than the build start. An output older than the
// build start is an output the compile did not produce: the project was
// skipped, and whatever is in it was never compiled.
//
// # WHY THE ANCHOR IS THE BUILD START AND NOT THE SOURCE
//
// The mtimes of the sources are exactly what the normalisation is allowed to
// lie about: it stamps a file it believes unchanged with the base commit's
// time, and a file it believes changed one second after the outputs. A gate
// that asks "is the source newer than the output?" is asking the question
// the normalisation manipulates, and it inherits the manipulation. The build
// start is a fact the normalisation cannot reach: the workflow writes it
// after the normalisation, and nothing the normalisation does can move it.
//
// # WHY THE OUTPUTS AND NOT THE BUILD LOG
//
// The gate reads the FILES the build left behind, so it works where the
// build ran and needs neither the SDK nor a log parser.
//
// # WHICH TREE THE LANE WAS GIVEN
//
// The gate is told the commit the RESTORED OUTPUTS were built from, and that
// commit is the tree the lane was given. The cache has two mechanisms that
// decide which record that is, and both of them are the cache service's, not
// the gate's:
//
//   - the PREFIX: the key names the toolchain, the SDK, the package graph
//     and the props files, so a record written under another prefix is a
//     record of another build environment and the service never serves it.
//
//   - the VERSION: the hash of the path list, so a record written under
//     another list is not served either.
//
// So the lane either has the outputs of the tree it was given, or it compiles
// from scratch, and the commit the gate compares against is the commit of
// the record that was ACTUALLY restored — the key that MATCHED, not the key
// the resolve step named. A record of another version or another prefix is
// never under the lane's feet, so the gate is never asked to certify a tree
// it cannot see.
//
// # WHAT IS IN SCOPE, AND WHY
//
// The scope is the C# sources a project compiles, and nothing else. A
// compile error lives in a source file, so a source file is what the gate
// must reason about; the other inputs of the compile (the project file, the
// Directory.Build.props chain, the .editorconfig) are real inputs of
// CoreCompile, but a change in them is not a place a compile error hides,
// and modelling them exactly would put the gate at the mercy of MSBuild's
// item evaluation. The gate says what it can prove: every project whose
// SOURCES changed was recompiled.
//
// The exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package checkcompiledtree

import (
	"fmt"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"harbor.local/gates/internal/gate"
)

// Run is the gate entry point.
func Run(args []string) int {
	root := "."
	base := ""
	buildStart := ""
	solution := "Harbor.slnx"
	config := "Release"
	minProjects := 0
	selfTest := false

	for i := 0; i < len(args); i++ {
		name, value, hasValue := splitFlag(args[i])
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
		case "--base":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--base needs a commit")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			base = value
		case "--build-start":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--build-start needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			buildStart = value
		case "--solution":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--solution needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			solution = value
		case "--config":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--config needs a name")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			config = value
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
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-compiled-tree --base COMMIT --build-start FILE [--root DIR] [--solution Harbor.slnx] [--config Release] [--min-projects N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if base == "" {
		fmt.Fprintln(os.Stderr, "check-compiled-tree: --base is required")
		return gate.Broken
	}
	if buildStart == "" {
		fmt.Fprintln(os.Stderr, "check-compiled-tree: --build-start is required")
		return gate.Broken
	}

	absolute, err := filepath.Abs(root)
	if err != nil {
		fmt.Fprintf(os.Stderr, "check-compiled-tree: %v\n", err)
		return gate.Broken
	}

	verdict, err := check(absolute, base, buildStart, solution, config)
	if err != nil {
		fmt.Fprintf(os.Stderr, "check-compiled-tree: %v\n", err)
		return gate.Broken
	}

	if minProjects > 0 && verdict.projects < minProjects {
		fmt.Fprintf(os.Stderr,
			"check-compiled-tree: the solution names %d projects, and the floor is %d — "+
				"the gate cannot see the tree it is meant to check\n",
			verdict.projects, minProjects)
		return gate.Broken
	}

	fmt.Printf("[check-compiled-tree] %s\n", verdict.summary)
	for _, finding := range verdict.findings {
		fmt.Printf("  %s\n", finding)
	}

	if len(verdict.findings) > 0 {
		fmt.Fprintf(os.Stderr,
			"check-compiled-tree: %d project(s) whose sources changed were NOT compiled in "+
				"this run — the lane compiled a tree other than the one it was given, so a "+
				"compile error in them would not have reached the build\n",
			len(verdict.findings))
		return gate.Red
	}
	return gate.Green
}

// project is one project of the solution.
type project struct {
	// path is the repository-relative path of the project file.
	path string
	// dir is the repository-relative directory that holds the project file.
	dir string
	// assembly is the name of the assembly the project produces.
	assembly string
	// sources are the repository-relative source files the project compiles.
	sources []string
}

// finding is one project that was not compiled although its sources changed.
type finding struct {
	project string
	changed string
	reason  string
}

// String is the one-line form of a finding.
func (f finding) String() string {
	return fmt.Sprintf("%s  (changed: %s)  %s", f.project, f.changed, f.reason)
}

// verdict is the result of the check.
type verdict struct {
	summary  string
	projects int
	findings []finding
}

// check runs the gate over the tree at root.
func check(root string, base string, buildStartPath string, solution string, config string) (*verdict, error) {
	start, err := buildStartMoment(buildStartPath)
	if err != nil {
		return nil, err
	}

	paths, err := solutionProjects(filepath.Join(root, solution))
	if err != nil {
		return nil, err
	}
	if len(paths) == 0 {
		return nil, fmt.Errorf("%s names no project", solution)
	}

	changed, err := changedFiles(root, base)
	if err != nil {
		return nil, err
	}

	projects, err := readProjects(root, paths)
	if err != nil {
		return nil, err
	}

	findings, checked, err := evaluate(projects, changed, start, root, config)
	if err != nil {
		return nil, err
	}

	summary := fmt.Sprintf(
		"base=%s projects=%d changed_files=%d changed_projects=%d build_start=%d findings=%d",
		base, len(projects), len(changed), checked, start.Unix(), len(findings))
	return &verdict{summary: summary, projects: len(projects), findings: findings}, nil
}

// evaluate is the gate's decision, over the projects and the changed set.
//
// It is separated from the reading of the tree so that the self-test can
// drive the same decision on a synthetic tree, which is what proves the
// gate can go red.
func evaluate(projects []project, changed map[string]bool, start time.Time, root string, config string) ([]finding, int, error) {
	findings := []finding{}
	checked := 0
	for _, p := range projects {
		touched, ok := firstChanged(p, changed)
		if !ok {
			continue
		}
		checked++
		output, moment, err := newestOutput(root, p, config)
		if err != nil {
			return nil, 0, err
		}
		if output == "" {
			findings = append(findings, finding{
				project: p.path,
				changed: touched,
				reason:  "the project has no build output, so the compile never reached it",
			})
			continue
		}
		if moment.Before(start) {
			findings = append(findings, finding{
				project: p.path,
				changed: touched,
				reason: fmt.Sprintf(
					"the output %s is older than the start of this build, so the compile "+
						"SKIPPED this project and never read %s",
					output, touched),
			})
		}
	}
	sort.Slice(findings, func(i, j int) bool { return findings[i].project < findings[j].project })
	return findings, checked, nil
}

// buildStartMoment reads the moment the build started from the marker file.
//
// The marker is written by the workflow right before it compiles, so its
// modification time IS the start of the build: every output the build
// produced is newer than it, and every output the build did not produce is
// older than it.
func buildStartMoment(marker string) (time.Time, error) {
	info, err := os.Stat(marker)
	if err != nil {
		return time.Time{}, fmt.Errorf("cannot read the build-start marker %s: %w", marker, err)
	}
	return info.ModTime(), nil
}

// solutionProjects reads the project paths of a solution file.
//
// The `<Project Path="..."/>` elements are the projects, and the folders
// around them are the solution's own grouping, which the gate does not need.
func solutionProjects(file string) ([]string, error) {
	data, err := os.ReadFile(file)
	if err != nil {
		return nil, fmt.Errorf("cannot read %s: %w", file, err)
	}
	var paths []string
	for _, line := range strings.Split(string(data), "\n") {
		start := strings.Index(line, "<Project Path=\"")
		if start < 0 {
			continue
		}
		rest := line[start+len("<Project Path=\""):]
		end := strings.Index(rest, "\"")
		if end < 0 {
			continue
		}
		paths = append(paths, strings.ReplaceAll(rest[:end], "\\", "/"))
	}
	sort.Strings(paths)
	return paths, nil
}

// readProjects reads every project of the solution.
func readProjects(root string, paths []string) ([]project, error) {
	projects := make([]project, 0, len(paths))
	for _, p := range paths {
		project, err := readProject(root, p, paths)
		if err != nil {
			return nil, err
		}
		projects = append(projects, project)
	}
	sort.Slice(projects, func(i, j int) bool { return projects[i].path < projects[j].path })
	return projects, nil
}

// readProject reads one project: its directory, its assembly name and the
// sources it compiles.
//
// The sources are the C# files under the project's own directory, minus the
// directories that hold a project of their own, plus the files the project
// names itself with a <Compile Include="..."/> element — the shared sources,
// which live outside the project directory and are compiled into several
// projects at once.
func readProject(root string, projectPath string, all []string) (project, error) {
	dir := dirOf(projectPath)

	assembly, err := readAssemblyName(filepath.Join(root, projectPath))
	if err != nil {
		return project{}, err
	}

	excluded := excludedDirs(projectPath, all)
	sources := map[string]bool{}

	entries, err := os.ReadDir(filepath.Join(root, dir))
	if err != nil {
		return project{}, err
	}
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		if excluded[entry.Name()] {
			continue
		}
		walkSources(filepath.Join(root, dir, entry.Name()), dir+"/"+entry.Name(), sources)
	}
	walkSources(filepath.Join(root, dir), dir, sources)

	included, removed, err := readCompileItems(filepath.Join(root, projectPath), dir)
	if err != nil {
		return project{}, err
	}
	for _, item := range included {
		sources[item] = true
	}
	for _, item := range removed {
		delete(sources, item)
	}

	list := make([]string, 0, len(sources))
	for source := range sources {
		list = append(list, source)
	}
	sort.Strings(list)

	return project{path: projectPath, dir: dir, assembly: assembly, sources: list}, nil
}

// excludedDirs names the directories inside a project's own directory that
// hold a project of their own: the sources under them belong to that
// project, not to this one.
func excludedDirs(projectPath string, all []string) map[string]bool {
	excluded := map[string]bool{}
	prefix := dirOf(projectPath) + "/"
	for _, other := range all {
		if !strings.HasPrefix(other, prefix) {
			continue
		}
		rest := other[len(prefix):]
		index := strings.Index(rest, "/")
		if index < 0 {
			continue
		}
		excluded[rest[:index]] = true
	}
	return excluded
}

// dirOf returns the directory of a repository-relative project path.
func dirOf(projectPath string) string {
	if index := strings.LastIndex(projectPath, "/"); index >= 0 {
		return projectPath[:index]
	}
	return "."
}

// walkSources collects the C# files under a directory.
func walkSources(absolute string, relative string, into map[string]bool) {
	entries, err := os.ReadDir(absolute)
	if err != nil {
		return
	}
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() {
			if name == "bin" || name == "obj" {
				continue
			}
			walkSources(filepath.Join(absolute, name), relative+"/"+name, into)
			continue
		}
		if !strings.HasSuffix(name, ".cs") {
			continue
		}
		into[relative+"/"+name] = true
	}
}

// readAssemblyName reads the assembly name of a project file.
//
// The default is the file name without its extension, and the name the
// intermediate assembly carries is what the gate looks for on the disk.
func readAssemblyName(file string) (string, error) {
	data, err := os.ReadFile(file)
	if err != nil {
		return "", fmt.Errorf("cannot read %s: %w", file, err)
	}
	for _, line := range strings.Split(string(data), "\n") {
		trimmed := strings.TrimSpace(line)
		if !strings.HasPrefix(trimmed, "<AssemblyName>") {
			continue
		}
		start := strings.Index(trimmed, ">")
		end := strings.LastIndex(trimmed, "</AssemblyName>")
		if start < 0 || end < 0 || end <= start {
			continue
		}
		name := strings.TrimSpace(trimmed[start+1 : end])
		if name != "" {
			return name, nil
		}
	}
	base := filepath.Base(file)
	return strings.TrimSuffix(base, filepath.Ext(base)), nil
}

// readCompileItems reads the <Compile Include="..."/> and
// <Compile Remove="..."/> elements of a project file.
//
// The included files are the shared sources: they live outside the project
// directory and are compiled into several projects at once, so a change to
// one of them must rebuild every project that names it.
func readCompileItems(file string, dir string) (included []string, removed []string, err error) {
	data, err := os.ReadFile(file)
	if err != nil {
		return nil, nil, fmt.Errorf("cannot read %s: %w", file, err)
	}
	for _, line := range strings.Split(string(data), "\n") {
		trimmed := strings.TrimSpace(line)
		if !strings.HasPrefix(trimmed, "<Compile ") {
			continue
		}
		value := readAttribute(trimmed, "Include")
		if value == "" {
			value = readAttribute(trimmed, "Remove")
			if value == "" {
				continue
			}
			removed = append(removed, resolveItem(dir, value))
			continue
		}
		if strings.ContainsAny(value, "*?") {
			// A glob: the shared sources of this repository are named one
			// by one, and a glob would have to be expanded against the
			// tree, which the normalisation does not do either.
			continue
		}
		included = append(included, resolveItem(dir, value))
	}
	return included, removed, nil
}

// resolveItem turns a project-relative item into a repository-relative one.
func resolveItem(dir string, value string) string {
	value = strings.ReplaceAll(value, "\\", "/")
	if strings.HasPrefix(value, "/") {
		return strings.TrimPrefix(value, "/")
	}
	return path.Join(dir, value)
}

// readAttribute reads the value of an XML attribute of a single line.
func readAttribute(line string, name string) string {
	marker := name + "=\""
	start := strings.Index(line, marker)
	if start < 0 {
		return ""
	}
	rest := line[start+len(marker):]
	end := strings.Index(rest, "\"")
	if end < 0 {
		return ""
	}
	return rest[:end]
}

// firstChanged returns the first changed source of a project.
func firstChanged(p project, changed map[string]bool) (string, bool) {
	for _, source := range p.sources {
		if changed[source] {
			return source, true
		}
	}
	return "", false
}

// newestOutput returns the newest build output of a project and its moment.
//
// The output is the intermediate assembly in obj/<Config>/<tfm>/, the file
// the Csc task writes, and the copy under bin/<Config>/<tfm>/, which the
// copy task refreshes only when the compile produced a new one.
func newestOutput(root string, p project, config string) (string, time.Time, error) {
	patterns := []string{
		filepath.Join(root, p.dir, "obj", config, "*", p.assembly+".dll"),
		filepath.Join(root, p.dir, "bin", config, "*", p.assembly+".dll"),
	}
	newest := ""
	var moment time.Time
	for _, pattern := range patterns {
		matches, err := filepath.Glob(pattern)
		if err != nil {
			return "", time.Time{}, err
		}
		for _, match := range matches {
			info, err := os.Stat(match)
			if err != nil {
				continue
			}
			if newest == "" || info.ModTime().After(moment) {
				newest = match
				moment = info.ModTime()
			}
		}
	}
	if newest == "" {
		return "", time.Time{}, nil
	}
	relative, err := filepath.Rel(root, newest)
	if err != nil {
		relative = newest
	}
	return filepath.ToSlash(relative), moment, nil
}

// splitFlag splits a flag into its name and its value.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	index := strings.Index(arg, "=")
	if index < 0 {
		return arg, "", false
	}
	return arg[:index], arg[index+1:], true
}

// parseInt parses a non-negative integer.
func parseInt(text string) (int, bool) {
	value, err := strconv.Atoi(strings.TrimSpace(text))
	if err != nil || value < 0 {
		return 0, false
	}
	return value, true
}
