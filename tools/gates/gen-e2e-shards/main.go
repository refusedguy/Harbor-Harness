// Package gene2eshards is the compiled port of tools/gen-e2e-shards.py.
//
// It generates the process plan of the headless Avalonia E2E suite: the
// class universe comes from the source tree, the costs from a measured TRX,
// and the split is a balanced partition of the measured durations.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package gene2eshards

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
	"harbor.local/gates/internal/xmltree"
)

// trxNamespace is the TRX schema's namespace.
//
// TRX is a fixed-namespace schema; strip it once and match on bare tags.
const trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

// projectDirs are the two project directories whose test classes this
// assembly discovers: its own, and the referenced test project whose classes
// TUnit runs here too.
var projectDirs = []string{"Harbor.E2E.App.Avalonia", "Harbor.E2E.Framework"}

// A top-level type declaration. Column 0 only - a nested type is not a
// suite and TUnit does not schedule one. Mirrors the matcher the
// process-environment arch rule uses, so the two readers agree on what a
// "class" is.
var typeDeclaration = regexp.MustCompile(
	`^(?:\[[^\]]*\]\s*|public\s+|internal\s+|private\s+|protected\s+|sealed\s+|abstract\s+` +
		`|static\s+|partial\s+|file\s+|unsafe\s+)*(?:class|record|struct)\s+(?P<name>[A-Za-z_]\w*)`)

// A `[Test]` attribute at the start of a line, ignoring what follows the
// name so `[Test]`, `[Test(...)]` and `[Test, NotInParallel("k")]` all match
// while `[TestKitSomething]` does not.
var testAttribute = regexp.MustCompile(`^\s*\[Test(\]|\(|\s|,)`)

// Build output is not source. A stale copy under bin/ would be counted as a
// second class of the same name.
var excludedDirs = []string{"/bin/", "/obj/", "/.worktrees/"}

// Run is the generator's entry point.
func Run(args []string) int {
	var trxFiles []string
	shards := 2
	var dedicated []string
	measuredRun := ""
	measuredArtifact := "test-results-e2e"
	out := ""
	printUniverse := false
	selfTest := false

	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--trx":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --trx needs a path")
				return gate.Broken
			}
			trxFiles = append(trxFiles, args[index])
		case "--shards":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --shards needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --shards needs a number: %s\n", args[index])
				return gate.Broken
			}
			shards = value
		case "--dedicated":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --dedicated needs a class name")
				return gate.Broken
			}
			dedicated = append(dedicated, args[index])
		case "--measured-run":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --measured-run needs a run id")
				return gate.Broken
			}
			measuredRun = args[index]
		case "--measured-artifact":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --measured-artifact needs a name")
				return gate.Broken
			}
			measuredArtifact = args[index]
		case "--out":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --out needs a path")
				return gate.Broken
			}
			out = args[index]
		case "--print-universe":
			printUniverse = true
		case "--self-test":
			selfTest = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := repoRoot(".")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	universe := classUniverse(root)

	if printUniverse {
		for _, name := range sortedKeys(universe) {
			fmt.Printf("%s\t%s\n", name, universe[name])
		}
		fmt.Fprintf(os.Stderr, "# %d test classes\n", len(universe))
		return gate.Green
	}

	if len(trxFiles) == 0 {
		fmt.Fprintln(os.Stderr, "gen-e2e-shards.py: --trx is required")
		return gate.Broken
	}
	if shards < 1 {
		fmt.Fprintln(os.Stderr, "gen-e2e-shards.py: --shards must be >= 1")
		return gate.Broken
	}
	if len(dedicated) > shards {
		fmt.Fprintln(os.Stderr, "gen-e2e-shards.py: --dedicated names more classes than there are shards")
		return gate.Broken
	}

	outPath := out
	if outPath == "" {
		outPath = filepath.Join(root, "tests", "Harbor.E2E.App.Avalonia", "e2e-shards.json")
	}
	plan, err := buildPlan(root, trxFiles, shards, dedicated, measuredRun, measuredArtifact, outPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}

	maximum := 0.0
	for _, entry := range plan.Shards {
		if shardSeconds(entry) > maximum {
			maximum = shardSeconds(entry)
		}
	}
	fmt.Printf("wrote %s: %d shard(s), %d test(s), max shard %.2fs\n",
		outPath, shards, plan.Totals.MeasuredTests, maximum)
	return gate.Green
}

