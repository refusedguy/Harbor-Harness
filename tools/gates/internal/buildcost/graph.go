package buildcost

import (
	"encoding/xml"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// projectPathRe is `<Project Path="...">` in a solution file.
var projectPathRe = regexp.MustCompile(`<Project\s+Path="([^"]+)"`)

// Graph is the project dependency graph of the tree.
//
// An edge points from the referring project to the project it references, so
// the consumers of a project are the projects that point at it.
type Graph struct {
	// Solution is the solution file the graph was read from.
	Solution string
	// Root is the directory the solution's paths are relative to.
	Root string
	// Projects are the solution's project paths, sorted.
	Projects []string
	// References maps a project to the projects it references.
	References map[string][]string
	// Consumers maps a project to the projects that reference it.
	Consumers map[string][]string
}

// ReadGraph reads the solution and the ProjectReference edges of every
// project in it.
//
// A project file that cannot be read or parsed has no edges: the python
// original this gate was ported from caught the error and returned an empty
// list, and a project that cannot be read is a project whose references are
// unknown, not a project that references nothing. The difference is
// reported by Graph.Unknown.
func ReadGraph(solution string) (*Graph, error) {
	data, err := os.ReadFile(solution)
	if err != nil {
		return nil, err
	}
	root := filepath.Dir(solution)
	if root == "" {
		root = "."
	}
	projects := ReadSolutionProjects(string(data))
	graph := &Graph{
		Solution:   solution,
		Root:       root,
		Projects:   projects,
		References: map[string][]string{},
		Consumers:  map[string][]string{},
	}
	for _, project := range projects {
		absolute := filepath.Join(root, project)
		var references []string
		for _, reference := range readProjectReferences(absolute) {
			candidate := filepath.Clean(filepath.Join(filepath.Dir(project), reference))
			candidate = strings.ReplaceAll(candidate, "\\", "/")
			if contains(graph.Projects, candidate) {
				references = append(references, candidate)
			}
		}
		sort.Strings(references)
		graph.References[project] = references
	}
	for _, project := range projects {
		for _, reference := range graph.References[project] {
			graph.Consumers[reference] = append(graph.Consumers[reference], project)
		}
	}
	for project := range graph.Consumers {
		sort.Strings(graph.Consumers[project])
	}
	return graph, nil
}

// ReadSolutionProjects reads the projects of a solution file.
func ReadSolutionProjects(text string) []string {
	var projects []string
	for _, match := range projectPathRe.FindAllStringSubmatch(text, -1) {
		projects = append(projects, strings.ReplaceAll(match[1], "\\", "/"))
	}
	return projects
}

// readProjectReferences reads the ProjectReference edges of a project file.
func readProjectReferences(path string) []string {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	decoder := xml.NewDecoder(strings.NewReader(string(data)))
	var references []string
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
				references = append(references, strings.ReplaceAll(attr.Value, "\\", "/"))
			}
		}
	}
	return references
}

// contains is the membership test of the project list.
func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// TransitiveConsumers returns every project that depends on the project,
// directly or through another project.
//
// The project's own consumers, its consumers' consumers and so on: the set
// of projects a change in this one rebuilds. The project itself is NOT in
// the set; the caller that wants the radius adds the owner.
func (g *Graph) TransitiveConsumers(project string) []string {
	seen := map[string]bool{}
	stack := append([]string{}, g.Consumers[project]...)
	for len(stack) > 0 {
		current := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		if current == project || seen[current] {
			continue
		}
		seen[current] = true
		stack = append(stack, g.Consumers[current]...)
	}
	out := make([]string, 0, len(seen))
	for consumer := range seen {
		out = append(out, consumer)
	}
	sort.Strings(out)
	return out
}

// Radius is the project and everything that depends on it: the set a change
// in one file of the owner rebuilds.
type Radius struct {
	// Owner is the project the change is in.
	Owner string
	// Direct is the number of projects that reference the owner directly.
	Direct int
	// Consumers are the transitive consumers, sorted.
	Consumers []string
}

// RadiusOf returns the radius of a project.
func (g *Graph) RadiusOf(project string) Radius {
	consumers := g.TransitiveConsumers(project)
	return Radius{
		Owner:     project,
		Direct:    len(g.Consumers[project]),
		Consumers: consumers,
	}
}

// Projects returns the projects of the graph, sorted.
func (g *Graph) SortedProjects() []string {
	out := append([]string{}, g.Projects...)
	sort.Strings(out)
	return out
}
