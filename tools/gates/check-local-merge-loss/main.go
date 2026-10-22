// Package mergeloss is the compiled port of tools/check-local-merge-loss.py.
//
// It answers one question per local squash-merge: is anything the merge
// dropped still missing from the tree? The pull request's own diff
// (`merge-base..<head>`) is compared with dev, and a line that a NORMAL
// commit removed on purpose is not a loss.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package mergeloss

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

// marker is the marker the local squash-merges carry in their body.
//
// The pull request number and the pull request's head commit are both in it,
// which is what lets this gate work without the network.
var marker = regexp.MustCompile(
	`Squash-merged into dev \(PR #(?P<pr>\d+), head (?P<head>[0-9a-f]{7,40})\)`)

// Run is the gate's entry point.
func Run(args []string) int {
	return runGate(args, false)
}

// runGate is the gate, with the count of git processes either printed or
// not.
//
// The self-test calls the gate itself, and the number of processes a
// throwaway fixture needs is noise there.
func runGate(args []string, quiet bool) int {
	rangeRev := "origin/dev"
	tip := "HEAD"
	var onlyMerges []string
	reportAll := false
	minMerges := 1
	allowMissingHeads := false
	exceptions := ""
	asJSON := false
	selfTest := false

	defer func() {
		if !quiet {
			// The count of the processes of git is how the gate's own
			// growth is seen: a run that starts 67 processes is not a run
			// that starts 1547.
			fmt.Fprintln(os.Stderr, gitCallsSummary())
		}
	}()

	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--range":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --range needs a revision")
				return gate.Broken
			}
			rangeRev = args[index]
		case "--tip":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --tip needs a revision")
				return gate.Broken
			}
			tip = args[index]
		case "--merge":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --merge needs a commit")
				return gate.Broken
			}
			onlyMerges = append(onlyMerges, args[index])
		case "--report-all":
			reportAll = true
		case "--min-merges":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --min-merges needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --min-merges needs a number: %s\n", args[index])
				return gate.Broken
			}
			minMerges = value
		case "--allow-missing-heads":
			allowMissingHeads = true
		case "--exceptions":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --exceptions needs a path")
				return gate.Broken
			}
			exceptions = args[index]
		case "--json":
			asJSON = true
		case "--self-test":
			selfTest = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		// The self-test's own processes of git are the fixture's, and the
		// count of them is not what a reader of the log is looking for.
		quiet = true
		return selfTestRun()
	}

	if exceptions == "" {
		root, err := gate.BinaryRoot()
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		exceptions = filepath.Join(root, "tools", "merge-loss-exceptions.txt")
	}

	if !gitOK("rev-parse", "--verify", tip+"^{commit}") {
		fmt.Fprintf(os.Stderr, "error: %s is not a commit\n", tip)
		return gate.Broken
	}
	if !gitOK("rev-parse", "--verify", rangeRev+"^{commit}") {
		fmt.Fprintf(os.Stderr, "error: %s is not a commit\n", rangeRev)
		return gate.Broken
	}

	allMerges := findMerges(rangeRev)
	merges := allMerges
	if len(onlyMerges) > 0 {
		var narrowed []merge
		for _, item := range merges {
			for _, wanted := range onlyMerges {
				if strings.HasPrefix(item.sha, wanted) {
					narrowed = append(narrowed, item)
					break
				}
			}
		}
		merges = narrowed
	}
	if len(merges) < minMerges {
		fmt.Fprintf(os.Stderr, "error: only %d squash-merge(s) examined, the floor is %d\n",
			len(merges), minMerges)
		return gate.Broken
	}

	// Every local squash-merge in the history, not only the ones under
	// report: a commit that is one of them has its removals EXCLUDED from
	// the "somebody removed this on purpose" set, and that set must not
	// change when the report is narrowed to one merge.
	squashMerges := map[string]bool{}
	for _, item := range allMerges {
		squashMerges[item.sha] = true
	}
	// ONE process for every object of the whole run: the file at the
	// parent, the file at the merge, the file at the tip, the parent of
	// the merge and the pull request head.
	batch, err := newObjectBatch("")
	if err != nil {
		fmt.Fprintf(os.Stderr, "error: the object reader could not start: %v\n", err)
		return gate.Broken
	}
	defer batch.close()

	cache := newCaches("", tip, squashMerges, batch)
	var reports []*mergeReport
	for _, item := range merges {
		reports = append(reports, checkMerge(item, cache))
	}
	if !reportAll {
		// A loss is only a loss while it is missing: the tree may have it
		// again, or a NORMAL commit may have removed it on purpose. Both
		// answers are the same for every merge, so they are asked once,
		// after every merge has said which lines it dropped.
		cache.resolveRestored(reports)
	}

	var live []loss
	for _, report := range reports {
		for _, item := range report.losses {
			if !item.restored {
				live = append(live, item)
			}
		}
	}

	exceptionsList, err := loadExceptions(exceptions)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	var accepted []loss
	var stillLive []loss
	used := map[int]bool{}
	for _, item := range live {
		key := normalize(item.line)
		hit := -1
		for position, exception := range exceptionsList {
			if exception.path != item.path || exception.pr != item.pr {
				continue
			}
			if exception.pattern.MatchString(key) {
				hit = position
				break
			}
		}
		if hit < 0 {
			stillLive = append(stillLive, item)
		} else {
			used[hit] = true
			accepted = append(accepted, item)
		}
	}
	var stale []exception
	for position, exception := range exceptionsList {
		if !used[position] {
			stale = append(stale, exception)
		}
	}

	var missing []*mergeReport
	for _, report := range reports {
		if report.skipped != "" {
			missing = append(missing, report)
		}
	}
	if len(missing) > 0 && !allowMissingHeads {
		for _, report := range missing {
			fmt.Fprintf(os.Stderr,
				"error: PR #%d: %s\n    fetch it with: git fetch origin '+refs/pull/%d/head:refs/remotes/pull/%d/head'\n",
				report.pr, report.skipped, report.pr, report.pr)
		}
		return gate.Broken
	}

	filesExamined := 0
	historicalLosses := 0
	for _, report := range reports {
		filesExamined += report.files
		historicalLosses += len(report.losses)
	}

	if asJSON {
		payload := jsonPayload{
			Tip:              tip,
			MergesExamined:   len(reports),
			FilesExamined:    filesExamined,
			Skipped:          skippedEntries(missing),
			Losses:           lossEntries(stillLive),
			AcceptedLosses:   len(accepted),
			StaleExceptions:  staleEntries(stale),
			HistoricalLosses: historicalLosses,
		}
		data, err := pycompat.JSONDumpASCII(payload, "  ")
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Red
		}
		fmt.Println(string(data))
	} else {
		for _, report := range missing {
			fmt.Printf("skipped  %s  PR #%d: %s\n", shortSHA(report.merge), report.pr, report.skipped)
		}
		for _, item := range stillLive {
			fmt.Printf("PR #%d  %s  %s: lost %s line: %s\n",
				item.pr, shortSHA(item.merge), item.path, item.side,
				trimLine(item.line))
		}
		fmt.Printf("merges examined: %d   files examined: %d   live losses: %d   "+
			"accepted: %d   historical losses: %d\n",
			len(reports), filesExamined, len(stillLive), len(accepted), historicalLosses)
		for _, exception := range stale {
			if len(onlyMerges) > 0 {
				// The report was narrowed: an exception that matches nothing
				// in one merge says nothing about the whole history.
				continue
			}
			fmt.Printf("note: the exception for PR #%d %s (%s) matches nothing — the "+
				"content is back, so the exception can go\n",
				exception.pr, exception.path, exception.pattern.String())
		}
	}

	if len(stillLive) > 0 {
		return gate.Red
	}
	return gate.Green
}

