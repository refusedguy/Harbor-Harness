// Package goquality is the compiled gate over the Go sources of the gates.
//
// # WHY THIS GATE EXISTS
//
// The gates of this repository are Go programs under tools/gates/, and until
// this gate existed NOTHING checked them: no gofmt, no `go vet`, no proof
// that the module still builds, no proof that every gate package carries a
// self-test, and no proof that the registry in tools/gates/main.go still
// names the packages that exist. A gate that is not formatted, does not vet
// and is not registered still compiles, still runs, and still reads as
// finished in review. The tree carried exactly that: one struct literal
// aligned for a field that is not there, in
// tools/gates/check-shard-balance/main.go, and nobody noticed for a day.
//
// WHAT IT CHECKS
//
//	GO-PARSE             every .go file under tools/gates/ parses
//	GO-FORMAT            every .go file under tools/gates/ is gofmt-clean
//	GO-SELFTEST-MISSING  every gate package carries a selftest.go
//	GO-REGISTRY-MISSING  every gate package is named in the registry
//	GO-REGISTRY-STALE    every registry entry names a package that exists
//	GO-REGISTRY-DUPLICATE the registry names no gate twice
//	GO-VET               `go vet ./...` finds nothing
//	GO-BUILD             `go build ./...` succeeds
//
// "EVERY GATE PACKAGE" IS A QUESTION ABOUT THE POLICY TABLE
//
// A package under tools/gates/ is not automatically a gate: the Mode column
// of the table in docs/CI_GATES.md says what it is, and a row that says
// "helper" is a one-shot codemod a human runs, which owes the repository no
// self-test. The gate reads that column, so the policy stays in one place.
//
// # THE TOOLCHAIN, AND WHAT HAPPENS WITHOUT IT
//
// The formatting rule needs NO toolchain: go/format is the same code gofmt
// is built on, so the gate formats the file itself and compares the bytes.
// That is why the rule can live in a binary that runs where there is no Go:
// the question is not "is gofmt installed", it is "is the file in gofmt's
// shape".
//
// The two rules that DO need the toolchain -- vet and build -- say so. If
// `go` is not on PATH, the gate exits 2 (broken: the gate could not run),
// never 0 (green: the gate found nothing). A gate that quietly skips half
// its rules and reports a clean tree is the failure this repository has
// already paid for three times.
package goquality

