// Package testskips is the compiled port of tools/test-skips.py.
//
// The gate renders the SKIPPED tests of a run, with the reason each one
// declared, into the job summary.
//
// It is REPORTING, never a gate: it must not be able to fail a build. A
// missing, empty or corrupt TRX set exits 0 with a one-line note. The only
// non-zero exit is a bad invocation.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package testskips

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// MAX_CELL is the length of a cell. A reason is a sentence a human reads;
// longer than this is a bug report pasted into a skip message, not a reason.
const MAX_CELL = 200

// DEFAULT_MAX_ROWS is the number of rows the table shows.
//
// The table is a list, not a report. Past this many rows the summary stops
// being scannable; the full set is in the test-results-* artifact.
const DEFAULT_MAX_ROWS = 100

// row is one skipped test: the project, the class-qualified name, and the
// reason the test declared.
type row struct {
	project string
	name    string
	reason  string
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
			fmt.Println("usage: harbor-gates test-skips [dirs...] [--summary PATH] [--max-rows N] [--self-test]")
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

	if maxRows <= 0 {
		fmt.Fprintln(os.Stderr, "error: --max-rows must be positive")
		return gate.Broken
	}

	trxPaths := findTRX(dirs)
	if len(trxPaths) == 0 {
		fmt.Println("no TRX files found — nothing to report")
		return gate.Green
	}

	rows := collect(trxPaths)
	text := render(rows, maxRows)
	fmt.Println(text)
	if summary != "" {
		if err := appendSummary(summary, text); err != nil {
			fmt.Fprintln(os.Stderr, "cannot write the summary:", err)
			return gate.Broken
		}
	}
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

// findTRX lists the TRX files the python original read.
//
// A target that is a FILE named .trx is taken as it is; a target that is a
// directory is walked, and the files of one directory are sorted.
func findTRX(targets []string) []string {
	var paths []string
	for _, target := range targets {
		info, err := os.Stat(target)
		if err == nil && !info.IsDir() {
			if strings.HasSuffix(target, ".trx") {
				paths = append(paths, target)
			}
			continue
		}
		walkSorted(target, &paths)
	}
	return paths
}

// walkSorted visits the .trx files under root: the files of one directory
// sorted, then the subdirectories in the order the filesystem lists them.
func walkSorted(root string, paths *[]string) {
	var files []string
	var subdirs []string
	for _, name := range gatekit.RawDirNames(root) {
		full := filepath.Join(root, name)
		info, err := os.Lstat(full)
		if err != nil {
			continue
		}
		if info.IsDir() {
			subdirs = append(subdirs, full)
			continue
		}
		if strings.HasSuffix(name, ".trx") {
			files = append(files, full)
		}
	}
	sort.Strings(files)
	*paths = append(*paths, files...)
	for _, sub := range subdirs {
		walkSorted(sub, paths)
	}
}

// collect returns one row per skipped test, in file order.
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
			if result.Outcome != "NotExecuted" {
				continue
			}
			name, ok := trx.Owners[result.TestID]
			if !ok || name == "" {
				name = result.TestName
				if name == "" {
					name = "(unknown test)"
				}
			}
			rows = append(rows, row{project: project, name: name, reason: cell(reasonOf(result))})
		}
	}
	return rows
}

// reasonOf is the skip reason the test declared.
//
// MTP records it in <Output><DebugTrace> as "Skipped: <reason>". The prefix
// is stripped: the reason is the sentence, not the marker. A skip with no
// DebugTrace has no reason — that is a hole in the house rule and the table
// says so.
func reasonOf(result gatekit.TRXResult) string {
	text := strings.TrimSpace(result.DebugTrace())
	const marker = "Skipped:"
	if strings.HasPrefix(text, marker) {
		text = strings.TrimSpace(text[len(marker):])
	}
	return text
}

// cell renders one table cell.
func cell(text string) string {
	return gatekit.Cell(text, MAX_CELL, "(no reason given)")
}

// render is the markdown table. Split by project so a reader sees which
// suite skipped.
func render(rows []row, maxRows int) string {
	var out []string
	out = append(out, "")
	out = append(out, fmt.Sprintf("### Skipped tests: %d", len(rows)))
	out = append(out, "")
	if len(rows) == 0 {
		out = append(out, "_No test was skipped in this shard._")
		return strings.Join(out, "\n")
	}

	out = append(out, "A skipped test asserts nothing. Every skip below carries the reason it was skipped; a skip without a reason is a hole.")
	out = append(out, "")
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
		out = append(out, fmt.Sprintf("<details><summary><b>%s</b> — %d skipped</summary>",
			cell(project), len(group)))
		out = append(out, "")
		out = append(out, "| test | reason |")
		out = append(out, "|---|---|")
		for _, r := range group {
			out = append(out, fmt.Sprintf("| `%s` | %s |", r.name, r.reason))
		}
		out = append(out, "")
		out = append(out, "</details>")
	}
	if len(rows) > maxRows {
		out = append(out, "")
		out = append(out, fmt.Sprintf("_%d further skipped test(s) not shown (raise --max-rows; the full set is in the test-results-* artifact)._",
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

// appendSummary appends the table to the summary file.
func appendSummary(path string, text string) error {
	fh, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer fh.Close()
	_, err = fh.WriteString(text + "\n")
	return err
}