// loss is one piece of content that a squash-merge dropped.
type loss struct {
	pr           int
	merge        string
	mergeSubject string
	path         string
	line         string
	side         string
	restored     bool
}

// mergeReport is what one squash-merge cost.
type mergeReport struct {
	merge   string
	pr      int
	subject string
	head    string
	base    string
	files   int
	losses  []loss
	skipped string
}

// merge is one squash-merge commit on the first-parent line.
type merge struct {
	sha     string
	subject string
	pr      int
	head    string
}

// exception is one accepted loss.
type exception struct {
	path    string
	pr      int
	pattern *regexp.Regexp
	reason  string
}

// jsonPayload is the machine-readable verdict, in the order the python
// original wrote it.
type jsonPayload struct {
	Tip              string         `json:"tip"`
	MergesExamined   int            `json:"merges_examined"`
	FilesExamined    int            `json:"files_examined"`
	Skipped          []skippedEntry `json:"skipped"`
	Losses           []lossEntry    `json:"losses"`
	AcceptedLosses   int            `json:"accepted_losses"`
	StaleExceptions  []staleEntry   `json:"stale_exceptions"`
	HistoricalLosses int            `json:"historical_losses"`
}

// skippedEntry is one merge the gate could not examine.
type skippedEntry struct {
	Merge  string `json:"merge"`
	PR     int    `json:"pr"`
	Reason string `json:"reason"`
}