// buildPlan is the plan the generator writes.
//
// The measurements, the universe, the split and the plan itself are one
// function, so the self-test exercises the same code the tool runs.
func buildPlan(root string, trxFiles []string, shards int, dedicated []string,
	measuredRun string, measuredArtifact string, outPath string) (planDocument, error) {
	universe := classUniverse(root)

	measured := map[string]measurement{}
	for _, path := range trxFiles {
		read, err := readTrx(path)
		if err != nil {
			return planDocument{}, err
		}
		for name, value := range read {
			entry, ok := measured[name]
			if !ok {
				entry = measurement{}
			}
			entry.Seconds += value.Seconds
			entry.Count += value.Count
			measured[name] = entry
		}
	}

	// The universe is the source tree's; the TRX only supplies the costs. A
	// class the measurement predates still gets a shard (with a zero cost),
	// so the generated plan is never missing a class the tree has.
	seconds := map[string]float64{}
	counts := map[string]int{}
	for name := range universe {
		seconds[name] = measured[name].Seconds
		counts[name] = measured[name].Count
	}
	var unknown []string
	for name := range measured {
		if _, ok := universe[name]; !ok {
			unknown = append(unknown, name)
		}
	}
	sort.Strings(unknown)
	if len(unknown) > 0 {
		fmt.Fprintf(os.Stderr, "gen-e2e-shards.py: warning: TRX names classes the tree does not have: %s\n",
			strings.Join(unknown, ", "))
	}
	// A dedicated class runs in a process of its own.
	var dedicatedClasses []string
	for _, name := range dedicated {
		if _, ok := universe[name]; ok {
			dedicatedClasses = append(dedicatedClasses, name)
		}
	}
	for _, name := range dedicated {
		if _, ok := universe[name]; !ok {
			fmt.Fprintf(os.Stderr, "gen-e2e-shards.py: warning: --dedicated %s is not a test class of this assembly\n", name)
		}
	}

	pooled := map[string]float64{}
	for name := range universe {
		isDedicated := false
		for _, dedicatedName := range dedicatedClasses {
			if dedicatedName == name {
				isDedicated = true
				break
			}
		}
		if !isDedicated {
			pooled[name] = seconds[name]
		}
	}
	pooledShards := shards - len(dedicatedClasses)
	var bins [][]string
	if pooledShards < 1 {
		for _, name := range dedicatedClasses {
			bins = append(bins, []string{name})
		}
	} else {
		bins = balance(pooled, pooledShards)
		dedicatedBins := make([][]string, 0, len(dedicatedClasses))
		for _, name := range dedicatedClasses {
			dedicatedBins = append(dedicatedBins, []string{name})
		}
		bins = append(dedicatedBins, bins...)
	}

	if outPath == "" {
		outPath = filepath.Join(root, "tests", "Harbor.E2E.App.Avalonia", "e2e-shards.json")
	}

	var shardEntries []shardEntry
	for _, names := range bins {
		sortedNames := append([]string{}, names...)
		sort.Strings(sortedNames)
		total := 0.0
		for _, name := range sortedNames {
			total += seconds[name]
		}
		testCount := 0
		for _, name := range sortedNames {
			testCount += counts[name]
		}
		shardEntries = append(shardEntries, shardEntry{
			ID:              fmt.Sprintf("shard-%d", len(shardEntries)),
			Classes:         sortedNames,
			Dedicated:       len(sortedNames) == 1 && contains(dedicatedClasses, sortedNames[0]),
			MeasuredSeconds: pycompat.FloatJSON(pycompat.Round(total, 2)),
			MeasuredTests:   testCount,
		})
	}

	// Heaviest shard first: the report reads in the order the wall is spent,
	// and the slowest shard is the one that decides it.
	sort.SliceStable(shardEntries, func(i, j int) bool {
		left := shardSeconds(shardEntries[i])
		right := shardSeconds(shardEntries[j])
		if left != right {
			return left > right
		}
		return shardEntries[i].ID < shardEntries[j].ID
	})
	for index := range shardEntries {
		shardEntries[index].ID = fmt.Sprintf("shard-%d", index)
	}

	measuredTotal := 0.0
	for _, value := range seconds {
		measuredTotal += value
	}
	measuredTests := 0
	for _, value := range counts {
		measuredTests += value
	}
	sortedDedicated := append([]string{}, dedicatedClasses...)
	sort.Strings(sortedDedicated)

	plan := planDocument{
		SchemaVersion: 1,
		Project:       "Harbor.E2E.App.Avalonia",
		Description: "One process per shard. The Avalonia Application, $HOME and the pinned " +
			"Harbor home are process-wide, so the classes cannot share a process. " +
			"Generated by tools/gen-e2e-shards.py - do not hand-edit.",
		Generated: generatedBlock{
			Tool: "tools/gen-e2e-shards.py",
			ClassUniverseFrom: "the source tree (tests/Harbor.E2E.App.Avalonia + " +
				"tests/Harbor.E2E.Framework)",
			CostsFrom:            "the TRX durations of the CI run named below",
			MeasuredFromCiRun:    measuredRun,
			MeasuredFromArtifact: measuredArtifact,
			MeasuredAtUtc:        time.Now().UTC().Format("2006-01-02T15:04:05Z"),
			TrxFiles:             baseNames(trxFiles),
			// Classes the measurement names that the tree no longer has: the
			// measurement is older than the tree. They cost nothing here (a
			// class that moved to a project of its own is not discovered here
			// any more), but the plan says so rather than leaving a reader to
			// compare the counts.
			ClassesInTheMeasurementButNotInTheTree: emptyIfNil(unknown),
		},
		Totals: totalsBlock{
			Shards:              shards,
			DedicatedClasses:    emptyIfNil(sortedDedicated),
			MeasuredTests:       measuredTests,
			MeasuredTestSeconds: pycompat.FloatJSON(pycompat.Round(measuredTotal, 2)),
		},
		Shards: shardEntries,
	}

	data, err := pycompat.JSONDumpASCII(plan, "  ")
	if err != nil {
		return planDocument{}, err
	}
	data = append(data, '\n')
	if err := os.WriteFile(outPath, data, 0o644); err != nil {
		return planDocument{}, err
	}
	return plan, nil
}

