package testprojects

// projects.go -- the projects: what they are, what they reference, and how
// many tests they carry.
//
// The universe is the projects under `tests/` that the solution carries.
// A test project has TWO ways to reach a CI run: a list names it, or a
// project that a list names REFERENCES it, and its test classes are
// discovered inside that referencing project's process.

import (
	"encoding/xml"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// The files the gate reads.
const (
	solutionFile = "Harbor.slnx"
	testsDir     = "tests"
	testsProps   = "tests/Directory.Build.props"
)

// The patterns.
var (
	// `[Test]` and `[TestAttribute]`, and nothing that merely starts with
	// "Test" (`[TestFixture]` does not match: `\b` fails before the "F").
	testAttributeRe = regexp.MustCompile(`\[\s*Test(?:Attribute)?\b`)
	// Comments and string literals, blanked out before the attributes are
	// counted. A `[Test]` inside a doc comment or a string is not a test.
	notCodeRe = regexp.MustCompile(`(?s)//[^\n]*|/\*.*?\*/|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'`)
	// A type declaration: the name of a class, record, struct, interface or
	// enum the project declares.
	typeDeclarationRe = regexp.MustCompile(`(?m)^\s*(?:public|internal|file)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+)*(?:class|record|struct|interface|enum)\s+([A-Za-z0-9_]+)`)
	// The treenode filter expression a test project uses to split itself
	// into shards: `/*/*/(ClassA)|(ClassB)/*`. Its presence in code (not in
	// a comment) is the marker that the project defines a partition.
	treenodePartitionRe = regexp.MustCompile(`"(/\*/\*/\()`)
	// `<Project Path="..."/>` in the solution.
	solutionProjectRe = regexp.MustCompile(`<Project\s+Path="([^"]+)"`)
	// `<IsTestProject>true</IsTestProject>`.
	isTestProjectRe = regexp.MustCompile(`(?i)<IsTestProject>\s*(true|false)`)
)

// project is one project of the universe.
type project struct {
	name          string
	path          string
	dir           string
	isTestProject bool
	tests         int
}

// reference is one `<ProjectReference>` of a project.
type reference struct {
	name       string
	attributes map[string]string
}

// isRuntimeReference reports whether a reference pulls the referenced
// assembly into the process.
//
// A build-time reference drags no test classes into a test process.
func isRuntimeReference(attributes map[string]string) bool {
	if attributes["referenceoutputassembly"] == "false" {
		return false
	}
	if attributes["outputitemtype"] == "analyzer" {
		return false
	}
	return true
}

// read reads a file, and an unreadable one is an empty one.
func read(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return string(data)
}

// trackedTestProjects returns every `.csproj` under `tests/`,
// repository-relative.
//
// The INDEX (`git ls-files`) rather than a glob: an untracked scratch
// project must not be able to change what the gate sees, and a tracked
// project must not be able to hide from it. The walk is the fallback for a
// throwaway fixture tree, which is not a git repository.
func trackedTestProjects(root string) []string {
	if tracked, ok := gitLsFiles(root, testsDir); ok && len(tracked) > 0 {
		return tracked
	}
	var found []string
	_ = filepath.Walk(filepath.Join(root, testsDir), func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			if info.Name() == "bin" || info.Name() == "obj" {
				return filepath.SkipDir
			}
			return nil
		}
		if strings.HasSuffix(info.Name(), ".csproj") {
			found = append(found, relativeTo(root, path))
		}
		return nil
	})
	sort.Strings(found)
	return found
}

// solutionProjects returns every project path the solution carries,
// repository-relative.
func solutionProjects(root string) []string {
	text := read(filepath.Join(root, solutionFile))
	seen := map[string]bool{}
	var found []string
	for _, match := range solutionProjectRe.FindAllStringSubmatch(text, -1) {
		if seen[match[1]] {
			continue
		}
		seen[match[1]] = true
		found = append(found, match[1])
	}
	sort.Strings(found)
	return found
}

// defaultIsTestProject reads the `IsTestProject` value a project under
// `tests/` inherits.
//
// Read out of `tests/Directory.Build.props` rather than assumed, so a change
// to that file changes the gate's universe with it.
func defaultIsTestProject(root string) (bool, bool) {
	text := read(filepath.Join(root, testsProps))
	match := isTestProjectRe.FindStringSubmatch(text)
	if match == nil {
		return false, false
	}
	return strings.EqualFold(match[1], "true"), true
}

