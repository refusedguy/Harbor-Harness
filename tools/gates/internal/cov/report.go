package cov

// report.go -- the three renderings the reporter writes.
//
// There is no single percentage in any of them, on purpose. The tables are
// per project and per file, and the scope is written into the header, so a
// reader cannot mistake a number for the coverage of the whole tree.

import (
	"fmt"
	"sort"
	"strings"
)

// RenderReport is the per-file report.
//
// There is no single percentage in here, on purpose. The tables are per
// project and per file, and the scope is written into the header, so a
// reader cannot mistake a number for the coverage of the whole tree.
func RenderReport(model *Model, top int) string {
	var lines []string
	lines = append(lines, "## Coverage per file (report-only, issue #99 and #1753)")
	lines = append(lines, "")
	product := model.ProductFiles()
	covered := model.CoveredProductFiles()
	holes := model.Holes()
	accepted := model.AcceptedFiles()
	unaccepted := model.UnacceptedFiles()
	lines = append(lines,
		"_No global percentage: the numbers below are per file and per project, "+
			"and the scope is the 20 core+platform unit suites the nightly lane runs "+
			"(ui/e2e/load are excluded). The suites OVERLAP on the shared `src/` "+
			"assemblies, so the file union is what is counted, never a sum of the "+
			"suites._")
	lines = append(lines, "")

	// The per-project table.
	projects := map[string][]string{}
	for _, path := range product {
		name := ProjectOf(path)
		projects[name] = append(projects[name], path)
	}
	lines = append(lines, "| project | files | covered | never ran | not loaded | no code to cover |")
	lines = append(lines, "|---|---|---|---|---|---|")
	for _, name := range sortedKeys(projects) {
		members := projects[name]
		neverRan := 0
		notLoaded := 0
		noCode := 0
		for _, path := range members {
			switch model.StatusOf(path) {
			case "never-ran":
				neverRan++
			case "not-loaded":
				notLoaded++
			case "not-instrumented":
				noCode++
			}
		}
		lines = append(lines, fmt.Sprintf(
			"| `%s` | %d | %d | %d | %d | %d |",
			name, len(members), len(members)-neverRan-notLoaded-noCode,
			neverRan, notLoaded, noCode))
	}
	neverRanTotal := 0
	notLoadedTotal := 0
	for _, path := range product {
		switch model.StatusOf(path) {
		case "never-ran":
			neverRanTotal++
		case "not-loaded":
			notLoadedTotal++
		}
	}
	noCodeTotal := len(model.NotInstrumented)
	lines = append(lines, fmt.Sprintf(
		"| **total** | **%d** | **%d** | **%d** | **%d** | **%d** |",
		len(product), len(covered), neverRanTotal, notLoadedTotal, noCodeTotal))
	lines = append(lines, "")

	// The registry split: what is consciously uncovered and what is not.
	lines = append(lines, fmt.Sprintf(
		"**Holes: %d of %d product files** (%.1f%%) — "+
			"%d the measurement saw and never ran, "+
			"%d in a project this lane does not load. "+
			"%d are in the registry with a reason, "+
			"%d are not.",
		len(holes), len(product), 100.0*float64(len(holes))/float64(len(product)),
		neverRanTotal, notLoadedTotal, len(accepted), len(unaccepted)))
	lines = append(lines, "")

	// The dead-code candidates: the files the measurement SAW and that ran
	// not one line.
	var candidates []string
	for _, path := range holes {
		if model.StatusOf(path) == "never-ran" {
			candidates = append(candidates, path)
		}
	}
	lines = append(lines, fmt.Sprintf(
		"### The %d files that never ran a line (deletion candidates)", len(candidates)))
	lines = append(lines, "")
	if len(candidates) == 0 {
		lines = append(lines, "_None: every file the measurement touched ran at least one line._")
	} else {
		lines = append(lines, "| file | lines | uncovered | in the registry |")
		lines = append(lines, "|---|---|---|---|")
		sort.SliceStable(candidates, func(i, j int) bool {
			left := len(model.Files[candidates[i]].UncoveredLines())
			right := len(model.Files[candidates[j]].UncoveredLines())
			if left != right {
				return left > right
			}
			return candidates[i] < candidates[j]
		})
		for _, path := range candidates[:min(top, len(candidates))] {
			entry := model.Files[path]
			row := model.RowFor(path)
			kind := "**NO**"
			if row != nil {
				kind = "`" + row.Kind + "`"
			}
			lines = append(lines, fmt.Sprintf("| `%s` | %d | %d | %s |",
				path, entry.Total(), len(entry.UncoveredLines()), kind))
		}
		if len(candidates) > top {
			lines = append(lines, fmt.Sprintf("| … %d more | | | |", len(candidates)-top))
		}
	}
	lines = append(lines, "")

	// The projects the lane does not load.
	var notLoaded []string
	for _, path := range holes {
		if model.StatusOf(path) == "not-loaded" {
			notLoaded = append(notLoaded, path)
		}
	}
	lines = append(lines, fmt.Sprintf(
		"### The %d files in projects this lane does not load", len(notLoaded)))
	lines = append(lines, "")
	if len(notLoaded) > 0 {
		sorted := append([]string{}, notLoaded...)
		sort.Strings(sorted)
		for _, path := range sorted[:min(top, len(sorted))] {
			row := model.RowFor(path)
			if row != nil {
				lines = append(lines, fmt.Sprintf("- `%s` — `%s`", path, row.Kind))
			} else {
				lines = append(lines, fmt.Sprintf("- `%s` — **NO REGISTRY ROW**", path))
			}
		}
		if len(notLoaded) > top {
			lines = append(lines, fmt.Sprintf("- … %d more", len(notLoaded)-top))
		}
	} else {
		lines = append(lines, "_None: every product file belongs to a project this lane loads._")
	}
	lines = append(lines, "")

	// The files with no code to cover, and the cross-check of that answer.
	lines = append(lines, fmt.Sprintf(
		"_%d product file(s) carry no code to cover: the "+
			"engine instrumented their module and reported no line for them, which is "+
			"what an interface, an enum, a global-using file or a source-generated "+
			"context looks like._", len(model.NotInstrumented)))
	if len(model.NotInstrumentedWithCode) > 0 {
		lines = append(lines, "")
		shown := model.NotInstrumentedWithCode
		if len(shown) > 10 {
			shown = shown[:10]
		}
		quoted := make([]string, 0, len(shown))
		for _, path := range shown {
			quoted = append(quoted, "`"+path+"`")
		}
		lines = append(lines, fmt.Sprintf(
			"**%d of them carry code anyway** — the engine did not instrument them, "+
				"so the measurement is blind there: %s",
			len(model.NotInstrumentedWithCode), strings.Join(quoted, ", ")))
	}
	lines = append(lines, "")

	if len(model.Unmapped) > 0 {
		shown := model.Unmapped
		if len(shown) > 5 {
			shown = shown[:5]
		}
		quoted := make([]string, 0, len(shown))
		for _, path := range shown {
			quoted = append(quoted, "`"+path+"`")
		}
		lines = append(lines, fmt.Sprintf(
			"_%d path(s) in the reports could not be mapped to "+
				"the tree and were left out: %s._", len(model.Unmapped),
			strings.Join(quoted, ", ")))
		lines = append(lines, "")
	}
	if len(model.Unreadable) > 0 {
		quoted := make([]string, 0, len(model.Unreadable))
		for _, path := range model.Unreadable {
			quoted = append(quoted, "`"+path+"`")
		}
		lines = append(lines, fmt.Sprintf(
			"**%d report(s) could not be read**: %s. The measurement is PARTIAL: "+
				"the files those suites would have covered look uncovered, so the "+
				"registry is compared only in the direction a partial measurement supports.",
			len(model.Unreadable), strings.Join(quoted, ", ")))
		lines = append(lines, "")
	}
	return strings.Join(lines, "\n")
}

