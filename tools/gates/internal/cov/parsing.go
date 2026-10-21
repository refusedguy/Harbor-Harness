package cov

// parsing.go -- the cobertura reports and the registry.
//
// The two readers are the ones the python original had: the coverage
// engine's XML, and the plain-text registry whose rows name the files that
// are consciously not covered.

import (
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/pycompat"
	"harbor.local/gates/internal/xmltree"
)

// ReadCobertura reads one cobertura file: the coverage per source file, what
// it could not map, and whether the file could be read at all.
//
// A report that cannot be parsed is a measurement that did not happen, and
// the caller has to know: the files it would have covered look uncovered,
// and a gate that called them holes would be reporting a broken report.
func ReadCobertura(path string, projects map[string]string, roots map[string]bool) (
	map[string]*FileCoverage, []string, bool) {
	files := map[string]*FileCoverage{}
	var unmapped []string
	data, err := os.ReadFile(path)
	if err != nil {
		return files, unmapped, false
	}
	root, err := xmltree.Parse(data)
	if err != nil {
		return files, unmapped, false
	}
	suite := filepath.Base(path)
	for _, node := range root.Iter("class") {
		raw := node.Attr("filename")
		relative := NormalizeSourcePath(raw, projects, roots)
		if relative == "" {
			if raw != "" {
				unmapped = append(unmapped, raw)
			}
			continue
		}
		entry, ok := files[relative]
		if !ok {
			entry = &FileCoverage{Path: relative, Lines: map[int]bool{}, Suites: map[string]bool{}}
			files[relative] = entry
		}
		entry.Suites[suite] = true
		linesNode := node.FindChild("lines")
		if linesNode == nil {
			continue
		}
		for _, line := range linesNode.FindChildren("line") {
			number, ok := pycompat.Atoi(line.Attr("number"))
			if !ok {
				// python: int(line.get("number", "0")) — a value that is
				// not a number is not a line, and the line is skipped.
				continue
			}
			hits, ok := parseHits(line.Attr("hits"))
			if !ok {
				hits = 0
			}
			hit := hits > 0
			existing, seen := entry.Lines[number]
			if !seen {
				entry.Lines[number] = hit
			} else {
				entry.Lines[number] = existing || hit
			}
		}
	}
	return files, unmapped, true
}

// parseHits reads the `hits` attribute the way python's int(float(x)) does.
//
// A value that is not a number at all is not a count, and the count is
// zero: the line was not hit.
func parseHits(raw string) (int, bool) {
	value, err := strconv.ParseFloat(raw, 64)
	if err != nil || math.IsInf(value, 0) || math.IsNaN(value) {
		return 0, false
	}
	return int(value), true
}

