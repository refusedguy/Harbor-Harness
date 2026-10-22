// Package cov is the model the two coverage gates share: the coverage
// engine's reports, the product tree, and the registry of the files that are
// consciously not covered.
//
// The python original was ONE module (`tools/coverage_report.py`) that was
// both the model and the reporter, and the second gate imported it. The
// port keeps the same split: the model lives here, and each gate is a
// package of its own that uses it.
//
// # WHY A MODEL AND NOT A SCRIPT
//
// Issue #1753 asks for the honest form of a coverage number. The form the
// repository had was a percentage over everything the instrument happened to
// see, and it was wrong twice over: the percentage was over a SUM of suites
// whose per-suite columns OVERLAP on the shared `src/` assemblies, and the
// number was a percentage of the whole tree while the measurement only ever
// touched a part of it. A reader could not tell which of those two numbers
// they were looking at.
//
// The honest form is per FILE, with the scope written down, and a registry of
// the files that are consciously not covered — each row with a reason. That is
// what this package models.
//
// THE THREE THINGS THE MODEL KNOWS
//
//  1. The MEASUREMENT: the cobertura files the nightly lane produced. Every
//     line of every class, with the number of times it was hit.
//
//  2. The PRODUCT: the source files under `src/` and `apps/`. This is the
//     universe the registry talks about. The test tree is not product code
//     and is never in the registry.
//
//  3. The REGISTRY: `tools/coverage-registry.txt`, the rows that say which
//     product files are consciously not covered, and why.
//
// WHAT "COVERED" MEANS HERE
//
// A file is COVERED when at least one of its lines was hit at least once by
// the measurement. A file that is not covered is one of four things, and the
// model keeps them apart because they call for different answers:
//
//   - never-ran      the engine REPORTED the file and not one of its lines
//     was hit. The code never ran. That is the shape a DEAD CODE CANDIDATE
//     has (the finding class of #772 and #755), and the report lists it as
//     one.
//   - not-loaded     the file belongs to a project no suite of this lane
//     loads at all, so the measurement never saw it.
//   - not-instrumented  the engine reported NOTHING for this file, while it
//     did report other files of the same project. The engine instruments whole
//     modules, so a file it says nothing about in a module it did instrument
//     holds no code to cover: an interface, an enum, a global-using file, a
//     source-generated context. There is nothing in it for a test to cover,
//     so it is NOT a hole.
//   - covered        at least one line was hit.
//
// # THE ORACLE IS THE ENGINE, NOT A GUESS
//
// The third case is the one that could have been a heuristic and is not. The
// model does not parse C# to decide whether a file has code in it: it asks
// the coverage engine, which instrumented the module and reported every
// file of it that has anything to instrument. A file it did not report has
// nothing to cover.
//
// The report still CROSS-CHECKS that answer against the source, because a
// silent skip is the failure mode this repository keeps meeting: the files
// the engine did not report are read, and if one of them carries code, the
// report says so. On the tree this registry was measured on, the answer was
// zero such files.
package cov

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// ProductRoots are the roots of the product tree.
//
// A file under any other root is not product code: the test tree is measured
// too (the coverage report says so), but it is never in the registry, because
// a test file that no test runs is a different problem with a different gate.
var ProductRoots = []string{"src", "apps"}

// Kinds are the kinds a registry row may carry.
//
// The kind is not a judgement the gate makes: it is the reason CLASS, so a
// reader can sort the registry and see at a glance what kind of debt the tree
// carries. The two classes are the two shapes a hole has.
var Kinds = []string{"NEVER-RAN", "NOT-MEASURED"}

// MinReasonLength is the shortest reason a registry row may carry.
//
// A reason is prose, not a link: the two existing registries say the same,
// and a URL says where the debt is, not why it is allowed to stay.
const MinReasonLength = 40

// FileCoverage is what the measurement says about one file.
//
// The line numbers are kept, not just the counts: the suites overlap on the
// shared product assemblies, so the merge has to be the UNION of the line
// numbers. A line two suites report is ONE line, and a line one suite hit
// and another missed is covered.
type FileCoverage struct {
	Path   string
	Lines  map[int]bool
	Suites map[string]bool
}

// Total is the number of lines the engine reported.
func (f *FileCoverage) Total() int {
	if f == nil {
		return 0
	}
	return len(f.Lines)
}

