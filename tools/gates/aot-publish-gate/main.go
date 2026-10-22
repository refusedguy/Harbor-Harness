// Package aotpublish is the compiled port of tools/aot-publish-gate.py.
//
// It holds an AOT publish log against a committed record of what that
// publish is known to do: the outcome, the diagnostic inventory, and the
// first-party sites. The record is `.github/aot-warning-baseline.txt`.
//
// The text this prints is the text the python original printed, and the exit
// codes are its own:
//
//	0  the publish reported its outcome, and it matches the committed record
//	1  the gate ran and said no
//	2  the gate could not run: no log, a log under the byte floor, or no marker
package aotpublish

import (
	"fmt"
	"os"
	"regexp"
	"sort"
	"strings"
	"unicode"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

// toolName is the name the tool prints in every line.
const toolName = "aot-publish-gate"

// defaultMarker is written by the CI job when the publish step reaches its
// end, whatever the outcome.
//
// Its ABSENCE is what distinguishes "the publish did what we recorded" from
// "there was no publish here".
const defaultMarker = "HARBOR_AOT_PUBLISH_DONE"

// defaultMinBytes is the floor on the log's size.
//
// A log smaller than this cannot be a real `dotnet publish` of a 40-project
// closure — it is a truncated file, an empty file, or the wrong file.
const defaultMinBytes = 2000

// diagnostic is `path(line,col): warning IL2026: text [project]`, and the
// same with `error`.
//
// The severity is captured because the two are different facts: a demoted
// warning does not fail the publish, an error does.
var diagnosticRe = regexp.MustCompile(`(?i)(?:^|[\s\]])(?P<severity>warning|error)\s+(?P<id>(?:IL|CS)\d{3,4})\b`)

// siteRe is a first-party SITE, read from the diagnostic's own text rather
// than from a path.
//
// ILC and ILLink name a type and a method — `IL3000:
// Harbor.Plugins.Compilation.PluginAssemblyReferences.BuildReferences(): ...`
// — and do NOT print a source file for the site. The two things that look
// like paths in an ILC log are both wrong answers: `Harbor.App.Cli.cs` is a
// csproj that merely ends in ".cs", and `src/Compilers/Core/.../CommonCompiler.cs`
// is ROSLYN'S OWN source path out of Microsoft.CodeAnalysis, which would be
// recorded as first-party and is not. So the site is the Harbor type name,
// which is what the message actually carries and what points at the project.
// A diagnostic with no Harbor type (e.g. `IL2104: Assembly 'MessagePack'
// produced trim warnings`) is third-party, which is the correct answer.
var siteRe = regexp.MustCompile(`\b(?P<site>Harbor\.[A-Za-z0-9_.]*[A-Za-z0-9_])`)

// inScopeRe is the set of ids this inventory is about.
//
// CS is in scope only because the minimal recipe currently fails to compile
// at all; if that is fixed the CS rows go away and the gate says so.
var inScopeRe = regexp.MustCompile(`^(?:IL[23]\d{3}|CS\d{4})$`)

// projectTag is MSBuild's trailing ` [/path/to/Project.csproj]` tag.
//
// It must be cut before the site is read: `apps/Harbor.App.Cli/Harbor.App.Cli.csproj`
// contains the string `Harbor.App.Cli`, and without this every per-assembly
// rollup (IL2104, IL3053) would be recorded as a first-party site at the CLI.
var projectTagRe = regexp.MustCompile(`\s\[/?[^\]]*\]\s*$`)

var (
	ridRe    = regexp.MustCompile(`\brid=([A-Za-z0-9._-]+)`)
	recipeRe = regexp.MustCompile(`\brecipe=([A-Za-z0-9._-]+)`)
	statusRe = regexp.MustCompile(`\bstatus=(published|failed)\b`)
	exitRe   = regexp.MustCompile(`\bexit=(-?\d+)`)
)

var severities = []string{"warning", "error"}
var verdicts = []string{"ACCEPTED", "REJECTED"}
var statuses = []string{"published", "failed"}

// Run is the gate's entry point.
func Run(args []string) int {
	logPath := ""
	baselinePath := ""
	marker := defaultMarker
	minBytes := defaultMinBytes
	expectRid := ""
	expectRecipe := ""
	expectStatus := ""
	selfTest := false

	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--log":
			index++
			if index >= len(args) {
				return usageError("argument --log: expected one argument")
			}
			logPath = args[index]
		case "--baseline":
			index++
			if index >= len(args) {
				return usageError("argument --baseline: expected one argument")
			}
			baselinePath = args[index]
		case "--marker":
			index++
			if index >= len(args) {
				return usageError("argument --marker: expected one argument")
			}
			marker = args[index]
		case "--min-bytes":
			index++
			if index >= len(args) {
				return usageError("argument --min-bytes: expected one argument")
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				return usageError("argument --min-bytes: invalid int value: '" + args[index] + "'")
			}
			minBytes = value
		case "--expect-rid":
			index++
			if index >= len(args) {
				return usageError("argument --expect-rid: expected one argument")
			}
			expectRid = args[index]
		case "--expect-recipe":
			index++
			if index >= len(args) {
				return usageError("argument --expect-recipe: expected one argument")
			}
			expectRecipe = args[index]
		case "--expect-status":
			index++
			if index >= len(args) {
				return usageError("argument --expect-status: expected one argument")
			}
			expectStatus = args[index]
			if expectStatus != "published" && expectStatus != "failed" {
				return usageError("argument --expect-status: invalid choice: '" + expectStatus +
					"' (choose from 'published', 'failed')")
			}
		case "--selftest":
			selfTest = true
		default:
			return usageError("unrecognized arguments: " + arg)
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if logPath == "" || baselinePath == "" {
		return usageError("--log and --baseline are both required (or pass --selftest)")
	}
	return run(logPath, baselinePath, marker, minBytes, expectRid, expectRecipe, expectStatus,
		func(line string) { fmt.Println(line) })
}

// usageError prints the error the way argparse prints it, and returns the
// exit code a bad invocation gets.
func usageError(message string) int {
	fmt.Fprintf(os.Stderr, "%s: error: %s\n", toolName, message)
	return gate.Broken
}

// row is one committed inventory row.
type row struct {
	ident    string
	severity string
	count    int
	verdict  string
	files    map[string]bool
	reason   string
	lineno   int
}

// key is what the comparison holds the row to.
func (r row) key() (int, string, map[string]bool) {
	return r.count, r.verdict, r.files
}

// marker is one completed publish's outcome.
type markerEntry struct {
	rid     string
	recipe  string
	status  string
	exit    int
	hasExit bool
}

// inventory is what the log actually contained.
type inventory struct {
	counts       map[string]int
	firstParty   map[string]map[string]bool
	markers      []markerEntry
	logBytes     int
	logLines     int
	mismatch     bool
	expectStatus string
	seenStatus   []string
}

func (i *inventory) count(ident, severity string) int {
	return i.counts[ident+"/"+severity]
}

func (i *inventory) setCount(ident, severity string, value int) {
	i.counts[ident+"/"+severity] = value
}

func (i *inventory) sites(ident, severity string) map[string]bool {
	return i.firstParty[ident+"/"+severity]
}

// run is the gate's verdict for a log and a baseline.
func run(logPath, baselinePath, marker string, minBytes int,
	expectRid, expectRecipe, expectStatus string, out func(string)) int {
	logText, err := readText(logPath, "publish log")
	if err != nil {
		return cannotRun(out, err.Error())
	}
	inv, err := parseLog(logText, logPath, minBytes, marker, expectRid, expectRecipe, expectStatus)
	if err != nil {
		return cannotRun(out, err.Error())
	}
	baselineText, err := readText(baselinePath, "baseline")
	if err != nil {
		return cannotRun(out, err.Error())
	}
	baseline, failures := parseBaseline(baselineText, baselinePath)
	if len(failures) > 0 {
		return cannotRun(out, "the baseline is not well formed:\n  "+strings.Join(failures, "\n  "))
	}

	outcomes := make([]string, 0, len(inv.markers))
	sorted := append([]markerEntry{}, inv.markers...)
	sort.Slice(sorted, func(i, j int) bool {
		a, b := sorted[i], sorted[j]
		if a.rid != b.rid {
			return a.rid < b.rid
		}
		if a.recipe != b.recipe {
			return a.recipe < b.recipe
		}
		if a.status != b.status {
			return a.status < b.status
		}
		return a.exit < b.exit
	})
	for _, entry := range sorted {
		exit := "None"
		if entry.hasExit {
			exit = fmt.Sprintf("%d", entry.exit)
		}
		outcomes = append(outcomes, fmt.Sprintf("rid=%s recipe=%s status=%s exit=%s",
			entry.rid, entry.recipe, entry.status, exit))
	}
	out(fmt.Sprintf("%s: publish log %s — %d bytes, %d lines; publish outcome: %s",
		toolName, logPath, inv.logBytes, inv.logLines, strings.Join(outcomes, ", ")))
	out(fmt.Sprintf("%s: %s", toolName, describe(inv)))
	baselineKeys := make([]string, 0, len(baseline))
	for key := range baseline {
		baselineKeys = append(baselineKeys, key)
	}
	sort.Strings(baselineKeys)
	names := make([]string, 0, len(baselineKeys))
	for _, key := range baselineKeys {
		ident, severity := splitKey(key)
		names = append(names, ident+" "+severity)
	}
	listed := "none"
	if len(names) > 0 {
		listed = strings.Join(names, ", ")
	}
	out(fmt.Sprintf("%s: baseline %s — %d row(s): %s",
		toolName, baselinePath, len(baseline), listed))

	problems, notes := compare(baseline, inv)
	if len(notes) > 0 {
		out("")
		for _, note := range notes {
			out(fmt.Sprintf("%s: %s", toolName, note))
		}
		out("")
	}
	if len(problems) > 0 {
		out("")
		out(fmt.Sprintf("%s: FAIL (exit 1) — the publish does not match the committed record:", toolName))
		for _, problem := range problems {
			out("  " + problem)
		}
		out("")
		out("Gate semantics: the pass criterion is NOT 'zero IL2026 warnings'. It is 'the AOT")
		out("publish's outcome and diagnostics are exactly what the committed record says, and")
		out("that record was written by a human who read them'. See docs/AOT_PUBLISH.md.")
		return gate.Red
	}

	out(fmt.Sprintf("%s: PASS (exit 0) — the publish's outcome and inventory match the committed record.",
		toolName))
	return gate.Green
}

// cannotRun prints the "the gate could not run" line.
func cannotRun(out func(string), message string) int {
	out(fmt.Sprintf("%s: CANNOT RUN (exit 2) — %s", toolName, message))
	return gate.Broken
}

// readText reads a file, replacing what cannot be decoded.
func readText(path, what string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("cannot read the %s at %s: %s: '%s'",
			what, path, pycompat.PyOSError(err), path)
	}
	return string(data), nil
}

