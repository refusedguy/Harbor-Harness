package testdup

// baseline.go -- the committed baseline: the groups the tree is allowed to
// have, the exemptions, and the way they are written back.
//
// The baseline is keyed on the MEMBERS of a group, never on the text of the
// bodies: the text changes every time somebody edits a duplicated test, and
// an edit is not new duplication.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/pyfmt"
)

// pyFloat is a float that marshals the way python writes it.
type pyFloat float64

// MarshalJSON writes the number the way python's json.dump writes it.
func (f pyFloat) MarshalJSON() ([]byte, error) {
	return []byte(pyfmt.Repr(float64(f))), nil
}

// roundTo rounds a float the way python's round does.
func roundTo(value float64, digits int) float64 {
	return pyfmt.Round(value, digits)
}

// orderedObject is a JSON object that remembers the order of its keys.
//
// The baseline's "groups" object is compared entry by entry, and the FIRST
// entry that matches wins, so the order of the committed file is part of the
// gate's verdict.
type orderedObject struct {
	keys   []string
	values map[string]json.RawMessage
}

// UnmarshalJSON reads an object, keeping the order of its keys.
func (o *orderedObject) UnmarshalJSON(data []byte) error {
	o.keys = nil
	o.values = map[string]json.RawMessage{}
	decoder := json.NewDecoder(bytes.NewReader(data))
	token, err := decoder.Token()
	if err != nil {
		return err
	}
	if delim, ok := token.(json.Delim); !ok || delim != '{' {
		return fmt.Errorf("not an object")
	}
	for decoder.More() {
		keyToken, err := decoder.Token()
		if err != nil {
			return err
		}
		key, ok := keyToken.(string)
		if !ok {
			return fmt.Errorf("not a key")
		}
		var raw json.RawMessage
		if err := decoder.Decode(&raw); err != nil {
			return err
		}
		if _, seen := o.values[key]; !seen {
			o.keys = append(o.keys, key)
		}
		o.values[key] = raw
	}
	return nil
}

// MarshalJSON writes the object, keeping the order of its keys.
func (o orderedObject) MarshalJSON() ([]byte, error) {
	var buffer bytes.Buffer
	buffer.WriteByte('{')
	for index, key := range o.keys {
		if index > 0 {
			buffer.WriteByte(',')
		}
		encoded, err := json.Marshal(key)
		if err != nil {
			return nil, err
		}
		buffer.Write(encoded)
		buffer.WriteByte(':')
		buffer.Write(o.values[key])
	}
	buffer.WriteByte('}')
	return buffer.Bytes(), nil
}

// baselineGroup is one group the baseline knows.
type baselineGroup struct {
	Members []string `json:"members"`
	Count   *int     `json:"count"`
	Lines   int      `json:"lines"`
	Files   int      `json:"files"`
	Exact   int      `json:"exact"`
	Shape   string   `json:"shape"`
}

// entryCount is how many copies a baseline entry recorded.
func (g *baselineGroup) entryCount() int {
	if g.Count != nil {
		return *g.Count
	}
	return len(g.Members)
}

// baselineMetrics is what the baseline measured.
type baselineMetrics struct {
	DuplicateLines *int `json:"duplicate_lines"`
}

// exemption is one exemption of the baseline.
type exemption struct {
	Paths   []string `json:"paths"`
	Reason  string   `json:"reason"`
	Expires string   `json:"expires"`
}

// baselineFile is the committed baseline.
type baselineFile struct {
	Version                int             `json:"version"`
	Generator              string          `json:"generator"`
	Policy                 json.RawMessage `json:"policy"`
	Census                 json.RawMessage `json:"census"`
	Metrics                baselineMetrics `json:"metrics"`
	BlockingDuplicateLines *int            `json:"blocking_duplicate_lines"`
	Groups                 orderedObject   `json:"groups"`
	Exemptions             []exemption     `json:"exemptions"`

	present bool
}

// baselinePath is the baseline file's path.
func baselinePath(root string) string {
	return filepath.Join(root, "tools", baselineName)
}

// loadBaseline reads the committed baseline, or nil when it is absent.
func loadBaseline(root string) *baselineFile {
	path := baselinePath(root)
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	var baseline baselineFile
	if err := json.Unmarshal(data, &baseline); err != nil {
		return nil
	}
	baseline.present = true
	return &baseline
}

// groupEntries returns the baseline's groups, in the order the file holds
// them.
func (b *baselineFile) groupEntries() []*baselineGroup {
	var entries []*baselineGroup
	for _, key := range b.Groups.keys {
		var entry baselineGroup
		if err := json.Unmarshal(b.Groups.values[key], &entry); err != nil {
			continue
		}
		entries = append(entries, &entry)
	}
	return entries
}