// Covered is the number of lines that were hit at least once.
func (f *FileCoverage) Covered() int {
	if f == nil {
		return 0
	}
	count := 0
	for _, hit := range f.Lines {
		if hit {
			count++
		}
	}
	return count
}

// UncoveredLines are the lines the engine reported and nothing hit.
func (f *FileCoverage) UncoveredLines() []int {
	if f == nil {
		return nil
	}
	out := make([]int, 0, len(f.Lines))
	for number, hit := range f.Lines {
		if !hit {
			out = append(out, number)
		}
	}
	sort.Ints(out)
	return out
}

// Rate is the share of the reported lines that were hit.
func (f *FileCoverage) Rate() float64 {
	if f == nil || f.Total() == 0 {
		return 1.0
	}
	return float64(f.Covered()) / float64(f.Total())
}

// RegistryRow is one row of `tools/coverage-registry.txt`.
type RegistryRow struct {
	Path   string
	Kind   string
	Reason string
	Owner  string
	Line   int
}

// IsDirectory reports whether the row names a directory.
func (r RegistryRow) IsDirectory() bool {
	return strings.HasSuffix(r.Path, "/")
}

// Model is the measurement, the product tree and the registry, together.
type Model struct {
	Files    map[string]*FileCoverage
	Product  []string
	Registry []RegistryRow
	Suites   []string
	Unmapped []string
	// Unreadable are the reports that could not be read at all. A suite
	// whose report is unreadable produced no measurement, and the files it
	// would have covered look uncovered — so the measurement is partial.
	Unreadable []string
	// NotInstrumented are the files the engine reported nothing for, while
	// it reported other files of the same project. The engine instruments
	// whole modules, so these hold no code to cover.
	NotInstrumented []string
	// NotInstrumentedWithCode are the files the engine did not report that
	// nevertheless carry code. The cross-check of the engine's answer;
	// empty on the measured tree.
	NotInstrumentedWithCode []string

	loadedProjects map[string]bool
	loadedResolved bool
}

// NewModel is an empty model.
func NewModel() *Model {
	return &Model{Files: map[string]*FileCoverage{}}
}

// ProductFiles are the product source files, repo-relative.
func (m *Model) ProductFiles() []string {
	return append([]string{}, m.Product...)
}

// CoveredProductFiles are the product files the measurement covered.
func (m *Model) CoveredProductFiles() []string {
	var out []string
	for _, path := range m.Product {
		if entry, ok := m.Files[path]; ok && entry.Covered() > 0 {
			out = append(out, path)
		}
	}
	return out
}

// LoadedProjects are the projects the measurement loaded at all.
//
// A project is loaded when the engine reported at least one of its files:
// the engine instrumented the module and it was in the test process.
func (m *Model) LoadedProjects() map[string]bool {
	if !m.loadedResolved {
		m.loadedProjects = map[string]bool{}
		for path := range m.Files {
			m.loadedProjects[ProjectOf(path)] = true
		}
		m.loadedResolved = true
	}
	return m.loadedProjects
}

// Holes are the product files this measurement cannot account for.
//
// Two shapes, and only two:
//   - `never-ran`   the engine saw the code and ran none of it;
//   - `not-loaded`  the project is not in this lane at all.
//
// The files the engine reported nothing for are NOT holes: there is no code
// in them to cover.
func (m *Model) Holes() []string {
	var out []string
	for _, path := range m.Product {
		switch m.StatusOf(path) {
		case "never-ran", "not-loaded":
			out = append(out, path)
		}
	}
	return out
}

// IsCovered reports whether the measurement covered the file.
func (m *Model) IsCovered(path string) bool {
	entry, ok := m.Files[path]
	return ok && entry.Covered() > 0
}

// StatusOf is the state of a product file: covered, never-ran,
// not-instrumented or not-loaded.
func (m *Model) StatusOf(path string) string {
	if entry, ok := m.Files[path]; ok {
		if entry.Covered() > 0 {
			return "covered"
		}
		return "never-ran"
	}
	if m.LoadedProjects()[ProjectOf(path)] {
		return "not-instrumented"
	}
	return "not-loaded"
}

