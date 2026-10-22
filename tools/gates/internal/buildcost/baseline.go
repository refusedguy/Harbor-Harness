package buildcost

import (
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"
)

// The sections of the baseline file.
const (
	SectionCompiler = "[compiler]"
	SectionProject  = "[project]"
	SectionRadius   = "[radius]"
)

// Baseline is the recorded cost of the tree: what every project cost the
// compiler, and what a change in it costs everybody who depends on it.
//
// It is a RECORDED measurement, not a target: the file says what the tree
// cost on the day it was measured, and the gate says whether the tree still
// costs that.
type Baseline struct {
	// Source is where the measurement came from.
	Source string
	// CscCalls and CscSeconds are the compiler's totals of the
	// measurement.
	CscCalls   int
	CscSeconds float64
	// CoreCompileSeconds is the CoreCompile target's total.
	CoreCompileSeconds float64
	// AttributionRatio is the sum of the own times over the compiler's
	// total. It is the number that says whether the per-project numbers
	// are compile costs or scheduler.
	AttributionRatio float64
	// SolutionProjects is the number of projects the solution held when
	// the measurement was taken.
	SolutionProjects int
	// MeasuredProjects is the number of them the log carried a row for.
	// The two numbers together say how much of the tree the measurement
	// covers: a project the log says nothing about is NOT a project that
	// cost nothing, and it is not recorded as one.
	MeasuredProjects int
	// Projects maps a project path to its recorded cost in seconds.
	Projects map[string]float64
	// ProjectFiles and ProjectLines are the recorded size of every
	// project: the .cs files the compiler reads, and their lines.
	ProjectFiles map[string]int
	ProjectLines map[string]int
	// Radius maps a project path to the recorded price of its radius, in
	// seconds.
	Radius map[string]float64
	// RadiusConsumers maps a project path to the recorded number of
	// transitive consumers of its radius.
	RadiusConsumers map[string]int
}

// NewBaseline returns an empty baseline.
func NewBaseline() *Baseline {
	return &Baseline{
		Projects:        map[string]float64{},
		ProjectFiles:    map[string]int{},
		ProjectLines:    map[string]int{},
		Radius:          map[string]float64{},
		RadiusConsumers: map[string]int{},
	}
}

// WriteBaseline writes the baseline in the format the reader reads.
//
// The format is a text file with three sections, so a reader can see the
// measurement without running anything:
//
//	[compiler]
//	csc-calls 112
//	csc-seconds 144.9
//	corecompile-seconds 146.3
//	attribution-ratio 1.13
//	source <the log the measurement came from>
//
//	[project]
//	src/Harbor.Core/Harbor.Core.csproj | 12.4 | 48 | 9918
//
//	[radius]
//	src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj | 103 | 159.0
func WriteBaseline(path string, baseline *Baseline) error {
	var text strings.Builder
	text.WriteString(baselineHeader)
	text.WriteString("\n")
	text.WriteString(SectionCompiler + "\n")
	text.WriteString(fmt.Sprintf("csc-calls %d\n", baseline.CscCalls))
	text.WriteString(fmt.Sprintf("csc-seconds %.1f\n", baseline.CscSeconds))
	text.WriteString(fmt.Sprintf("corecompile-seconds %.1f\n", baseline.CoreCompileSeconds))
	text.WriteString(fmt.Sprintf("attribution-ratio %.2f\n", baseline.AttributionRatio))
	text.WriteString(fmt.Sprintf("solution-projects %d\n", baseline.SolutionProjects))
	text.WriteString(fmt.Sprintf("measured-projects %d\n", baseline.MeasuredProjects))
	text.WriteString(fmt.Sprintf("source %s\n", baseline.Source))
	text.WriteString("\n")
	text.WriteString(SectionProject + "\n")
	projects := make([]string, 0, len(baseline.Projects))
	for project := range baseline.Projects {
		projects = append(projects, project)
	}
	sort.Strings(projects)
	for _, project := range projects {
		text.WriteString(fmt.Sprintf("%s | %.1f | %d | %d\n", project,
			baseline.Projects[project], baseline.ProjectFiles[project], baseline.ProjectLines[project]))
	}
	text.WriteString("\n")
	text.WriteString(SectionRadius + "\n")
	radii := make([]string, 0, len(baseline.Radius))
	for project := range baseline.Radius {
		radii = append(radii, project)
	}
	sort.Strings(radii)
	for _, project := range radii {
		text.WriteString(fmt.Sprintf("%s | %d | %.1f\n",
			project, baseline.RadiusConsumers[project], baseline.Radius[project]))
	}
	return os.WriteFile(path, []byte(text.String()), 0o644)
}

