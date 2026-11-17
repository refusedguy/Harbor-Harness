package guardtriage

// firings.go -- the firings, where they come from and how they are
// classified.

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// firing is one guard firing.
type firing struct {
	rule           string
	testName       string
	project        string
	evidence       string
	source         string // "trx" or "log"
	origin         string
	runID          string
	guardFile      string
	hasGuardFile   bool
	guardLine      int
	hasGuardLine   bool
	classification string
	classifiedBy   string
	reason         string
	ratchet        string
	run            jsonValue
	locations      []location
}

// location is a file:line an assertion message points at.
type location struct {
	path string
	line int
	has  bool
}

// guardIndex is where a rule lives: the guard file, and the line of the
// test method.
type guardIndex struct {
	classLine     map[string]location
	methodLine    map[string]location
	methodLineAny map[string]location
	source        map[string]string
}

// buildGuardIndex reads the guard tree and remembers where each class and
// each method is.
func buildGuardIndex(guardDir string, repoRoot string) *guardIndex {
	index := &guardIndex{
		classLine:     map[string]location{},
		methodLine:    map[string]location{},
		methodLineAny: map[string]location{},
		source:        map[string]string{},
	}
	info, err := os.Stat(guardDir)
	if err != nil || !info.IsDir() {
		return index
	}
	var paths []string
	filepath.WalkDir(guardDir, func(path string, entry os.DirEntry, err error) error {
		if err != nil {
			return nil
		}
		if !entry.IsDir() && strings.HasSuffix(entry.Name(), ".cs") {
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
		if r, err := filepath.Rel(repoRoot, path); err == nil {
			rel = r
		}
		index.source[rel] = text
		lines := strings.Split(text, "\n")
		if len(lines) > 0 && lines[len(lines)-1] == "" {
			lines = lines[:len(lines)-1]
		}
		currentClass := ""
		for number, line := range lines {
			stripped := strings.TrimSpace(line)
			if strings.HasPrefix(stripped, "//") || strings.HasPrefix(stripped, "*") ||
				strings.HasPrefix(stripped, "///") {
				continue
			}
			if m := classRe.FindStringSubmatch(line); m != nil {
				currentClass = m[1]
				if _, ok := index.classLine[currentClass]; !ok {
					index.classLine[currentClass] = location{path: rel, line: number + 1, has: true}
				}
				continue
			}
			if m := methodRe.FindStringSubmatch(line); m != nil {
				key := currentClass + "\x00" + m[1]
				if _, ok := index.methodLine[key]; !ok {
					index.methodLine[key] = location{path: rel, line: number + 1, has: true}
				}
				// A file can declare helper types between the class and
				// its methods (the non-vacuity controls do), so the
				// nearest preceding class is not always the right
				// owner. The first occurrence of the method name in the
				// file is the fallback.
				if _, ok := index.methodLineAny[m[1]]; !ok {
					index.methodLineAny[m[1]] = location{path: rel, line: number + 1, has: true}
				}
			}
		}
	}
	return index
}

// locate returns where the rule lives.
func (index *guardIndex) locate(className string, methodName string) (string, int, bool) {
	if loc, ok := index.methodLine[className+"\x00"+methodName]; ok {
		return loc.path, loc.line, true
	}
	if loc, ok := index.methodLineAny[methodName]; ok {
		return loc.path, loc.line, true
	}
	if loc, ok := index.classLine[className]; ok {
		return loc.path, loc.line, true
	}
	return "", 0, false
}

// fileOfClass returns the file a class is declared in.
func (index *guardIndex) fileOfClass(className string) string {
	loc := index.classLine[className]
	return loc.path
}

// allowanceText returns the content of the allowance files the rule
// declares.
func (reg *registry) allowanceText(repoRoot string, rule string) map[string]string {
	out := map[string]string{}
	entry, ok := reg.rules[rule]
	if !ok {
		return out
	}
	for _, pattern := range entry.allowanceFiles {
		matches, err := filepath.Glob(filepath.Join(repoRoot, pattern))
		if err != nil {
			continue
		}
		sort.Strings(matches)
		for _, path := range matches {
			info, err := os.Stat(path)
			if err != nil || !info.Mode().IsRegular() {
				continue
			}
			data, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			rel, err := filepath.Rel(repoRoot, path)
			if err != nil {
				continue
			}
			out[rel] = string(data)
		}
	}
	return out
}

// extractLocations returns the file:line pairs an assertion message points
// at.
//
// Only the part of the message after a citation marker counts as a
// citation.
func extractLocations(evidence string) []location {
	cited := evidence
	for _, marker := range citedMarkers {
		if index := strings.Index(evidence, marker); index >= 0 {
			cited = evidence[index+len(marker):]
			break
		}
	}
	var found []location
	seen := map[string]bool{}
	for _, m := range pathLine.FindAllStringSubmatch(cited, -1) {
		path := m[1]
		// A path like `Foo.cs:12` is only a repository path when it has
		// a directory part or a known root.
		if !strings.Contains(path, "/") && !strings.HasSuffix(path, ".cs") {
			continue
		}
		key := path + "\x00" + m[2]
		if seen[key] {
			continue
		}
		seen[key] = true
		line := 0
		for _, r := range m[2] {
			line = line*10 + int(r-'0')
		}
		found = append(found, location{path: path, line: line, has: true})
	}
	for _, m := range barePath.FindAllStringSubmatch(cited, -1) {
		path := strings.TrimRight(m[1], ".,;:)")
		key := path + "\x00"
		if seen[key] {
			continue
		}
		seen[key] = true
		found = append(found, location{path: path, has: false})
	}
	return found
}

// ruleFromFrame is the rule a stack frame names.
func ruleFromFrame(ns string, cls string, method string) string {
	if strings.HasSuffix(ns, "Architecture.Tests") || strings.HasSuffix(ns, ".Tests") {
		return cls + "." + method
	}
	return ns + "." + cls + "." + method
}

// splitTestName is the class and the method of a test name.
func splitTestName(testName string, className string) (string, string) {
	if strings.Contains(testName, ".") {
		parts := strings.Split(testName, ".")
		return parts[len(parts)-2], parts[len(parts)-1]
	}
	if className != "" {
		parts := strings.Split(className, ".")
		return parts[len(parts)-1], testName
	}
	return "", testName
}

// projectOf is the assembly the rule lives in: the namespace without the
// class.
func projectOf(className string, clsName string) string {
	if className == "" {
		return ""
	}
	if clsName != "" && strings.HasSuffix(className, "."+clsName) {
		return className[:len(className)-(len(clsName)+1)]
	}
	if strings.Contains(className, ".") {
		return className[:strings.LastIndex(className, ".")]
	}
	return className
}

// classifyFiring fills in the classification, the reason and the ratchet
// of one firing.
func classifyFiring(f *firing, reg *registry, index *guardIndex, allowanceText map[string]string) {
	// 1. A per-firing entry wins over everything else.
	for _, entry := range reg.firings {
		if entry.rule != f.rule {
			continue
		}
		if entry.evidenceContains != "" && !strings.Contains(f.evidence, entry.evidenceContains) {
			continue
		}
		f.classification = entry.class
		if f.classification == "" {
			f.classification = "unknown"
		}
		f.classifiedBy = "registry:firing"
		f.reason = entry.reason
		f.ratchet = entry.ratchet
		return
	}

	// 2. A per-rule entry.
	if entry, ok := reg.rules[f.rule]; ok {
		f.classification = entry.class
		f.classifiedBy = "registry:rule"
		f.reason = entry.reason
		f.ratchet = entry.ratchet
		return
	}

	// 3. The two mechanical detectors.
	guardSource := ""
	if f.hasGuardFile {
		guardSource = index.source[f.guardFile]
	}
	for _, loc := range f.locations {
		if guardSource == "" {
			continue
		}
		if strings.Contains(guardSource, loc.path) && ledgerStale.MatchString(f.evidence) {
			f.classification = "noise"
			f.classifiedBy = "detector:ledger-retirement"
			f.reason = "the guard's own source lists " + loc.path +
				" as allowed code and the assertion says the allowance is stale: " +
				"the rule fired on its own allowance"
			f.ratchet = "ledger-edit"
			return
		}
	}
	for _, loc := range f.locations {
		for _, allowancePath := range sortedKeys(allowanceText) {
			text := allowanceText[allowancePath]
			if strings.Contains(text, loc.path) {
				f.classification = "noise"
				f.classifiedBy = "detector:allowance-listed"
				f.reason = loc.path + " is named in " + allowancePath +
					": the repository allows that code, so the firing carries no new information"
				f.ratchet = "ledger-edit"
				return
			}
		}
	}

	// 4. Unknown: the worklist.
	f.classification = "unknown"
	f.classifiedBy = "none"
	f.reason = "no registry entry and no mechanical signal: needs a human decision"
}

// sortedKeys returns the map's keys, sorted.
func sortedKeys(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// dedupe returns one firing per (run, rule, evidence).
func dedupe(firings []firing) []firing {
	merged := map[string]*firing{}
	var order []string
	for i := range firings {
		head := headOf(firings[i].evidence)
		key := firings[i].runID + "\x00" + firings[i].rule + "\x00" + head
		existing, ok := merged[key]
		if !ok {
			copied := firings[i]
			merged[key] = &copied
			order = append(order, key)
			continue
		}
		existing.origin = existing.origin + ", " + firings[i].origin
		if !existing.hasGuardFile && firings[i].hasGuardFile {
			existing.guardFile = firings[i].guardFile
			existing.hasGuardFile = true
			existing.guardLine = firings[i].guardLine
			existing.hasGuardLine = firings[i].hasGuardLine
		}
	}
	out := make([]firing, 0, len(order))
	for _, key := range order {
		out = append(out, *merged[key])
	}
	return out
}

// headOf is the assertion line, normalized.
func headOf(evidence string) string {
	first := ""
	if evidence != "" {
		lines := splitLines(evidence)
		if len(lines) > 0 {
			first = lines[0]
		}
	}
	return strings.Join(strings.Fields(first), " ")
}

// splitLines splits text into lines the way python's `str.splitlines()`
// does.
func splitLines(text string) []string {
	var out []string
	start := 0
	runes := []rune(text)
	for i := 0; i < len(runes); i++ {
		width := 1
		switch runes[i] {
		case '\r':
			if i+1 < len(runes) && runes[i+1] == '\n' {
				width = 2
			}
		case '\n', '\v', '\f', 0x1c, 0x1d, 0x1e, 0x85, 0x2028, 0x2029:
		default:
			continue
		}
		out = append(out, string(runes[start:i]))
		start = i + width
	}
	if start < len(runes) {
		out = append(out, string(runes[start:]))
	}
	return out
}

// guessRunID is the run number in the name of the artifact.
//
// The python pattern is `(?<!\d)(\d{8,})(?!\d)`: a run of eight or more
// digits, with no digit on either side. The lookarounds make the run
// MAXIMAL, so the number is the first run of eight or more digits in the
// name — which is what the scan below finds.
func guessRunID(origin string) string {
	name := filepath.Base(origin)
	digits := 0
	for i := 0; i < len(name); i++ {
		if name[i] >= '0' && name[i] <= '9' {
			digits++
			continue
		}
		if digits >= 8 {
			return name[i-digits : i]
		}
		digits = 0
	}
	if digits >= 8 {
		return name[len(name)-digits:]
	}
	return ""
}