// writeBaseline regenerates the baseline file from the current tree.
func writeBaseline(root string, groups []*dupGroup, scan scanCounts, minLines int, minSimilarity float64) (string, error) {
	existing := loadBaseline(root)
	exemptions := []exemption{}
	if existing != nil {
		exemptions = existing.Exemptions
	}
	rows := exemptRows(exemptions)
	blockingLines := 0
	for _, group := range groups {
		if groupExempt(group, rows) < 0 {
			blockingLines += group.duplicateLines
		}
	}

	groupsNode := jsonObject()
	sorted := append([]*dupGroup(nil), groups...)
	sort.SliceStable(sorted, func(i, j int) bool { return sorted[i].memberKey < sorted[j].memberKey })
	for _, group := range sorted {
		members := jsonArray()
		for _, key := range group.memberKeys {
			members.append(jsonString(key))
		}
		entry := jsonObject()
		entry.set("members", members)
		entry.set("count", jsonNumber(group.count))
		entry.set("lines", jsonNumber(group.lines))
		entry.set("files", jsonNumber(group.fileCount))
		entry.set("exact", jsonNumber(group.exactCount))
		entry.set("shape", jsonString(group.fingerprint))
		groupsNode.set(group.memberKey, entry)
	}

	policy := jsonObject()
	policy.set("scope", jsonString("tests/**/*.cs minus bin/, obj/ and tests/Harbor.Architecture.Tests"))
	policy.set("min_body_lines", jsonNumber(minLines))
	policy.set("min_members", jsonNumber(minMembers))
	policy.set("min_similarity", jsonFloat(minSimilarity))
	policy.set("normalisation", jsonString(
		"comments dropped; string literals -> S; char literals -> C; "+
			"numeric literals -> N; identifiers -> I; whitespace collapsed"))

	censusNode := jsonObject()
	censusNode.set("files", jsonNumber(scan.files))
	censusNode.set("lines", jsonNumber(scan.lines))
	censusNode.set("methods", jsonNumber(scan.methods))
	censusNode.set("test_methods", jsonNumber(scan.testMethods))

	metrics := census(groups, scan, 0)
	metricsNode := jsonObject()
	metricsNode.set("groups", jsonNumber(metrics.groups))
	metricsNode.set("intra_file_groups", jsonNumber(metrics.intraFileGroups))
	metricsNode.set("cross_file_groups", jsonNumber(metrics.crossFileGroups))
	metricsNode.set("exact_groups", jsonNumber(metrics.exactGroups))
	metricsNode.set("duplicate_lines", jsonNumber(metrics.duplicateLines))
	metricsNode.set("group_lines", jsonNumber(metrics.groupLines))
	metricsNode.set("shape_coincidences", jsonNumber(metrics.shapeCoincidences))
	metricsNode.set("files", jsonNumber(metrics.files))
	metricsNode.set("lines", jsonNumber(metrics.lines))
	metricsNode.set("methods", jsonNumber(metrics.methods))
	metricsNode.set("test_methods", jsonNumber(metrics.testMethods))

	exemptionsNode := jsonArray()
	for _, entry := range exemptions {
		paths := jsonArray()
		for _, path := range entry.Paths {
			paths.append(jsonString(path))
		}
		one := jsonObject()
		one.set("paths", paths)
		one.set("reason", jsonString(entry.Reason))
		one.set("expires", jsonString(entry.Expires))
		exemptionsNode.append(one)
	}

	payload := jsonObject()
	payload.set("version", jsonNumber(1))
	payload.set("generator", jsonString("tools/bin/harbor-gates-linux-x64 check-test-dup"))
	payload.set("policy", policy)
	payload.set("census", censusNode)
	payload.set("metrics", metricsNode)
	payload.set("blocking_duplicate_lines", jsonNumber(blockingLines))
	payload.set("groups", groupsNode)
	payload.set("exemptions", exemptionsNode)

	path := baselinePath(root)
	if err := os.WriteFile(path, []byte(payload.render(0)+"\n"), 0o644); err != nil {
		return "", err
	}
	return path, nil
}

// pyFloatText renders a float the way python's json.dump writes it.
func pyFloatText(value float64) string {
	return pyfmt.Repr(value)
}

// exemptRows flattens the exemption list into (patterns, reason, expires)
// rows.
func exemptRows(exemptions []exemption) []exemptRow {
	var rows []exemptRow
	for _, entry := range exemptions {
		rows = append(rows, exemptRow{
			patterns: entry.Paths,
			reason:   strings.TrimSpace(entry.Reason),
			expires:  strings.TrimSpace(entry.Expires),
		})
	}
	return rows
}

// exemptRow is one exemption, flattened.
type exemptRow struct {
	patterns []string
	reason   string
	expires  string
}

// groupExempt is the index of the exemption covering this group, or -1.
//
// An exemption covers a group only when EVERY member of the group is
// inside it: a duplicate that reaches outside the exempted zone is still a
// duplicate of code that is not being migrated.
func groupExempt(group *dupGroup, rows []exemptRow) int {
	for index, row := range rows {
		if len(row.patterns) == 0 {
			continue
		}
		covered := true
		for _, member := range group.members {
			matched := false
			for _, pattern := range row.patterns {
				if matchGlob(pattern, member.path) {
					matched = true
					break
				}
			}
			if !matched {
				covered = false
				break
			}
		}
		if covered {
			return index
		}
	}
	return -1
}

// matchGlob is python's glob match for the patterns the baseline uses.
//
// A pattern that ends in "/**" covers the directory and everything under it;
// otherwise `*` matches within one path segment, `?` matches one character.
func matchGlob(pattern string, path string) bool {
	if strings.HasSuffix(pattern, "/**") {
		prefix := strings.TrimSuffix(pattern, "/**")
		return path == prefix || strings.HasPrefix(path, prefix+"/")
	}
	if strings.ContainsAny(pattern, "*?") {
		return globToRegex(pattern).MatchString(path)
	}
	return path == pattern
}

// globToRegex turns a glob into an anchored regular expression.
func globToRegex(pattern string) *regexp.Regexp {
	var out strings.Builder
	out.WriteString("^")
	for i := 0; i < len(pattern); i++ {
		ch := pattern[i]
		switch ch {
		case '*':
			if strings.HasPrefix(pattern[i:], "**") {
				out.WriteString(".*")
				i++
				continue
			}
			out.WriteString("[^/]*")
		case '?':
			out.WriteString("[^/]")
		default:
			out.WriteString(regexp.QuoteMeta(string(ch)))
		}
	}
	out.WriteString("$")
	return regexp.MustCompile(out.String())
}
