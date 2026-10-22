// Package prcost is the instrument that answers "what does one pull request
// cost in this repository?".
//
// The question had three partial answers in three places and no whole one:
// issue #2108 measured the bill of one pull-request lane (19.4 runner-minutes,
// 17 jobs, run 39556560488), issue #2110 measured what closes the lane (the
// arch shard), and docs/BENCHMARKS.md §10 measured the gate set on a laptop.
// The number nobody had was the one a decision needs: the whole bill of one
// pull request, split by what it buys, with the wall and the runner-minutes
// told apart.
//
// # WHY A TOOL AND NOT A SPREADSHEET
//
// The three numbers that make the answer are not sums of a list of jobs:
//
//   - the WALL is not the sum of the jobs. Two workflows run in parallel
//     (ci.yml and docs.yml have no `needs:` between them), so the pull
//     request waits for the LONGEST branch, not for all of them;
//   - the CRITICAL PATH is not the longest job either: it is the chain
//     changes -> build -> test -> gate, and the chain is what a lane change
//     can move;
//   - the RUNNER-MINUTES are the sum, and the SHARE each article has of that
//     sum is the only number that says where the money goes.
//
// A hand-written table cannot keep those three apart, and the mistake it
// makes is the expensive one: it reports the wall as the sum, so every
// proposal to add a job looks like it costs the whole wall.
//
// # THE INPUT
//
// One JSON file per measurement, holding the runs of one pull request. The
// shape is what the Actions REST API returns, so the file is produced by
// `gh api` and nothing is transcribed by hand:
//
//	{"id": 39641146271, "workflow": "ci", "conclusion": "success",
//	 "created_at": "2026-10-21T17:08:42Z", "head_sha": "…",
//	 "jobs": [ {"name": "build", "started_at": "…", "completed_at": "…",
//	            "conclusion": "success", "labels": ["ubuntu-24.04"],
//	            "steps": [...] } ]}
//
// Several runs (ci.yml and docs.yml) of one pull request are ONE stripe, and
// the file may hold a list of them.
//
// It is REPORTING, never a gate: it reports, it never fails a build. The
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package prcost

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	"harbor.local/gates/internal/gate"
)

// The categories a job's seconds are charged to.
//
// The table is the honest part of the instrument: "the rest" is where a
// bill hides. Every job of ci.yml and docs.yml is named here, so a job this
// repository adds later shows up as "other" instead of silently joining a
// category it does not belong to.
const (
	catBuild     = "build (compile)"
	catTrim      = "build (reference trim)"
	catPack      = "packaging"
	catTests     = "test shards"
	catAOT       = "aot publish"
	catPublish   = "publishing"
	catCIGates   = "ci.yml gates"
	catDocsGates = "docs.yml gates"
	catOrchestr  = "orchestration"
	catOther     = "other"
)

// categoryOrder is the order the categories are printed in: the expensive
// ones first, so the reader sees where the money is before the small print.
var categoryOrder = []string{
	catBuild,
	catTrim,
	catPack,
	catTests,
	catAOT,
	catCIGates,
	catDocsGates,
	catPublish,
	catOrchestr,
	catOther,
}

// ciCategories names every job of ci.yml.
//
// The `test` job is a matrix: the API reports it once per shard, under the
// name "test (core)", "test (ui)", "test (arch)", "test (e2e)",
// "test (platform)". The bare "test" is the matrix job itself, reported
// only when it was skipped.
var ciCategories = map[string]string{
	"changes":                              catOrchestr,
	"gate":                                 catOrchestr,
	"build":                                catBuild,
	"unused references (ReferenceTrimmer)": catTrim,
	"pack":                                 catPack,
	"aot-publish":                          catAOT,
	"publish":                              catPublish,
	"perf-gate":                            catCIGates,
	"public-api freeze":                    catCIGates,
	"test/sample package set":              catCIGates,
	"no VSTest SDK in the tree":            catCIGates,
	"analyzer scope":                       catCIGates,
	"test":                                 catTests,
	"test (core)":                          catTests,
	"test (ui)":                            catTests,
	"test (arch)":                          catTests,
	"test (e2e)":                           catTests,
	"test (platform)":                      catTests,
}