// shardEntry is one shard of the plan.
type shardEntry struct {
	ID              string          `json:"id"`
	Classes         []string        `json:"classes"`
	Dedicated       bool            `json:"dedicated"`
	MeasuredSeconds json.RawMessage `json:"measuredSeconds"`
	MeasuredTests   int             `json:"measuredTests"`
}

// generatedBlock is the provenance of the plan.
type generatedBlock struct {
	Tool                                   string   `json:"tool"`
	ClassUniverseFrom                      string   `json:"classUniverseFrom"`
	CostsFrom                              string   `json:"costsFrom"`
	MeasuredFromCiRun                      string   `json:"measuredFromCiRun"`
	MeasuredFromArtifact                   string   `json:"measuredFromArtifact"`
	MeasuredAtUtc                          string   `json:"measuredAtUtc"`
	TrxFiles                               []string `json:"trxFiles"`
	ClassesInTheMeasurementButNotInTheTree []string `json:"classesInTheMeasurementButNotInTheTree"`
}

// totalsBlock is the plan's totals.
type totalsBlock struct {
	Shards              int             `json:"shards"`
	DedicatedClasses    []string        `json:"dedicatedClasses"`
	MeasuredTests       int             `json:"measuredTests"`
	MeasuredTestSeconds json.RawMessage `json:"measuredTestSeconds"`
}

