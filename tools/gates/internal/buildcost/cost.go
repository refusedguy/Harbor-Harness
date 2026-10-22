package buildcost

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// Source is the size of a project: what the compiler reads for it.
type Source struct {
	// Files is the number of C# files the project compiles.
	Files int
	// Lines is the number of lines in those files.
	Lines int
}

// ReadSources counts the C# files and lines of every project of the graph.
//
// The count is of the files under the project's own directory, minus the
// directories of the OTHER projects: a project that holds a nested project
// (tests/Harbor.TestKit and tests/Harbor.TestKit.Tui) must not be charged
// for the nested one's files, or the tree's totals count them twice.
//
// The generated files (the ones under obj/) are not counted: they are not
// in the tree, and a project that has never been built has none of them.
func ReadSources(graph *Graph) map[string]Source {
	owned := map[string]bool{}
	for _, project := range graph.Projects {
		owned[filepath.Clean(filepath.Join(graph.Root, filepath.Dir(project)))] = true
	}
	out := map[string]Source{}
	for _, project := range graph.Projects {
		directory := filepath.Join(graph.Root, filepath.Dir(project))
		out[project] = countSources(directory, owned)
	}
	return out
}

// countSources counts the C# files and lines of one directory.
func countSources(directory string, owned map[string]bool) Source {
	var total Source
	_ = filepath.Walk(directory, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			if path == directory {
				return nil
			}
			name := info.Name()
			if name == "bin" || name == "obj" || name == ".git" || name == ".vs" {
				return filepath.SkipDir
			}
			// The directory of another project is that project's.
			if owned[filepath.Clean(path)] {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(info.Name(), ".cs") {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		total.Files++
		total.Lines += countLines(data)
		return nil
	})
	return total
}

// countLines counts the lines of a file: the number of line breaks, plus the
// last line when the file does not end with one.
func countLines(data []byte) int {
	if len(data) == 0 {
		return 0
	}
	lines := strings.Count(string(data), "\n")
	if !strings.HasSuffix(string(data), "\n") {
		lines++
	}
	return lines
}

// Cost is what one project costs the compiler.
type Cost struct {
	// Project is the project's path, relative to the solution.
	Project string
	// Millis is the project's OWN time: the time MSBuild reported for the
	// project, summed over the instances of it. Nothing is subtracted from
	// it, because in a build that runs one compiler at a time the build of a
	// referenced project is not inside the window of the project that
	// references it (see the package doc).
	Millis int64
	// Measured says the log carried a row for this project. A project the
	// log says NOTHING about has no cost: its zero is the absence of a
	// measurement, not a measurement of zero, and a caller that treats it
	// as one prices a project that was never built.
	Measured bool
	// Source is the size of the project.
	Source Source
}

// Seconds is the cost in seconds.
func (c Cost) Seconds() float64 {
	return float64(c.Millis) / 1000.0
}

// ComputeCosts reads the cost of every project of the graph out of the log.
//
// The log's per-project row is MSBuild's OWN time for the project, summed
// over the instances of it: the build of a project that many projects
// reference happens many times, and most of those times are the negotiation
// of the target frameworks rather than the compilation.
//
// NOTHING IS SUBTRACTED FROM THE ROW, and that is a measurement, not a
// simplification: in a build that runs one compiler at a time the build of a
// referenced project is NOT inside the window of the project that references
// it (the graph builds the references first), so subtracting the references'
// times would take away time that was never inside. The sign of the error
// is visible in the data: a project that references a 7.5-second project
// and whose own row reads 0.3 seconds cannot contain it.
//
// What a project costs the compiler is its SHARE of the compiler's measured
// total, and the share is what the caller prices a radius with. The ratio
// of the sum of the rows over the compiler's total says whether the rows
// are usable at all (See AttributionRatio).
func ComputeCosts(graph *Graph, log *Log, sources map[string]Source) map[string]Cost {
	out := map[string]Cost{}
	times, measured := ResolveProjectTimes(graph, log)
	for _, project := range graph.SortedProjects() {
		out[project] = Cost{
			Project:  project,
			Millis:   times[project],
			Measured: measured[project],
			Source:   sources[project],
		}
	}
	return out
}

// ResolveProjectTimes maps the solution's projects onto the times the log
// reported, and says which of them the log measured at all.
//
// The log spells a project with the ABSOLUTE path of the machine that built
// it ("/home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Core/
// Harbor.Core.csproj") and the solution spells it relative to the solution
// ("src/Harbor.Core/Harbor.Core.csproj"), so the two have to be matched.
//
// The match is by the longest suffix that ends at a path separator, and the
// file name is the last resort: two projects of the same file name in
// different directories would then share a number, which is wrong, but a
// tree does not have two projects with the same file name, and a wrong
// number here would be visible in the table.
//
// The second result says which projects the log carried a ROW for. A project
// the log says nothing about is not a project that cost nothing: it is a
// project the log does not measure, and the caller must be able to tell the
// two apart.
func ResolveProjectTimes(graph *Graph, log *Log) (map[string]int64, map[string]bool) {
	out := map[string]int64{}
	measured := map[string]bool{}
	byName := map[string]int64{}
	for path, millis := range log.ProjectMillis {
		byName[baseName(path)] += millis
	}
	for _, project := range graph.SortedProjects() {
		if millis, ok := log.ProjectMillis[project]; ok {
			out[project] = millis
			measured[project] = true
			continue
		}
		suffix := "/" + strings.ReplaceAll(project, "\\", "/")
		found := false
		for path, millis := range log.ProjectMillis {
			normalized := strings.ReplaceAll(path, "\\", "/")
			if strings.HasSuffix(normalized, suffix) {
				out[project] = millis
				measured[project] = true
				found = true
				break
			}
		}
		if found {
			continue
		}
		// The last resort: the file name. The map holds only the names
		// the log actually carries, so a name that is not in it is a
		// project the log says NOTHING about, and it is NOT a project
		// that cost nothing.
		if millis, ok := byName[baseName(project)]; ok {
			out[project] = millis
			measured[project] = true
		}
	}
	return out, measured
}

// MeasuredCount is the number of projects the log carried a row for.
func MeasuredCount(costs map[string]Cost) int {
	count := 0
	for _, cost := range costs {
		if cost.Measured {
			count++
		}
	}
	return count
}

// MeasuredCosts returns the costs of the projects the log measured, the
// most expensive first.
//
// The projects the log says nothing about are NOT in the list: their zero
// is the absence of a measurement, and a caller that priced them would
// price a project that was never built.
func MeasuredCosts(costs map[string]Cost) []Cost {
	out := make([]Cost, 0, len(costs))
	for _, cost := range costs {
		if !cost.Measured {
			continue
		}
		out = append(out, cost)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Millis != out[j].Millis {
			return out[i].Millis > out[j].Millis
		}
		return out[i].Project < out[j].Project
	})
	return out
}

