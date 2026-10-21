// Package measureprojectgraph is the compiled port of
// tools/measure-project-graph.py, extended with the price of a change
// (issue #2112).
//
// The gate measures the shape of the project dependency graph: the depth,
// the level widths and the critical path, parsed from the solution and the
// ProjectReference edges.
//
// It is a HELPER, not a gate: it measures and reports, and no workflow runs
// it. The exit code is the contract of the binary: 0 green, 1 red, 2 the
// gate could not run.
//
// THE TEXT THIS PRINTS IS THE TEXT THE PYTHON ORIGINAL PRINTED, plus the
// radius: how many projects a change in one project rebuilds, and what that
// set costs.
package measureprojectgraph

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/buildcost"
	"harbor.local/gates/internal/gate"
)

// Run is the gate entry point.
func Run(args []string) int {
	solution := "Harbor.slnx"
	baseline := ""
	jsonOut := ""
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--solution":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--solution needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			solution = value
		case "--baseline":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--baseline needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			baseline = value
		case "--json":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--json needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			jsonOut = value
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates measure-project-graph [--solution Harbor.slnx]")
			fmt.Println("                                             [--baseline tools/compile-cost-baseline.txt]")
			fmt.Println("                                             [--json out.json] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		root = "."
	}
	// The report names the solution the way the caller named it; the gate
	// resolves it to find the tree. The python original printed the
	// argument, and the workflows read that line.
	label := solution
	if !filepath.IsAbs(solution) {
		candidate := filepath.Join(root, solution)
		if _, statErr := os.Stat(candidate); statErr == nil {
			solution = candidate
		}
	}

	graph, err := buildcost.ReadGraph(solution)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the solution:", err)
		return gate.Broken
	}
	graph.Solution = label
	measured := analyze(graph)

	printResult(measured)

	// The price of the radius comes from the recorded measurement: the
	// baseline says what every project cost the compiler, and the radius
	// says what those projects cost together.
	prices := map[string]float64{}
	if baseline != "" {
		path := baseline
		if !filepath.IsAbs(path) {
			path = filepath.Join(root, path)
		}
		recorded, err := buildcost.ReadBaseline(path)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		prices = recorded.Projects
		printRadius(graph, prices, recorded.Radius)
	} else {
		printRadius(graph, prices, nil)
	}

	if jsonOut != "" {
		if err := os.WriteFile(jsonOut, []byte(renderJSON(measured, graph, prices)), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the json:", err)
			return gate.Broken
		}
	}
	return gate.Green
}

// result is the measured shape of the graph.
type result struct {
	solution     string
	projects     []string
	edges        map[string]map[string]bool
	levels       map[string]int
	byLevel      map[int][]string
	levelNumbers []int
	depth        int
	width        int
	edgeCount    int
	levelsWide4  int
	levelsWide2  int
	critical     []string
}

// analyze measures the shape of the graph.
func analyze(graph *buildcost.Graph) *result {
	out := &result{
		solution: graph.Solution,
		projects: graph.Projects,
		edges:    map[string]map[string]bool{},
		levels:   map[string]int{},
		byLevel:  map[int][]string{},
	}

	// An edge points from the referring project to the project it references.
	for _, project := range graph.Projects {
		references := graph.References[project]
		if len(references) == 0 {
			continue
		}
		out.edges[project] = map[string]bool{}
		for _, reference := range references {
			out.edges[project][reference] = true
		}
	}

	// Longest-path level of every project: level(v) = 0 for a project with no
	// project references, else 1 + max(level of its references).
	visiting := map[string]bool{}
	var levelOf func(project string) int
	levelOf = func(project string) int {
		if level, ok := out.levels[project]; ok {
			return level
		}
		if visiting[project] {
			// a cycle: treat as a leaf, the build would fail
			return 0
		}
		visiting[project] = true
		value := 0
		if deps := out.edges[project]; len(deps) > 0 {
			value = 1
			best := 0
			for dep := range deps {
				if level := levelOf(dep); level > best {
					best = level
				}
			}
			value += best
		}
		delete(visiting, project)
		out.levels[project] = value
		return value
	}
	for _, project := range graph.Projects {
		levelOf(project)
	}

	for project, level := range out.levels {
		out.byLevel[level] = append(out.byLevel[level], project)
	}

	for _, level := range out.levels {
		if level > out.depth {
			out.depth = level
		}
	}
	for _, items := range out.byLevel {
		if len(items) > out.width {
			out.width = len(items)
		}
	}
	for _, deps := range out.edges {
		out.edgeCount += len(deps)
	}

	// Critical path: the longest chain of project references.
	memo := map[string][]string{}
	var longestChain func(project string) []string
	longestChain = func(project string) []string {
		if chain, ok := memo[project]; ok {
			return chain
		}
		best := []string{}
		deps := make([]string, 0, len(out.edges[project]))
		for dep := range out.edges[project] {
			deps = append(deps, dep)
		}
		sort.Strings(deps)
		for _, dep := range deps {
			chain := longestChain(dep)
			if len(chain) > len(best) {
				best = chain
			}
		}
		chain := append([]string{project}, best...)
		memo[project] = chain
		return chain
	}
	for _, project := range out.projects {
		chain := longestChain(project)
		if len(chain) > len(out.critical) {
			out.critical = chain
		}
	}

	for level := range out.byLevel {
		out.levelNumbers = append(out.levelNumbers, level)
	}
	sort.Ints(out.levelNumbers)
	for _, level := range out.levelNumbers {
		count := len(out.byLevel[level])
		if count >= 4 {
			out.levelsWide4++
		}
		if count >= 2 {
			out.levelsWide2++
		}
	}
	return out
}