// planDocument is the whole plan.
type planDocument struct {
	SchemaVersion int            `json:"schemaVersion"`
	Project       string         `json:"project"`
	Description   string         `json:"description"`
	Generated     generatedBlock `json:"generated"`
	Totals        totalsBlock    `json:"totals"`
	Shards        []shardEntry   `json:"shards"`
}

// shardSeconds is the measured seconds of a shard entry.
func shardSeconds(entry shardEntry) float64 {
	var value float64
	fmt.Sscanf(string(entry.MeasuredSeconds), "%g", &value)
	return value
}

// baseNames are the file names of the paths.
func baseNames(paths []string) []string {
	out := make([]string, 0, len(paths))
	for _, path := range paths {
		out = append(out, filepath.Base(path))
	}
	return out
}

// emptyIfNil is python's `[]` for an empty list.
func emptyIfNil(values []string) []string {
	if values == nil {
		return []string{}
	}
	return values
}

// contains reports whether the list holds the value.
func contains(values []string, value string) bool {
	for _, item := range values {
		if item == value {
			return true
		}
	}
	return false
}

// sortedKeys are the keys of a map, in sorted order.
func sortedKeys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// repoRoot walks up from `start` until a `Harbor.slnx` marks the repository
// root.
func repoRoot(start string) (string, error) {
	directory, err := filepath.Abs(start)
	if err != nil {
		return "", err
	}
	for {
		if info, err := os.Stat(filepath.Join(directory, "Harbor.slnx")); err == nil && !info.IsDir() {
			return directory, nil
		}
		parent := filepath.Dir(directory)
		if parent == directory {
			return "", fmt.Errorf("no Harbor.slnx above %s", start)
		}
		directory = parent
	}
}

// sourceFiles is every .cs file under the two project directories, sorted.
func sourceFiles(root string) []string {
	var found []string
	for _, project := range projectDirs {
		base := filepath.Join(root, "tests", project)
		if info, err := os.Stat(base); err != nil || !info.IsDir() {
			continue
		}
		filepath.Walk(base, func(current string, info os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			normalised := filepath.ToSlash(current)
			if info.IsDir() {
				name := info.Name()
				if contains(excludedDirs, "/"+name+"/") {
					return filepath.SkipDir
				}
				return nil
			}
			for _, marker := range excludedDirs {
				if strings.Contains(normalised+"/", marker) {
					return nil
				}
			}
			if strings.HasSuffix(info.Name(), ".cs") {
				found = append(found, current)
			}
			return nil
		})
	}
	sort.Strings(found)
	return found
}

// Comment stripping, mirroring `SourceScan.StripComments` in
// tests/Harbor.Architecture.Tests exactly: block comments first, then line
// comments, both replaced by spaces so the line count is preserved. The two
// readers must agree on what a "test class" is - the arch rule grades the
// plan this file writes, and a rule that disagreed with its own generator
// would report a class that does not exist.
var (
	blockComment = regexp.MustCompile(`(?s)/\*.*?\*/`)
	lineComment  = regexp.MustCompile(`//[^\n]*`)
)

// stripComments removes // and /* */ comments, preserving the line count.
//
// A block comment is blanked character by character (newlines kept), exactly
// as `SourceScan.StripComments` does in the architecture tests: the line
// numbers the class scan indexes into must still point at the real lines.
func stripComments(text string) string {
	return lineComment.ReplaceAllString(blockComment.ReplaceAllStringFunc(text, blank), " ")
}

// blank is the block comment's own shape, with every non-newline character
// blanked.
func blank(match string) string {
	return strings.Map(func(r rune) rune {
		if r == '\n' {
			return '\n'
		}
		return ' '
	}, match)
}

