package contractdomains

import (
	"encoding/xml"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// # THE MEASUREMENT
//
// This file reads the tree and answers one question per type: how many
// projects of the solution USE it. The answer is a fact about the sources,
// not a guess about the compiler: a project uses a type when its code names
// the type.
//
// WHY BY NAME AND NOT BY THE COMPILER
//
// The compiler is not available here (and the machine this runs on cannot
// hold a build of this tree), and the question the issue asks does not need
// one: "which projects would stop depending on the contract root if this
// type moved out" is answered by "which projects name it". The compiler
// would answer the same question exactly, and would also answer it about
// GENERIC instantiations and implicit conversions that a name scan cannot
// see. The two differences are named in the report.
//
// The three things a name scan must get right, and how they are handled:
//
//   - COMMENTS AND LITERALS. A type named in a comment or in a string is
//     not a use. Both are stripped before the scan.
//   - NAMES THAT LIVE IN TWO PLACES. Five names in this tree are declared
//     twice (`None`, `Session`, `Auto`, `HarborPaths`, `IProcessHandle`).
//     For those, the mention is resolved per FILE by the namespaces that
//     file imports: a file that imports the contract's namespace and not
//     the other one's means the contract's type; a file that imports both
//     is ambiguous and is reported rather than counted.
//   - FILES A PROJECT DOES NOT COMPILE. A project compiles the .cs files
//     under its own directory (minus bin/obj and minus the directories of
//     the projects nested in it), plus the files its project file links in,
//     minus the files its project file removes.

// noise strips the comments and the string literals of a C# file.
//
// One alternation, matched left to right, so whichever construct starts
// first wins. The raw-string branch is FIRST, because `"""…"""` would
// otherwise be read as three empty strings. The line-comment branch is
// bounded to its line: written as `//.*` it is "the rest of the file" and it
// silently swallows every `using` after the first comment.
var noise = regexp.MustCompile(
	`(?s)"""[\s\S]*?"""` +
		`|//[^\n]*` +
		`|/\*.*?\*/` +
		`|"(?:[^"\\\n]|\\.)*"` +
		`|'(?:[^'\\\n]|\\.)*'`)

// identRe is an identifier: the atom the scan counts.
var identRe = regexp.MustCompile(`[A-Za-z_][A-Za-z0-9_]*`)

// usingRe is a `using` directive: the namespace a file imports.
var usingRe = regexp.MustCompile(
	`(?m)^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?([A-Za-z_][A-Za-z0-9_.]*)[ \t]*;`)

// typeRe is a type declaration.
//
// The keyword alternation lists `record struct` and `record class` BEFORE
// `record` and `struct`, because the leftmost alternative wins and
// `record struct Foo` must not be read as a record named `struct`.
//
// The modifiers before the keyword are optional and repeated: `public
// sealed partial record None : ToolChoice;` is one declaration, and the
// modifiers are not part of the name.
var typeRe = regexp.MustCompile(
	`(?m)^[ \t]*` +
		`(?:(?:public|internal|protected|private|static|sealed|abstract|readonly|partial|` +
		`unsafe|file|new|ref|extern|virtual|override|async|const|required)\s+)*` +
		`(?:(record\s+struct|record\s+class|record|struct|class|interface|enum|delegate)\s+)` +
		`([A-Za-z_][A-Za-z0-9_]*)`)

// namespaceRe is a namespace declaration.
var namespaceRe = regexp.MustCompile(`(?m)^[ \t]*namespace[ \t]+([A-Za-z_][A-Za-z0-9_.]*)`)

// fileScopedNamespaceRe is a FILE-SCOPED namespace declaration: the one
// with a semicolon. It owns every type of the file after it.
var fileScopedNamespaceRe = regexp.MustCompile(`(?m)^[ \t]*namespace[ \t]+([A-Za-z_][A-Za-z0-9_.]*)[ \t]*;`)

// sourceFile is one C# file, as the scan sees it.
type sourceFile struct {
	// Path is the repository-relative path of the file.
	Path string
	// Code is the file without its comments and its string literals.
	Code string
	// Idents is every identifier of the code.
	Idents map[string]bool
	// Usings is every namespace the file imports.
	Usings map[string]bool
	// Lines is the number of lines of the file.
	Lines int
}

// project is one project of the solution, as the scan sees it.
type project struct {
	// Path is the solution-relative path of the project file.
	Path string
	// Dir is the repository-relative directory of the project.
	Dir string
	// Files are the C# files the project compiles.
	Files []*sourceFile
	// Idents is every identifier of the project's code.
	Idents map[string]bool
	// GlobalUsings are the namespaces the project's GlobalUsings.cs imports.
	GlobalUsings map[string]bool
	// References are the projects the project file references.
	References []string
}

// csproj is the part of a project file the scan reads.
type csproj struct {
	PropertyGroups []struct {
		EnableDefaultCompileItems *string `xml:"EnableDefaultCompileItems"`
	} `xml:"PropertyGroup"`
	ItemGroups []struct {
		Compile []struct {
			Include string `xml:"Include,attr"`
			Remove  string `xml:"Remove,attr"`
		} `xml:"Compile"`
		ProjectReferences []struct {
			Include string `xml:"Include,attr"`
		} `xml:"ProjectReference"`
	} `xml:"ItemGroup"`
}

// readProjectFile reads the project file of a project.
func readProjectFile(root string, projectPath string) (*csproj, error) {
	data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(projectPath)))
	if err != nil {
		return nil, fmt.Errorf("cannot read %s: %w", projectPath, err)
	}
	out := &csproj{}
	if err := xml.Unmarshal(data, out); err != nil {
		return nil, fmt.Errorf("cannot parse %s: %w", projectPath, err)
	}
	return out, nil
}