// explicitIsTestProject reads the `IsTestProject` value a project says
// itself.
func explicitIsTestProject(csprojText string) (bool, bool) {
	match := isTestProjectRe.FindStringSubmatch(csprojText)
	if match == nil {
		return false, false
	}
	return strings.EqualFold(match[1], "true"), true
}

// projectReferences returns the projects a `.csproj` references, with their
// attributes.
//
// The attributes matter: `ReferenceOutputAssembly=false` and
// `OutputItemType=Analyzer` mark a BUILD-TIME reference.
func projectReferences(csprojText string) []reference {
	var found []reference
	decoder := xml.NewDecoder(strings.NewReader(csprojText))
	for {
		token, err := decoder.Token()
		if err != nil {
			break
		}
		start, ok := token.(xml.StartElement)
		if !ok {
			continue
		}
		if !strings.HasSuffix(start.Name.Local, "ProjectReference") {
			continue
		}
		attributes := map[string]string{}
		include := ""
		for _, attribute := range start.Attr {
			if attribute.Name.Local == "Include" {
				include = attribute.Value
				continue
			}
			attributes[strings.ToLower(attribute.Name.Local)] = strings.ToLower(attribute.Value)
		}
		include = strings.ReplaceAll(include, "\\", "/")
		name := filepath.Base(include)
		if !strings.HasSuffix(name, ".csproj") {
			continue
		}
		found = append(found, reference{
			name:       strings.TrimSuffix(name, ".csproj"),
			attributes: attributes,
		})
	}
	return found
}

// projectGraph returns every project in the tree and the projects it
// references.
func projectGraph(root string) map[string][]reference {
	graph := map[string][]reference{}
	for _, pattern := range []string{"src/*/*.csproj", "apps/*/*.csproj", "tests/*/*.csproj"} {
		matches, _ := filepath.Glob(filepath.Join(root, pattern))
		sort.Strings(matches)
		for _, path := range matches {
			name := strings.TrimSuffix(filepath.Base(path), ".csproj")
			graph[name] = projectReferences(read(path))
		}
	}
	return graph
}

// assemblyClosure returns the projects reachable from `start`, following
// runtime references.
func assemblyClosure(graph map[string][]reference, start string, skip map[string]bool) map[string]bool {
	seen := map[string]bool{}
	stack := []string{start}
	for len(stack) > 0 {
		current := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		for _, item := range graph[current] {
			if skip[item.name] || !isRuntimeReference(item.attributes) {
				continue
			}
			if !seen[item.name] {
				seen[item.name] = true
				stack = append(stack, item.name)
			}
		}
	}
	return seen
}

// countTests returns how many `[Test]` methods the project carries.
//
// Comments and string literals are blanked out first, so a `[Test]` in a doc
// comment or in a message string is not counted as a test.
func countTests(root string, projectDir string) int {
	total := 0
	base := filepath.Join(root, projectDir)
	_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			if info.Name() == "bin" || info.Name() == "obj" {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(info.Name(), ".cs") {
			return nil
		}
		text := notCodeRe.ReplaceAllString(read(path), " ")
		total += len(testAttributeRe.FindAllString(text, -1))
		return nil
	})
	return total
}

// declaredTypes returns the names of the types a project declares.
func declaredTypes(root string, projectDir string) map[string]bool {
	names := map[string]bool{}
	base := filepath.Join(root, projectDir)
	_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			if info.Name() == "bin" || info.Name() == "obj" {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(info.Name(), ".cs") {
			return nil
		}
		text := notCodeRe.ReplaceAllString(read(path), " ")
		for _, match := range typeDeclarationRe.FindAllStringSubmatch(text, -1) {
			names[match[1]] = true
		}
		return nil
	})
	return names
}