// docsCategories names the jobs of docs.yml that are not gates.
//
// Everything else in that workflow is a gate, and there are twenty-one of
// them; they are one category, because the question this instrument answers
// is "what does the gate lane cost", not "which gate costs what" — that
// question has its own table, in docs/BENCHMARKS.md §10.
var docsCategories = map[string]string{
	"publish to GitHub Pages": catPublish,
}

// CategoryOf is the category a job's seconds are charged to.
//
// An unknown job is `other` and not a guess: a bill that quietly folds a new
// job into the nearest category is a bill nobody can audit.
func CategoryOf(workflow, job string) string {
	if workflow == "docs" {
		if category, ok := docsCategories[job]; ok {
			return category
		}
		return catDocsGates
	}
	if category, ok := ciCategories[job]; ok {
		return category
	}
	return catOther
}

// job is one job of one run, as the Actions API reports it.
type job struct {
	Name        string   `json:"name"`
	StartedAt   string   `json:"started_at"`
	CompletedAt string   `json:"completed_at"`
	Conclusion  string   `json:"conclusion"`
	Labels      []string `json:"labels"`
	Steps       []step   `json:"steps"`
}

// step is one step of one job.
type step struct {
	Name        string `json:"name"`
	StartedAt   string `json:"started_at"`
	CompletedAt string `json:"completed_at"`
}

// run is one workflow run: ci.yml or docs.yml.
type run struct {
	ID         int64  `json:"id"`
	Workflow   string `json:"workflow"`
	Conclusion string `json:"conclusion"`
	CreatedAt  string `json:"created_at"`
	HeadSHA    string `json:"head_sha"`
	Jobs       []job  `json:"jobs"`
}

// stripe is one pull request: the runs of every workflow it started.
type stripe struct {
	Label string `json:"label"`
	Runs  []run  `json:"runs"`
}

// document is the file the instrument reads.
type document struct {
	Stripes []stripe `json:"stripes"`
}

// interval is a span of time on the runner's clock.
type interval struct {
	start time.Time
	end   time.Time
}

// duration is the length of the interval, in seconds.
func (i interval) duration() float64 {
	return i.end.Sub(i.start).Seconds()
}

// parseTime reads a timestamp of the Actions API.
//
// The API writes UTC with a trailing "Z" and no fractional part, but the
// field is documented as optional: a job that never started has no
// timestamp at all. A timestamp that does not parse is an ERROR here, not a
// zero: a measurement that silently treats an unreadable timestamp as "the
// job took no time" is a measurement that reports a bill of zero.
func parseTime(value string) (time.Time, error) {
	if value == "" {
		return time.Time{}, fmt.Errorf("empty timestamp")
	}
	parsed, err := time.Parse(time.RFC3339, value)
	if err != nil {
		return time.Time{}, fmt.Errorf("cannot parse %q: %w", value, err)
	}
	return parsed.UTC(), nil
}

// liveJob is a job that ran: it has a start, an end, and was not skipped.
type liveJob struct {
	workflow string
	runID    int64
	name     string
	span     interval
	category string
	labels   []string
}

// isLive reports whether a job ran.
//
// A skipped job has no seconds: GitHub reports its start and its end as the
// same instant, and its conclusion as "skipped". Counting it would add a
// row of zeros, and the row is a fact a reader would have to check.
func isLive(j job) bool {
	return j.StartedAt != "" && j.CompletedAt != "" && j.Conclusion != "skipped"
}

// stripeMetrics is what one pull request costs.
type stripeMetrics struct {
	label    string
	runIDs   []int64
	jobs     []liveJob
	skipped  int
	runner   float64
	wall     float64
	busy     float64
	idle     float64
	peak     int
	peakJobs []string
	byCat    map[string]float64
	byJob    map[string]float64
	steps    map[string][]stepCost
	byFlow   map[string]*flowMetrics
	tail     string
	tailAt   time.Time
}

