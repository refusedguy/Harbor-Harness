package resultchains

// jsonreport.go -- the machine-readable report of the census.

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
)

// jsonReport renders the `--json` output.
//
// The member order is the python original's: total_guards, by_kind,
// survivors, per_file, sites. The `by_kind` and `per_file` objects carry
// their keys in the order the census first saw them, which is the order
// the walk produced.
func jsonReport(
	sites []site,
	byKind map[string]int,
	byKindOrder []string,
	perFile map[string]int,
	perFileOrder []string,
	survivors []site,
) string {
	entries := make([]string, 0, len(sites))
	for _, s := range sites {
		entries = append(entries, jsonObjectAt([]jsonField{
			{"path", jsonString(s.path)},
			{"line", jsonNumber(s.line)},
			{"var", jsonString(s.guardVar)},
			{"kind", jsonString(s.kind)},
			{"detail", jsonString(s.detail)},
		}, false, 2))
	}

	var b strings.Builder
	b.WriteString("{\n")
	b.WriteString("  \"total_guards\": ")
	b.WriteString(jsonNumber(len(sites)))
	b.WriteString(",\n")
	b.WriteString("  \"by_kind\": ")
	b.WriteString(jsonCounts(byKind, byKindOrder, false))
	b.WriteString(",\n")
	b.WriteString("  \"survivors\": ")
	b.WriteString(jsonNumber(len(survivors)))
	b.WriteString(",\n")
	b.WriteString("  \"per_file\": ")
	b.WriteString(jsonCounts(perFile, perFileOrder, false))
	b.WriteString(",\n")
	b.WriteString("  \"sites\": ")
	b.WriteString(jsonArrayAt(entries, 1))
	b.WriteString("\n}\n")
	return b.String()
}

// baselineSurvivors reads the `survivors` number of a baseline file.
//
// The baseline is JSON the python original wrote; the port reads it with
// the standard library's decoder and takes the one number the gate
// compares against. The error message is python's, because the message
// names the file a human has to fix.
func baselineSurvivors(path string) (int, bool, string) {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0, false, osErrorText(path, err)
	}
	var parsed map[string]any
	if err := json.Unmarshal(data, &parsed); err != nil {
		return 0, false, jsonErrorText(data, err)
	}
	value, ok := parsed["survivors"]
	if !ok {
		return 0, true, ""
	}
	switch typed := value.(type) {
	case float64:
		return int(typed), true, ""
	case string:
		n, err := strconv.Atoi(typed)
		if err != nil {
			return 0, false, fmt.Sprintf("invalid literal for int() with base 10: '%s'", typed)
		}
		return n, true, ""
	default:
		return 0, false, fmt.Sprintf("int() argument must be a string or a number, not '%T'", value)
	}
}

// osErrorText is python's message for a file that could not be read.
func osErrorText(path string, err error) string {
	switch {
	case os.IsNotExist(err):
		return fmt.Sprintf("[Errno 2] No such file or directory: '%s'", path)
	case os.IsPermission(err):
		return fmt.Sprintf("[Errno 13] Permission denied: '%s'", path)
	}
	return fmt.Sprintf("[Errno %d] %s: '%s'", 0, err.Error(), path)
}

// jsonErrorText is python's message for a file that is not JSON.
func jsonErrorText(data []byte, err error) string {
	var syn *json.SyntaxError
	if errors.As(err, &syn) {
		line, column := lineAndColumn(data, syn.Offset)
		return fmt.Sprintf("Expecting value: line %d column %d (char %d)", line, column, syn.Offset-1)
	}
	return err.Error()
}

// lineAndColumn is the 1-based line and column of a byte offset.
func lineAndColumn(data []byte, offset int64) (int, int) {
	if offset < 0 {
		offset = 0
	}
	if offset > int64(len(data)) {
		offset = int64(len(data))
	}
	line := 1
	column := 1
	for i := int64(0); i < offset; i++ {
		if data[i] == '\n' {
			line++
			column = 1
			continue
		}
		column++
	}
	return line, column
}
