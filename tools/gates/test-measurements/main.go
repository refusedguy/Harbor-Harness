// Package testmeasurements is the compiled port of tools/test-measurements.py.
//
// The gate renders the per-test measurements of a run — the values a test
// printed with Console.WriteLine — into the job summary.
//
// It is REPORTING, never a gate: it must not be able to fail a build. A
// missing, empty or corrupt TRX set exits 0 with a one-line note. The only
// non-zero exit is a bad invocation.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package testmeasurements

import (
	"fmt"
	"os"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// DEFAULT_MAX_ROWS is the number of rows the table shows.
//
// 200 keeps the summary scannable if a future test prints per-iteration. The
// cap is a backstop against log/summary explosion, not an expected operating
// point.
const DEFAULT_MAX_ROWS = 200

// MAX_CELL is the length of a cell. A measurement is a line a human reads in
// a table cell; longer is a debug dump.
const MAX_CELL = 200

// row is one measurement: the project, the class-qualified test, the text
// the test printed, and the duration the runner measured.
type row struct {
	project  string
	name     string
	text     string
	duration float64
	hasTime  bool
}

// Run is the gate entry point.
func Run(args []string) int {
	dirs := []string{}
	summary := ""
	maxRows := DEFAULT_MAX_ROWS
	selfTest := false
	seenDirs := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--summary":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "error: --summary needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			summary = value
		case "--max-rows":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "error: --max-rows needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			n, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "error: --max-rows needs a number")
				return gate.Broken
			}
			maxRows = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates test-measurements [dirs...] [--summary PATH] [--max-rows N] [--self-test]")
			return gate.Green
		default:
			if strings.HasPrefix(arg, "-") && arg != "-" {
				fmt.Fprintf(os.Stderr, "error: unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			dirs = append(dirs, arg)
			seenDirs = true
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if !seenDirs {
		dirs = []string{"TestResults"}
	}
	if maxRows < 1 {
		fmt.Fprintln(os.Stderr, "--max-rows must be >= 1")
		return gate.Broken
	}

	trxPaths := findTRX(dirs)
	if len(trxPaths) == 0 {
		fmt.Printf("test-measurements: no .trx found under %s — nothing to report\n",
			strings.Join(dirs, " "))
		return gate.Green
	}

	rows := collect(trxPaths)
	if len(rows) == 0 {
		fmt.Printf("test-measurements: %d TRX file(s), no test emitted output — nothing to report\n",
			len(trxPaths))
		return gate.Green
	}

	body := fmt.Sprintf("\n### Test measurements (%d)\n", len(rows))
	body += "_Values a test printed with `Console.WriteLine`, bound to the test that printed them. Source: the TRX artifacts of this run._"
	body += render(rows, maxRows)

	if summary != "" {
		if err := appendSummary(summary, body); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the summary:", err)
			return gate.Broken
		}
	}

	tests := map[string]bool{}
	for _, r := range rows {
		tests[r.project+"\x00"+r.name] = true
	}
	fmt.Printf("test-measurements: %d measurement(s) from %d test(s) across %d TRX file(s)",
		len(rows), len(tests), len(trxPaths))
	if summary != "" {
		fmt.Print(" -> job summary")
	}
	fmt.Println()
	return gate.Green
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// parseInt reads a decimal integer.
func parseInt(text string) (int, bool) {
	if text == "" {
		return 0, false
	}
	negative := false
	body := text
	if body[0] == '+' || body[0] == '-' {
		negative = body[0] == '-'
		body = body[1:]
	}
	if body == "" {
		return 0, false
	}
	value := 0
	for _, r := range body {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	if negative {
		value = -value
	}
	return value, true
}

// findTRX lists the TRX files the python original read, sorted.
//
// A target that is a FILE is taken as it is, whatever its name; a target
// that is a directory is walked for the files whose name ends in ".trx",
// case-insensitively.
func findTRX(targets []string) []string {
	var paths []string
	for _, target := range targets {
		info, err := os.Stat(target)
		if err == nil && !info.IsDir() {
			paths = append(paths, target)
			continue
		}
		for _, path := range gatekit.FindTRX([]string{target}, true) {
			paths = append(paths, path)
		}
	}
	sort.Strings(paths)
	return paths
}

// collect returns one row per measurement, in file order.
//
// A test's identity comes from <TestDefinitions> (which carries the class)
// and its text from the matching <Results> entry. The result element is
// authoritative for the text, so the definition map is consulted first and
// the bare method name is the fallback. The duration is the result element's
// own @duration — the cost of the test that printed the measurement, not of
// the assembly.
func collect(trxPaths []string) []row {
	var rows []row
	for _, path := range trxPaths {
		project := gatekit.SplitExt(gatekit.Basename(path))
		trx, err := gatekit.ParseTRX(path)
		if err != nil {
			fmt.Printf("  warn: unreadable TRX %s: %v\n", gatekit.Basename(path), err)
			continue
		}
		for _, result := range trx.Results {
			var chunks []string
			for _, chunk := range result.Output {
				if (chunk.Tag == "StdOut" || chunk.Tag == "StdErr") && chunk.Text != "" {
					chunks = append(chunks, chunk.Text)
				}
			}
			text := strings.TrimSpace(strings.Join(chunks, "\n"))
			if text == "" {
				continue
			}
			name, ok := trx.Owners[result.TestID]
			if !ok || name == "" {
				name = result.TestName
				if name == "" {
					name = "(unknown test)"
				}
			}
			duration, hasTime := gatekit.DurationSeconds(result.Duration)
			rows = append(rows, row{
				project:  project,
				name:     name,
				text:     cell(text),
				duration: duration,
				hasTime:  hasTime,
			})
		}
	}
	return rows
}

// cell renders one table cell.
func cell(text string) string {
	return gatekit.Cell(text, MAX_CELL, "(empty)")
}

// durationCell renders the duration column.
func durationCell(seconds float64, hasTime bool) string {
	if !hasTime {
		return "—"
	}
	if seconds < 1.0 {
		return fmt.Sprintf("%.0f ms", seconds*1000)
	}
	return fmt.Sprintf("%.2f s", seconds)
}

// render is the markdown table. Split by project so a reader sees which
// suite emitted.
func render(rows []row, maxRows int) string {
	var out []string
	shown := rows
	if len(shown) > maxRows {
		shown = shown[:maxRows]
	}
	for _, project := range firstSeen(shown) {
		var group []row
		for _, r := range shown {
			if r.project == project {
				group = append(group, r)
			}
		}
		out = append(out, "")
		out = append(out, fmt.Sprintf("<details><summary><b>%s</b> — %d measurement(s)</summary>",
			cell(project), len(group)))
		out = append(out, "")
		out = append(out, "| test | duration | measurement |")
		out = append(out, "|---|---|---|")
		for _, r := range group {
			out = append(out, fmt.Sprintf("| `%s` | %s | %s |", r.name, durationCell(r.duration, r.hasTime), r.text))
		}
		out = append(out, "")
		out = append(out, "</details>")
	}
	if len(rows) > maxRows {
		out = append(out, "")
		out = append(out, fmt.Sprintf("_%d further measurement(s) not shown (raise --max-rows; full text is in the test-results-* artifact)._",
			len(rows)-maxRows))
	}
	return strings.Join(out, "\n")
}

// firstSeen returns the distinct project names, in the order they first
// appear: the python original wrote dict.fromkeys(...).
func firstSeen(rows []row) []string {
	seen := map[string]bool{}
	var out []string
	for _, r := range rows {
		if !seen[r.project] {
			seen[r.project] = true
			out = append(out, r.project)
		}
	}
	return out
}

// appendSummary appends the body to the summary file.
func appendSummary(path string, text string) error {
	fh, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer fh.Close()
	_, err = fh.WriteString(text + "\n")
	return err
}