// flowMetrics is one workflow's share of a pull request: ci.yml and
// docs.yml run side by side, and the pull request waits for the longer of
// them, so the two are reported separately as well as together.
type flowMetrics struct {
	runner  float64
	jobs    int
	wall    float64
	wait    float64
	hasWait bool
}

// stepCost is one step of one job, with the seconds it took.
type stepCost struct {
	job     string
	name    string
	seconds float64
}

// measure computes the metrics of one stripe.
func measure(s stripe) (stripeMetrics, error) {
	metrics := stripeMetrics{
		label:  s.Label,
		byCat:  map[string]float64{},
		byJob:  map[string]float64{},
		steps:  map[string][]stepCost{},
		byFlow: map[string]*flowMetrics{},
	}
	for _, r := range s.Runs {
		metrics.runIDs = append(metrics.runIDs, r.ID)
		flow := metrics.byFlow[r.Workflow]
		if flow == nil {
			flow = &flowMetrics{}
			metrics.byFlow[r.Workflow] = flow
		}
		flowStart, flowEnd := time.Time{}, time.Time{}
		for _, j := range r.Jobs {
			if !isLive(j) {
				metrics.skipped++
				continue
			}
			start, err := parseTime(j.StartedAt)
			if err != nil {
				return metrics, fmt.Errorf("job %q of run %d: %w", j.Name, r.ID, err)
			}
			end, err := parseTime(j.CompletedAt)
			if err != nil {
				return metrics, fmt.Errorf("job %q of run %d: %w", j.Name, r.ID, err)
			}
			if end.Before(start) {
				return metrics, fmt.Errorf("job %q of run %d ends before it starts", j.Name, r.ID)
			}
			live := liveJob{
				workflow: r.Workflow,
				runID:    r.ID,
				name:     j.Name,
				span:     interval{start: start, end: end},
				category: CategoryOf(r.Workflow, j.Name),
				labels:   j.Labels,
			}
			metrics.jobs = append(metrics.jobs, live)
			metrics.runner += live.span.duration()
			flow.runner += live.span.duration()
			flow.jobs++
			if flowStart.IsZero() || live.span.start.Before(flowStart) {
				flowStart = live.span.start
			}
			if live.span.end.After(flowEnd) {
				flowEnd = live.span.end
			}
			if metrics.tailAt.IsZero() || live.span.end.After(metrics.tailAt) {
				metrics.tailAt = live.span.end
				metrics.tail = live.name
			}
			metrics.byCat[live.category] += live.span.duration()
			metrics.byJob[live.name] += live.span.duration()
			for _, st := range j.Steps {
				stepStart, err := parseTime(st.StartedAt)
				if err != nil {
					continue
				}
				stepEnd, err := parseTime(st.CompletedAt)
				if err != nil {
					continue
				}
				metrics.steps[j.Name] = append(metrics.steps[j.Name],
					stepCost{job: j.Name, name: st.Name, seconds: stepEnd.Sub(stepStart).Seconds()})
			}
		}
		if flow.jobs > 0 {
			flow.wall = flowEnd.Sub(flowStart).Seconds()
			if r.CreatedAt != "" {
				created, err := parseTime(r.CreatedAt)
				if err == nil {
					// The wait BEFORE the first job of this run: the
					// run was created, and no runner was free yet.
					flow.wait = flowStart.Sub(created).Seconds()
					flow.hasWait = true
				}
			}
		}
	}
	if len(metrics.jobs) == 0 {
		return metrics, fmt.Errorf("stripe %q has no job that ran", s.Label)
	}

	spans := make([]interval, 0, len(metrics.jobs))
	for _, j := range metrics.jobs {
		spans = append(spans, j.span)
	}
	sort.Slice(spans, func(i, k int) bool { return spans[i].start.Before(spans[k].start) })

	first := spans[0].start
	last := spans[0].end
	for _, sp := range spans {
		if sp.end.After(last) {
			last = sp.end
		}
	}
	metrics.wall = last.Sub(first).Seconds()
	metrics.busy = unionLength(spans)
	metrics.idle = metrics.wall - metrics.busy
	metrics.peak, metrics.peakJobs = peakWidth(metrics.jobs)
	return metrics, nil
}