// parseMarkers is every completed publish's (rid, recipe, status, exit) the
// log names.
func parseMarkers(text, marker string) []markerEntry {
	var found []markerEntry
	seen := map[string]bool{}
	for _, line := range pycompat.SplitLines(text) {
		if !strings.Contains(line, marker) {
			continue
		}
		rid := ridRe.FindStringSubmatch(line)
		recipe := recipeRe.FindStringSubmatch(line)
		status := statusRe.FindStringSubmatch(line)
		code := exitRe.FindStringSubmatch(line)
		if rid == nil || recipe == nil || status == nil {
			continue
		}
		entry := markerEntry{rid: rid[1], recipe: recipe[1], status: status[1]}
		if code != nil {
			value, ok := pycompat.Atoi(code[1])
			if ok {
				entry.exit = value
				entry.hasExit = true
			}
		}
		key := fmt.Sprintf("%s|%s|%s|%v", entry.rid, entry.recipe, entry.status, entry.exit)
		if seen[key] {
			continue
		}
		seen[key] = true
		found = append(found, entry)
	}
	return found
}

// parseLog reads a publish log into an inventory, or returns the reason the
// gate cannot run.
//
// `expectRid` / `expectRecipe` mismatches raise: they mean the log is a
// DIFFERENT publish's, and the gate has nothing to say about it. An
// `expectStatus` mismatch does NOT raise — it is returned as a problem, because
// a publish that started succeeding is a finding the gate must report loudly,
// not a reason to declare that it could not run.
func parseLog(text, logPath string, minBytes int, marker, expectRid, expectRecipe,
	expectStatus string) (*inventory, error) {
	logBytes := len([]byte(text))
	if logBytes < minBytes {
		return nil, fmt.Errorf(
			"the publish log is %d bytes, under the %d-byte floor. "+
				"That is a truncated or empty file, not a publish of a 40-project closure. "+
				"Raising the floor is a decision to make in review, not in the failing job.",
			logBytes, minBytes)
	}

	markers := parseMarkers(text, marker)
	if len(markers) == 0 {
		return nil, fmt.Errorf(
			"the publish log at %s carries no '%s rid=… recipe=… status=…' "+
				"line, so no publish reported an outcome into it. Treating this as the recorded "+
				"state is how a gate goes green on a process that died: the job writes the marker "+
				"only after `dotnet publish` has returned, and its absence is the whole signal.",
			logPath, marker)
	}

	var mismatches []string
	if expectRid != "" {
		found := false
		for _, entry := range markers {
			if entry.rid == expectRid {
				found = true
			}
		}
		if !found {
			mismatches = append(mismatches, "expected rid="+expectRid)
		}
	}
	if expectRecipe != "" {
		found := false
		for _, entry := range markers {
			if entry.recipe == expectRecipe {
				found = true
			}
		}
		if !found {
			mismatches = append(mismatches,
				"expected recipe="+expectRecipe+" — the two AOT recipes in Harbor.App.Cli.csproj "+
					"build DIFFERENT programs, so an inventory from one does not describe the other")
		}
	}
	if len(mismatches) > 0 {
		return nil, fmt.Errorf(
			"the publish log at %s does not describe the publish this baseline is "+
				"about: %s. This is a different publish's log.",
			logPath, strings.Join(mismatches, "; "))
	}

	inv := &inventory{
		counts:     map[string]int{},
		firstParty: map[string]map[string]bool{},
		markers:    markers,
		logBytes:   logBytes,
	}
	lines := pycompat.SplitLines(text)
	inv.logLines = len(lines)
	for _, line := range lines {
		match := diagnosticRe.FindStringSubmatch(line)
		if match == nil {
			continue
		}
		ident := strings.ToUpper(match[diagnosticRe.SubexpIndex("id")])
		if !inScopeRe.MatchString(ident) {
			continue
		}
		severity := strings.ToLower(match[diagnosticRe.SubexpIndex("severity")])
		inv.setCount(ident, severity, inv.count(ident, severity)+1)
		if site := firstPartySite(line); site != "" {
			key := ident + "/" + severity
			if inv.firstParty[key] == nil {
				inv.firstParty[key] = map[string]bool{}
			}
			inv.firstParty[key][site] = true
		}
	}

	if expectStatus != "" {
		seen := map[string]bool{}
		for _, entry := range markers {
			seen[entry.status] = true
		}
		matches := false
		for _, entry := range markers {
			if entry.status == expectStatus {
				matches = true
			}
		}
		if !matches {
			inv.mismatch = true
			inv.expectStatus = expectStatus
			for status := range seen {
				inv.seenStatus = append(inv.seenStatus, status)
			}
			sort.Strings(inv.seenStatus)
		}
	}
	return inv, nil
}

