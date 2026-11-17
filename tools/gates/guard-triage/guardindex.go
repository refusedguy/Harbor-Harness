package guardtriage

// guardindex.go -- where a rule lives: the guard file and the line of the
// test method.
//
// The index is built from the guard project's .cs files. It is what turns a
// rule name from a stack frame into a file:line citation in the report, and
// it is what the ledger-retirement detector reads to see whether a guard's
// own source lists the path it fired on.

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

type guardIndex struct {
	classLine     map[string]fileLine
	methodLine    map[string]fileLine // key: class + "\x00" + method
	methodLineAny map[string]fileLine
	source        map[string]string
	files         int
}

type fileLine struct {
	path string
	line int
}

var (
	classRE  = regexp.MustCompile(`^\s*(?:public|internal|private|protected)?\s*(?:sealed\s+|static\s+|abstract\s+|partial\s+|file\s+)*class\s+([A-Za-z0-9_]+)`)
	methodRE = regexp.MustCompile(`^\s*(?:public|internal|private|protected)\s+(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|partial\s+)*(?:Task|void|ValueTask|Task<[^>]+>|bool|int|string|IReadOnlyList<[^>]+>|IEnumerable<[^>]+>)\s+([A-Za-z0-9_]+)\s*\(`)
)

// buildGuardIndex walks guardDir for .cs files, the way the python original
// did with sorted(rglob("*.cs")).
func buildGuardIndex(guardDir string, repoRoot string) *guardIndex {
	index := &guardIndex{
		classLine:     map[string]fileLine{},
		methodLine:    map[string]fileLine{},
		methodLineAny: map[string]fileLine{},
		source:        map[string]string{},
	}
	info, err := os.Stat(guardDir)
	if err != nil || !info.IsDir() {
		return index
	}
	var paths []string
	_ = filepath.Walk(guardDir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if !info.IsDir() && strings.HasSuffix(path, ".cs") {
			paths = append(paths, path)
		}
		return nil
	})
	sort.Strings(paths)

	for _, path := range paths {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		text := string(data)
		rel := path
		if repoRoot != "" {
			if candidate, err := filepath.Rel(repoRoot, path); err == nil {
				rel = filepath.ToSlash(candidate)
			}
		}
		index.source[rel] = text
		index.files++

		lines := strings.Split(text, "\n")
		if len(lines) > 0 && lines[len(lines)-1] == "" {
			lines = lines[:len(lines)-1]
		}
		currentClass := ""
		for number, line := range lines {
			line = strings.TrimSuffix(line, "\r")
			trimmed := strings.TrimSpace(line)
			if strings.HasPrefix(trimmed, "//") || strings.HasPrefix(trimmed, "*") || strings.HasPrefix(trimmed, "///") {
				continue
			}
			if m := classRE.FindStringSubmatch(line); m != nil {
				currentClass = m[1]
				if _, exists := index.classLine[currentClass]; !exists {
					index.classLine[currentClass] = fileLine{path: rel, line: number + 1}
				}
				continue
			}
			if m := methodRE.FindStringSubmatch(line); m != nil {
				key := currentClass + "\x00" + m[1]
				if _, exists := index.methodLine[key]; !exists {
					index.methodLine[key] = fileLine{path: rel, line: number + 1}
				}
				if _, exists := index.methodLineAny[m[1]]; !exists {
					index.methodLineAny[m[1]] = fileLine{path: rel, line: number + 1}
				}
			}
		}
	}
	return index
}

func (index *guardIndex) locate(className string, methodName string) (string, int) {
	if found, ok := index.methodLine[className+"\x00"+methodName]; ok {
		return found.path, found.line
	}
	if found, ok := index.methodLineAny[methodName]; ok {
		return found.path, found.line
	}
	if found, ok := index.classLine[className]; ok {
		return found.path, found.line
	}
	return "", 0
}

func (index *guardIndex) fileOfClass(className string) string {
	if found, ok := index.classLine[className]; ok {
		return found.path
	}
	return ""
}