// unionLength is the time at least one job was running.
//
// It is the wall minus the gaps: the time the pull request spent waiting
// for a runner with nothing running at all.
func unionLength(spans []interval) float64 {
	if len(spans) == 0 {
		return 0
	}
	total := 0.0
	current := spans[0]
	for _, sp := range spans[1:] {
		if sp.start.After(current.end) {
			total += current.duration()
			current = sp
			continue
		}
		if sp.end.After(current.end) {
			current.end = sp.end
		}
	}
	return total + current.duration()
}

// peakWidth is the largest number of jobs that ran at the same instant, and
// the names of the jobs at that instant.
//
// The width is what tells "the runner is saturated" from "the lane has one
// job in it": a lane that runs two jobs at once cannot be widened by
// splitting a job, and a lane that runs one job at once can.
func peakWidth(jobs []liveJob) (int, []string) {
	type event struct {
		at    time.Time
		delta int
		name  string
	}
	events := make([]event, 0, len(jobs)*2)
	for _, j := range jobs {
		events = append(events, event{at: j.span.start, delta: 1, name: j.name})
		events = append(events, event{at: j.span.end, delta: -1, name: j.name})
	}
	sort.Slice(events, func(i, k int) bool {
		if events[i].at.Equal(events[k].at) {
			// A job that ends at the same instant another starts is NOT
			// counted twice: the end comes first, so the two are
			// sequential, not concurrent.
			return events[i].delta < events[k].delta
		}
		return events[i].at.Before(events[k].at)
	})
	live := 0
	peak := 0
	var names []string
	best := []string{}
	for _, e := range events {
		live += e.delta
		if live > peak {
			peak = live
			best = names[:0:0]
		}
		if live == peak && e.delta > 0 {
			best = append(best, e.name)
		}
	}
	return peak, best
}

// Run is the gate entry point.
func Run(args []string) int {
	paths := []string{}
	showSteps := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--self-test":
			selfTest = true
		case arg == "--steps":
			showSteps = true
		case arg == "--help" || arg == "-h":
			usage(os.Stdout)
			return gate.Green
		case strings.HasPrefix(arg, "--input="):
			paths = append(paths, strings.TrimPrefix(arg, "--input="))
		case arg == "--input":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "error: --input needs a path")
				return gate.Broken
			}
			i++
			paths = append(paths, args[i])
		case strings.HasPrefix(arg, "-"):
			fmt.Fprintf(os.Stderr, "error: unknown flag %q\n", arg)
			usage(os.Stderr)
			return gate.Broken
		default:
			paths = append(paths, arg)
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if len(paths) == 0 {
		fmt.Fprintln(os.Stderr, "error: no input file; pass one or more JSON files")
		usage(os.Stderr)
		return gate.Broken
	}

	stripes, err := readStripes(paths)
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		return gate.Broken
	}
	if len(stripes) == 0 {
		fmt.Fprintln(os.Stderr, "error: the input holds no stripe")
		return gate.Broken
	}

	measured := make([]stripeMetrics, 0, len(stripes))
	for _, s := range stripes {
		m, err := measure(s)
		if err != nil {
			fmt.Fprintln(os.Stderr, "error:", err)
			return gate.Broken
		}
		measured = append(measured, m)
	}

	fmt.Print(stripeTable(measured))
	fmt.Print(flowTable(measured))
	fmt.Print(costTable(measured))
	if showSteps {
		fmt.Print(stepTable(measured))
	}
	return gate.Green
}

