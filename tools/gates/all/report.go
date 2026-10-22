package all

import (
	"fmt"
	"io"
	"strings"
	"time"

	"harbor.local/gates/internal/gate"
)

// The report is ONE table and a short list.
//
// The table is the gates that ran: the gate, the code it exited with, how
// long it took, and the first line of what it printed. The list under it is
// the gates of the list that did not run, and why. A reader of the report
// has to be able to tell the difference between "the gate found nothing"
// and "the gate never ran", because those are the two ways a tree gate
// stops checking anything.
const (
	gateColumn = 28
	codeColumn = 4
	timeColumn = 9
)

// row is one line of the table.
type row struct {
	gate    treeGate
	code    int
	elapsed time.Duration
	first   string
	detail  string
	err     error
}

// report is what the whole run produced.
type report struct {
	rows     []row
	notRun   []planned
	compiled int
	pending  int
	writers  int
	missing  int
	elapsed  time.Duration
	code     int
}

// buildReport puts the results and the plan together.
//
// The order of the rows is by gate name, and it does not depend on the
// order the gates finished in: the gates run in parallel, and a report
// whose order changes with the speed of the machine is a report two runs
// cannot be compared by.
func buildReport(plan []planned, results []result, opts options, base string) report {
	byName := make(map[string]result, len(results))
	for _, res := range results {
		byName[res.gate.name] = res
	}
	out := report{}
	for _, p := range plan {
		switch p.state {
		case stateCompiled:
			out.compiled++
		case statePython:
			out.pending++
		case stateWriter:
			out.writers++
		case stateMissing:
			out.missing++
		}
	}
	for _, p := range plan {
		if p.state != stateCompiled {
			out.notRun = append(out.notRun, p)
			continue
		}
		res, ok := byName[p.gate.name]
		if !ok {
			continue
		}
		out.elapsed += res.elapsed
		r := row{
			gate:    p.gate,
			code:    res.code,
			elapsed: res.elapsed,
			first:   res.firstLine(),
			err:     res.err,
		}
		if res.code != gate.Green {
			r.detail = res.output()
		}
		out.rows = append(out.rows, r)
	}
	out.code = verdict(out)
	return out
}

// verdict is the exit code of the whole run.
//
// Broken beats red: a run that could not run every gate must not look like
// a run that found nothing, and a run that found something must not look
// like a run that could not start.
func verdict(r report) int {
	code := gate.Green
	for _, row := range r.rows {
		switch row.code {
		case gate.Broken:
			return gate.Broken
		case gate.Red:
			code = gate.Red
		}
	}
	if r.missing > 0 {
		return gate.Broken
	}
	return code
}

// printReport prints the table and the list of the gates that did not run.
func printReport(w io.Writer, r report) {
	fmt.Fprintf(w, "tree gates: %d in the list, %d compiled, %d still python, %d not run\n",
		r.compiled+r.pending+r.writers+r.missing, r.compiled, r.pending, r.writers)
	fmt.Fprintln(w)
	fmt.Fprintf(w, "%-*s  %-*s  %-*s  %s\n", gateColumn, "gate", codeColumn, "code", timeColumn, "time", "first line of the output")
	fmt.Fprintf(w, "%s  %s  %s  %s\n",
		strings.Repeat("-", gateColumn), strings.Repeat("-", codeColumn),
		strings.Repeat("-", timeColumn), strings.Repeat("-", 32))
	for _, row := range r.rows {
		first := row.first
		if first == "" {
			first = "(no output)"
		}
		fmt.Fprintf(w, "%-*s  %-*d  %-*s  %s\n",
			gateColumn, row.gate.name, codeColumn, row.code,
			timeColumn, formatDuration(row.elapsed), first)
	}
	fmt.Fprintln(w)
	fmt.Fprintf(w, "%d gate(s) ran, %s in total (the sum of the gates, not the wall clock of the run)\n",
		len(r.rows), formatDuration(r.elapsed))

	if len(r.notRun) > 0 {
		fmt.Fprintln(w)
		fmt.Fprintln(w, "not run:")
		for _, p := range r.notRun {
			fmt.Fprintf(w, "  %-*s  %-8s %s\n", gateColumn, p.gate.name, p.state, p.reason)
		}
	}

	// The gates that found something, in full. The table names them; this
	// says why.
	for _, row := range r.rows {
		if row.code == gate.Green {
			continue
		}
		fmt.Fprintln(w)
		fmt.Fprintf(w, "%s (%s):\n", row.gate.name, verdictName(row.code))
		if row.err != nil {
			fmt.Fprintf(w, "  %v\n", row.err)
		}
		if detail := strings.TrimRight(row.detail, "\n"); detail != "" {
			for _, line := range strings.Split(detail, "\n") {
				fmt.Fprintf(w, "  %s\n", line)
			}
		}
	}
}

// verdictName is the name of an exit code, for the report.
func verdictName(code int) string {
	switch code {
	case gate.Green:
		return "green"
	case gate.Red:
		return "red"
	case gate.Broken:
		return "could not run"
	}
	return "unknown"
}

// formatDuration prints a duration the way a reader compares them: in
// milliseconds until it is worth seconds.
func formatDuration(d time.Duration) string {
	switch {
	case d < time.Millisecond:
		return fmt.Sprintf("%d us", d.Microseconds())
	case d < time.Second:
		return fmt.Sprintf("%d ms", d.Milliseconds())
	default:
		return fmt.Sprintf("%.3f s", d.Seconds())
	}
}