import (
	"bytes"
	"fmt"
	"go/ast"
	"go/format"
	"go/parser"
	"go/scanner"
	"go/token"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The rule codes. A finding names one of them, and the self-test names the
// same codes, so a reader can tie a message to the rule that produced it.
const (
	ruleParse     = "GO-PARSE"
	ruleFormat    = "GO-FORMAT"
	ruleNoSelf    = "GO-SELFTEST-MISSING"
	ruleNoEntry   = "GO-REGISTRY-MISSING"
	ruleStaleRow  = "GO-REGISTRY-STALE"
	ruleDuplicate = "GO-REGISTRY-DUPLICATE"
	ruleVet       = "GO-VET"
	ruleBuild     = "GO-BUILD"
	ruleFloor     = "GO-FLOOR"
	ruleRead      = "GO-READ"
)

// finding is one defect, and where it is.
type finding struct {
	path string
	line int
	rule string
	msg  string
}

// tree is the module the gate reads.
//
// The paths are the repository-relative ones, because the report is read by
// a person who is looking at the repository and not at the module.
type tree struct {
	root     string
	gatesDir string
}

// package is one gate package: a directory directly under tools/gates/ that
// holds Go sources.
//
// `internal/` is NOT a gate package: it holds the helpers the gates share,
// and a helper has no subcommand of its own.
type packageDir struct {
	name string
	dir  string
}

// Run is the gate entry point.
func Run(args []string) int {
	minFiles := 0
	minPackages := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-files":
			n, ok := numberArg(args, i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = n
			i++
		case "--min-packages":
			n, ok := numberArg(args, i)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-packages needs a number")
				return gate.Broken
			}
			minPackages = n
			i++
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates go-quality [--min-files N] [--min-packages N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}
	t := tree{
		root:     root,
		gatesDir: filepath.Join(root, "tools", "gates"),
	}
	if _, err := os.Stat(t.gatesDir); err != nil {
		fmt.Fprintf(os.Stderr, "cannot read %s: %v\n", relPath(t.root, t.gatesDir), err)
		return gate.Broken
	}

	findings, files, packages, err := checkSources(t)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}

	// The floors are the non-vacuity proof: a scan that quietly narrowed --
	// the module moved, the walk broke, the directory was renamed -- must go
	// red instead of reporting a clean tree.
	findings = append(findings, checkFloors(t, files, packages, minFiles, minPackages)...)

	// The two rules that need the toolchain. A missing toolchain is not a
	// clean tree: it is a gate that could not run, and it says so.
	toolFindings, toolErr := checkToolchain(t)
	findings = append(findings, toolFindings...)

	fmt.Printf("scanned %d Go file(s) in %d gate package(s) under %s\n",
		files, packages, relPath(t.root, t.gatesDir))

	if len(findings) > 0 {
		fmt.Println()
		for _, f := range findings {
			if f.line > 0 {
				fmt.Printf("%s:%d [%s] %s\n", f.path, f.line, f.rule, f.msg)
			} else {
				fmt.Printf("%s [%s] %s\n", f.path, f.rule, f.msg)
			}
		}
	}

	if len(findings) > 0 {
		fmt.Printf("\nFAIL: %d problem(s)\n", len(findings))
		return gate.Red
	}
	if toolErr != nil {
		fmt.Printf("\nBROKEN: %v\n", toolErr)
		fmt.Println("The rules that need the Go toolchain did not run. This is NOT a clean tree.")
		return gate.Broken
	}
	fmt.Println("\nOK: every gate source is gofmt-clean, the module vets and builds, every gate package has a self-test, and the registry names exactly the packages that exist.")
	return gate.Green
}

// numberArg reads the number that follows a flag.
func numberArg(args []string, i int) (int, bool) {
	if i+1 >= len(args) {
		return 0, false
	}
	n, err := strconv.Atoi(args[i+1])
	if err != nil || n < 0 {
		return 0, false
	}
	return n, true
}

// checkSources runs the rules that need nothing but the standard library.
//
// It returns the findings, the number of files it read, and the number of
// gate packages it found. The error is for the case where the registry
// could not be READ: that is a gate that cannot do its job, not a finding.
func checkSources(t tree) ([]finding, int, int, error) {
	var findings []finding

	files, err := collectGoFiles(t.gatesDir)
	if err != nil {
		return nil, 0, 0, err
	}
	for _, file := range files {
		if f, ok := checkFile(t, file); ok {
			findings = append(findings, f)
		}
	}

	packages := gatePackages(t.gatesDir)
	modes, err := tableModes(t.root)
	if err != nil {
		return nil, 0, 0, err
	}
	for _, pkg := range packages {
		mode, documented := modes[pkg.name]
		if !documented {
			// The table does not name this package. That is the policy
			// checker's finding (POLICY-GATE-UNDOCUMENTED), and it is not
			// this gate's question: the table says what a package IS, and
			// until it says, this gate has nothing to ask about it.
			continue
		}
		if needsSelfTest(mode) && !fileExists(filepath.Join(pkg.dir, "selftest.go")) {
			findings = append(findings, finding{
				path: relPath(t.root, pkg.dir),
				rule: ruleNoSelf,
				msg: fmt.Sprintf("the table calls %q a %s, and it has no selftest.go, so nothing proves that it can fail",
					pkg.name, mode),
			})
		}
	}

	registered, duplicated, err := registryNames(t)
	if err != nil {
		return findings, len(files), len(packages), err
	}
	for _, pkg := range packages {
		if !registered[pkg.name] {
			findings = append(findings, finding{
				path: relPath(t.root, pkg.dir),
				rule: ruleNoEntry,
				msg:  "the gate package is not in the registry of tools/gates/main.go, so it has no subcommand",
			})
		}
	}
	for _, name := range sortedKeys(registered) {
		if !hasPackage(packages, name) {
			findings = append(findings, finding{
				path: relPath(t.root, filepath.Join(t.gatesDir, "main.go")),
				rule: ruleStaleRow,
				msg:  fmt.Sprintf("the registry names the gate %q, and there is no tools/gates/%s/", name, name),
			})
		}
	}
	for _, name := range duplicated {
		findings = append(findings, finding{
			path: relPath(t.root, filepath.Join(t.gatesDir, "main.go")),
			rule: ruleDuplicate,
			msg:  fmt.Sprintf("the registry names the gate %q more than once", name),
		})
	}

	return findings, len(files), len(packages), nil
}