// defaultCompileItems says whether the project takes the SDK's default glob.
func (c *csproj) defaultCompileItems() bool {
	for _, group := range c.PropertyGroups {
		if group.EnableDefaultCompileItems == nil {
			continue
		}
		value := strings.TrimSpace(*group.EnableDefaultCompileItems)
		return value != "false" && value != "0"
	}
	return true
}

// compileIncludes are the files the project file links in.
func (c *csproj) compileIncludes() []string {
	var out []string
	for _, group := range c.ItemGroups {
		for _, item := range group.Compile {
			if item.Include != "" {
				out = append(out, item.Include)
			}
		}
	}
	return out
}

// compileRemoves are the files the project file removes from the glob.
func (c *csproj) compileRemoves() []string {
	var out []string
	for _, group := range c.ItemGroups {
		for _, item := range group.Compile {
			if item.Remove != "" {
				out = append(out, item.Remove)
			}
		}
	}
	return out
}

// projectReferences are the projects the project file references.
func (c *csproj) projectReferences() []string {
	var out []string
	for _, group := range c.ItemGroups {
		for _, item := range group.ProjectReferences {
			if item.Include != "" {
				out = append(out, strings.ReplaceAll(item.Include, "\\", "/"))
			}
		}
	}
	return out
}

// tree is the measured tree.
type tree struct {
	// Root is the repository root.
	Root string
	// Projects are the projects of the solution, in solution order.
	Projects []*project
	// ByPath indexes the projects.
	ByPath map[string]*project
	// OwnedDirs are the directories of the projects: a project does not
	// compile the files of a project nested in it.
	OwnedDirs map[string]bool
}

// loadTree reads the solution and every project of it.
func loadTree(root string, solutionPath string) (*tree, error) {
	data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(solutionPath)))
	if err != nil {
		return nil, fmt.Errorf("cannot read the solution %s: %w", solutionPath, err)
	}
	paths := solutionProjects(string(data))
	out := &tree{
		Root:      root,
		ByPath:    map[string]*project{},
		OwnedDirs: map[string]bool{},
	}
	for _, path := range paths {
		out.OwnedDirs[filepath.ToSlash(filepath.Dir(path))] = true
	}
	for _, path := range paths {
		proj, err := loadProject(root, path, out.OwnedDirs)
		if err != nil {
			return nil, err
		}
		out.Projects = append(out.Projects, proj)
		out.ByPath[path] = proj
	}
	return out, nil
}

