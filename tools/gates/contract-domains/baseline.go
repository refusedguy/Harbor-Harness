package contractdomains

import (
	"fmt"
	"os"
	"strconv"
	"strings"
)

// # THE COMPILE-COST BASELINE
//
// The price of a radius is the sum of the seconds the compiler spent on
// the projects in it, and those seconds are a MEASUREMENT: they come from
// the compile-cost instrument of #2112, and its baseline file records them.
//
// The baseline is read here, and not imported, for one reason: the
// instrument and its baseline land with their own pull request (#2151),
// and this gate must run on a tree that does not have them yet. The file
// is optional: without it the gate measures the CONSUMER COUNTS (facts
// about the tree) and says that the prices are unavailable; with it, the
// prices are there too.
//
// The format is the one the instrument writes:
//
//	[compiler]
//	csc-calls 112
//	csc-seconds 144.9
//	...
//
//	[project]
//	src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj | 2.5 | 48 | 9919
//
//	[radius]
//	src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj | 103 | 143.0
//
// A missing file is not an error: the gate measures what it can measure.

// baseline is the recorded compile cost of the tree.
type baseline struct {
	// Projects maps a project path to the seconds the compiler spent on
	// it.
	Projects map[string]float64
	// ProjectFiles and ProjectLines are the recorded size of every
	// project.
	ProjectFiles map[string]int
	ProjectLines map[string]int
	// Radius maps a project path to the recorded price of its radius.
	Radius map[string]float64
	// RadiusConsumers maps a project path to the recorded number of
	// transitive consumers of its radius.
	RadiusConsumers map[string]int
	// Source is where the measurement came from.
	Source string
}

// newBaseline returns an empty baseline.
func newBaseline() *baseline {
	return &baseline{
		Projects:        map[string]float64{},
		ProjectFiles:    map[string]int{},
		ProjectLines:    map[string]int{},
		Radius:          map[string]float64{},
		RadiusConsumers: map[string]int{},
	}
}

// readBaseline reads a compile-cost baseline file.
//
// The file is optional: a path that does not exist gives an empty baseline
// and no error, because the tree that has no compile-cost measurement yet
// still has a census.
func readBaseline(path string) (*baseline, error) {
	out := newBaseline()
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return out, nil
		}
		return nil, fmt.Errorf("cannot read the baseline %s: %w", path, err)
	}
	section := ""
	for number, line := range strings.Split(string(data), "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" || strings.HasPrefix(trimmed, "#") {
			continue
		}
		switch trimmed {
		case "[compiler]", "[project]", "[radius]":
			section = trimmed
			continue
		}
		switch section {
		case "[compiler]":
			key, value, ok := strings.Cut(trimmed, " ")
			if !ok {
				continue
			}
			value = strings.TrimSpace(value)
			if key == "source" {
				out.Source = value
			}
		case "[project]":
			fields := splitRow(trimmed)
			if len(fields) != 4 {
				return nil, fmt.Errorf("%s:%d: a project row needs a path, a cost, a file count and a line count",
					path, number+1)
			}
			seconds, err := strconv.ParseFloat(fields[1], 64)
			if err != nil {
				return nil, fmt.Errorf("%s:%d: the cost is not a number: %w", path, number+1, err)
			}
			files, err := strconv.Atoi(fields[2])
			if err != nil {
				return nil, fmt.Errorf("%s:%d: the file count is not a number: %w", path, number+1, err)
			}
			lines, err := strconv.Atoi(fields[3])
			if err != nil {
				return nil, fmt.Errorf("%s:%d: the line count is not a number: %w", path, number+1, err)
			}
			out.Projects[fields[0]] = seconds
			out.ProjectFiles[fields[0]] = files
			out.ProjectLines[fields[0]] = lines
		case "[radius]":
			fields := splitRow(trimmed)
			if len(fields) != 3 {
				return nil, fmt.Errorf("%s:%d: a radius row needs a path, a consumer count and a price",
					path, number+1)
			}
			consumers, err := strconv.Atoi(fields[1])
			if err != nil {
				return nil, fmt.Errorf("%s:%d: the consumer count is not a number: %w", path, number+1, err)
			}
			seconds, err := strconv.ParseFloat(fields[2], 64)
			if err != nil {
				return nil, fmt.Errorf("%s:%d: the price is not a number: %w", path, number+1, err)
			}
			out.Radius[fields[0]] = seconds
			out.RadiusConsumers[fields[0]] = consumers
		}
	}
	return out, nil
}

// splitRow splits a baseline row on the "|" separator.
func splitRow(row string) []string {
	parts := strings.Split(row, "|")
	out := make([]string, 0, len(parts))
	for _, part := range parts {
		out = append(out, strings.TrimSpace(part))
	}
	return out
}
