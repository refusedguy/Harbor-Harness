package dupjscpd

// analyze.go -- the report, the baseline and the rules over them.

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"

	"harbor.local/gates/internal/pyfmt"
)

// reportFile is the jscpd JSON report, as far as the gate reads it.
type reportFile struct {
	Statistics struct {
		Total struct {
			Sources            float64 `json:"sources"`
			Lines              float64 `json:"lines"`
			Tokens             float64 `json:"tokens"`
			Clones             float64 `json:"clones"`
			DuplicatedLines    float64 `json:"duplicatedLines"`
			DuplicatedTokens   float64 `json:"duplicatedTokens"`
			Percentage         float64 `json:"percentage"`
			PercentageTokens   float64 `json:"percentageTokens"`
			NewClones          float64 `json:"newClones"`
			NewDuplicatedLines float64 `json:"newDuplicatedLines"`
		} `json:"total"`
	} `json:"statistics"`
}

// totals is the headline numbers of one report.
type totals struct {
	files              int
	lines              int
	tokens             int
	clones             int
	duplicatedLines    int
	duplicatedTokens   int
	percentage         float64
	percentageTokens   float64
	newClones          int
	newDuplicatedLines int
}

// totalsOf reads the totals out of a report.
func totalsOf(report reportFile) totals {
	total := report.Statistics.Total
	return totals{
		files:              int(total.Sources),
		lines:              int(total.Lines),
		tokens:             int(total.Tokens),
		clones:             int(total.Clones),
		duplicatedLines:    int(total.DuplicatedLines),
		duplicatedTokens:   int(total.DuplicatedTokens),
		percentage:         pyfmt.Round(total.Percentage, 2),
		percentageTokens:   pyfmt.Round(total.PercentageTokens, 2),
		newClones:          int(total.NewClones),
		newDuplicatedLines: int(total.NewDuplicatedLines),
	}
}

// readReport reads the JSON report the tool wrote.
func readReport(path string) (reportFile, error) {
	var report reportFile
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return report, fmt.Errorf("  no report at %s; run with --run first", path)
		}
		return report, fmt.Errorf("  no report at %s; run with --run first", path)
	}
	if err := json.Unmarshal(data, &report); err != nil {
		return report, fmt.Errorf("  %s is not JSON: %s", path, jsonError(err))
	}
	var probe map[string]json.RawMessage
	if err := json.Unmarshal(data, &probe); err != nil {
		return report, fmt.Errorf("  %s is not a %s report", path, toolName)
	}
	if _, ok := probe["statistics"]; !ok {
		return report, fmt.Errorf("  %s is not a %s report", path, toolName)
	}
	return report, nil
}

// jsonError renders a JSON error the way python's json module does: the
// message and the position.
func jsonError(err error) string {
	offset := -1
	switch typed := err.(type) {
	case *json.SyntaxError:
		offset = int(typed.Offset)
	case *json.UnmarshalTypeError:
		offset = int(typed.Offset)
	}
	if offset < 0 {
		return err.Error()
	}
	line := 1
	column := 1
	return fmt.Sprintf("Expecting value: line %d column %d (char %d)", line, column, offset)
}

// orderedMap is a JSON object that remembers the order of its keys.
//
// The baseline's "floors" is copied from the committed file when the gate
// rewrites it, and the ORDER of the keys is part of the file: a baseline
// that reorders its rows on every rewrite is a baseline nobody can diff.
type orderedMap struct {
	keys   []string
	values map[string]json.RawMessage
}

// UnmarshalJSON reads an object, keeping the order of its keys.
func (m *orderedMap) UnmarshalJSON(data []byte) error {
	m.keys = nil
	m.values = map[string]json.RawMessage{}
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
		if _, seen := m.values[key]; !seen {
			m.keys = append(m.keys, key)
		}
		m.values[key] = raw
	}
	return nil
}

// MarshalJSON writes the object, keeping the order of its keys.
func (m orderedMap) MarshalJSON() ([]byte, error) {
	var buffer bytes.Buffer
	buffer.WriteByte('{')
	for index, key := range m.keys {
		if index > 0 {
			buffer.WriteByte(',')
		}
		encoded, err := json.Marshal(key)
		if err != nil {
			return nil, err
		}
		buffer.Write(encoded)
		buffer.WriteByte(':')
		buffer.Write(m.values[key])
	}
	buffer.WriteByte('}')
	return buffer.Bytes(), nil
}

// getInt returns an integer value of the map, or nil when the key is absent.
func (m orderedMap) getInt(key string) *int {
	raw, ok := m.values[key]
	if !ok {
		return nil
	}
	var value int
	if err := json.Unmarshal(raw, &value); err != nil {
		return nil
	}
	return &value
}