// printResult is the report the python original printed.
func printResult(out *result) {
	fmt.Printf("solution            : %s\n", out.solution)
	fmt.Printf("projects            : %d\n", len(out.projects))
	fmt.Printf("project edges       : %d\n", out.edgeCount)
	fmt.Printf("graph depth         : %d\n", out.depth)
	fmt.Printf("max level width     : %d projects at one level\n", out.width)
	fmt.Printf("levels with >= 4    : %d of %d\n", out.levelsWide4, len(out.levelNumbers))
	fmt.Printf("levels with >= 2    : %d of %d\n", out.levelsWide2, len(out.levelNumbers))
	fmt.Println("level widths        :")
	for _, level := range out.levelNumbers {
		fmt.Printf("  level %2d: %3d projects\n", level, len(out.byLevel[level]))
	}
	fmt.Printf("critical path (%d):\n", len(out.critical))
	for _, step := range out.critical {
		fmt.Printf("  %s\n", step)
	}
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

// renderJSON writes the result the way python's json.dump(indent=2) wrote
// it: the keys in the order of the literal, two spaces of indent, and no
// trailing newline.
func renderJSON(out *result, graph *buildcost.Graph, prices map[string]float64) string {
	var text strings.Builder
	text.WriteString("{\n")
	keys := []string{
		"solution",
		"projects",
		"edges",
		"depth",
		"max_level_width",
		"level_widths",
		"levels_wide_enough_for_4_compilers",
		"levels_wide_enough_for_2_compilers",
		"critical_path",
		"critical_path_length",
		"radii",
	}
	for index, key := range keys {
		text.WriteString("  ")
		text.WriteString(jsonString(key))
		text.WriteString(": ")
		text.WriteString(jsonValue(out, graph, prices, key))
		if index < len(keys)-1 {
			text.WriteString(",")
		}
		text.WriteString("\n")
	}
	text.WriteString("}")
	return text.String()
}

// jsonValue renders one value of the result.
func jsonValue(out *result, graph *buildcost.Graph, prices map[string]float64, key string) string {
	switch key {
	case "solution":
		return jsonString(out.solution)
	case "projects":
		return fmt.Sprintf("%d", len(out.projects))
	case "edges":
		return fmt.Sprintf("%d", out.edgeCount)
	case "depth":
		return fmt.Sprintf("%d", out.depth)
	case "max_level_width":
		return fmt.Sprintf("%d", out.width)
	case "level_widths":
		if len(out.levelNumbers) == 0 {
			return "{}"
		}
		var text strings.Builder
		text.WriteString("{\n")
		for index, level := range out.levelNumbers {
			text.WriteString("    ")
			text.WriteString(jsonString(fmt.Sprintf("%d", level)))
			text.WriteString(": ")
			text.WriteString(fmt.Sprintf("%d", len(out.byLevel[level])))
			if index < len(out.levelNumbers)-1 {
				text.WriteString(",")
			}
			text.WriteString("\n")
		}
		text.WriteString("  }")
		return text.String()
	case "levels_wide_enough_for_4_compilers":
		return fmt.Sprintf("%d", out.levelsWide4)
	case "levels_wide_enough_for_2_compilers":
		return fmt.Sprintf("%d", out.levelsWide2)
	case "critical_path":
		if len(out.critical) == 0 {
			return "[]"
		}
		var text strings.Builder
		text.WriteString("[\n")
		for index, step := range out.critical {
			text.WriteString("    ")
			text.WriteString(jsonString(step))
			if index < len(out.critical)-1 {
				text.WriteString(",")
			}
			text.WriteString("\n")
		}
		text.WriteString("  ]")
		return text.String()
	case "critical_path_length":
		return fmt.Sprintf("%d", len(out.critical))
	case "radii":
		return renderRadiiJSON(graph, prices)
	}
	return "null"
}

// jsonString renders a string the way python's json module wrote it.
func jsonString(text string) string {
	var out strings.Builder
	out.WriteByte('"')
	for _, r := range text {
		switch r {
		case '"':
			out.WriteString(`\"`)
		case '\\':
			out.WriteString(`\\`)
		case '\n':
			out.WriteString(`\n`)
		case '\r':
			out.WriteString(`\r`)
		case '\t':
			out.WriteString(`\t`)
		default:
			if r < 0x20 {
				out.WriteString(fmt.Sprintf(`\u%04x`, r))
				continue
			}
			out.WriteRune(r)
		}
	}
	out.WriteByte('"')
	return out.String()
}