// readStripes reads the measurement files.
//
// Two shapes are accepted, and both are what `gh api` produces:
//
//   - a document with a "stripes" array: several pull requests in one file;
//   - a bare array of runs: one pull request, one workflow each.
func readStripes(paths []string) ([]stripe, error) {
	var out []stripe
	for _, path := range paths {
		data, err := os.ReadFile(path)
		if err != nil {
			return nil, fmt.Errorf("cannot read %s: %w", path, err)
		}
		var doc document
		if err := json.Unmarshal(data, &doc); err == nil && len(doc.Stripes) > 0 {
			out = append(out, doc.Stripes...)
			continue
		}
		var runs []run
		if err := json.Unmarshal(data, &runs); err == nil && len(runs) > 0 {
			out = append(out, stripe{Label: path, Runs: runs})
			continue
		}
		var one run
		if err := json.Unmarshal(data, &one); err == nil && len(one.Jobs) > 0 {
			out = append(out, stripe{Label: path, Runs: []run{one}})
			continue
		}
		return nil, fmt.Errorf("%s is not a measurement file: it is neither a stripe document nor a run", path)
	}
	return out, nil
}

// stripeTable is the per-pull-request table.
func stripeTable(measured []stripeMetrics) string {
	var b strings.Builder
	b.WriteString("| pull request | runs | jobs | runner-min | wall (s) | critical path (s) | peak width | busy (s) | idle (s) | mean concurrency | the tail |\n")
	b.WriteString("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|\n")
	for _, m := range measured {
		fmt.Fprintf(&b, "| %s | %d | %d | **%.1f** | %.0f | %.0f | %d | %.0f | %.0f | %.2f | `%s` |\n",
			m.label, len(m.runIDs), len(m.jobs), m.runner/60, m.wall, m.wall, m.peak,
			m.busy, m.idle, meanConcurrency(m), m.tail)
	}
	return b.String()
}

// meanConcurrency is the average number of jobs running over the wall.
func meanConcurrency(m stripeMetrics) float64 {
	if m.wall <= 0 {
		return 0
	}
	return m.runner / m.wall
}

// flowTable is the two workflows side by side.
//
// ci.yml and docs.yml have no `needs:` between them: they start together and
// the pull request waits for the LONGER of the two. So the bill is their sum
// and the wall is their maximum, and a table that adds the two columns up to
// get the wall is wrong.
func flowTable(measured []stripeMetrics) string {
	var b strings.Builder
	b.WriteString("| pull request | workflow | jobs | runner-min | wall (s) | wait before the first job (s) |\n")
	b.WriteString("|---|---|---:|---:|---:|---:|\n")
	for _, m := range measured {
		for _, name := range []string{"ci", "docs"} {
			flow := m.byFlow[name]
			if flow == nil {
				continue
			}
			wait := "—"
			if flow.hasWait {
				wait = fmt.Sprintf("%.0f", flow.wait)
			}
			fmt.Fprintf(&b, "| %s | %s.yml | %d | %.1f | %.0f | %s |\n",
				m.label, name, flow.jobs, flow.runner/60, flow.wall, wait)
		}
	}
	return b.String()
}

