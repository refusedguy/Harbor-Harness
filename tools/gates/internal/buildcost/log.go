package buildcost

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"regexp"
	"strconv"
	"strings"
)

// The four sections the MSBuild Performance Summary has. The parser keys on
// the header line, so a log that carries only some of them still parses.
const (
	LogSectionEvaluation = "Project Evaluation Performance Summary:"
	LogSectionProject    = "Project Performance Summary:"
	LogSectionTarget     = "Target Performance Summary:"
	LogSectionTask       = "Task Performance Summary:"
)

// rowRe is one row of a performance summary section:
//
//	1234 ms  /w/src/Harbor.Core/Harbor.Core.csproj                       1 calls
//	1234 ms  Csc                                                          109 calls
//
// The scope may contain spaces (a project path may), so it is matched
// lazily and the trailing "N calls" is what closes the row.
var rowRe = regexp.MustCompile(`^\s*(\d+|\*)\s+ms\s+(.*?)\s+(\d+)\s+calls\s*$`)

// timestampPrefixRe is the prefix the build meter puts on every line of the
// log it captures: "<seconds> | ". A log captured straight from a terminal
// has no prefix, so the prefix is optional.
var timestampPrefixRe = regexp.MustCompile(`^[0-9]+(?:\.[0-9]+)?\s*\|\s?`)

// Log is what a build log says about the compiler's work.
//
// The totals are what the compiler did: the number of Csc calls and the
// milliseconds they took. The projects are what MSBuild reported per project:
// the time it spent on that project, summed over the instances of it.
type Log struct {
	// Source is where the log came from.
	Source string
	// CscCalls is the number of Csc task invocations.
	CscCalls int
	// CscMillis is the time the Csc task took, summed over the calls.
	CscMillis int64
	// CoreCompileCalls and CoreCompileMillis are the same for the
	// CoreCompile target, which is the target that calls the compiler.
	CoreCompileCalls  int
	CoreCompileMillis int64
	// ProjectMillis is MSBuild's own per-project time, keyed by the
	// project path as the log spells it.
	ProjectMillis map[string]int64
	// ProjectCalls is the number of target executions per project.
	ProjectCalls map[string]int
	// Tasks and Targets are the per-name totals of the other two
	// sections, kept so a reader can see what else the build spent time
	// on.
	Tasks   map[string]int64
	Targets map[string]int64
}

// ProjectTimes returns the per-project times of the log, keyed by the
// project's FILE NAME (Harbor.Core.csproj). Two projects of the same name in
// different directories would collide, so the caller that has the solution
// should use Log.ProjectMillis with the solution's paths instead; this
// helper is for the log alone.
func (l *Log) ProjectTimes() map[string]int64 {
	out := make(map[string]int64, len(l.ProjectMillis))
	for path, millis := range l.ProjectMillis {
		out[baseName(path)] += millis
	}
	return out
}

// baseName is the file name of a path, with either separator.
func baseName(path string) string {
	path = strings.ReplaceAll(path, "\\", "/")
	if index := strings.LastIndex(path, "/"); index >= 0 {
		return path[index+1:]
	}
	return path
}

// ParseLog reads a build log and returns what it says about the compiler.
//
// A log without the Task Performance Summary is not an error here: the
// caller decides what a log without it means. The error this returns is for
// a log that cannot be read at all.
func ParseLog(path string) (*Log, error) {
	handle, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("cannot read the build log %s: %w", path, err)
	}
	defer handle.Close()
	return parseLog(path, handle)
}

// ParseLogReader parses a log from a reader.
func ParseLogReader(source string, reader io.Reader) (*Log, error) {
	return parseLog(source, reader)
}

func parseLog(source string, reader io.Reader) (*Log, error) {
	out := &Log{
		Source:        source,
		ProjectMillis: map[string]int64{},
		ProjectCalls:  map[string]int{},
		Tasks:         map[string]int64{},
		Targets:       map[string]int64{},
	}
	section := ""
	scanner := bufio.NewScanner(reader)
	scanner.Buffer(make([]byte, 0, 1<<20), 1<<24)
	for scanner.Scan() {
		line := stripTimestamp(scanner.Text())
		trimmed := strings.TrimSpace(line)
		switch trimmed {
		case LogSectionEvaluation, LogSectionProject, LogSectionTarget, LogSectionTask:
			section = trimmed
			continue
		}
		if section == "" {
			continue
		}
		match := rowRe.FindStringSubmatch(line)
		if match == nil {
			// A blank line ends a section; anything else that does not
			// parse is not a row and is ignored.
			if trimmed == "" {
				section = ""
			}
			continue
		}
		if match[1] == "*" {
			// A re-entrant row: MSBuild had no value for it. It is
			// counted in the section it belongs to and never summed.
			continue
		}
		millis, err := strconv.ParseInt(match[1], 10, 64)
		if err != nil {
			continue
		}
		scope := strings.TrimSpace(match[2])
		calls, err := strconv.Atoi(match[3])
		if err != nil {
			continue
		}
		switch section {
		case LogSectionProject:
			if !strings.HasSuffix(scope, ".csproj") {
				continue
			}
			out.ProjectMillis[scope] += millis
			out.ProjectCalls[scope] += calls
		case LogSectionTask:
			out.Tasks[scope] += millis
			if scope == "Csc" {
				out.CscCalls += calls
				out.CscMillis += millis
			}
		case LogSectionTarget:
			out.Targets[scope] += millis
			if scope == "CoreCompile" {
				out.CoreCompileCalls += calls
				out.CoreCompileMillis += millis
			}
		}
	}
	if err := scanner.Err(); err != nil {
		return nil, fmt.Errorf("cannot read the build log %s: %w", source, err)
	}
	return out, nil
}

// stripTimestamp removes the "<seconds> | " prefix the build meter puts on
// every captured line.
func stripTimestamp(line string) string {
	return timestampPrefixRe.ReplaceAllString(line, "")
}

// LogHasCompilerTotals says whether the log carries the compiler's totals.
// A log without them cannot price anything: there is no denominator.
func LogHasCompilerTotals(log *Log) bool {
	return log != nil && log.CscCalls > 0 && log.CscMillis > 0
}
