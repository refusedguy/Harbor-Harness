// Package aggregateanalyzercost is the compiled port of
// tools/aggregate-analyzer-cost.py.
//
// The gate turns the per-arm measurements of the analyzer cost harness into
// the table: the marginal wall cost of every analyzer family and the
// compiler's own per-analyzer seconds.
//
// It is a REPORTER, not a gate: it prints and never fails a build.
//
// The text this prints is the text the python original printed, and the files
// it writes are the files the python original wrote.
package aggregateanalyzercost

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// assemblyMarkers is the family a measured analyzer assembly belongs to. The
// assembly name is the part of the assembly identity before ", Version=".
var assemblyMarkers = []struct {
	marker string
	family string
}{
	{"Roslynator", "Roslynator.Analyzers"},
	{"SonarAnalyzer", "SonarAnalyzer.CSharp"},
	{"NetAnalyzers", "Microsoft.CodeAnalysis.NetAnalyzers"},
	{"BannedApiAnalyzers", "Microsoft.CodeAnalysis.BannedApiAnalyzers"},
	{"PublicApiAnalyzers", "Microsoft.CodeAnalysis.PublicApiAnalyzers"},
	{"VisualStudio.Threading.Analyzers", "Microsoft.VisualStudio.Threading.Analyzers"},
	{"Meziantou", "Meziantou.Analyzer"},
	{"AsyncFixer", "AsyncFixer"},
	{"ReflectionAnalyzers", "ReflectionAnalyzers"},
	{"DependencyInjection.Lifetime.Analyzers", "DependencyInjection.Lifetime.Analyzers"},
	{"CSharpFunctionalExtensions.Analyzers", "CSharpFunctionalExtensions.Analyzers"},
	{"DotNetProjectFile.Analyzers", "DotNetProjectFile.Analyzers"},
}

// sdkAssembly is the assembly the SDK itself contributes. It is not one of
// the packages this repository references, and it is listed so that its time
// is not silently folded into somebody else's row.
const sdkAssembly = "Microsoft.CodeAnalysis.Analyzers"

// reportRowRe is "   0.242   48   Roslyn.Diagnostics.CSharp.Analyzers, Version=1.2.0.0, ...".
var reportRowRe = regexp.MustCompile(`^\s*(?P<seconds>[<>]?\d+(?:\.\d+)?)\s+(?P<pct>\d+|<1)\s{2,}(?P<name>\S.*?)\s*$`)

// logPrefixRe is the elapsed-seconds prefix the log of measure-build-cpu.py
// writes before every line.
var logPrefixRe = regexp.MustCompile(`^\s*\d+\.\d+\s\|\s?`)

// totalRe is the compiler's own total line.
var totalRe = regexp.MustCompile(`^Total analyzer execution time:\s*(?P<seconds>\d+(?:\.\d+)?)\s*seconds`)