// LoadCoverage reads every cobertura file in `coverageDir` and merges them.
func LoadCoverage(coverageDir string, sourceRoot string) *Model {
	projects := ProjectDirs(sourceRoot)
	roots := RepoRoots(sourceRoot)
	model := NewModel()
	model.Product = ProductSourceFiles(sourceRoot)
	if info, err := os.Stat(coverageDir); err != nil || !info.IsDir() {
		return model
	}
	entries, err := os.ReadDir(coverageDir)
	if err != nil {
		return model
	}
	var names []string
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		if strings.HasSuffix(entry.Name(), ".cobertura.xml") || strings.HasSuffix(entry.Name(), ".xml") {
			names = append(names, entry.Name())
		}
	}
	sort.Strings(names)
	for _, name := range names {
		full := filepath.Join(coverageDir, name)
		if info, err := os.Stat(full); err != nil || info.IsDir() {
			continue
		}
		suite := strings.TrimSuffix(name, ".cobertura.xml")
		suite = strings.TrimSuffix(suite, ".xml")
		model.Suites = append(model.Suites, suite)
		files, unmapped, readable := ReadCobertura(full, projects, roots)
		if !readable {
			model.Unreadable = append(model.Unreadable, name)
			continue
		}
		for path, entry := range files {
			merged, ok := model.Files[path]
			if !ok {
				merged = &FileCoverage{Path: path, Lines: map[int]bool{}, Suites: map[string]bool{}}
				model.Files[path] = merged
			}
			// The suites overlap on the shared product assemblies, so the
			// merge is a UNION of the line numbers, never a sum: a line two
			// suites hit is one line, and a line one suite hit and another
			// missed is covered.
			for number, hit := range entry.Lines {
				existing, seen := merged.Lines[number]
				if !seen {
					merged.Lines[number] = hit
				} else {
					merged.Lines[number] = existing || hit
				}
			}
			merged.Suites[suite] = true
		}
		model.Unmapped = append(model.Unmapped, unmapped...)
	}
	model.Unmapped = sortedUnique(model.Unmapped)
	// The files the engine reported nothing for, while it reported other
	// files of the same project: they hold no code to cover.
	loaded := model.LoadedProjects()
	for _, path := range model.Product {
		if _, seen := model.Files[path]; seen {
			continue
		}
		if loaded[ProjectOf(path)] {
			model.NotInstrumented = append(model.NotInstrumented, path)
		}
	}
	for _, path := range model.NotInstrumented {
		if CarriesCode(filepath.Join(sourceRoot, path)) {
			model.NotInstrumentedWithCode = append(model.NotInstrumentedWithCode, path)
		}
	}
	return model
}

// sortedUnique is python's `sorted(set(values))`.
func sortedUnique(values []string) []string {
	seen := map[string]bool{}
	out := make([]string, 0, len(values))
	for _, value := range values {
		if seen[value] {
			continue
		}
		seen[value] = true
		out = append(out, value)
	}
	sort.Strings(out)
	return out
}

// ParseRegistry reads the registry. A malformed row is an error, not a
// warning.
func ParseRegistry(path string) ([]RegistryRow, error) {
	var rows []RegistryRow
	if _, err := os.Stat(path); err != nil {
		return rows, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return rows, err
	}
	for index, raw := range pythonLines(string(data)) {
		number := index + 1
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		fields := strings.Split(line, "|")
		for field := range fields {
			fields[field] = strings.TrimSpace(fields[field])
		}
		if len(fields) != 4 {
			return nil, fmt.Errorf(
				"%s:%d: a row needs four fields (path | kind | reason | owner)",
				path, number)
		}
		row := RegistryRow{
			Path:   fields[0],
			Kind:   fields[1],
			Reason: fields[2],
			Owner:  fields[3],
			Line:   number,
		}
		if !knownKind(row.Kind) {
			return nil, fmt.Errorf("%s:%d: unknown kind %s; the kinds are %s",
				path, number, quoteKind(row.Kind), strings.Join(Kinds, ", "))
		}
		if len(row.Reason) < MinReasonLength {
			return nil, fmt.Errorf(
				"%s:%d: the reason is %d characters; it needs at least %d — a URL says where the debt is, not why it is allowed to stay",
				path, number, len(row.Reason), MinReasonLength)
		}
		rows = append(rows, row)
	}
	return rows, nil
}

// pythonLines splits a text into the lines python's file iteration yields.
//
// The difference from strings.Split: python's universal newlines turn
// "\r\n" and a lone "\r" into "\n", and the last line of a file that ends
// with a newline is not an empty line.
func pythonLines(text string) []string {
	text = strings.ReplaceAll(text, "\r\n", "\n")
	text = strings.ReplaceAll(text, "\r", "\n")
	if text == "" {
		return nil
	}
	lines := strings.Split(text, "\n")
	if lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	return lines
}

func knownKind(kind string) bool {
	for _, known := range Kinds {
		if known == kind {
			return true
		}
	}
	return false
}

// quoteKind renders a kind the way python's repr() does.
func quoteKind(kind string) string {
	return "'" + kind + "'"
}