// lossEntry is one live loss.
type lossEntry struct {
	PR       int    `json:"pr"`
	Merge    string `json:"merge"`
	Path     string `json:"path"`
	Line     string `json:"line"`
	Side     string `json:"side"`
	Restored bool   `json:"restored"`
}

// staleEntry is one exception that matches nothing.
type staleEntry struct {
	Path   string `json:"path"`
	PR     int    `json:"pr"`
	Regex  string `json:"regex"`
	Reason string `json:"reason"`
}

func skippedEntries(missing []*mergeReport) []skippedEntry {
	out := make([]skippedEntry, 0, len(missing))
	for _, report := range missing {
		out = append(out, skippedEntry{report.merge, report.pr, report.skipped})
	}
	return out
}

func lossEntries(live []loss) []lossEntry {
	out := make([]lossEntry, 0, len(live))
	for _, item := range live {
		out = append(out, lossEntry{
			PR:       item.pr,
			Merge:    item.merge,
			Path:     item.path,
			Line:     item.line,
			Side:     item.side,
			Restored: item.restored,
		})
	}
	return out
}

func staleEntries(stale []exception) []staleEntry {
	out := make([]staleEntry, 0, len(stale))
	for _, item := range stale {
		out = append(out, staleEntry{item.path, item.pr, item.pattern.String(), item.reason})
	}
	return out
}

// caches holds the per-file lookups the checker needs, computed once per run.
//
// The tip's version of a file and the lines a normal commit removed from it
// are the same for every merge, so they are computed once and shared.
type caches struct {
	dir          string
	tip          string
	squashMerges map[string]bool
	batch        *objectBatch
	tipLines     map[string]map[string]int
	deliberate   map[string]map[string]bool
}

func newCaches(dir string, tip string, squashMerges map[string]bool, batch *objectBatch) *caches {
	return &caches{
		dir:          dir,
		tip:          tip,
		squashMerges: squashMerges,
		batch:        batch,
		tipLines:     map[string]map[string]int{},
		deliberate:   map[string]map[string]bool{},
	}
}

// tipLines is the tip's version of a file, as a count per normalized line.
func (c *caches) tipLinesOf(path string) (map[string]int, bool) {
	if _, seen := c.tipLines[path]; !seen {
		lines, ok := c.batch.fileLines(c.tip, path)
		if !ok {
			c.tipLines[path] = nil
		} else {
			counts := map[string]int{}
			for _, line := range lines {
				counts[normalize(line)]++
			}
			c.tipLines[path] = counts
		}
	}
	counts, ok := c.tipLines[path]
	return counts, ok
}