// UnmeasuredCosts returns the costs of the projects the log carried no row
// for, sorted by the project's path.
func UnmeasuredCosts(costs map[string]Cost) []Cost {
	out := make([]Cost, 0, len(costs))
	for _, cost := range costs {
		if cost.Measured {
			continue
		}
		out = append(out, cost)
	}
	sort.Slice(out, func(i, j int) bool {
		return out[i].Project < out[j].Project
	})
	return out
}

// AttributionRatio is the sum of the per-project times over the compiler's
// own total.
//
// A build that ran one compiler at a time puts every project's work in its
// own row and nothing else, so the rows add up to a small multiple of the
// compiler's total: the ratio is about 3 (the rest is the negotiation of
// the target frameworks, which happens once per referencing context). A
// build that ran four compilers at once has the references inside the
// windows of the projects that reference them, so every project counts the
// build of its references as well, and the ratio is an order of magnitude
// larger.
//
// A ratio far above the first number therefore means the rows are dominated
// by waiting rather than by work, and a caller that prices a radius with
// them is pricing the scheduler rather than the compiler.
func AttributionRatio(costs map[string]Cost, cscMillis int64) float64 {
	if cscMillis <= 0 {
		return 0
	}
	var total int64
	for _, cost := range costs {
		total += cost.Millis
	}
	return float64(total) / float64(cscMillis)
}

// TotalCost is the sum of the own times of the given projects.
func TotalCost(costs map[string]Cost, projects []string) int64 {
	var total int64
	for _, project := range projects {
		total += costs[project].Millis
	}
	return total
}