// usesTypes reports whether a project's sources name any of the type names.
//
// The check errs towards silence: a type name that collides with a local
// declaration counts as a use.
func usesTypes(root string, projectDir string, typeNames map[string]bool) bool {
	if len(typeNames) == 0 {
		return false
	}
	names := make([]string, 0, len(typeNames))
	for name := range typeNames {
		names = append(names, name)
	}
	sort.Strings(names)
	quoted := make([]string, 0, len(names))
	for _, name := range names {
		quoted = append(quoted, regexp.QuoteMeta(name))
	}
	pattern := regexp.MustCompile(`\b(?:` + strings.Join(quoted, "|") + `)\b`)
	base := filepath.Join(root, projectDir)
	found := false
	_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil || found {
			return nil
		}
		if info.IsDir() {
			if info.Name() == "bin" || info.Name() == "obj" {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(info.Name(), ".cs") {
			return nil
		}
		text := notCodeRe.ReplaceAllString(read(path), " ")
		if pattern.MatchString(text) {
			found = true
		}
		return nil
	})
	return found
}

// definesPartition reports whether a project defines a treenode-filter
// partition of itself.
//
// The marker is the filter expression itself, in code rather than in a
// comment.
func definesPartition(root string, projectDir string) bool {
	base := filepath.Join(root, projectDir)
	found := false
	_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil || found {
			return nil
		}
		if info.IsDir() {
			if info.Name() == "bin" || info.Name() == "obj" {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(info.Name(), ".cs") {
			return nil
		}
		if treenodePartitionRe.MatchString(stripComments(read(path))) {
			found = true
		}
		return nil
	})
	return found
}

// collectProjects returns the projects the gate reasons about, and what it
// knows about them.
func collectProjects(root string) (map[string]*project, bool, bool) {
	inherited, hasDefault := defaultIsTestProject(root)
	projects := map[string]*project{}
	for _, relative := range trackedTestProjects(root) {
		name := strings.TrimSuffix(filepath.Base(relative), ".csproj")
		text := read(filepath.Join(root, relative))
		explicit, hasExplicit := explicitIsTestProject(text)
		isTest := inherited
		if hasExplicit {
			isTest = explicit
		}
		projectDir := filepath.Dir(relative)
		tests := 0
		if isTest {
			tests = countTests(root, projectDir)
		}
		projects[name] = &project{
			name:          name,
			path:          relative,
			dir:           projectDir,
			isTestProject: isTest,
			tests:         tests,
		}
	}
	return projects, inherited, hasDefault
}

// stripComments returns the text with its comments blanked out and its
// strings left alone.
//
// A scanner and not a regex: the treenode filter expression this gate looks
// for is a STRING literal that starts with `/*`, and a regex for block
// comments would eat it and everything after it.
func stripComments(text string) string {
	var out strings.Builder
	index := 0
	size := len(text)
	for index < size {
		char := text[index]
		if char == '/' && index+1 < size && text[index+1] == '/' {
			end := strings.IndexByte(text[index:], '\n')
			if end < 0 {
				end = size
			} else {
				end = index + end
			}
			out.WriteString(strings.Repeat(" ", end-index))
			index = end
			continue
		}
		if char == '/' && index+1 < size && text[index+1] == '*' {
			end := strings.Index(text[index+2:], "*/")
			if end < 0 {
				end = size
			} else {
				end = index + 2 + end + 2
			}
			out.WriteString(blankKeepNewlines(text[index:end]))
			index = end
			continue
		}
		if char == '@' && index+1 < size && text[index+1] == '"' {
			end := index + 2
			for end < size {
				if text[end] == '"' {
					if end+1 < size && text[end+1] == '"' {
						end += 2
						continue
					}
					end++
					break
				}
				end++
			}
			out.WriteString(text[index:end])
			index = end
			continue
		}
		if char == '"' || char == '\'' {
			end := index + 1
			for end < size {
				if text[end] == '\\' {
					end += 2
					continue
				}
				if text[end] == char {
					end++
					break
				}
				if text[end] == '\n' {
					break
				}
				end++
			}
			out.WriteString(text[index:end])
			index = end
			continue
		}
		out.WriteByte(char)
		index++
	}
	return out.String()
}

// blankKeepNewlines blanks a text but keeps its newlines.
func blankKeepNewlines(text string) string {
	var out strings.Builder
	for i := 0; i < len(text); i++ {
		if text[i] == '\n' {
			out.WriteByte('\n')
			continue
		}
		out.WriteByte(' ')
	}
	return out.String()
}

// relativeTo returns a path relative to the root, with '/' separators.
func relativeTo(root string, path string) string {
	relative, err := filepath.Rel(root, path)
	if err != nil {
		return filepath.ToSlash(path)
	}
	return filepath.ToSlash(relative)
}