// baselineFile is the committed baseline.
type baselineFile struct {
	Version           int            `json:"version"`
	Generator         string         `json:"generator"`
	Tool              baselineTool   `json:"tool"`
	Policy            baselinePolicy `json:"policy"`
	ConfigFingerprint string         `json:"config_fingerprint"`
	Census            baselineCensus `json:"census"`
	Metrics           baselineMetric `json:"metrics"`
	Floors            orderedMap     `json:"floors"`

	toolVersion string
	empty       bool
}

// baselineTool names the tool the baseline was measured with.
type baselineTool struct {
	Name    string `json:"name"`
	Version string `json:"version"`
	License string `json:"license"`
}

// baselinePolicy is the scope the baseline was measured over.
type baselinePolicy struct {
	Scope     string `json:"scope"`
	MinTokens int    `json:"min_tokens"`
	MinLines  int    `json:"min_lines"`
	Mode      string `json:"mode"`
	Format    string `json:"format"`
}

// baselineCensus is what the scan measured.
type baselineCensus struct {
	Files  int `json:"files"`
	Lines  int `json:"lines"`
	Tokens int `json:"tokens"`
}

// baselineMetric is what the scan found.
type baselineMetric struct {
	Clones           int     `json:"clones"`
	DuplicatedLines  int     `json:"duplicated_lines"`
	DuplicatedTokens int     `json:"duplicated_tokens"`
	Percentage       float64 `json:"percentage"`
}

// readBaseline reads the committed baseline.
func readBaseline(path string) (baselineFile, error) {
	var baseline baselineFile
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			baseline.empty = true
			return baseline, nil
		}
		return baseline, fmt.Errorf("  %s is not JSON: %s", path, err.Error())
	}
	if err := json.Unmarshal(data, &baseline); err != nil {
		return baseline, fmt.Errorf("  %s is not JSON: %s", path, jsonError(err))
	}
	baseline.toolVersion = baseline.Tool.Version
	var probe map[string]json.RawMessage
	if err := json.Unmarshal(data, &probe); err != nil || len(probe) == 0 {
		baseline.empty = true
	}
	return baseline, nil
}

// configFingerprint is the hash of the tool's config, first 16 hex digits.
func configFingerprint(root string) string {
	data, err := os.ReadFile(filepath.Join(root, configName))
	if err != nil {
		return ""
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])[:16]
}

// summary is the numbers the gate prints.
type summary struct {
	files                int
	lines                int
	tokens               int
	clones               int
	duplicatedLines      int
	duplicatedTokens     int
	percentage           float64
	percentageTokens     float64
	newClones            int
	newDuplicatedLines   int
	floorFiles           int
	floorDuplicatedLines *int
}

// Analyse returns the findings, the warnings and the summary for one report.
func Analyse(report reportFile, baseline baselineFile, minFiles int, failOnGrowth bool, failOnNewClones int, fingerprint string) ([]string, []string, summary) {
	measured := totalsOf(report)
	var findings []string
	var warnings []string

	floorFiles := 0
	if value := baseline.Floors.getInt("files"); value != nil {
		floorFiles = *value
	}
	if minFiles != 0 {
		floorFiles = minFiles
	}
	if floorFiles != 0 && measured.files < floorFiles {
		findings = append(findings, fmt.Sprintf(
			"DUP-FLOOR files %d < floor %d — the scan analysed less of the tree than it should; a scan that measured nothing cannot report that everything is fine",
			measured.files, floorFiles))
	}

	floorLines := baseline.Floors.getInt("duplicated_lines")
	if floorLines != nil {
		if measured.duplicatedLines > *floorLines {
			message := fmt.Sprintf(
				"DUP-GROWTH duplicated lines %d > baseline %d — the tree grew more duplication than the baseline allows",
				measured.duplicatedLines, *floorLines)
			if failOnGrowth {
				findings = append(findings, message)
			} else {
				warnings = append(warnings, message+" (advisory: --fail-on-growth is off)")
			}
		} else if measured.duplicatedLines < *floorLines && len(findings) == 0 {
			// The "the tree got cleaner" reading is only true of a scan that
			// measured the tree. A narrowed scan measured nothing, and its
			// smaller total says nothing about the tree.
			warnings = append(warnings, fmt.Sprintf(
				"DUP-BASELINE-STALE duplicated lines %d < baseline %d — the tree got cleaner; lower the baseline with `--update` when the programme lands",
				measured.duplicatedLines, *floorLines))
		}
	}

	if !baseline.empty {
		recordedVersion := baseline.Tool.Version
		if recordedVersion != "" && recordedVersion != toolVersion {
			findings = append(findings, fmt.Sprintf(
				"DUP-MEASUREMENT the tool moved from %s to %s — the two measurements are not comparable; re-measure with `--update`",
				recordedVersion, toolVersion))
		}
		recordedFingerprint := baseline.ConfigFingerprint
		if recordedFingerprint != "" && fingerprint != "" && recordedFingerprint != fingerprint {
			findings = append(findings, fmt.Sprintf(
				"DUP-MEASUREMENT the config moved since the baseline was written (%s -> %s) — the two measurements are not comparable; re-measure with `--update`",
				recordedFingerprint, fingerprint))
		}
	}

	if failOnNewClones >= 0 && measured.newClones > failOnNewClones {
		findings = append(findings, fmt.Sprintf(
			"DUP-NEW-CLONES new clones %d > %d — the clone count relative to the base ref grew. NOTE: this number is edit-sensitive (an edit of an existing duplicate counts as new); read it as a pointer, not as a verdict",
			measured.newClones, failOnNewClones))
	}

	result := summary{
		files:                measured.files,
		lines:                measured.lines,
		tokens:               measured.tokens,
		clones:               measured.clones,
		duplicatedLines:      measured.duplicatedLines,
		duplicatedTokens:     measured.duplicatedTokens,
		percentage:           measured.percentage,
		percentageTokens:     measured.percentageTokens,
		newClones:            measured.newClones,
		newDuplicatedLines:   measured.newDuplicatedLines,
		floorFiles:           floorFiles,
		floorDuplicatedLines: floorLines,
	}
	return findings, warnings, result
}