// parseBaseline reads the committed record, or the reasons it is not well
// formed.
func parseBaseline(text, baselinePath string) (map[string]row, []string) {
	rows := map[string]row{}
	var failures []string
	for index, raw := range pycompat.SplitLines(text) {
		lineno := index + 1
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.Split(line, "|")
		for part := range parts {
			parts[part] = strings.TrimSpace(parts[part])
		}
		if len(parts) < 6 {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: expected 6 '|'-separated fields "+
					"(ID | severity | count | verdict | first-party files | reason) and found "+
					"%d: %s", baselinePath, lineno, len(parts), pycompat.Repr(line)))
			continue
		}
		ident, severity, countText, verdict, filesText, reason := parts[0], parts[1], parts[2],
			parts[3], parts[4], parts[5]
		ident = strings.ToUpper(ident)
		if !inScopeRe.MatchString(ident) {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: %s is not an IL2xxx/IL3xxx/CS id. This inventory is the AOT publish one.",
				baselinePath, lineno, pycompat.Repr(ident)))
			continue
		}
		severity = strings.ToLower(severity)
		if !contains(severities, severity) {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: severity %s is not one of %s.",
				baselinePath, lineno, pycompat.Repr(severity), tuple(severities)))
			continue
		}
		key := ident + "/" + severity
		if previous, seen := rows[key]; seen {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: %s %s already has a row on line %d",
				baselinePath, lineno, ident, severity, previous.lineno))
			continue
		}
		count, ok := positiveInt(countText)
		if !ok {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: count %s is not a positive integer. A row for a diagnostic that does not "+
					"occur is a permission, not a measurement.",
				baselinePath, lineno, pycompat.Repr(countText)))
			continue
		}
		if !contains(verdicts, verdict) {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: verdict %s is not one of %s. Every emitted diagnostic is either a "+
					"concession we carry or a defect to fix; 'tolerated for now' is not one of the two.",
				baselinePath, lineno, pycompat.Repr(verdict), tuple(verdicts)))
			continue
		}
		if len(reason) < 40 {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: reason is %d characters; a reason that cannot fit in fewer than 40 is "+
					"not an argument (same floor as ExemptionReason).",
				baselinePath, lineno, len(reason)))
			continue
		}
		if isBareURL(reason) {
			failures = append(failures, fmt.Sprintf(
				"%s:%d: the reason is a URL. A URL says where the debt is tracked, not why it is "+
					"tolerated here.", baselinePath, lineno))
			continue
		}
		sites := map[string]bool{}
		if filesText != "-" {
			for _, site := range strings.Split(filesText, ",") {
				site = strings.TrimSpace(site)
				if site != "" {
					sites[site] = true
				}
			}
		}
		for _, site := range sortedKeys(sites) {
			if !strings.HasPrefix(site, "Harbor.") {
				failures = append(failures, fmt.Sprintf(
					"%s:%d: %s is not a first-party site. A site is the Harbor type the diagnostic "+
						"names (e.g. Harbor.Plugins.Instantiation.ReflectionPluginInstantiator); "+
						"third-party-only is '-'. A file path is not accepted: ILC prints no source "+
						"file, and the only real path in an ILC log is Roslyn's own src/Compilers/…",
					baselinePath, lineno, pycompat.Repr(site)))
			}
		}
		rows[key] = row{
			ident:    ident,
			severity: severity,
			count:    count,
			verdict:  verdict,
			files:    sites,
			reason:   reason,
			lineno:   lineno,
		}
	}
	return rows, failures
}