// classUniverse maps class name -> declaring file, for every test class in
// the tree.
//
// A type is a test class when ITS OWN extent declares a `[Test]`: the extent
// runs from the type's declaration to the next top-level declaration, so a
// helper class in the same file does not inherit its neighbour's tests and a
// file with two suites yields two classes.
func classUniverse(root string) map[string]string {
	universe := map[string]string{}
	for _, path := range sourceFiles(root) {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		code := strings.Split(stripComments(string(data)), "\n")
		type declaration struct {
			index int
			name  string
		}
		var declarations []declaration
		for index, line := range code {
			match := typeDeclaration.FindStringSubmatch(line)
			if match == nil {
				continue
			}
			declarations = append(declarations, declaration{index: index, name: match[typeDeclaration.SubexpIndex("name")]})
		}
		relative, err := filepath.Rel(root, path)
		if err != nil {
			continue
		}
		relative = filepath.ToSlash(relative)
		for position, decl := range declarations {
			end := len(code)
			if position+1 < len(declarations) {
				end = declarations[position+1].index
			}
			found := false
			for line := decl.index; line < end; line++ {
				if testAttribute.MatchString(code[line]) {
					found = true
					break
				}
			}
			if found {
				if _, seen := universe[decl.name]; !seen {
					universe[decl.name] = relative
				}
			}
		}
	}
	return universe
}

// measurement is what a TRX says about one class.
type measurement struct {
	Seconds float64
	Count   int
}

// trxDuration reads a TRX duration: `hh:mm:ss.fffffff`.
func trxDuration(value string) float64 {
	if value == "" {
		return 0
	}
	parts := strings.Split(value, ":")
	switch len(parts) {
	case 3:
		hours, ok := pycompat.Atoi(parts[0])
		if !ok {
			return 0
		}
		minutes, ok := pycompat.Atoi(parts[1])
		if !ok {
			return 0
		}
		seconds, err := parseFloat(parts[2])
		if err != nil {
			return 0
		}
		return float64(hours)*3600 + float64(minutes)*60 + seconds
	case 2:
		minutes, ok := pycompat.Atoi(parts[0])
		if !ok {
			return 0
		}
		seconds, err := parseFloat(parts[1])
		if err != nil {
			return 0
		}
		return float64(minutes)*60 + seconds
	default:
		seconds, err := parseFloat(parts[0])
		if err != nil {
			return 0
		}
		return seconds
	}
}

// readTrx maps class simple name -> (seconds, test count) for one TRX file.
func readTrx(path string) (map[string]measurement, error) {
	files := map[string]measurement{}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("cannot read TRX %s: %v", path, err)
	}
	root, err := xmltree.Parse(data)
	if err != nil {
		return nil, fmt.Errorf("cannot read TRX %s: %v", path, err)
	}
	methods := map[string]string{}
	for _, unitTest := range root.IterNS(trxNamespace, "UnitTest") {
		method := unitTest.FindChildNS(trxNamespace, "TestMethod")
		if method == nil {
			continue
		}
		className := method.Attr("className")
		methods[unitTest.Attr("id")] = lastSegment(className)
	}
	for _, result := range root.IterNS(trxNamespace, "UnitTestResult") {
		className, ok := methods[result.Attr("testId")]
		if !ok {
			className = "?"
		}
		entry := files[className]
		entry.Seconds += trxDuration(result.Attr("duration"))
		entry.Count++
		files[className] = entry
	}
	return files, nil
}

// lastSegment is the last dot-separated segment of a name.
func lastSegment(name string) string {
	if index := strings.LastIndex(name, "."); index >= 0 {
		return name[index+1:]
	}
	return name
}

// longestProcessingTimeFirst splits classes over `shards` bins, heaviest
// class first onto the lightest bin.
func longestProcessingTimeFirst(costs map[string]float64, shards int) [][]string {
	bins := make([][]string, shards)
	loads := make([]float64, shards)
	names := sortedKeys(costs)
	sort.SliceStable(names, func(i, j int) bool {
		if costs[names[i]] != costs[names[j]] {
			return costs[names[i]] > costs[names[j]]
		}
		return names[i] < names[j]
	})
	for _, name := range names {
		target := 0
		for index := 1; index < shards; index++ {
			if loads[index] < loads[target] {
				target = index
			}
		}
		bins[target] = append(bins[target], name)
		loads[target] += costs[name]
	}
	return bins
}