// checkFile reads one file and asks whether it parses and whether it is in
// gofmt's shape.
//
// The formatting question is asked of go/format, which is the code gofmt
// itself is built on: `gofmt -l` lists a file when format.Source of its
// bytes differs from those bytes, and this asks exactly that. It is the
// same question without the toolchain.
func checkFile(t tree, path string) (finding, bool) {
	rel := relPath(t.root, path)
	src, err := os.ReadFile(path)
	if err != nil {
		return finding{path: rel, rule: ruleRead, msg: "cannot read: " + err.Error()}, true
	}
	fset := token.NewFileSet()
	if _, err := parser.ParseFile(fset, path, src, parser.ParseComments); err != nil {
		return finding{
			path: rel,
			line: errorLine(err),
			rule: ruleParse,
			msg:  err.Error(),
		}, true
	}
	formatted, err := format.Source(src)
	if err != nil {
		return finding{
			path: rel,
			line: errorLine(err),
			rule: ruleParse,
			msg:  err.Error(),
		}, true
	}
	if bytes.Equal(src, formatted) {
		return finding{}, false
	}
	return finding{
		path: rel,
		line: firstDifferingLine(src, formatted),
		rule: ruleFormat,
		msg:  "the file is not gofmt-clean; run gofmt -w on it",
	}, true
}

// checkFloors is the non-vacuity proof: a scan that quietly narrowed must
// go red instead of reporting a clean tree.
//
// A floor of zero is no floor at all, and the gate says nothing about it:
// the flag is the caller’s decision.
func checkFloors(t tree, files, packages, minFiles, minPackages int) []finding {
	var findings []finding
	if minFiles > 0 && files < minFiles {
		findings = append(findings, finding{
			path: relPath(t.root, t.gatesDir),
			rule: ruleFloor,
			msg:  fmt.Sprintf("scanned %d Go file(s), the floor is %d -- the scan narrowed", files, minFiles),
		})
	}
	if minPackages > 0 && packages < minPackages {
		findings = append(findings, finding{
			path: relPath(t.root, t.gatesDir),
			rule: ruleFloor,
			msg:  fmt.Sprintf("scanned %d gate package(s), the floor is %d -- the scan narrowed", packages, minPackages),
		})
	}
	return findings
}

// checkToolchain runs the two rules that need the Go toolchain.
//
// The toolchain is asked for, not assumed: without it the gate returns the
// error and the caller reports "the gate could not run". The child process
// gets GOTOOLCHAIN=local and GOPROXY=off, so the check never downloads a
// toolchain and never reaches the network: a gate that fetches something at
// run time could not run on a runner with no network.
func checkToolchain(t tree) ([]finding, error) {
	goBin, err := exec.LookPath("go")
	if err != nil {
		return nil, fmt.Errorf("no Go toolchain on PATH, so the vet and build rules did not run: %w", err)
	}

	var findings []finding
	for _, rule := range []struct {
		code string
		args []string
	}{
		{ruleVet, []string{"vet", "./..."}},
		{ruleBuild, []string{"build", "./..."}},
	} {
		out, err := runGo(goBin, t.gatesDir, rule.args...)
		if err == nil {
			continue
		}
		findings = append(findings, parseToolOutput(t, rule.code, out)...)
	}
	return findings, nil
}

