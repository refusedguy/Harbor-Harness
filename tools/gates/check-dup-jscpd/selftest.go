package dupjscpd

// selftest.go -- the failure path, proved on fixtures.
//
// The fixtures are the rules one by one: a clean report passes, an empty
// scan fails the floor, a narrowed scan fails the floor, growth fails and
// stays advisory when the rule is off, a cleaner tree warns and does not
// fail, an empty scan does not get to say the tree got cleaner, the new
// clone count is advisory by default, a moved config or a moved tool version
// is not comparable, and a corrupted download is refused before the tool is
// run.
//
// A report that measured nothing must fail, a report that grew must fail, a
// clean report must pass, and a corrupted download must be refused.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
)

// fakeReport is a minimal report for a fixture.
func fakeReport(files int, clones int, duplicatedLines int, newClones ...int) reportFile {
	newCloneCount := 0
	if len(newClones) > 0 {
		newCloneCount = newClones[0]
	}
	var report reportFile
	report.Statistics.Total.Sources = float64(files)
	report.Statistics.Total.Lines = 1000
	report.Statistics.Total.Tokens = 5000
	report.Statistics.Total.Clones = float64(clones)
	report.Statistics.Total.DuplicatedLines = float64(duplicatedLines)
	report.Statistics.Total.DuplicatedTokens = float64(duplicatedLines * 5)
	report.Statistics.Total.Percentage = 2.0
	report.Statistics.Total.PercentageTokens = 2.0
	report.Statistics.Total.NewClones = float64(newCloneCount)
	report.Statistics.Total.NewDuplicatedLines = float64(newCloneCount * 10)
	return report
}

// fixtureBaseline is the baseline the fixtures compare against.
func fixtureBaseline() baselineFile {
	var baseline baselineFile
	baseline.Tool = baselineTool{Name: toolName, Version: toolVersion}
	baseline.Metrics = baselineMetric{Clones: 286, DuplicatedLines: 8353}
	baseline.ConfigFingerprint = "abc123"
	baseline.Floors = orderedMap{
		keys: []string{"files", "duplicated_lines"},
		values: map[string]json.RawMessage{
			"files":            json.RawMessage("1700"),
			"duplicated_lines": json.RawMessage("8353"),
		},
	}
	return baseline
}

func selfTestRun() int {
	var failures []string

	expect := func(condition bool, message string) {
		if !condition {
			failures = append(failures, message)
		}
	}

	baseline := fixtureBaseline()

	// 1. a clean report passes.
	findings, warnings, result := Analyse(fakeReport(1971, 286, 8353), baseline, 0, true, -1, "abc123")
	expect(len(findings) == 0, fmt.Sprintf("a clean report must pass, got %v", findings))
	expect(len(warnings) == 0, fmt.Sprintf("a clean report must not warn, got %v", warnings))
	expect(result.files == 1971, "the summary must carry the file count")

	// 2. an empty scan fails the floor. This is the load-bearing fixture:
	//    a scan that measured nothing must not be green.
	findings, _, _ = Analyse(fakeReport(0, 0, 0), baseline, 0, true, -1, "abc123")
	expect(hasPrefix(findings, "DUP-FLOOR"), fmt.Sprintf("an empty scan must fail the floor, got %v", findings))

	// 3. a narrowed scan fails the floor too.
	findings, _, _ = Analyse(fakeReport(12, 1, 10), baseline, 0, true, -1, "abc123")
	expect(hasPrefix(findings, "DUP-FLOOR"), fmt.Sprintf("a narrowed scan must fail the floor, got %v", findings))

	// 4. growth fails, and only when the growth rule is on.
	findings, warnings, _ = Analyse(fakeReport(1971, 300, 8400), baseline, 0, true, -1, "abc123")
	expect(hasPrefix(findings, "DUP-GROWTH"), fmt.Sprintf("growth must fail the gate, got %v", findings))
	findings, warnings, _ = Analyse(fakeReport(1971, 300, 8400), baseline, 0, false, -1, "abc123")
	expect(!hasPrefix(findings, "DUP-GROWTH"), fmt.Sprintf("growth must stay advisory when the rule is off, got %v", findings))
	expect(hasPrefix(warnings, "DUP-GROWTH"), fmt.Sprintf("an advisory growth must be reported, got %v", warnings))

	// 5. a cleaner tree warns and does not fail.
	findings, warnings, _ = Analyse(fakeReport(1971, 280, 8000), baseline, 0, true, -1, "abc123")
	expect(len(findings) == 0, fmt.Sprintf("a cleaner tree must not fail, got %v", findings))
	expect(hasPrefix(warnings, "DUP-BASELINE-STALE"), fmt.Sprintf("a cleaner tree must warn about the stale baseline, got %v", warnings))

	// 5b. a scan that measured nothing does not get to say the tree got
	//     cleaner: it measured nothing, and its total says nothing.
	findings, warnings, _ = Analyse(fakeReport(0, 0, 0), baseline, 0, true, -1, "abc123")
	expect(!hasPrefix(warnings, "DUP-BASELINE-STALE"), fmt.Sprintf("an empty scan must not claim the tree got cleaner, got %v", warnings))

	// 6. the new-clone count is only blocking when it is asked for.
	findings, _, _ = Analyse(fakeReport(1971, 290, 8353, 4), baseline, 0, true, -1, "abc123")
	expect(len(findings) == 0, fmt.Sprintf("the new-clone count must be advisory by default, got %v", findings))
	findings, _, _ = Analyse(fakeReport(1971, 290, 8353, 4), baseline, 0, true, 0, "abc123")
	expect(hasPrefix(findings, "DUP-NEW-CLONES"), fmt.Sprintf("--fail-on-new-clones must fail on a new clone, got %v", findings))

	// 7. a moved config or a moved tool version is not comparable.
	findings, _, _ = Analyse(fakeReport(1971, 286, 8353), baseline, 0, true, -1, "deadbeef")
	expect(hasPrefix(findings, "DUP-MEASUREMENT"), fmt.Sprintf("a moved config must fail the comparison, got %v", findings))
	moved := fixtureBaseline()
	moved.Tool.Version = "5.0.0"
	findings, _, _ = Analyse(fakeReport(1971, 286, 8353), moved, 0, true, -1, "abc123")
	expect(hasPrefix(findings, "DUP-MEASUREMENT"), fmt.Sprintf("a moved tool version must fail the comparison, got %v", findings))

	// 8. a corrupted download is refused before the tool is run.
	tmp, err := os.MkdirTemp("", "jscpd-gate-")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	defer os.RemoveAll(tmp)
	archive := filepath.Join(tmp, "fake.tar.gz")
	if err := os.WriteFile(archive, []byte("not a tarball"), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	if err := verifyArchive(archive, "0000000000000000000000000000000000000000000000000000000000000000"); err == nil {
		failures = append(failures, "a corrupted archive must be refused")
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Printf("  FAIL %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("  ok: the empty scan, the narrowed scan, the growth, the stale baseline, the new-clone count, the moved config and the corrupted download all behave")
	return gate.Green
}

// hasPrefix reports whether any finding starts with the given prefix.
func hasPrefix(findings []string, prefix string) bool {
	for _, finding := range findings {
		if len(finding) >= len(prefix) && finding[:len(prefix)] == prefix {
			return true
		}
	}
	return false
}