// firstPartySite is the Harbor site a diagnostic is attributed to, or the
// empty string.
//
// The whole line is searched, not just the text after the diagnostic id,
// because the two shapes put the site on opposite sides: MSBuild writes
// `path(line,col): warning IL2026: text` with the site BEFORE the severity,
// while ILC writes `ILC : error IL3000: Harbor.X.Y.Z(): text` with it after.
func firstPartySite(line string) string {
	text := projectTagRe.ReplaceAllString(line, "")
	match := siteRe.FindStringSubmatch(text)
	if match == nil {
		return ""
	}
	return match[siteRe.SubexpIndex("site")]
}

// describe is never a bare 'found 0' — #901 reported 'Found 0' from a matcher
// that matched nothing.
func describe(inv *inventory) string {
	if len(inv.counts) == 0 {
		return "found 0 distinct diagnostic id/severity pair(s), 0 occurrence(s) (ids: none)"
	}
	byID := map[string]int{}
	total := 0
	errors := 0
	for key, count := range inv.counts {
		ident, severity := splitKey(key)
		byID[ident] += count
		total += count
		if severity == "error" {
			errors += count
		}
	}
	ids := make([]string, 0, len(byID))
	for ident := range byID {
		ids = append(ids, ident)
	}
	sort.Strings(ids)
	parts := make([]string, 0, len(ids))
	for _, ident := range ids {
		parts = append(parts, fmt.Sprintf("%s(%d)", ident, byID[ident]))
	}
	owned := 0
	for _, sites := range inv.firstParty {
		owned += len(sites)
	}
	return fmt.Sprintf("found %d distinct diagnostic id(s), %d occurrence(s) (ids: %s); "+
		"%d error-severity; first-party sites: %d",
		len(byID), total, strings.Join(parts, " "), errors, owned)
}

