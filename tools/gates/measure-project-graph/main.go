// Package measureprojectgraph is the compiled port of
// tools/measure-project-graph.py.
//
// The gate measures the shape of the project dependency graph: the depth,
// the level widths and the critical path, parsed from the solution and the
// ProjectReference edges.
//
// It is a HELPER, not a gate: it measures and reports, and no workflow runs
// it. The exit code is the contract of the binary: 0 green, 1 red, 2 the
// gate could not run.
//
// The text this prints is the text the python original printed.
package measureprojectgraph

import (
	"encoding/xml"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// projectPathRe is `<Project Path="...">` in a solution file.
var projectPathRe = regexp.MustCompile(`<Project\s+Path="([^"]+)"`)

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

// Run is the gate entry point.
func Run(args []string) int {
	solution := "Harbor.slnx"
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
			fmt.Println("usage: harbor-gates measure-project-graph [--solution Harbor.slnx] [--json out.json] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	data, err := os.ReadFile(solution)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the solution:", err)
		return gate.Broken
	}
	projects := readSolutionProjects(string(data))

	root := filepath.Dir(absolutePath(solution))
	if root == "" {
		root = "."
	}

	measured := analyze(solution, projects, root, readProjectReferences)
	printResult(measured)

	if jsonOut != "" {
		if err := os.WriteFile(jsonOut, []byte(renderJSON(measured)), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the json:", err)
			return gate.Broken
		}
	}
	return gate.Green
}

// analyze measures the shape of the graph.
//
// The reader of the edges is a parameter so the self-test can plant a graph
// without touching the disk.
func analyze(solution string, projects []string, root string, references func(path string) []string) *result {
	out := &result{
		solution: solution,
		projects: projects,
		edges:    map[string]map[string]bool{},
		levels:   map[string]int{},
		byLevel:  map[int][]string{},
	}

	// An edge points from the referring project to the project it references.
	for _, project := range projects {
		absolute := filepath.Join(root, project)
		for _, reference := range references(absolute) {
			// Normalize to the solution-relative form when the reference is
			// a path inside the repo; framework/nuget references are skipped.
			candidate := filepath.Clean(filepath.Join(filepath.Dir(project), reference))
			candidate = strings.ReplaceAll(candidate, "\\", "/")
			if contains(projects, candidate) {
				if out.edges[project] == nil {
					out.edges[project] = map[string]bool{}
				}
				out.edges[project][candidate] = true
			}
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
	for _, project := range projects {
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
	for _, project := range projects {
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

// absolutePath is the absolute form of a path, the way python's
// os.path.abspath resolves it against the working directory.
func absolutePath(path string) string {
	if filepath.IsAbs(path) {
		return filepath.Clean(path)
	}
	cwd, err := os.Getwd()
	if err != nil {
		return filepath.Clean(path)
	}
	return filepath.Join(cwd, path)
}

// readSolutionProjects reads the projects of a solution file.
func readSolutionProjects(text string) []string {
	var projects []string
	for _, match := range projectPathRe.FindAllStringSubmatch(text, -1) {
		projects = append(projects, strings.ReplaceAll(match[1], "\\", "/"))
	}
	return projects
}

// readProjectReferences reads the ProjectReference edges of a project file.
//
// A file that cannot be read or parsed has no edges: the python original
// caught the error and returned an empty list.
func readProjectReferences(path string) []string {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	decoder := xml.NewDecoder(strings.NewReader(string(data)))
	var refs []string
	for {
		token, err := decoder.Token()
		if err != nil {
			break
		}
		element, ok := token.(xml.StartElement)
		if !ok || element.Name.Local != "ProjectReference" {
			continue
		}
		for _, attr := range element.Attr {
			if attr.Name.Local == "Include" && attr.Value != "" {
				refs = append(refs, strings.ReplaceAll(attr.Value, "\\", "/"))
			}
		}
	}
	return refs
}

// contains is the membership test of the python original's project list.
func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// renderJSON writes the result the way python's json.dump(indent=2) wrote
// it: the keys in the order of the literal, two spaces of indent, and no
// trailing newline.
func renderJSON(out *result) string {
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
	}
	for index, key := range keys {
		text.WriteString("  ")
		text.WriteString(jsonString(key))
		text.WriteString(": ")
		text.WriteString(jsonValue(out, key))
		if index < len(keys)-1 {
			text.WriteString(",")
		}
		text.WriteString("\n")
	}
	text.WriteString("}")
	return text.String()
}

// jsonValue renders one value of the result.
func jsonValue(out *result, key string) string {
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
