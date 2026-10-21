package pinschema

// analyze.go -- the rule: the pin and the declarations must agree.

import (
	"fmt"
	"sort"

	"harbor.local/gates/internal/toolschema"
)

// Analyze returns the problems with one (declarations, pin) pair.
//
// An empty list means the two agree: every declared tool is pinned with the
// hash its declaration renders to, and every pin names a tool that is there.
func Analyze(entries []toolschema.Entry, pins map[string]string) []string {
	var problems []string

	if len(pins) == 0 {
		problems = append(problems, fmt.Sprintf(
			"the pin file %s pins no schema at all: either it is missing, or it is empty, and a gate that reads nothing protects nothing",
			toolschema.PinRelative))
		return problems
	}

	declared := map[string]bool{}
	for _, entry := range entries {
		declared[entry.Name] = true
	}

	for _, entry := range entries {
		rendered := toolschema.Digest(entry.Schema)
		pin, ok := pins[entry.Name]
		if !ok {
			problems = append(problems, fmt.Sprintf(
				"the tool '%s' declares a schema, but no hash is pinned for it: its schema would change unnoticed",
				entry.Name))
			continue
		}
		if pin != rendered {
			problems = append(problems, fmt.Sprintf(
				"the tool '%s' renders as %s and is pinned as %s: its parameter schema changed. The schema is the tool's contract with the model — re-sign the pin (harbor-gates check-tool-schema-pin --write-pin) in the same commit, or revert the change",
				entry.Name, rendered, pin))
		}
	}

	names := make([]string, 0, len(pins))
	for name := range pins {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		if !declared[name] {
			problems = append(problems, fmt.Sprintf(
				"a hash is pinned for the tool '%s', but no tool declares that schema: either the tool was renamed or it is gone",
				name))
		}
	}

	return problems
}