// compare is both directions: a new diagnostic fails, and a row nothing
// produces fails.
//
// Only `problems` affect the exit code; `notes` are printed on a pass so the
// reader knows what they are passing ON, which is the difference between a
// ratchet and a rubber stamp.
func compare(baseline map[string]row, inv *inventory) ([]string, []string) {
	var problems, notes []string

	if inv.mismatch {
		direction := "the AOT state got WORSE"
		for _, status := range inv.seenStatus {
			if status == "published" {
				direction = "the AOT state IMPROVED"
			}
		}
		problems = append(problems, fmt.Sprintf(
			"OUTCOME the committed record says the publish reports status=%s, and this run "+
				"reported %v — %s. That is not something the gate may absorb: if the build now "+
				"publishes, or now fails differently, the AOT record has to be updated in the same "+
				"diff by whoever read the new log. See docs/AOT_PUBLISH.md.",
			inv.expectStatus, pycompat.ListRepr(inv.seenStatus), direction))
	}

	// The keys the LOG produced, and only those: a row the baseline carries
	// and the log does not is a row that is GONE, and that is the
	// difference between "the publish stopped emitting it" and "the publish
	// emits it a different number of times".
	actual := map[string]bool{}
	for key := range inv.counts {
		actual[key] = true
	}
	actualKeys := make([]string, 0, len(actual))
	for key := range actual {
		actualKeys = append(actualKeys, key)
	}
	sort.Strings(actualKeys)
	expectedKeys := make([]string, 0, len(baseline))
	for key := range baseline {
		expectedKeys = append(expectedKeys, key)
	}
	sort.Strings(expectedKeys)

	expectedSet := map[string]bool{}
	for _, key := range expectedKeys {
		expectedSet[key] = true
	}
	actualSet := map[string]bool{}
	for _, key := range actualKeys {
		actualSet[key] = true
	}

	for _, key := range actualKeys {
		if expectedSet[key] {
			continue
		}
		ident, severity := splitKey(key)
		sites := sortedKeys(inv.sites(ident, severity))
		where := " (third-party only)"
		if len(sites) > 0 {
			where = " at " + strings.Join(sites, ", ")
		}
		problems = append(problems, fmt.Sprintf(
			"NEW  %s %s: %d occurrence(s)%s — the baseline has no row for it. Add a row with a "+
				"verdict and a reason, or fix what emits it. If this is the AOT state IMPROVING, "+
				"say so in the row's reason: the reason field is where the next reader learns "+
				"whether this is progress or a regression.",
			ident, severity, inv.count(ident, severity), where))
	}

	for _, key := range expectedKeys {
		if actualSet[key] {
			continue
		}
		ident, severity := splitKey(key)
		verdict := baseline[key].verdict
		tail := "The warning was fixed or the dependency moved — delete the row in this same diff, " +
			"or it has become a permission for a concession nobody is making."
		if verdict == "REJECTED" {
			tail = "That is a REJECTED row, so this is the AOT state IMPROVING — promote the row or " +
				"delete it in this same diff, or the fix lands unrecorded and the gate goes red " +
				"again the next time it reappears."
		}
		problems = append(problems, fmt.Sprintf(
			"GONE  %s %s: the baseline carries a row the publish no longer produces. %s",
			ident, severity, tail))
	}

	for _, key := range expectedKeys {
		if !actualSet[key] {
			continue
		}
		ident, severity := splitKey(key)
		wantCount, _, wantFiles := baseline[key].key()
		gotCount := inv.count(ident, severity)
		gotFiles := inv.sites(ident, severity)
		if gotCount != wantCount {
			problems = append(problems, fmt.Sprintf(
				"COUNT %s %s: %d occurrence(s) now, %d in the baseline. Re-triage the delta and "+
					"update the count in the same diff.", ident, severity, gotCount, wantCount))
		}
		if !sameSites(gotFiles, wantFiles) {
			var added, removed []string
			for site := range gotFiles {
				if !wantFiles[site] {
					added = append(added, site)
				}
			}
			for site := range wantFiles {
				if !gotFiles[site] {
					removed = append(removed, site)
				}
			}
			sort.Strings(added)
			sort.Strings(removed)
			var detail []string
			if len(added) > 0 {
				detail = append(detail, "new first-party site(s): "+strings.Join(added, ", "))
			}
			if len(removed) > 0 {
				detail = append(detail, "site(s) that no longer warn: "+strings.Join(removed, ", "))
			}
			problems = append(problems, fmt.Sprintf(
				"SITES %s %s: %s (baseline: %s, now: %s — '-' means third-party only).",
				ident, severity, strings.Join(detail, "; "),
				siteList(wantFiles), siteList(gotFiles)))
		}
	}

	var rejected []string
	for _, key := range expectedKeys {
		if baseline[key].verdict != "REJECTED" {
			continue
		}
		ident, severity := splitKey(key)
		rejected = append(rejected, ident+" "+severity)
	}
	if len(rejected) > 0 {
		notes = append(notes,
			"NOTE  recorded as REJECTED, i.e. defects that ought to be fixed and are still "+
				"present: "+strings.Join(rejected, ", ")+". This run PASSES on them "+
				"because that is the state the repository is in. The pass is a ratchet, not an "+
				"endorsement: when one of them stops appearing, the gate goes red and the row has to "+
				"be promoted or deleted. See docs/AOT_PUBLISH.md.")
	}
	return problems, notes
}