// runGo runs the toolchain with the environment that keeps it offline.
func runGo(goBin, dir string, args ...string) (string, error) {
	cmd := exec.Command(goBin, args...)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(),
		"GOTOOLCHAIN=local",
		"GOPROXY=off",
		"GOFLAGS=-mod=readonly",
		"GOWORK=off",
	)
	out, err := cmd.CombinedOutput()
	return string(out), err
}

// parseToolOutput turns the toolchain's diagnostics into findings.
//
// The shape is the compiler's own: a package line, then one line per
// diagnostic, `file:line:column: message`, with the file relative to the
// module directory.
func parseToolOutput(t tree, rule string, out string) []finding {
	var findings []finding
	for _, line := range strings.Split(out, "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" || strings.HasPrefix(trimmed, "#") {
			continue
		}
		path, lineNo, msg, ok := splitDiagnostic(trimmed)
		if !ok {
			continue
		}
		findings = append(findings, finding{
			path: relPath(t.root, filepath.Join(t.gatesDir, path)),
			line: lineNo,
			rule: rule,
			msg:  msg,
		})
	}
	if len(findings) == 0 && strings.TrimSpace(out) != "" {
		findings = append(findings, finding{
			path: relPath(t.root, t.gatesDir),
			rule: rule,
			msg:  firstLine(out),
		})
	}
	return findings
}

// splitDiagnostic reads `file:line:column: message`.
func splitDiagnostic(text string) (string, int, string, bool) {
	rest := text
	var path string
	if index := strings.Index(rest, ":"); index > 0 {
		path = rest[:index]
		rest = rest[index+1:]
	} else {
		return "", 0, "", false
	}
	if !strings.HasSuffix(path, ".go") {
		return "", 0, "", false
	}
	index := strings.Index(rest, ":")
	if index <= 0 {
		return "", 0, "", false
	}
	number, err := strconv.Atoi(rest[:index])
	if err != nil {
		return "", 0, "", false
	}
	rest = rest[index+1:]
	index = strings.Index(rest, ":")
	if index <= 0 {
		return "", 0, "", false
	}
	msg := strings.TrimSpace(rest[index+1:])
	if msg == "" {
		return "", 0, "", false
	}
	return path, number, msg, true
}

// firstLine is the first non-empty line of a toolchain's output.
func firstLine(out string) string {
	for _, line := range strings.Split(out, "\n") {
		if trimmed := strings.TrimSpace(line); trimmed != "" {
			return trimmed
		}
	}
	return "the toolchain failed without saying why"
}

// errorLine is the line a parse error falls on.
func errorLine(err error) int {
	if list, ok := err.(scanner.ErrorList); ok && len(list) > 0 {
		return list[0].Pos.Line
	}
	return 0
}

// firstDifferingLine is the first line where the file and its formatted
// shape disagree, counted the way a file:line citation counts.
func firstDifferingLine(src, formatted []byte) int {
	before := strings.Split(string(src), "\n")
	after := strings.Split(string(formatted), "\n")
	for i := 0; i < len(before) && i < len(after); i++ {
		if before[i] != after[i] {
			return i + 1
		}
	}
	if len(before) != len(after) {
		return min(len(before), len(after)) + 1
	}
	return 1
}