// resolveRestored marks every loss that is not a loss any more.
//
// A line a squash-merge dropped is not a loss when the tree has it again
// (the tip's version of the file holds it) or when a NORMAL commit removed
// it on purpose (somebody asked for it to go). The two answers are the same
// for every merge, so they are computed once, over every path that has a
// loss, and not once per merge.
func (c *caches) resolveRestored(reports []*mergeReport) {
	paths := []string{}
	seen := map[string]bool{}
	for _, report := range reports {
		for _, item := range report.losses {
			if !seen[item.path] {
				seen[item.path] = true
				paths = append(paths, item.path)
			}
		}
	}
	if len(paths) == 0 {
		return
	}
	for _, path := range paths {
		c.tipLinesOf(path)
	}
	c.deliberate = deliberateRemovalsFor(c.dir, paths, c.tip, c.squashMerges)
	for _, report := range reports {
		for index := range report.losses {
			item := &report.losses[index]
			key := normalize(item.line)
			tipNorm, tipOk := c.tipLinesOf(item.path)
			restored := tipOk && tipNorm[key] >= 1
			if !restored && c.deliberate[item.path][key] {
				// A normal commit removed this line on purpose. The file may
				// even be gone from the tree now — a commit that deleted the
				// file removed every line in it.
				restored = true
			}
			item.restored = restored
		}
	}
}

// normalize makes two lines that differ only in whitespace the same line.
//
// The merge that lost content rewrote whole files, and a re-indent is not a
// loss. Collapsing the whitespace keeps the check on the content.
func normalize(line string) string {
	return strings.Join(strings.Fields(line), " ")
}

// trimLine is the line as the report prints it: at most 200 characters.
//
// The cut is on CHARACTERS, not on bytes: python's string is a sequence of
// characters, and a byte-wise cut lands in the middle of a Cyrillic letter
// and leaves a replacement character where the letter was.
func trimLine(line string) string {
	trimmed := strings.TrimSpace(line)
	count := 0
	for index := range trimmed {
		if count == 200 {
			return trimmed[:index]
		}
		count++
	}
	return trimmed
}

// shortSHA is the first twelve characters of a commit.
func shortSHA(sha string) string {
	if len(sha) > 12 {
		return sha[:12]
	}
	return sha
}

// checkMerge compares one squash-merge against the pull request it squashed.
func checkMerge(item merge, cache *caches) *mergeReport {
	return checkMergeAt("", item, cache)
}

// checkMergeAt compares one squash-merge against the pull request it
// squashed, in a directory.
func checkMergeAt(dir string, item merge, cache *caches) *mergeReport {
	parent := cache.batch.firstParent(item.sha)
	report := &mergeReport{merge: item.sha, pr: item.pr, subject: item.subject, head: item.head}

	if parent == "" {
		report.skipped = "the merge commit has no parent to compare against"
		return report
	}
	if !cache.batch.hasCommit(item.head) {
		report.skipped = "the pull request head " + shortSHA(item.head) +
			" is not in the local object store"
		return report
	}

	base := mergeBaseAt(dir, item.head, parent)
	if base == "" {
		report.skipped = "no merge base between the pull request head and " + shortSHA(parent)
		return report
	}
	report.base = base

	// ONE diff for the whole pull request: the lines are the same lines
	// the per-file diff carried, and the file a line belongs to is the
	// file the header names.
	out, ok := gitTextAt(dir, "diff", "--unified=0", "--no-color", "--no-renames", base, item.head)
	if !ok {
		report.skipped = "the pull request diff could not be read"
		return report
	}
	diff := parseDiff(out)

	for _, path := range diff.examined() {
		report.files++
		ours := cache.batch.fileLinesOrEmpty(parent, path)
		merged := cache.batch.fileLinesOrEmpty(item.sha, path)
		added, removed := diff.file(path).added, diff.file(path).removed

		mergedNorm := map[string]int{}
		for _, line := range merged {
			mergedNorm[normalize(line)]++
		}
		removedNorm := map[string]bool{}
		for _, line := range removed {
			removedNorm[normalize(line)] = true
		}

		record := func(line string, side string) {
			key := normalize(line)
			if key == "" {
				return
			}
			report.losses = append(report.losses, loss{
				pr:           item.pr,
				merge:        item.sha,
				mergeSubject: item.subject,
				path:         path,
				line:         line,
				side:         side,
			})
		}

		// (a) every line the pull request added must be in the merge result.
		//     A resolution that dropped the pull request's own work is a loss
		//     too, and this is the half that catches it.
		seen := map[string]bool{}
		for _, line := range added {
			key := normalize(line)
			if key == "" || seen[key] {
				continue
			}
			seen[key] = true
			if mergedNorm[key] >= 1 {
				continue
			}
			record(line, "pull-request")
		}

		// (b) every line dev had before the merge that the pull request did
		//     not remove must be in the merge result. This is the half that
		//     catches the wholesale file takeover.
		seen = map[string]bool{}
		for _, line := range ours {
			key := normalize(line)
			if key == "" || seen[key] {
				continue
			}
			seen[key] = true
			if removedNorm[key] {
				// The pull request removed this line on purpose.
				continue
			}
			if mergedNorm[key] >= 1 {
				continue
			}
			record(line, "dev")
		}
	}

	return report
}