// solutionProjects reads the projects of a solution file.
func solutionProjects(text string) []string {
	var out []string
	for _, match := range regexp.MustCompile(`<Project\s+Path="([^"]+)"`).FindAllStringSubmatch(text, -1) {
		out = append(out, filepath.ToSlash(match[1]))
	}
	return out
}

// normalizePath makes a path repository-relative and slash-separated.
//
// The project files of this repository write their paths with BACKSLASHES
// (`..\Harbor.Abstractions.Contracts\...`), and they are read on linux,
// where a backslash is a legal file name character and not a separator.
// `filepath.Clean` alone would leave the path alone, and the reference
// would point at nothing.
func normalizePath(path string) string {
	return filepath.ToSlash(filepath.Clean(strings.ReplaceAll(path, "\\", "/")))
}

// loadProject reads one project: its file set, its identifiers and its
// references.
func loadProject(root string, path string, owned map[string]bool) (*project, error) {
	file, err := readProjectFile(root, path)
	if err != nil {
		return nil, err
	}
	dir := filepath.ToSlash(filepath.Dir(path))
	proj := &project{
		Path:         path,
		Dir:          dir,
		Idents:       map[string]bool{},
		GlobalUsings: map[string]bool{},
	}
	for _, reference := range file.projectReferences() {
		candidate := normalizePath(filepath.Join(dir, reference))
		proj.References = append(proj.References, candidate)
	}
	sort.Strings(proj.References)

	files, err := sourceFiles(root, path, file, owned)
	if err != nil {
		return nil, err
	}
	for _, relative := range files {
		full := filepath.Join(root, filepath.FromSlash(relative))
		data, err := os.ReadFile(full)
		if err != nil {
			return nil, fmt.Errorf("cannot read %s: %w", relative, err)
		}
		stripped := noise.ReplaceAllString(string(data), " ")
		source := &sourceFile{
			Path:   relative,
			Code:   stripped,
			Idents: map[string]bool{},
			Usings: map[string]bool{},
			Lines:  strings.Count(string(data), "\n") + 1,
		}
		for _, ident := range identRe.FindAllString(stripped, -1) {
			source.Idents[ident] = true
			proj.Idents[ident] = true
		}
		for _, match := range usingRe.FindAllStringSubmatch(stripped, -1) {
			source.Usings[match[1]] = true
		}
		if filepath.Base(relative) == "GlobalUsings.cs" {
			for namespace := range source.Usings {
				proj.GlobalUsings[namespace] = true
			}
		}
		proj.Files = append(proj.Files, source)
	}
	return proj, nil
}

// sourceFiles are the C# files a project compiles.
func sourceFiles(root string, path string, file *csproj, owned map[string]bool) ([]string, error) {
	dir := filepath.ToSlash(filepath.Dir(path))
	full := filepath.Join(root, filepath.FromSlash(dir))
	set := map[string]bool{}
	if file.defaultCompileItems() {
		err := filepath.Walk(full, func(path string, info os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if info.IsDir() {
				if path == full {
					return nil
				}
				name := info.Name()
				if name == "bin" || name == "obj" || name == ".git" || name == ".vs" {
					return filepath.SkipDir
				}
				relative := filepath.ToSlash(filepath.Join(dir, filepath.Base(path)))
				if owned[relative] && relative != dir {
					return filepath.SkipDir
				}
				return nil
			}
			if !strings.HasSuffix(path, ".cs") {
				return nil
			}
			relative, err := filepath.Rel(full, path)
			if err != nil {
				return nil
			}
			// The path is REPOSITORY-relative, so that a file linked
			// in from another directory and a file of the project's own
			// directory are the same kind of thing.
			set[filepath.ToSlash(filepath.Join(dir, relative))] = true
			return nil
		})
		if err != nil {
			return nil, fmt.Errorf("cannot walk %s: %w", dir, err)
		}
	}
	for _, include := range file.compileIncludes() {
		resolved := normalizePath(filepath.Join(dir, include))
		full := filepath.Join(root, filepath.FromSlash(resolved))
		if info, err := os.Stat(full); err == nil && !info.IsDir() && strings.HasSuffix(resolved, ".cs") {
			set[resolved] = true
		}
	}
	for _, remove := range file.compileRemoves() {
		resolved := normalizePath(filepath.Join(dir, remove))
		delete(set, resolved)
	}
	out := make([]string, 0, len(set))
	for name := range set {
		out = append(out, name)
	}
	sort.Strings(out)
	return out, nil
}