// siteList is the sites of a set, in the form the report prints.
func siteList(sites map[string]bool) string {
	if len(sites) == 0 {
		return "-"
	}
	return strings.Join(sortedKeys(sites), ", ")
}

// sameSites reports whether two site sets hold the same sites.
func sameSites(a, b map[string]bool) bool {
	if len(a) != len(b) {
		return false
	}
	for site := range a {
		if !b[site] {
			return false
		}
	}
	return true
}

// splitKey splits an "id/severity" key.
func splitKey(key string) (string, string) {
	index := strings.LastIndex(key, "/")
	if index < 0 {
		return key, ""
	}
	return key[:index], key[index+1:]
}

// positiveInt reads a positive integer the way python's int() and the
// baseline's isdigit() do.
func positiveInt(text string) (int, bool) {
	if text == "" {
		return 0, false
	}
	for _, r := range text {
		if !unicode.IsDigit(r) {
			return 0, false
		}
	}
	value, ok := pycompat.Atoi(text)
	if !ok || value < 1 {
		return 0, false
	}
	return value, true
}

// isBareURL reports whether the reason is nothing but a URL.
//
// The python original matched `^\W*https?://\S+[.,;]?\W*$`, and the rule it
// implements is "the reason is a URL and nothing else": a leading run of
// non-word characters, an http or https scheme, and then no whitespace at all.
func isBareURL(reason string) bool {
	runes := []rune(reason)
	start := 0
	for start < len(runes) && !isWordRune(runes[start]) {
		start++
	}
	rest := string(runes[start:])
	lower := strings.ToLower(rest)
	var tail string
	switch {
	case strings.HasPrefix(lower, "https://"):
		tail = rest[len("https://"):]
	case strings.HasPrefix(lower, "http://"):
		tail = rest[len("http://"):]
	default:
		return false
	}
	if tail == "" {
		return false
	}
	return !strings.ContainsAny(tail, " \t\n\r\v\f")
}

// isWordRune reports whether the rune is a word character, the way python's
// `\\w` sees it.
func isWordRune(r rune) bool {
	return r == '_' || unicode.IsLetter(r) || unicode.IsDigit(r)
}

// contains reports whether the list holds the value.
func contains(values []string, value string) bool {
	for _, item := range values {
		if item == value {
			return true
		}
	}
	return false
}

// tuple renders a list of strings the way python renders a tuple of them.
func tuple(values []string) string {
	quoted := make([]string, 0, len(values))
	for _, value := range values {
		quoted = append(quoted, pycompat.Repr(value))
	}
	return "(" + strings.Join(quoted, ", ") + ")"
}

// sortedKeys are the keys of a set, in sorted order.
func sortedKeys(set map[string]bool) []string {
	out := make([]string, 0, len(set))
	for key := range set {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}