// baselineHeader is the header of the baseline file: what it is, how it was
// measured, and how to read it.
const baselineHeader = `# compile-cost-baseline - what every project of the tree costs the
# compiler, and what a change in one of them costs everybody who depends on
# it.
#
# OWNER: issue #2112. Read by tools/bin/harbor-gates-linux-x64
# measure-compile-cost, which compares a fresh measurement against this file
# and goes red when a project grew past its budget.
#
# WHAT IS RECORDED HERE
#
#   [compiler]  the compiler's own totals of the measurement: how many Csc
#               calls the build made, how many seconds they took, and the
#               attribution ratio (the sum of the per-project own times over
#               the compiler's total). A ratio of about 1 to 3 means the
#               measurement came from a build that ran one compiler at a
#               time, and the per-project numbers are compile costs; a ratio
#               of an order of magnitude more means the build ran its
#               compilers in parallel and the per-project numbers are
#               dominated by waiting, so the instrument refuses to price a
#               radius with them. A ratio BELOW 1 means the log is missing
#               rows, and the instrument refuses that too.
#
#               solution-projects and measured-projects say how much of the
#               tree the measurement covers: the log measured that many of
#               the solution's projects. A project the log says nothing
#               about is NOT a project that cost nothing, and it is not
#               recorded as one.
#
#   [project]   what one project cost the compiler, in seconds, and how big
#               it is (the .cs files the compiler reads, and their lines):
#                 <path> | <seconds> | <files> | <lines>
#               The rows are the projects the log measured. A project the
#               log says nothing about has no row: its cost is unknown, and
#               a row with a zero would say it cost nothing.
#
#   [radius]    what a change in one project costs: the number of projects
#               that depend on it, directly or through another project, and
#               the price of that set in seconds:
#                 <path> | <transitive consumers> | <seconds>
#               The rows are the radii whose set was measured WHOLE. A set
#               with a project missing from the log is priced with the
#               measured part of it, and that price is a lower bound, so it
#               is not recorded here: a baseline that holds a lower bound as
#               if it were the price compares a fresh measurement against a
#               number that was never true.
#
# HOW TO RE-MEASURE
#
#   1. Take a build log with the performance summary in it:
#        dotnet build Harbor.slnx -c Release --no-restore \
#          -maxcpucount:1 -v:m -clp:PerformanceSummary
#      The -maxcpucount:1 matters: a build that runs many compilers at once
#      cannot say what one project costs the compiler.
#   2. Write the new baseline:
#        harbor-gates measure-compile-cost --log <log> \
#          --write-baseline tools/compile-cost-baseline.txt
#      The "source" line is the path of the log. A measurement taken from a
#      CI run says which run it was, and names it with --source-label:
#        harbor-gates measure-compile-cost --log <log> \
#          --source-label "ci-run 39483960190, arm c-verbose" \
#          --write-baseline tools/compile-cost-baseline.txt
#
# THE BUDGET
#
#   A project is over budget when its cost grew by more than 20 % AND by
#   more than 10 seconds. Both, so a small project that grew by a third but
#   is worth half a second is not a finding.
#
#   The growth is measured as the project's SHARE of the compiler's time,
#   and not as its absolute seconds: a threshold on a live wall clock fails
#   on a slow runner, and a machine that is 30 % slower makes every project
#   look 30 % more expensive. A share does not move when the machine does.
#
# WHERE THE MEASUREMENT CAME FROM
#
#   The "source" line below says which build the numbers are from. A
#   measurement taken from a CI run names the run and the arm; a
#   measurement taken from a local build names the log file.
`

// ReadBaseline reads a baseline file.
func ReadBaseline(path string) (*Baseline, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("cannot read the baseline %s: %w", path, err)
	}
	out := NewBaseline()
	section := ""
	for number, line := range strings.Split(string(data), "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" || strings.HasPrefix(trimmed, "#") {
			continue
		}
		switch trimmed {
		case SectionCompiler, SectionProject, SectionRadius:
			section = trimmed
			continue
		}
		switch section {
		case SectionCompiler:
			key, value, ok := strings.Cut(trimmed, " ")
			if !ok {
				return nil, fmt.Errorf("%s:%d: a compiler row needs a key and a value", path, number+1)
			}
			value = strings.TrimSpace(value)
			switch key {
			case "csc-calls":
				parsed, err := strconv.Atoi(value)
				if err != nil {
					return nil, fmt.Errorf("%s:%d: csc-calls is not a number: %w", path, number+1, err)
				}
				out.CscCalls = parsed
			case "csc-seconds":
				parsed, err := strconv.ParseFloat(value, 64)
				if err != nil {
					return nil, fmt.Errorf("%s:%d: csc-seconds is not a number: %w", path, number+1, err)
				}
				out.CscSeconds = parsed
			case "corecompile-seconds":
				parsed, err := strconv.ParseFloat(value, 64)
				if err != nil {
					return nil, fmt.Errorf("%s:%d: corecompile-seconds is not a number: %w", path, number+1, err)
				}
				out.CoreCompileSeconds = parsed
			case "attribution-ratio":
				parsed, err := strconv.ParseFloat(value, 64)
				if err != nil {
					return nil, fmt.Errorf("%s:%d: attribution-ratio is not a number: %w", path, number+1, err)
				}
				out.AttributionRatio = parsed
			case "solution-projects":
				parsed, err := strconv.Atoi(value)
				if err != nil {
					return nil, fmt.Errorf("%s:%d: solution-projects is not a number: %w", path, number+1, err)
				}
				out.SolutionProjects = parsed
			case "measured-projects":
				parsed, err := strconv.Atoi(value)
				if err != nil {
					return nil, fmt.Errorf("%s:%d: measured-projects is not a number: %w", path, number+1, err)
				}
				out.MeasuredProjects = parsed
			case "source":
				out.Source = value
			}
		case SectionProject:
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
		case SectionRadius:
			fields := splitRow(trimmed)
			if len(fields) != 3 {
				return nil, fmt.Errorf("%s:%d: a radius row needs a path, a consumer count and a price", path, number+1)
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

// splitRow splits a baseline row on the " | " separator.
func splitRow(row string) []string {
	parts := strings.Split(row, "|")
	out := make([]string, 0, len(parts))
	for _, part := range parts {
		out = append(out, strings.TrimSpace(part))
	}
	return out
}