// RowFor is the row that covers `path`, if any.
//
// A row names a file or a directory (a path that ends in `/`). A directory
// row covers every product file under it. The most specific row wins, so a
// file row can override a directory row.
func (m *Model) RowFor(path string) *RegistryRow {
	var best *RegistryRow
	for index := range m.Registry {
		row := m.Registry[index]
		if row.IsDirectory() {
			if strings.HasPrefix(path, row.Path) &&
				(best == nil || len(row.Path) > len(best.Path)) {
				best = &m.Registry[index]
			}
		} else if row.Path == path {
			best = &m.Registry[index]
		}
	}
	return best
}

// AcceptedFiles are the holes the registry names.
func (m *Model) AcceptedFiles() []string {
	var out []string
	for _, path := range m.Holes() {
		if m.RowFor(path) != nil {
			out = append(out, path)
		}
	}
	return out
}

// UnacceptedFiles are the holes no registry row names.
func (m *Model) UnacceptedFiles() []string {
	var out []string
	for _, path := range m.Holes() {
		if m.RowFor(path) == nil {
			out = append(out, path)
		}
	}
	return out
}

// ProjectOf is the project a product file belongs to: the directory under
// the root.
func ProjectOf(path string) string {
	parts := strings.Split(path, "/")
	if len(parts) >= 2 && isProductRoot(parts[0]) {
		return parts[0] + "/" + parts[1]
	}
	if len(parts) > 0 {
		return parts[0]
	}
	return "?"
}

func isProductRoot(name string) bool {
	for _, root := range ProductRoots {
		if root == name {
			return true
		}
	}
	return false
}

// Tokens that say a file carries code. Used only as a CROSS-CHECK of the
// engine's answer, never as the answer itself: a file the engine did not
// report but that carries one of these is printed in the report, so a silent
// skip cannot hide.
//
// The tokens are the ones a BODY has. A method DECLARATION has none of
// them: `public Task PublishAsync(AgentEvent e);` is an interface member,
// not code, and reading it as code would put every interface in the tree
// into the cross-check.
var codeTokens = regexp.MustCompile(`=>|\breturn\b|\bawait\b|\basync\b|\bnew\s|\bif\s*\(|\bforeach\b` +
	`|\bwhile\b|\bthrow\b|\bvar\s|\byield\b|\btry\b|\bswitch\b|\bcase\b`)

// A `{` that opens a BODY. A record's parameter list is followed by a `{`
// too, so the line the `)` sits on is read: a type declaration there is
// not a body.
var bodyOpenRe = regexp.MustCompile(`\)\s*\{`)

var typeDeclRe = regexp.MustCompile(`\b(class|struct|record|interface|enum)\b`)

var commentRe = regexp.MustCompile(`//[^\n]*`)

var blockCommentRe = regexp.MustCompile(`(?s)/\*.*?\*/`)

var stringRe = regexp.MustCompile(`"(?:\\.|[^"\\])*"`)

// StripSource is the source without its comments and its string literals.
func StripSource(text string) string {
	text = blockCommentRe.ReplaceAllString(text, " ")
	text = commentRe.ReplaceAllString(text, " ")
	return stringRe.ReplaceAllString(text, `""`)
}

// CarriesCode reports whether the file at `path` looks like it carries code.
//
// The cross-check, not the oracle: the engine's answer is the answer.
func CarriesCode(path string) bool {
	data, err := os.ReadFile(path)
	if err != nil {
		return false
	}
	source := StripSource(string(data))
	if codeTokens.MatchString(source) {
		return true
	}
	for _, match := range bodyOpenRe.FindAllStringIndex(source, -1) {
		start := strings.LastIndex(source[:match[0]], "\n") + 1
		line := source[start:match[0]]
		if !typeDeclRe.MatchString(line) {
			return true
		}
	}
	return false
}

// ProjectDirs maps a PROJECT NAME to its repo-relative directory.
//
// The cobertura report names a file by the path the compiler saw, which for
// a project-local compile is relative to the project directory ("Foo.cs").
// This map is what turns that into "src/Harbor.Foo/Foo.cs".
func ProjectDirs(sourceRoot string) map[string]string {
	mapping := map[string]string{}
	for _, root := range ProductRoots {
		base := filepath.Join(sourceRoot, root)
		entries, err := os.ReadDir(base)
		if err != nil {
			continue
		}
		names := make([]string, 0, len(entries))
		for _, entry := range entries {
			if isDir(filepath.Join(base, entry.Name())) {
				names = append(names, entry.Name())
			}
		}
		sort.Strings(names)
		for _, name := range names {
			mapping[name] = root + "/" + name
		}
	}
	return mapping
}

