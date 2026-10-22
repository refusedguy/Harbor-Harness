package jsonreflection

// scan.go -- the C# reading.
//
// The question the gate asks is about SOURCE TEXT: does a `JsonSerializer`
// call under `tests/` name a source-generated type info, or does it take
// the reflection resolver?
//
// The hard part is telling CODE from TEXT. A `JsonSerializer.Serialize(...)`
// inside a comment is not a call, and a call inside the interpolation hole
// of a raw string IS code -- the hole's content is code, and it may hold a
// nested string literal. So the scan has to know what a string literal is,
// and where its holes are.
//
// The scanner below is the python original's, function for function.

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// The directories the rule skips: the build output, and the arch-guard tree
// (which belongs to programme #1724).
var (
	excludedDirs = []string{"Harbor.Architecture.Tests"}
	skipDirParts = []string{"obj", "bin"}
)

// callRe is a `JsonSerializer` call.
var callRe = regexp.MustCompile(`JsonSerializer\.(Serialize|Deserialize|SerializeToElement|SerializeToUtf8Bytes)\s*\(`)

// A call names a source-generated type info when its arguments mention one.
var sourceGenMarkers = []*regexp.Regexp{
	regexp.MustCompile(`\b[A-Za-z0-9_]*Context\.Default\b`),
	regexp.MustCompile(`\b[A-Za-z0-9_]*Context\.Options\b`),
	regexp.MustCompile(`\b[A-Za-z0-9_]*Context\.JsonOptions\b`),
	regexp.MustCompile(`\bGetTypeInfo\s*\(`),
}

// finding is one `JsonSerializer` call under the scope.
type finding struct {
	path            string
	line            int
	method          string
	args            string
	sourceGenerated bool
	call            string
}

// scan returns every JsonSerializer call under root and whether it is
// source-generated.
func scan(root string, repoRoot string) []finding {
	var findings []finding
	for _, path := range iterSourceFiles(root) {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		raw := string(data)
		for _, match := range callRe.FindAllStringSubmatchIndex(raw, -1) {
			start := match[0]
			if !isCode(raw, start) {
				continue
			}
			args, _ := extractCall(raw, match[1]-1)
			sourceGenerated := false
			for _, marker := range sourceGenMarkers {
				if marker.MatchString(args) {
					sourceGenerated = true
					break
				}
			}
			flattened := strings.Join(strings.Fields(args), " ")
			findings = append(findings, finding{
				path:            relativeTo(repoRoot, path),
				line:            lineOf(raw, start),
				method:          raw[match[2]:match[3]],
				args:            flattened,
				sourceGenerated: sourceGenerated,
				call:            "JsonSerializer." + raw[match[2]:match[3]] + "(" + flattened + ")",
			})
		}
	}
	return findings
}

// iterSourceFiles walks the scope the way python's os.walk does: the
// DIRECTORIES in the order the filesystem returns them, the FILES sorted,
// and the excluded directories dropped IN PLACE, so the recursion does not
// descend into them.
//
// The order matters: the census and the violations are printed in the order
// the scan found them, and a report that reorders itself between two runs of
// the same tree is a report nobody can diff.
func iterSourceFiles(root string) []string {
	var found []string
	var descend func(path string) error
	descend = func(path string) error {
		handle, err := os.Open(path)
		if err != nil {
			return nil
		}
		names, err := handle.Readdirnames(-1)
		handle.Close()
		if err != nil {
			return nil
		}
		var dirs, files []string
		for _, name := range names {
			info, err := os.Stat(filepath.Join(path, name))
			if err != nil {
				continue
			}
			if info.IsDir() {
				dirs = append(dirs, name)
			} else {
				files = append(files, name)
			}
		}
		// The filter is IN PLACE: os.walk recurses into the list the
		// caller filtered, so an excluded directory is never visited.
		kept := dirs[:0]
		for _, name := range dirs {
			if contains(skipDirParts, name) || contains(excludedDirs, name) {
				continue
			}
			kept = append(kept, name)
		}
		dirs = kept

		sorted := append([]string(nil), files...)
		sort.Strings(sorted)
		for _, name := range sorted {
			if strings.HasSuffix(name, ".cs") {
				found = append(found, filepath.Join(path, name))
			}
		}
		for _, name := range dirs {
			if err := descend(filepath.Join(path, name)); err != nil {
				return err
			}
		}
		return nil
	}
	descend(root)
	return found
}

