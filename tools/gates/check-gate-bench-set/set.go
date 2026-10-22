package benchset

// set.go -- the two files that say what the benchmark measures, and what it
// does not.
//
// bench/gates/tools.tsv    the MEASURED set: one row per gate, TAB-separated,
//                          the gate's name and the command the harness runs.
//
// bench/gates/excluded.tsv the gates CI runs that the measured set does NOT
//                          hold, one row per gate, with the reason it is out.
//
// Both files are data, not prose, and the gate reads them as data. A comment
// line starts with `#`; a blank line is nothing.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// measuredRow is one row of the measured set.
type measuredRow struct {
	name    string
	command string
}

// excludedRow is one row of the exclusion list.
type excludedRow struct {
	name   string
	reason string
}

// measuredSet is the whole measured set.
type measuredSet struct {
	rows []measuredRow
}

// excludedSet is the whole exclusion list.
type excludedSet struct {
	rows []excludedRow
}

// readMeasured reads bench/gates/tools.tsv.
func readMeasured(root string) (measuredSet, error) {
	path := filepath.Join(root, toolsFile)
	data, err := os.ReadFile(path)
	if err != nil {
		return measuredSet{}, fmt.Errorf("cannot read %s: %w", toolsFile, err)
	}
	var set measuredSet
	seen := map[string]bool{}
	for number, line := range strings.Split(string(data), "\n") {
		text := strings.TrimRight(line, "\r")
		if strings.TrimSpace(text) == "" || strings.HasPrefix(strings.TrimSpace(text), "#") {
			continue
		}
		fields := strings.Split(text, "\t")
		if len(fields) != 2 {
			return measuredSet{}, fmt.Errorf("%s:%d: a row is the gate's name, a TAB and the command; this one has %d field(s)",
				toolsFile, number+1, len(fields))
		}
		name := strings.TrimSpace(fields[0])
		command := strings.TrimSpace(fields[1])
		if name == "" || command == "" {
			return measuredSet{}, fmt.Errorf("%s:%d: a row names a gate and a command, and this one does not", toolsFile, number+1)
		}
		if seen[name] {
			return measuredSet{}, fmt.Errorf("%s:%d: %s has more than one row", toolsFile, number+1, name)
		}
		seen[name] = true
		set.rows = append(set.rows, measuredRow{name: name, command: command})
	}
	return set, nil
}

// readExcluded reads bench/gates/excluded.tsv.
func readExcluded(root string) (excludedSet, error) {
	path := filepath.Join(root, excludedFile)
	data, err := os.ReadFile(path)
	if err != nil {
		return excludedSet{}, fmt.Errorf("cannot read %s: %w", excludedFile, err)
	}
	var set excludedSet
	seen := map[string]bool{}
	for number, line := range strings.Split(string(data), "\n") {
		text := strings.TrimRight(line, "\r")
		if strings.TrimSpace(text) == "" || strings.HasPrefix(strings.TrimSpace(text), "#") {
			continue
		}
		fields := strings.Split(text, "\t")
		if len(fields) != 2 {
			return excludedSet{}, fmt.Errorf("%s:%d: a row is the gate's name, a TAB and the reason it is out; this one has %d field(s)",
				excludedFile, number+1, len(fields))
		}
		name := strings.TrimSpace(fields[0])
		reason := strings.TrimSpace(fields[1])
		if name == "" {
			return excludedSet{}, fmt.Errorf("%s:%d: a row names a gate, and this one does not", excludedFile, number+1)
		}
		if seen[name] {
			return excludedSet{}, fmt.Errorf("%s:%d: %s has more than one row", excludedFile, number+1, name)
		}
		seen[name] = true
		set.rows = append(set.rows, excludedRow{name: name, reason: reason})
	}
	return set, nil
}