// ProductSourceFiles are every product source file, repo-relative.
//
// `.cs` only: the language of the product. The `bin/` and `obj/`
// directories are skipped — they are build output, and a file in them is
// not a file of the tree.
func ProductSourceFiles(sourceRoot string) []string {
	var found []string
	for _, root := range ProductRoots {
		base := filepath.Join(sourceRoot, root)
		if info, err := os.Stat(base); err != nil || !info.IsDir() {
			continue
		}
		_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if info.IsDir() {
				name := info.Name()
				if name == "bin" || name == "obj" {
					return filepath.SkipDir
				}
				return nil
			}
			if !strings.HasSuffix(info.Name(), ".cs") {
				return nil
			}
			relative, err := filepath.Rel(sourceRoot, path)
			if err != nil {
				return nil
			}
			found = append(found, filepath.ToSlash(relative))
			return nil
		})
	}
	sort.Strings(found)
	return found
}

// RepoRoots are the top-level directories of the repository.
//
// The cobertura report names a file by the path the COMPILER saw, and that
// path is absolute on a CI runner. The repository's own top-level
// directories are the anchor: the first segment that is one of them starts
// the repo-relative path.
func RepoRoots(sourceRoot string) map[string]bool {
	roots := map[string]bool{}
	entries, err := os.ReadDir(sourceRoot)
	if err != nil {
		for _, root := range ProductRoots {
			roots[root] = true
		}
		return roots
	}
	for _, entry := range entries {
		if isDir(filepath.Join(sourceRoot, entry.Name())) {
			roots[entry.Name()] = true
		}
	}
	return roots
}

// isDir reports whether the path is a directory, following a symlink.
//
// python's os.path.isdir follows one, and the gate's answer about the
// product tree must not depend on how the tree was checked out.
func isDir(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

// NormalizeSourcePath turns a path from a cobertura report into a
// repo-relative path.
//
// The report carries the path the COMPILER saw, and that path depends on how
// the build was invoked. Four shapes occur, and all four are handled:
//
//   - the deterministic-source-root form the CI build produces
//     (`/_/src/Harbor.Foo/Bar.cs` — `ContinuousIntegrationBuild=true`
//     maps the build machine's paths onto `/_/`);
//   - an absolute path (`/home/runner/work/.../src/Harbor.Foo/Bar.cs`);
//   - a path relative to the repository (`src/Harbor.Foo/Bar.cs`);
//   - a path relative to the PROJECT directory (`Bar.cs`), which only the
//     project-name map can resolve.
//
// A path that resolves to nothing is returned empty, and the caller records
// it as unmapped rather than dropping it silently.
func NormalizeSourcePath(raw string, projects map[string]string, roots map[string]bool) string {
	if raw == "" {
		return ""
	}
	text := strings.ReplaceAll(raw, "\\", "/")
	text = strings.TrimSpace(text)
	if strings.HasPrefix(text, "file://") {
		text = text[len("file://"):]
	}
	var parts []string
	for _, part := range strings.Split(text, "/") {
		if part != "" && part != "." {
			parts = append(parts, part)
		}
	}
	if len(parts) == 0 {
		return ""
	}
	// A top-level directory of the repository starts the relative path.
	for index, part := range parts {
		if roots[part] {
			return strings.Join(parts[index:], "/")
		}
	}
	// The product root wins for a path that names no top-level directory:
	// the first `src` or `apps` segment.
	for index, part := range parts {
		if isProductRoot(part) {
			return strings.Join(parts[index:], "/")
		}
	}
	// The project directory form: "Bar.cs" or "Sub/Bar.cs" inside a project.
	if _, ok := projects[parts[0]]; ok {
		return projects[parts[0]] + "/" + strings.Join(parts[1:], "/")
	}
	if len(parts) > 1 {
		if directory, ok := projects[parts[len(parts)-2]]; ok {
			return directory + "/" + parts[len(parts)-1]
		}
	}
	return ""
}