// Run is the gate entry point.
func Run(args []string) int {
	measDir := ""
	jsonOut := "analyzer-cost.json"
	mdOut := "analyzer-cost.md"
	summary := ""
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--summary":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--summary needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			summary = value
		case "--json-out":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--json-out needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			jsonOut = value
		case "--md-out":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--md-out needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			mdOut = value
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates aggregate-analyzer-cost <meas-dir> [--summary PATH] [--json-out PATH] [--md-out PATH] [--self-test]")
			return gate.Green
		default:
			if strings.HasPrefix(arg, "-") && arg != "-" {
				fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			if measDir != "" {
				fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			measDir = arg
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if measDir == "" {
		fmt.Fprintln(os.Stderr, "aggregate-analyzer-cost: the measurement directory is required")
		return gate.Broken
	}

	arms := readArms(measDir)
	if len(arms) == 0 {
		fmt.Fprintln(os.Stderr, "no arms found")
		return gate.Red
	}

	var noise []float64
	var rows []*armRow
	var narrowed *armRow
	families := gatekit.NewJSONObject()
	familyOrder := []string{}
	familySeconds := map[string]float64{}
	familyAssemblies := map[string][]string{}

	labels := make([]string, 0, len(arms))
	for label := range arms {
		labels = append(labels, label)
	}
	sort.Strings(labels)

	for _, label := range labels {
		halves := arms[label]
		if halves["base"] == nil || halves["arm"] == nil {
			fmt.Printf("%s: incomplete arm, skipped\n", label)
			continue
		}
		baseWall := wallOf(halves["base"])
		armWall := wallOf(halves["arm"])
		delta := baseWall - armWall
		row := &armRow{
			label:             label,
			baselineWall:      baseWall,
			armWall:           armWall,
			delta:             delta,
			armCscMS:          cscMSOf(halves["arm"]),
			armAvgBusyCores:   coresOf(halves["arm"]),
			armAvgBusyCoresOK: coresOf(halves["arm"]) != nil,
		}
		switch {
		case strings.HasPrefix(label, "null-"):
			noise = append(noise, delta)
		case label == "narrowed":
			narrowed = row
		default:
			rows = append(rows, row)
		}

		if strings.HasSuffix(label, "-report") {
			totals := parseAnalyzerReport(filepath.Join(measDir, label+"-arm.log"))
			for _, assembly := range totals.order {
				seconds := totals.seconds[assembly]
				family := familyOf(assembly)
				if _, ok := familySeconds[family]; !ok {
					familyOrder = append(familyOrder, family)
					familySeconds[family] = 0
					familyAssemblies[family] = nil
				}
				familySeconds[family] += seconds
				familyAssemblies[family] = append(familyAssemblies[family], assembly)
			}
		}
	}
	for _, family := range familyOrder {
		sort.Strings(familyAssemblies[family])
		entry := gatekit.NewJSONObject()
		entry.Set("seconds", familySeconds[family])
		entry.Set("assemblies", familyAssemblies[family])
		families.Set(family, entry)
	}

	// The biggest difference first.
	sort.SliceStable(rows, func(i, j int) bool { return rows[i].delta > rows[j].delta })

	noiseFloor := 0.0
	for _, value := range noise {
		if value < 0 {
			value = -value
		}
		if value > noiseFloor {
			noiseFloor = value
		}
	}
	noiseMedian := 0.0
	if len(noise) > 0 {
		ordered := append([]float64{}, noise...)
		sort.Float64s(ordered)
		noiseMedian = median(ordered)
	}

	markdown := render(rows, noise, noiseFloor, noiseMedian, narrowed, families, familyOrder, familySeconds, familyAssemblies)
	fmt.Println(markdown)

	result := gatekit.NewJSONObject()
	result.Set("noise_floor_seconds", noiseFloor)
	result.Set("noise_median_seconds", noiseMedian)
	if narrowed == nil {
		result.Set("narrowed", nil)
	} else {
		result.Set("narrowed", narrowed.object())
	}
	var marginal []any
	for _, row := range rows {
		marginal = append(marginal, row.object())
	}
	result.Set("marginal_cost", marginal)
	result.Set("analyzer_seconds_per_family", families)

	armsObject := gatekit.NewJSONObject()
	for _, label := range labels {
		halves := arms[label]
		if halves["base"] == nil || halves["arm"] == nil {
			continue
		}
		entry := gatekit.NewJSONObject()
		entry.Set("baseline_wall_seconds", wallOf(halves["base"]))
		entry.Set("arm_wall_seconds", wallOf(halves["arm"]))
		entry.Set("wall_delta_seconds", wallOf(halves["base"])-wallOf(halves["arm"]))
		entry.Set("arm_csc_ms", cscMSOf(halves["arm"]))
		if cores := coresOf(halves["arm"]); cores != nil {
			entry.Set("arm_avg_busy_cores", *cores)
		} else {
			entry.Set("arm_avg_busy_cores", nil)
		}
		if exit, ok := gatekit.RawValue(halves["arm"], "exit_code"); ok {
			entry.Set("exit_code", exit)
		} else {
			entry.Set("exit_code", nil)
		}
		armsObject.Set(label, entry)
	}
	result.Set("arms", armsObject)

	if err := os.WriteFile(jsonOut, []byte(gatekit.WriteJSON(result, 0)), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "cannot write the json:", err)
		return gate.Broken
	}
	if err := os.WriteFile(mdOut, []byte(markdown), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "cannot write the markdown:", err)
		return gate.Broken
	}
	if summary != "" {
		if err := appendFile(summary, markdown+"\n"); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the summary:", err)
			return gate.Broken
		}
	}
	return gate.Green
}

