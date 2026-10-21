package jsonreflection

// report.go -- the census, and the two ways of printing it.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"strings"
)

// censusJSON is the JSON shape of the census.
type censusJSON struct {
	Calls           int             `json:"calls"`
	SourceGenerated int             `json:"source_generated"`
	Reflection      int             `json:"reflection"`
	Violations      []violationJSON `json:"violations"`
}

// violationJSON is one violation in the JSON shape.
type violationJSON struct {
	Path string `json:"path"`
	Line int    `json:"line"`
	Call string `json:"call"`
}

// partition splits the findings into the violations and the exceptions that
// covered one, and returns the exceptions that covered nothing.
//
// The `used` set here is the REPORT's computation: an exception counts as
// used when a finding matched THAT exception.
func partition(findings []finding, exceptions []exception) ([]finding, map[int]bool, []exception) {
	var violations []finding
	used := map[int]bool{}
	for _, found := range findings {
		if found.sourceGenerated {
			continue
		}
		cover := matchException(found, exceptions)
		if cover != nil {
			used[cover.line] = true
			continue
		}
		violations = append(violations, found)
	}
	var stale []exception
	for _, item := range exceptions {
		if !used[item.line] {
			stale = append(stale, item)
		}
	}
	return violations, used, stale
}

// gateUsed is the python original's computation in the PLAIN path.
//
// The original writes it as a set comprehension over (exception, finding)
// pairs whose condition never mentions the exception, so an exception counts
// as used when ANY reflection call matched ANY exception. The report path
// computes it per exception, and the two disagree on a tree where one
// exception matched and another did not.
//
// The port keeps the behaviour, because the VERDICT on this tree depends on
// it: the tree carries a stale exception, and the plain path is the one CI
// runs. The disagreement is a defect of the original, and it is worth a
// follow-up of its own -- a port must not change what a gate says.
func gateUsed(findings []finding, exceptions []exception) map[int]bool {
	used := map[int]bool{}
	for _, found := range findings {
		if found.sourceGenerated {
			continue
		}
		if matchException(found, exceptions) == nil {
			continue
		}
		for _, item := range exceptions {
			used[item.line] = true
		}
		break
	}
	return used
}

// report prints the census and every group. It returns 1 when there is
// something to report.
func report(w io.Writer, findings []finding, exceptions []exception, asJSON bool) int {
	violations, _, stale := partition(findings, exceptions)
	sourceGenerated := 0
	for _, found := range findings {
		if found.sourceGenerated {
			sourceGenerated++
		}
	}

	if asJSON {
		payload := censusJSON{
			Calls:           len(findings),
			SourceGenerated: sourceGenerated,
			Reflection:      len(findings) - sourceGenerated - len(violations),
			Violations:      []violationJSON{},
		}
		for _, violation := range violations {
			payload.Violations = append(payload.Violations, violationJSON{
				Path: violation.path,
				Line: violation.line,
				Call: violation.call,
			})
		}
		encoder := json.NewEncoder(w)
		// Python's json.dumps does not escape <, > or &, and Go's
		// encoder does unless it is told not to.
		encoder.SetEscapeHTML(false)
		encoder.SetIndent("", "  ")
		if err := encoder.Encode(payload); err != nil {
			return 1
		}
		// The JSON shape fails on the VIOLATIONS alone: the stale
		// exceptions are counted in the census, not in the verdict.
		if len(violations) > 0 {
			return 1
		}
		return 0
	}

	fmt.Fprintln(w, "reflection-JSON census (tests/, excluding tests/Harbor.Architecture.Tests)")
	fmt.Fprintf(w, "  JsonSerializer calls      : %d\n", len(findings))
	fmt.Fprintf(w, "  source-generated          : %d\n", sourceGenerated)
	fmt.Fprintf(w, "  reflection (grandfathered): %d\n", len(findings)-sourceGenerated-len(violations))
	fmt.Fprintf(w, "  reflection (VIOLATION)    : %d\n", len(violations))
	for _, violation := range violations {
		fmt.Fprintf(w, "    %s:%d  %s\n", violation.path, violation.line, truncate(violation.call, 110))
	}
	for _, item := range stale {
		fmt.Fprintf(w, "  STALE EXCEPTION %s:%d — %s\n", item.path, item.line, item.reason)
	}
	return boolToExit(violations, stale)
}

// boolToExit is 1 when there is a violation or a stale exception, 0 otherwise.
func boolToExit(violations []finding, stale []exception) int {
	if len(violations) > 0 || len(stale) > 0 {
		return 1
	}
	return 0
}

// truncate cuts a text to a length, the way python's slice does.
func truncate(text string, length int) string {
	if len(text) <= length {
		return text
	}
	return text[:length]
}

// jsonIndent renders a value the way python's json.dumps(indent=2) does.
// It is used by the self-test, which compares the shape of the census.
func jsonIndent(value any) string {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(value); err != nil {
		return ""
	}
	return strings.TrimRight(buffer.String(), "\n")
}