// collectGoFiles lists the Go files of the module.
//
// The FILESYSTEM, not the tracked-file list: a gate package that is new and
// not yet committed is still a gate package, and the registry rule has to
// see it. The directories the Go tool ignores -- the ones whose name starts
// with "." or "_" -- are ignored here too, so the gate never reports a file
// the compiler never reads.
func collectGoFiles(dir string) ([]string, error) {
	var files []string
	err := filepath.WalkDir(dir, func(path string, entry os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		name := entry.Name()
		if entry.IsDir() {
			if path != dir && (strings.HasPrefix(name, ".") || strings.HasPrefix(name, "_")) {
				return filepath.SkipDir
			}
			return nil
		}
		if strings.HasSuffix(name, ".go") {
			files = append(files, path)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Strings(files)
	return files, nil
}

// gatePackages lists the gate packages: the directories directly under
// tools/gates/ that hold Go sources.
func gatePackages(dir string) []packageDir {
	var packages []packageDir
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	for _, entry := range entries {
		if !entry.IsDir() || entry.Name() == "internal" {
			continue
		}
		path := filepath.Join(dir, entry.Name())
		files, err := collectGoFiles(path)
		if err != nil || len(files) == 0 {
			continue
		}
		packages = append(packages, packageDir{name: entry.Name(), dir: path})
	}
	sort.Slice(packages, func(i, j int) bool { return packages[i].name < packages[j].name })
	return packages
}

// needsSelfTest tells whether the mode of a table row demands a self-test.
//
// The table is the policy, and its Mode column says what a package IS. A
// row that says "helper" is a one-shot codemod a human runs, and a helper
// owes the repository nothing. Every other mode -- gate, advisory, control,
// reporter -- is a check, and a check that cannot say no is a check that
// enforces nothing.
func needsSelfTest(mode string) bool {
	switch mode {
	case "helper", "library":
		return false
	}
	return true
}

// tableModes reads the mode docs/CI_GATES.md gives every compiled gate.
//
// The table is markdown, and its header is pinned: the policy checker reads
// the same one. A table that cannot be read is an ERROR, not an empty map:
// a gate that could not find the table must not report that the packages and
// the table agree.
func tableModes(root string) (map[string]string, error) {
	path := filepath.Join(root, "docs", "CI_GATES.md")
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("cannot read docs/CI_GATES.md: %w", err)
	}
	lines := strings.Split(string(data), "\n")

	header := -1
	var headerCells []string
	for i, line := range lines {
		cells := tableCells(line)
		if cells == nil {
			continue
		}
		if indexOfCell(cells, "Implementation") >= 0 && indexOfCell(cells, "Mode") >= 0 {
			header = i
			headerCells = cells
			break
		}
	}
	if header < 0 {
		return nil, fmt.Errorf("docs/CI_GATES.md has no table with an Implementation and a Mode column")
	}
	implementation := indexOfCell(headerCells, "Implementation")
	mode := indexOfCell(headerCells, "Mode")

	const prefix = "compiled: tools/gates/"
	modes := map[string]string{}
	for _, line := range lines[header+1:] {
		cells := tableCells(line)
		if cells == nil {
			break // the table ended
		}
		if implementation >= len(cells) || mode >= len(cells) {
			continue
		}
		source := unquoteCell(cells[implementation])
		if !strings.HasPrefix(source, prefix) {
			continue
		}
		modes[strings.TrimPrefix(source, prefix)] = unquoteCell(cells[mode])
	}
	return modes, nil
}

// tableCells splits a markdown table row, or returns nil for a line that is
// not a table row.
func tableCells(line string) []string {
	text := strings.TrimSpace(line)
	if !strings.HasPrefix(text, "|") {
		return nil
	}
	parts := strings.Split(text, "|")
	if len(parts) < 2 {
		return nil
	}
	parts = parts[1 : len(parts)-1]
	cells := make([]string, 0, len(parts))
	for _, part := range parts {
		cells = append(cells, strings.TrimSpace(part))
	}
	return cells
}

// indexOfCell is the position of a column, or -1.
func indexOfCell(cells []string, name string) int {
	for i, cell := range cells {
		if unquoteCell(cell) == name {
			return i
		}
	}
	return -1
}

// unquoteCell strips the backticks a table cell wraps its code in.
func unquoteCell(cell string) string {
	return strings.ReplaceAll(cell, "`", "")
}

// registryNames reads the gate table out of the sources of the module.
//
// The table is the `registry` variable of the package in the module's root,
// and its entries name the gates. It is read with the PARSER, not with a
// regular expression: the registry is Go code, and the question "which
// gates does this module claim to have" is a question about code.
//
// A registry that cannot be read is an error, not an empty table: a gate
// that could not find the registry must not report that the registry and
// the tree agree.
func registryNames(t tree) (map[string]bool, []string, error) {
	entries, err := os.ReadDir(t.gatesDir)
	if err != nil {
		return nil, nil, fmt.Errorf("cannot read %s: %w", relPath(t.root, t.gatesDir), err)
	}
	names := map[string]bool{}
	counts := map[string]int{}
	found := false
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasSuffix(name, ".go") {
			continue
		}
		fset := token.NewFileSet()
		file, err := parser.ParseFile(fset, filepath.Join(t.gatesDir, name), nil, parser.ParseComments)
		if err != nil {
			return nil, nil, fmt.Errorf("cannot parse %s: %w", relPath(t.root, filepath.Join(t.gatesDir, name)), err)
		}
		for _, decl := range file.Decls {
			gen, ok := decl.(*ast.GenDecl)
			if !ok || gen.Tok != token.VAR {
				continue
			}
			for _, spec := range gen.Specs {
				value, ok := spec.(*ast.ValueSpec)
				if !ok {
					continue
				}
				for _, ident := range value.Names {
					if ident.Name != "registry" {
						continue
					}
					found = true
					for _, entry := range registryEntries(value) {
						names[entry] = true
						counts[entry]++
					}
				}
			}
		}
	}
	if !found {
		return nil, nil, fmt.Errorf("%s has no `registry` variable, so the gate table cannot be read",
			relPath(t.root, t.gatesDir))
	}
	var duplicated []string
	for name, count := range counts {
		if count > 1 {
			duplicated = append(duplicated, name)
		}
	}
	sort.Strings(duplicated)
	return names, duplicated, nil
}

// registryEntries reads the `name` of every entry of the registry.
func registryEntries(value *ast.ValueSpec) []string {
	var names []string
	for _, expr := range value.Values {
		list, ok := expr.(*ast.CompositeLit)
		if !ok {
			continue
		}
		for _, element := range list.Elts {
			entry, ok := element.(*ast.CompositeLit)
			if !ok {
				continue
			}
			for _, field := range entry.Elts {
				pair, ok := field.(*ast.KeyValueExpr)
				if !ok {
					continue
				}
				key, ok := pair.Key.(*ast.Ident)
				if !ok || key.Name != "name" {
					continue
				}
				literal, ok := pair.Value.(*ast.BasicLit)
				if !ok || literal.Kind != token.STRING {
					continue
				}
				name, err := strconv.Unquote(literal.Value)
				if err != nil {
					continue
				}
				names = append(names, name)
			}
		}
	}
	return names
}

// hasPackage tells whether the packages hold one of that name.
func hasPackage(packages []packageDir, name string) bool {
	for _, pkg := range packages {
		if pkg.name == name {
			return true
		}
	}
	return false
}

// sortedKeys returns the keys of a set, in order.
func sortedKeys(set map[string]bool) []string {
	keys := make([]string, 0, len(set))
	for key := range set {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

// fileExists tells whether a path is a file.
func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

// relPath is the repository-relative path of a file, for the report.
func relPath(root, path string) string {
	rel, err := filepath.Rel(root, path)
	if err != nil {
		return path
	}
	return filepath.ToSlash(rel)
}