// printSummary prints the headline numbers.
func printSummary(result summary) {
	fmt.Println()
	fmt.Printf("  files analysed    %d (floor %d)\n", result.files, result.floorFiles)
	fmt.Printf("  lines             %d\n", result.lines)
	fmt.Printf("  clones            %d\n", result.clones)
	if result.floorDuplicatedLines == nil {
		fmt.Printf("  duplicated lines  %d\n", result.duplicatedLines)
	} else {
		fmt.Printf("  duplicated lines  %d (baseline %d)\n", result.duplicatedLines, *result.floorDuplicatedLines)
	}
	fmt.Printf("  duplication       %s%% of lines, %s%% of tokens\n",
		pyfmt.Repr(result.percentage), pyfmt.Repr(result.percentageTokens))
	if result.newClones != 0 {
		fmt.Printf("  new clones        %d (%d lines) vs the base ref\n",
			result.newClones, result.newDuplicatedLines)
	}
	fmt.Println()
}

// buildBaseline rewrites the baseline from a report.
func buildBaseline(report reportFile, root string) (baselineFile, error) {
	measured := totalsOf(report)
	existing, err := readBaseline(filepath.Join(root, baselineName))
	if err != nil {
		return baselineFile{}, err
	}
	floors := existing.Floors
	if floors.values == nil {
		floors.values = map[string]json.RawMessage{}
	}
	// The file floor is a HAND-SET ratchet: it is lowered in the same diff
	// as a legitimate removal, where a reviewer sees it, and `--update` does
	// not touch it. The duplicated-lines floor is the reviewed measurement,
	// and `--update` rewrites it.
	if _, ok := floors.values["files"]; !ok {
		raw, _ := json.Marshal(measured.files)
		floors.keys = append(floors.keys, "files")
		floors.values["files"] = raw
	}
	raw, _ := json.Marshal(measured.duplicatedLines)
	floors.keys = removeKey(floors.keys, "duplicated_lines")
	floors.keys = append(floors.keys, "duplicated_lines")
	floors.values["duplicated_lines"] = raw

	return baselineFile{
		Version:   1,
		Generator: "tools/bin/harbor-gates-linux-x64 check-dup-jscpd",
		Tool: baselineTool{
			Name:    toolName,
			Version: toolVersion,
			License: toolLicense,
		},
		Policy: baselinePolicy{
			Scope: "the repository root, **/*.cs, minus bin/, obj/, contrib/, " +
				"samples/, tools/, build/, analyzers/ and " +
				"tests/Harbor.Architecture.Tests/",
			MinTokens: 100,
			MinLines:  6,
			Mode:      "weak",
			Format:    "csharp",
		},
		ConfigFingerprint: configFingerprint(root),
		Census: baselineCensus{
			Files:  measured.files,
			Lines:  measured.lines,
			Tokens: measured.tokens,
		},
		Metrics: baselineMetric{
			Clones:           measured.clones,
			DuplicatedLines:  measured.duplicatedLines,
			DuplicatedTokens: measured.duplicatedTokens,
			Percentage:       measured.percentage,
		},
		Floors: floors,
	}, nil
}

// removeKey drops a key from an ordered key list.
func removeKey(keys []string, key string) []string {
	out := keys[:0]
	for _, name := range keys {
		if name != key {
			out = append(out, name)
		}
	}
	return out
}

// JSON renders the baseline the way the python original wrote it:
// json.dumps(..., indent=2, ensure_ascii=False) plus a newline.
func (b baselineFile) JSON() (string, error) {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(b); err != nil {
		return "", err
	}
	return buffer.String(), nil
}

// sortedKeys is a helper for the ordered maps.
func sortedKeys(m orderedMap) []string {
	keys := append([]string(nil), m.keys...)
	sort.Strings(keys)
	return keys
}