// declSite is one place a type name is declared.
type declSite struct {
	// Project is the project that declares it.
	Project string
	// Namespace is the namespace it is declared in.
	Namespace string
	// File is the repository-relative file it is declared in.
	File string
}

// typeDecl is one declared type of a contract root.
type typeDecl struct {
	// Name is the type's name.
	Name string
	// Namespace is the namespace it is declared in.
	Namespace string
	// File is the repository-relative file it is declared in.
	File string
	// Lines is the number of lines of the file it is declared in.
	Lines int
	// Project is the project that declares it.
	Project string
}

// declsIn returns the types a file declares, with the namespace they are
// declared in.
func declsIn(source *sourceFile, projectPath string) []typeDecl {
	fileScoped := ""
	if match := fileScopedNamespaceRe.FindStringSubmatch(source.Code); match != nil {
		fileScoped = match[1]
	}
	namespaces := namespaceRe.FindAllStringSubmatchIndex(source.Code, -1)
	out := []typeDecl{}
	for _, match := range typeRe.FindAllStringSubmatchIndex(source.Code, -1) {
		name := source.Code[match[4]:match[5]]
		namespace := fileScoped
		if namespace == "" {
			for _, entry := range namespaces {
				if entry[0] < match[0] {
					namespace = source.Code[entry[2]:entry[3]]
				} else {
					break
				}
			}
		}
		out = append(out, typeDecl{
			Name:      name,
			Namespace: namespace,
			File:      source.Path,
			Project:   projectPath,
		})
	}
	return out
}

// typeCensus is the measured map: the types of the roots, and who uses them.
type typeCensus struct {
	// Roots are the contract roots the census covers.
	Roots []string
	// Types are the declared types of the roots, keyed by "<root>|<name>".
	Types map[string]*typeDecl
	// Consumers maps "<root>|<name>" to the projects that use the type.
	Consumers map[string]map[string]bool
	// Sites maps a type name to every place it is declared in the tree.
	Sites map[string][]declSite
	// Ambiguous maps a type name to the files that import two of its
	// namespaces at once: the mentions there are not counted for either.
	Ambiguous map[string][]string
	// FileLines is the number of lines of every file the census read.
	FileLines map[string]int
}