// armRow is one arm's numbers.
type armRow struct {
	label             string
	baselineWall      float64
	armWall           float64
	delta             float64
	armCscMS          int
	armAvgBusyCores   *float64
	armAvgBusyCoresOK bool
}

// object is the JSON form of a row.
func (r *armRow) object() *gatekit.JSONObject {
	out := gatekit.NewJSONObject()
	out.Set("label", r.label)
	out.Set("baseline_wall_seconds", r.baselineWall)
	out.Set("arm_wall_seconds", r.armWall)
	out.Set("wall_delta_seconds", r.delta)
	out.Set("arm_csc_ms", r.armCscMS)
	if r.armAvgBusyCoresOK {
		out.Set("arm_avg_busy_cores", *r.armAvgBusyCores)
	} else {
		out.Set("arm_avg_busy_cores", nil)
	}
	return out
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// median is the python statistics.median.
func median(sorted []float64) float64 {
	count := len(sorted)
	if count == 0 {
		return 0
	}
	middle := count / 2
	if count%2 == 1 {
		return sorted[middle]
	}
	return (sorted[middle-1] + sorted[middle]) / 2
}

// readArms reads the two halves of every arm.
//
// A file whose name does not end in "-base" or "-arm" is not an arm, and a
// file that cannot be read is skipped.
func readArms(measDir string) map[string]map[string]map[string]any {
	entries, err := os.ReadDir(measDir)
	if err != nil {
		return nil
	}
	names := make([]string, 0, len(entries))
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		names = append(names, entry.Name())
	}
	sort.Strings(names)

	arms := map[string]map[string]map[string]any{}
	for _, name := range names {
		base := strings.TrimSuffix(name, ".json")
		idx := strings.LastIndexByte(base, '-')
		if idx < 0 {
			continue
		}
		label, half := base[:idx], base[idx+1:]
		if half != "base" && half != "arm" {
			continue
		}
		if label == "" {
			continue
		}
		data, err := gatekit.ReadJSONObject(filepath.Join(measDir, name))
		if err != nil {
			continue
		}
		if arms[label] == nil {
			arms[label] = map[string]map[string]any{}
		}
		arms[label][half] = data
	}
	return arms
}

// wallOf is the wall time of a run.
func wallOf(arm map[string]any) float64 {
	value, _ := gatekit.JSONNumber(arm, "wall_seconds")
	return value
}

// cscMSOf is the compiler time of a run.
func cscMSOf(arm map[string]any) int {
	tasks := gatekit.JSONMap(arm, "tasks")
	entry := gatekit.JSONMap(tasks, "Csc")
	if entry == nil {
		return 0
	}
	value, _ := gatekit.JSONInt(entry, "total_ms")
	return value
}

// coresOf is the average busy cores of a run.
func coresOf(arm map[string]any) *float64 {
	cpu := gatekit.JSONMap(arm, "cpu")
	if cpu == nil {
		return nil
	}
	value, ok := gatekit.JSONNumber(cpu, "avg_busy_cores")
	if !ok {
		return nil
	}
	return &value
}

// familyOf is the family an analyzer assembly belongs to.
func familyOf(assembly string) string {
	if assembly == sdkAssembly {
		return sdkAssembly
	}
	for _, entry := range assemblyMarkers {
		if strings.Contains(assembly, entry.marker) {
			return entry.family
		}
	}
	return assembly
}

// analyzerTotals is the per-assembly time of one build, in the order the
// assemblies first appeared.
type analyzerTotals struct {
	order   []string
	seconds map[string]float64
}