// findMerges is every commit on the first-parent line whose body carries the
// marker.
func findMerges(tip string) []merge {
	return findMergesAt("", tip)
}

// findMergesAt is every commit on the first-parent line whose body carries
// the marker, in a directory.
func findMergesAt(dir string, tip string) []merge {
	out, ok := gitTextAt(dir, "log", "--first-parent", "--format=%H%x1f%s%x1f%B%x1e", tip)
	if !ok {
		return nil
	}
	var found []merge
	for _, record := range strings.Split(out, "\x1e") {
		record = strings.Trim(record, "\n")
		if record == "" {
			continue
		}
		parts := strings.Split(record, "\x1f")
		if len(parts) < 3 {
			continue
		}
		sha, subject, body := parts[0], parts[1], parts[2]
		match := marker.FindStringSubmatch(body)
		if match == nil {
			continue
		}
		pr, _ := pycompat.Atoi(match[marker.SubexpIndex("pr")])
		found = append(found, merge{
			sha:     sha,
			subject: subject,
			pr:      pr,
			head:    match[marker.SubexpIndex("head")],
		})
	}
	return found
}

// mergeBase is the commit the pull request head and dev share.
func mergeBase(head, parent string) string {
	return mergeBaseAt("", head, parent)
}

// mergeBaseAt is the commit the pull request head and dev share, in a
// directory.
func mergeBaseAt(dir string, head, parent string) string {
	return strings.TrimSpace(gitTextOrEmptyAt(dir, "merge-base", head, parent))
}