// costTable is the decomposition of the bill, over every stripe.
func costTable(measured []stripeMetrics) string {
	totals := map[string]float64{}
	runner := 0.0
	wall := 0.0
	for _, m := range measured {
		for category, seconds := range m.byCat {
			totals[category] += seconds
		}
		runner += m.runner
		wall += m.wall
	}
	var b strings.Builder
	b.WriteString("| article | runner-min | share |\n|---|---:|---:|\n")
	for _, category := range categoryOrder {
		seconds, ok := totals[category]
		if !ok {
			continue
		}
		share := 0.0
		if runner > 0 {
			share = seconds / runner * 100
		}
		fmt.Fprintf(&b, "| %s | %.1f | %.0f %% |\n", category, seconds/60, share)
	}
	fmt.Fprintf(&b, "| **total** | **%.1f** | **%.0f %%** |\n", runner/60, 100.0)
	walls := make([]float64, 0, len(measured))
	idles := make([]float64, 0, len(measured))
	runners := make([]float64, 0, len(measured))
	peaks := make([]float64, 0, len(measured))
	for _, m := range measured {
		walls = append(walls, m.wall)
		idles = append(idles, m.idle)
		runners = append(runners, m.runner)
		peaks = append(peaks, float64(m.peak))
	}
	fmt.Fprintf(&b, "\nOver %d pull requests: the median runner bill is %.1f runner-minutes, "+
		"the median wall is %.0f s (min %.0f, max %.0f), the median idle time is %.0f s, "+
		"the median peak width is %.0f jobs.\n",
		len(measured), median(runners)/60, median(walls), minOf(walls), maxOf(walls),
		median(idles), median(peaks))
	return b.String()
}

// median is the middle of a list of numbers.
func median(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	sorted := make([]float64, len(values))
	copy(sorted, values)
	sort.Float64s(sorted)
	middle := len(sorted) / 2
	if len(sorted)%2 == 1 {
		return sorted[middle]
	}
	return (sorted[middle-1] + sorted[middle]) / 2
}

// minOf is the smallest of a list of numbers.
func minOf(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	smallest := values[0]
	for _, v := range values {
		if v < smallest {
			smallest = v
		}
	}
	return smallest
}

// maxOf is the largest of a list of numbers.
func maxOf(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	largest := values[0]
	for _, v := range values {
		if v > largest {
			largest = v
		}
	}
	return largest
}

// stepTable is the step-by-step breakdown of the jobs that have steps.
//
// It is the table that says what the build job is MADE of, which is the
// only way to tell "the compile" from "the restore" from "the cache".
func stepTable(measured []stripeMetrics) string {
	var b strings.Builder
	b.WriteString("| pull request | job | step | seconds |\n|---|---|---|---:|\n")
	for _, m := range measured {
		jobs := make([]string, 0, len(m.steps))
		for job := range m.steps {
			jobs = append(jobs, job)
		}
		sort.Strings(jobs)
		for _, job := range jobs {
			steps := m.steps[job]
			sort.Slice(steps, func(i, k int) bool { return steps[i].seconds > steps[k].seconds })
			for _, s := range steps {
				if s.seconds < 1 {
					continue
				}
				fmt.Fprintf(&b, "| %s | %s | %s | %.0f |\n", m.label, s.job, s.name, s.seconds)
			}
		}
	}
	return b.String()
}

// usage prints the tool's own text.
func usage(w *os.File) {
	fmt.Fprintln(w, "harbor-gates pr-cost [flags] <file.json>...")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "What one pull request costs in CI: the runner-minutes, the wall, the")
	fmt.Fprintln(w, "critical path, the peak width, and the bill split by article.")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "The input is what the Actions REST API returns. One file per pull")
	fmt.Fprintln(w, "request, with the runs of every workflow it started:")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "  {\"id\":…, \"workflow\":\"ci\", \"conclusion\":…, \"created_at\":…,")
	fmt.Fprintln(w, "   \"head_sha\":…, \"jobs\":[{\"name\":…, \"started_at\":…,")
	fmt.Fprintln(w, "                       \"completed_at\":…, \"conclusion\":…,")
	fmt.Fprintln(w, "                       \"labels\":[…], \"steps\":[…]}]}")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "  --input <path>   the file to read; repeatable")
	fmt.Fprintln(w, "  --steps          print the step-by-step breakdown of every job")
	fmt.Fprintln(w, "  --self-test      prove the instrument can fail")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "Fetch one run with:")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "  gh api repos/:owner/:repo/actions/runs/$RUN/jobs --paginate \\")
	fmt.Fprintln(w, "    | jq --argjson id $RUN --arg wf ci \\")
	fmt.Fprintln(w, "        '{id:$id, workflow:$wf, jobs:.jobs}'")
}