// RenderFileTable is the FULL per-file table, one row per product file.
//
// This is the artifact, not the job summary: it is the thing a reader opens
// to see which file is uncovered and by how much.
func RenderFileTable(model *Model) string {
	lines := []string{
		"| file | lines | covered | share | uncovered lines | suites |",
		"|---|---|---|---|---|---|",
	}
	for _, path := range model.ProductFiles() {
		entry, ok := model.Files[path]
		if !ok {
			lines = append(lines, fmt.Sprintf("| `%s` | — | — | — | — | not measured |", path))
			continue
		}
		share := 0.0
		if entry.Total() != 0 {
			share = 100.0 * float64(entry.Covered()) / float64(entry.Total())
		}
		uncovered := entry.UncoveredLines()
		numbers := make([]string, 0, 40)
		for _, number := range uncovered[:min(40, len(uncovered))] {
			numbers = append(numbers, fmt.Sprintf("%d", number))
		}
		text := strings.Join(numbers, ", ")
		if len(uncovered) > 40 {
			text += fmt.Sprintf(", … (+%d)", len(uncovered)-40)
		}
		if text == "" {
			text = "—"
		}
		lines = append(lines, fmt.Sprintf("| `%s` | %d | %d | %.1f%% | %s | %d |",
			path, entry.Total(), entry.Covered(), share, text, len(entry.Suites)))
	}
	return strings.Join(lines, "\n") + "\n"
}

// RenderUncoveredLocations is every uncovered line, as `file:line` — the
// deletion-candidate list.
func RenderUncoveredLocations(model *Model) string {
	var lines []string
	for _, path := range model.ProductFiles() {
		entry, ok := model.Files[path]
		if !ok {
			continue
		}
		for _, number := range entry.UncoveredLines() {
			lines = append(lines, fmt.Sprintf("%s:%d", path, number))
		}
	}
	if len(lines) == 0 {
		return ""
	}
	return strings.Join(lines, "\n") + "\n"
}

// sortedKeys is the keys of a map, in sorted order.
func sortedKeys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// min is the smaller of two ints.
func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