// balance splits classes over `shards` bins, minimising the SLOWEST bin.
//
// Longest-processing-time-first alone is a heuristic, and on this suite it
// leaves 2.8 s on the table. LPT is the seed and a local search finishes it:
// repeatedly move one class from the slowest bin to another bin, or swap one
// class between two bins, for as long as the slowest bin gets lighter. Both
// moves are tried because a swap can improve a split that no single move can
// reach.
func balance(costs map[string]float64, shards int) [][]string {
	if shards <= 1 {
		return [][]string{sortedKeys(costs)}
	}

	bins := longestProcessingTimeFirst(costs, shards)
	loads := make([]float64, shards)
	for index, names := range bins {
		total := 0.0
		for _, name := range names {
			total += costs[name]
		}
		loads[index] = total
	}

	spread := func() float64 {
		maximum := loads[0]
		for _, load := range loads {
			if load > maximum {
				maximum = load
			}
		}
		return maximum
	}

	improved := true
	for improved {
		improved = false
		slowest := 0
		for index := 1; index < shards; index++ {
			// The heaviest bin; a tie goes to the LOWEST index, the way
			// python's max(range(shards), key=lambda i: (loads[i], -i))
			// does.
			if loads[index] > loads[slowest] {
				slowest = index
			}
		}

		// A MOVE: take one class out of the slowest bin and try it elsewhere.
		for _, sourceClass := range append([]string{}, bins[slowest]...) {
			for target := 0; target < shards; target++ {
				if target == slowest {
					continue
				}
				candidate := append([]float64{}, loads...)
				candidate[slowest] -= costs[sourceClass]
				candidate[target] += costs[sourceClass]
				if maxFloats(candidate) < spread() {
					bins[slowest] = remove(bins[slowest], sourceClass)
					bins[target] = append(bins[target], sourceClass)
					loads = candidate
					improved = true
					break
				}
			}
			if improved {
				break
			}
		}
		if improved {
			continue
		}

		// A SWAP: exchange one class of the slowest bin with one of another.
		for target := 0; target < shards; target++ {
			if target == slowest {
				continue
			}
			for _, sourceClass := range append([]string{}, bins[slowest]...) {
				for _, targetClass := range append([]string{}, bins[target]...) {
					candidate := append([]float64{}, loads...)
					candidate[slowest] += costs[targetClass] - costs[sourceClass]
					candidate[target] += costs[sourceClass] - costs[targetClass]
					if maxFloats(candidate) < spread() {
						bins[slowest] = remove(bins[slowest], sourceClass)
						bins[target] = remove(bins[target], targetClass)
						bins[slowest] = append(bins[slowest], targetClass)
						bins[target] = append(bins[target], sourceClass)
						loads = candidate
						improved = true
						break
					}
				}
				if improved {
					break
				}
			}
			if improved {
				break
			}
		}
	}

	out := make([][]string, shards)
	for index, names := range bins {
		sortedNames := append([]string{}, names...)
		sort.Strings(sortedNames)
		out[index] = sortedNames
	}
	return out
}

// remove takes the first occurrence of the value out of the list.
func remove(values []string, value string) []string {
	out := make([]string, 0, len(values))
	removed := false
	for _, item := range values {
		if !removed && item == value {
			removed = true
			continue
		}
		out = append(out, item)
	}
	return out
}

// maxFloats is the largest of the values.
func maxFloats(values []float64) float64 {
	maximum := values[0]
	for _, value := range values {
		if value > maximum {
			maximum = value
		}
	}
	return maximum
}

// parseFloat parses a float the way python's float() would.
func parseFloat(text string) (float64, error) {
	return strconv.ParseFloat(text, 64)
}