// parseAnalyzerReport reads the compiler's own per-analyzer report.
//
// The report is printed once per compilation, so the numbers are summed over
// every project of the build. Only the ASSEMBLY rows are read: they are the
// ones that carry a ", Version=" identity, and they are the unit a package is
// measured in.
func parseAnalyzerReport(logPath string) *analyzerTotals {
	totals := &analyzerTotals{seconds: map[string]float64{}}
	data, err := os.ReadFile(logPath)
	if err != nil {
		return totals
	}
	for _, raw := range strings.Split(string(data), "\n") {
		line := logPrefixRe.ReplaceAllString(raw, "")
		if totalRe.MatchString(strings.TrimSpace(line)) {
			continue
		}
		match := reportRowRe.FindStringSubmatch(line)
		if match == nil {
			continue
		}
		name := match[3]
		if !strings.Contains(name, ", Version=") {
			continue
		}
		assembly := strings.TrimSpace(strings.SplitN(name, ", Version=", 2)[0])
		seconds, err := gatekit.ParseFloat(match[1])
		if err != nil {
			continue
		}
		if _, ok := totals.seconds[assembly]; !ok {
			totals.order = append(totals.order, assembly)
		}
		totals.seconds[assembly] += seconds
	}
	return totals
}

// render is the markdown table.
func render(rows []*armRow, noise []float64, noiseFloor float64, noiseMedian float64,
	narrowed *armRow, families *gatekit.JSONObject, familyOrder []string,
	familySeconds map[string]float64, familyAssemblies map[string][]string) string {
	var lines []string
	lines = append(lines, "# The cost of each analyzer family")
	lines = append(lines, "")
	lines = append(lines, "Every arm builds the same tree twice in one job — the baseline (every analyzer family ON, in every tree) and then its own arm — so the runner's speed cancels out of the difference.")
	lines = append(lines, "")
	if len(noise) > 0 {
		lines = append(lines, fmt.Sprintf("The noise floor, measured by the control arms (the baseline against itself): median %+.1f s, largest %.1f s. A difference below that is not a difference.", noiseMedian, noiseFloor))
		lines = append(lines, "")
	}
	if narrowed != nil {
		lines = append(lines, fmt.Sprintf("**The narrowed tree: %.1f s against a baseline of %.1f s in the same job — %+.1f s (%+.1f %%).**",
			narrowed.armWall, narrowed.baselineWall, narrowed.delta, narrowed.delta/narrowed.baselineWall*100))
		lines = append(lines, "")
	}
	lines = append(lines, "## The marginal cost of each family")
	lines = append(lines, "")
	lines = append(lines, "The difference between the arm's own baseline and the arm, in the same job, on the same runner. Everything else in the two builds is the same tree and the same command.")
	lines = append(lines, "")
	lines = append(lines, "| family | baseline (s) | arm (s) | difference (s) | arm Csc (ms) | avg busy cores |")
	lines = append(lines, "|---|---|---|---|---|---|")
	for _, row := range rows {
		lines = append(lines, fmt.Sprintf("| `%s` | %.1f | %.1f | %+.1f | %d | %s |",
			row.label, row.baselineWall, row.armWall, row.delta, row.armCscMS, row.coresText()))
	}
	lines = append(lines, "")
	lines = append(lines, "## The compiler's own report")
	lines = append(lines, "")
	lines = append(lines, "The elapsed time per analyzer assembly, summed over every compilation of the build (`-p:ReportAnalyzer=true`). This is the cost inside one compilation; the wall difference above is what the runner pays for it.")
	lines = append(lines, "")
	if len(familyOrder) > 0 {
		lines = append(lines, "| family | analyzer seconds | assemblies |")
		lines = append(lines, "|---|---|---|")
		ordered := append([]string{}, familyOrder...)
		sort.SliceStable(ordered, func(i, j int) bool {
			return familySeconds[ordered[i]] > familySeconds[ordered[j]]
		})
		for _, family := range ordered {
			lines = append(lines, fmt.Sprintf("| `%s` | %.1f | %s |",
				family, familySeconds[family], strings.Join(familyAssemblies[family], ", ")))
		}
	} else {
		lines = append(lines, "_The compiler printed no analyzer report in this run._")
	}
	lines = append(lines, "")
	return strings.Join(lines, "\n")
}

// coresText is the average busy cores of a row, the way python printed it.
func (r *armRow) coresText() string {
	if !r.armAvgBusyCoresOK {
		return "None"
	}
	return gatekit.PyFloat(*r.armAvgBusyCores)
}

// appendFile appends text to a file.
func appendFile(path string, text string) error {
	fh, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer fh.Close()
	_, err = fh.WriteString(text)
	return err
}