// lineOf is the 1-based line an offset falls on.
func lineOf(src string, index int) int {
	return strings.Count(src[:index], "\n") + 1
}

// relativeTo returns a path relative to the root, with '/' separators.
func relativeTo(root string, path string) string {
	relative, err := filepath.Rel(root, path)
	if err != nil {
		return filepath.ToSlash(path)
	}
	return filepath.ToSlash(relative)
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

// holeSpan returns the index just past an interpolation hole that starts at
// `start`.
//
// The hole's content is CODE: it may hold braces, and it may hold nested
// string literals. The hole closes with `dollars` braces at brace depth 0.
func holeSpan(src string, start int, dollars int) int {
	n := len(src)
	depth := 0
	i := start
	for i < n {
		c := src[i]
		if c == '"' || c == '\'' {
			i = skipLiteral(src, i)
			continue
		}
		if c == '{' {
			depth++
			i++
			continue
		}
		if c == '}' {
			if depth == 0 {
				if dollars == 1 {
					return i + 1
				}
				run := 0
				j := i
				for j < n && src[j] == '}' && run < dollars {
					run++
					j++
				}
				if run == dollars {
					return j
				}
				i = j
				continue
			}
			depth--
			i++
			continue
		}
		i++
	}
	return n
}

// dollarsBefore is how many `$` signs precede the literal that starts at
// `index`.
func dollarsBefore(src string, index int) int {
	dollars := 0
	back := index - 1
	for back >= 0 && src[back] == '$' {
		dollars++
		back--
	}
	return dollars
}

// skipLiteral returns the index just past the string or char literal that
// starts at `i`.
//
// Handles the four shapes C# has: a regular literal, a verbatim literal, a
// raw literal, and a char literal. An interpolated literal is skipped HOLE
// BY HOLE, because the hole's content is code and may hold a nested string.
func skipLiteral(src string, i int) int {
	n := len(src)
	if src[i] == '\'' {
		j := i + 1
		for j < n {
			if src[j] == '\\' {
				j += 2
				continue
			}
			if src[j] == '\'' {
				return j + 1
			}
			j++
		}
		return n
	}
	dollars := dollarsBefore(src, i)
	if strings.HasPrefix(src[i:], `"""`) {
		j := i + 3
		for j < n {
			if strings.HasPrefix(src[j:], `"""`) {
				return j + 3
			}
			if dollars >= 2 && strings.HasPrefix(src[j:], "{{") {
				j = holeSpan(src, j+2, dollars)
				continue
			}
			if dollars == 1 && src[j] == '{' && src[j+1:j+2] != "{" {
				j = holeSpan(src, j+1, 1)
				continue
			}
			j++
		}
		return n
	}
	j := i + 1
	for j < n {
		c := src[j]
		if c == '\\' {
			j += 2
			continue
		}
		if dollars >= 1 && c == '{' {
			if dollars == 1 && src[j+1:j+2] == "{" {
				j += 2
				continue
			}
			j = holeSpan(src, j+1, dollars)
			continue
		}
		if c == '"' {
			return j + 1
		}
		j++
	}
	return n
}

// isCode reports whether the character at `index` is code rather than
// comment or string text.
//
// Walks the source from the start, skipping comments and literals. The
// content of an interpolation hole is CODE, so the walk treats a hole as
// code rather than as text.
func isCode(src string, index int) bool {
	i := 0
	n := len(src)
	for i < n {
		if i == index {
			return true
		}
		c := src[i]
		if c == '/' && src[i+1:i+2] == "/" {
			j := strings.Index(src[i:], "\n")
			if j < 0 {
				j = n
			} else {
				j = i + j
			}
			if j > index {
				return false
			}
			i = j + 1
			continue
		}
		if c == '/' && src[i+1:i+2] == "*" {
			j := strings.Index(src[i+2:], "*/")
			if j < 0 {
				j = n
			} else {
				j = i + 2 + j + 2
			}
			if j > index {
				return false
			}
			i = j
			continue
		}
		if c == '"' || c == '\'' {
			end := skipLiteral(src, i)
			if c == '"' && insideHole(src, i, end, index) {
				return true
			}
			if end > index {
				return false
			}
			i = end
			continue
		}
		i++
	}
	return false
}

// insideHole reports whether `index` is inside an interpolation hole of the
// literal at `start`.
func insideHole(src string, start int, end int, index int) bool {
	dollars := dollarsBefore(src, start)
	if dollars == 0 {
		return false
	}
	i := start
	for i < end {
		if strings.HasPrefix(src[start:], `"""`) {
			if dollars >= 2 && strings.HasPrefix(src[i:], "{{") {
				span := holeSpan(src, i+2, dollars)
				if i+2 <= index && index < span-dollars {
					return true
				}
				i = span
				continue
			}
			if dollars == 1 && src[i] == '{' && src[i+1:i+2] != "{" {
				span := holeSpan(src, i+1, 1)
				if i+1 <= index && index < span-1 {
					return true
				}
				i = span
				continue
			}
			i++
			continue
		}
		if src[i] == '{' {
			span := holeSpan(src, i+1, dollars)
			if i+1 <= index && index < span-dollars {
				return true
			}
			i = span
			continue
		}
		i++
	}
	return false
}

// extractCall returns the text of a call's argument list and the index after
// its ')'.
//
// The argument list may span lines, so the scan works on the whole file
// rather than line by line.
func extractCall(src string, openParenIndex int) (string, int) {
	depth := 1
	i := openParenIndex + 1
	start := i
	n := len(src)
	for i < n {
		c := src[i]
		switch c {
		case '(':
			depth++
		case ')':
			depth--
			if depth == 0 {
				return src[start:i], i + 1
			}
		case '"', '\'':
			i = skipLiteral(src, i)
			continue
		}
		i++
	}
	return src[start:], n
}

// exception is one line of the allow-list.
type exception struct {
	path   string
	needle string
	reason string
	line   int
}

// loadExceptions reads the allow-list.
//
// A line needs three fields: the path, the text that must appear in the
// call, and the reason. A line with a different number of fields is a
// refusal: the file is the one place where a permission is granted, and a
// permission nobody can justify is a silent hole.
func loadExceptions(path string) ([]exception, error) {
	var exceptions []exception
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return exceptions, nil
		}
		return nil, err
	}
	for number, line := range strings.Split(string(data), "\n") {
		text := strings.TrimSpace(line)
		if text == "" || strings.HasPrefix(text, "#") {
			continue
		}
		parts := strings.Split(text, "|")
		for i := range parts {
			parts[i] = strings.TrimSpace(parts[i])
		}
		if len(parts) != 3 {
			return nil, fmt.Errorf(
				"%s:%d: an exception needs three fields (path | text-in-the-call | reason), got %d",
				path, number+1, len(parts))
		}
		exceptions = append(exceptions, exception{
			path:   parts[0],
			needle: parts[1],
			reason: parts[2],
			line:   number + 1,
		})
	}
	return exceptions, nil
}

// matchException returns the exception that covers a finding, or nil.
//
// The FIRST exception in the file whose path is the finding's path and whose
// text is inside the finding's call. The order is the file's order, and the
// python original takes the first match too.
func matchException(found finding, exceptions []exception) *exception {
	for i := range exceptions {
		if exceptions[i].path != found.path {
			continue
		}
		if strings.Contains(found.call, exceptions[i].needle) {
			return &exceptions[i]
		}
	}
	return nil
}