// loadExceptions reads the accepted losses from the exceptions file.
//
// An exception is a piece of content a squash-merge dropped that is NOT a
// live loss: it was restored in another form, or an equivalent form was
// restored instead. The file names the pull request, the file and a regex
// that picks the accepted line out of the losses the gate reports.
func loadExceptions(path string) ([]exception, error) {
	var out []exception
	if _, err := os.Stat(path); err != nil {
		return out, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	number := 0
	for _, raw := range pycompat.SplitLines(string(data)) {
		number++
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		// A backslash-escaped pipe is a literal one, so a regex can
		// carry an alternation (and a table row can be named).
		fields := splitUnescapedPipes(line, 3)
		for index := range fields {
			fields[index] = strings.TrimSpace(strings.ReplaceAll(fields[index], "\\|", "|"))
		}
		if len(fields) != 4 || !allFilled(fields) {
			fmt.Fprintf(os.Stderr,
				"error: %s:%d: an exception needs four fields (path | PR | regex | reason)\n",
				path, number)
			return nil, fmt.Errorf("%s", path)
		}
		pattern, err := regexp.Compile(fields[2])
		if err != nil {
			fmt.Fprintf(os.Stderr, "error: %s:%d: bad regex: %v\n", path, number, err)
			return nil, fmt.Errorf("%s", path)
		}
		pr, _ := pycompat.Atoi(strings.TrimPrefix(fields[1], "#"))
		out = append(out, exception{fields[0], pr, pattern, fields[3]})
	}
	return out, nil
}

// splitUnescapedPipes splits a line on the pipes that are not escaped.
//
// The python original used a regex with a lookbehind, and RE2 has none: the
// split is written out instead.
func splitUnescapedPipes(line string, maxSplits int) []string {
	fields := []string{}
	start := 0
	splits := 0
	for index := 0; index < len(line); index++ {
		if line[index] != '|' {
			continue
		}
		if index > 0 && line[index-1] == '\\' {
			continue
		}
		if splits >= maxSplits {
			break
		}
		fields = append(fields, line[start:index])
		start = index + 1
		splits++
	}
	fields = append(fields, line[start:])
	return fields
}

// allFilled reports whether every field holds something.
func allFilled(fields []string) bool {
	for _, field := range fields {
		if field == "" {
			return false
		}
	}
	return true
}

// gitEnv is the environment for the gate's own `git` calls: never prompt,
// never inherit a pager, and never take a lock the checkout does not want.
func gitEnv() []string {
	env := os.Environ()
	out := make([]string, 0, len(env)+3)
	for _, kv := range env {
		if strings.HasPrefix(kv, "GIT_TERMINAL_PROMPT=") ||
			strings.HasPrefix(kv, "GIT_PAGER=") ||
			strings.HasPrefix(kv, "GIT_OPTIONAL_LOCKS=") {
			continue
		}
		out = append(out, kv)
	}
	return append(out, "GIT_TERMINAL_PROMPT=0", "GIT_PAGER=cat", "GIT_OPTIONAL_LOCKS=0")
}

// git runs `git` in the working directory and returns (exit code, stdout,
// stderr).
func git(args ...string) (int, string, string) {
	return gitAt("", args...)
}

// gitAt runs `git` in a directory and returns (exit code, stdout, stderr).
func gitAt(dir string, args ...string) (int, string, string) {
	command := exec.Command("git", args...)
	command.Dir = dir
	command.Env = gitEnv()
	gitCall(args)
	var stdout, stderr strings.Builder
	command.Stdout = &stdout
	command.Stderr = &stderr
	err := command.Run()
	code := 0
	if err != nil {
		if exitError, ok := err.(*exec.ExitError); ok {
			code = exitError.ExitCode()
		} else {
			code = 1
		}
	}
	return code, stdout.String(), stderr.String()
}

// gitText runs `git` and returns stdout, or nothing when git failed.
func gitText(args ...string) (string, bool) {
	return gitTextAt("", args...)
}

// gitTextAt runs `git` in a directory and returns stdout, or nothing when git
// failed.
func gitTextAt(dir string, args ...string) (string, bool) {
	code, out, _ := gitAt(dir, args...)
	if code != 0 {
		return "", false
	}
	return out, true
}

// gitTextOrEmpty runs `git` and returns stdout, or the empty string.
func gitTextOrEmpty(args ...string) string {
	return gitTextOrEmptyAt("", args...)
}

// gitTextOrEmptyAt runs `git` in a directory and returns stdout, or the empty
// string.
func gitTextOrEmptyAt(dir string, args ...string) string {
	out, _ := gitTextAt(dir, args...)
	return out
}

// gitOK reports whether `git` succeeded.
func gitOK(args ...string) bool {
	code, _, _ := git(args...)
	return code == 0
}

// gitOKAt reports whether `git` succeeded in a directory.
func gitOKAt(dir string, args ...string) bool {
	code, _, _ := gitAt(dir, args...)
	return code == 0
}