// measureTypes builds the census of the roots.
func measureTypes(measured *tree, roots []string) *typeCensus {
	out := &typeCensus{
		Roots:     roots,
		Types:     map[string]*typeDecl{},
		Consumers: map[string]map[string]bool{},
		Sites:     map[string][]declSite{},
		Ambiguous: map[string][]string{},
		FileLines: map[string]int{},
	}
	// Every place a name is declared, so that a name declared twice can be
	// resolved per file.
	for _, proj := range measured.Projects {
		for _, source := range proj.Files {
			for _, decl := range declsIn(source, proj.Path) {
				out.Sites[decl.Name] = append(out.Sites[decl.Name], declSite{
					Project:   proj.Path,
					Namespace: decl.Namespace,
					File:      decl.File,
				})
			}
		}
	}
	rootSet := map[string]bool{}
	for _, root := range roots {
		rootSet[root] = true
	}
	for _, root := range roots {
		proj, ok := measured.ByPath[root]
		if !ok {
			continue
		}
		for _, source := range proj.Files {
			lines := source.Lines
			out.FileLines[source.Path] = lines
			for _, decl := range declsIn(source, proj.Path) {
				key := root + "|" + decl.Name
				if existing, seen := out.Types[key]; seen {
					existing.Lines += lines
					continue
				}
				out.Types[key] = &typeDecl{
					Name:      decl.Name,
					Namespace: decl.Namespace,
					File:      decl.File,
					Lines:     lines,
					Project:   proj.Path,
				}
			}
		}
	}
	for key, decl := range out.Types {
		out.Consumers[key] = consumersOf(measured, decl.Name, out.Sites[decl.Name], out.Ambiguous)
		// The project that DECLARES a type does not USE it: the
		// declaration is where the name appears, not a reference to it.
		// The root's own types are therefore not consumers of the root,
		// and the counts say how many OTHER projects use the type.
		delete(out.Consumers[key], decl.Project)
	}
	return out
}

// consumersOf returns the projects that use a type name.
//
// The name is resolved per FILE when the tree declares it in more than one
// place: the file's imports decide which of the declarations the mention
// means. A file that imports two of them is ambiguous, and the mention is
// not counted for either: a wrong count is worse than a missing one.
func consumersOf(measured *tree, name string, sites []declSite, ambiguous map[string][]string) map[string]bool {
	out := map[string]bool{}
	if len(sites) == 0 {
		return out
	}
	for _, proj := range measured.Projects {
		for _, source := range proj.Files {
			if !source.Idents[name] {
				continue
			}
			if len(sites) == 1 {
				out[proj.Path] = true
				continue
			}
			resolved, state := resolveMention(source, proj, name, sites)
			switch state {
			case mentionResolved:
				out[proj.Path] = true
			case mentionAmbiguous:
				ambiguous[name] = append(ambiguous[name], source.Path)
			case mentionNotAType:
				_ = resolved
			}
		}
	}
	return out
}

// resolveMention decides which declaration of a name a file's mention means.
//
// The file's imports are the evidence. A file that imports exactly one of
// the namespaces the name is declared in means that one. A file that
// imports none of them means the mention is a local name (a variable, a
// parameter) and not a type reference at all. A file that imports two of
// them is ambiguous.
//
// A fully qualified mention (`Namespace.Name`) settles the question on its
// own, whatever the imports say.
// The three answers a mention can have.
const (
	// mentionResolved: the mention means one of the declarations.
	mentionResolved = iota
	// mentionAmbiguous: the file imports two of the namespaces the name
	// is declared in, so the mention could be either.
	mentionAmbiguous
	// mentionNotAType: the file imports none of them, so the name is a
	// local variable, a parameter or a member, not a type reference.
	mentionNotAType
)

// resolveMention decides which declaration of a name a file's mention means.
//
// The file's imports are the evidence. A file that imports exactly one of
// the namespaces the name is declared in means that one. A file that
// imports none of them means the mention is a local name (a variable, a
// parameter) and not a type reference at all. A file that imports two of
// them is ambiguous.
//
// A fully qualified mention (`Namespace.Name`) settles the question on its
// own, whatever the imports say.
func resolveMention(source *sourceFile, proj *project, name string, sites []declSite) (string, int) {
	for _, site := range sites {
		if strings.Contains(source.Code, site.Namespace+"."+name) {
			return site.Namespace, mentionResolved
		}
	}
	matched := ""
	for _, site := range sites {
		if !source.Usings[site.Namespace] && !proj.GlobalUsings[site.Namespace] {
			continue
		}
		if matched != "" {
			return "", mentionAmbiguous
		}
		matched = site.Namespace
	}
	if matched == "" {
		return "", mentionNotAType
	}
	return matched, mentionResolved
}
