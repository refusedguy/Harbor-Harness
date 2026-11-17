package demorepro

// cli.go -- the subcommands: compare, report, check-manifest, measure, and
// the entry point that dispatches them.

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

// compareResult is what the compare subcommand prints.
type compareResult struct {
	A      *gifJSON `json:"a"`
	B      *gifJSON `json:"b"`
	Equal  bool     `json:"equal"`
	Errors []string `json:"errors"`
}

// runNormalize is the `normalize` subcommand.
func runNormalize(args []string) int {
	input := ""
	output := ""
	dryRun := false
	for index := 0; index < len(args); index++ {
		switch args[index] {
		case "--input":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --input needs a path")
				return gate.Broken
			}
			input = args[index]
		case "--output":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --output needs a path")
				return gate.Broken
			}
			output = args[index]
		case "--dry-run":
			dryRun = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[index])
			return gate.Broken
		}
	}
	if input == "" || output == "" {
		fmt.Fprintln(os.Stderr, "demo_repro: normalize needs --input and --output")
		return gate.Broken
	}
	lock, err := readLock(lockPath())
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	result, err := normalize(input, output, lock, dryRun)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	data, err := pycompat.JSONDumpASCII(result, "  ")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	fmt.Println(string(data))
	return gate.Green
}

// runCompare is the `compare` subcommand: the record-twice check.
func runCompare(args []string) int {
	aPath := ""
	bPath := ""
	label := ""
	summary := false
	for index := 0; index < len(args); index++ {
		switch args[index] {
		case "--a":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --a needs a path")
				return gate.Broken
			}
			aPath = args[index]
		case "--b":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --b needs a path")
				return gate.Broken
			}
			bPath = args[index]
		case "--name":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --name needs a label")
				return gate.Broken
			}
			label = args[index]
		case "--summary":
			summary = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[index])
			return gate.Broken
		}
	}
	if aPath == "" || bPath == "" {
		fmt.Fprintln(os.Stderr, "demo_repro: compare needs --a and --b")
		return gate.Broken
	}

	a, err := measure(aPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	b, err := measure(bPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	equal, errs, notes := compare(a, b)
	if label == "" {
		label = filepath.Base(aPath)
	}
	for _, note := range notes {
		fmt.Printf("::notice::%s: %s\n", label, note)
	}
	for _, e := range errs {
		fmt.Printf("::error::%s: %s\n", label, e)
	}
	if summary {
		verdict := "reproducible"
		if !equal {
			verdict = "NOT reproducible"
		}
		lines := []string{
			fmt.Sprintf("### `%s` — %s", label, verdict),
			"",
			"| pass | bytes | frames | duration | raster | distinct screens |",
			"|---|---|---|---|---|---|",
			fmt.Sprintf("| A | %s | %d | %d ms | %d×%d | %d |",
				comma(a.Bytes), a.Frames, a.DurationMs, a.Width, a.Height, a.UniqueFrames),
			fmt.Sprintf("| B | %s | %d | %d ms | %d×%d | %d |",
				comma(b.Bytes), b.Frames, b.DurationMs, b.Width, b.Height, b.UniqueFrames),
			"",
		}
		for _, note := range notes {
			lines = append(lines, "- "+note)
		}
		for _, e := range errs {
			lines = append(lines, "- **"+e+"**")
		}
		lines = append(lines, "")
		path := os.Getenv("GITHUB_STEP_SUMMARY")
		if path != "" {
			appendFile(path, strings.Join(lines, "\n"))
		}
	}
	payload := compareResult{A: a.asJSON(), B: b.asJSON(), Equal: equal, Errors: errs}
	if payload.Errors == nil {
		payload.Errors = []string{}
	}
	data, err := pycompat.JSONDumpASCII(payload, "  ")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	fmt.Println(string(data))
	if !equal {
		return gate.Red
	}
	return gate.Green
}

// compare is the record-twice check.
//
// What is asserted, and why:
//
//   - the *envelope* — frame count, duration, per-frame delay, raster — must
//     be identical. `normalize` pins all four, so a difference here means the
//     toolchain lock and the recorder disagree;
//   - the *sequence of screens* must be identical, compared on decoded pixels
//     rather than on the encoded bytes;
//   - the *settled* final screen must be identical, for the same reason.
func compare(a, b *measurement) (bool, []string, []string) {
	var errs, notes []string

	for _, field := range []struct {
		name  string
		label string
		left  int
		right int
	}{
		{"frames", "frame count", a.Frames, b.Frames},
		{"durationMs", "duration", a.DurationMs, b.DurationMs},
		{"width", "width", a.Width, b.Width},
		{"height", "height", a.Height, b.Height},
	} {
		if field.left != field.right {
			errs = append(errs, fmt.Sprintf(
				"%s differs between the two passes: %d vs %d — frame pacing is not deterministic",
				field.label, field.left, field.right))
		}
	}

	if !sameInts(a.Delays, b.Delays) {
		errs = append(errs, fmt.Sprintf("per-frame delays differ: %v vs %v", a.Delays, b.Delays))
	}

	work, err := os.MkdirTemp("", "demo-compare-")
	if err != nil {
		errs = append(errs, err.Error())
		return false, errs, notes
	}
	defer os.RemoveAll(work)
	screensA, err := decodedScreens(a.Path, work, a.Width, a.Height)
	if err != nil {
		errs = append(errs, err.Error())
		return false, errs, notes
	}
	screensB, err := decodedScreens(b.Path, work, b.Width, b.Height)
	if err != nil {
		errs = append(errs, err.Error())
		return false, errs, notes
	}

	// Screens are compared pairwise in order, with a small tolerance for how
	// many differing pixels still counts as "the same screen".
	tolerance := 0.002
	if text, ok := os.LookupEnv("DEMO_PIXEL_TOLERANCE"); ok {
		if value, err := strconv.ParseFloat(strings.TrimSpace(text), 64); err == nil {
			tolerance = value
		}
	}
	if len(screensA) != len(screensB) {
		errs = append(errs, fmt.Sprintf(
			"the two passes show a different number of screens: %d vs %d — the recorded output is not reproducible",
			len(screensA), len(screensB)))
	} else {
		worst := 0.0
		worstAt := -1
		for index := range screensA {
			delta := screenSimilarity(screensA[index], screensB[index], 24)
			if delta > worst {
				worst, worstAt = delta, index
			}
		}
		if worst > tolerance {
			errs = append(errs, fmt.Sprintf(
				"screen %d/%d differs between the two passes (%.2f%% of pixels beyond the ±24-level "+
					"noise floor, tolerance %.2f%%) — the recorded output is not reproducible",
				worstAt+1, len(screensA), worst*100, tolerance*100))
		} else {
			notes = append(notes, fmt.Sprintf("all %d screens match within %.3f%% (dither tolerance)",
				len(screensA), worst*100))
		}
	}

	if len(screensA) > 0 && len(screensB) > 0 {
		finalDelta := screenSimilarity(screensA[len(screensA)-1], screensB[len(screensB)-1], 24)
		if finalDelta <= tolerance {
			notes = append(notes, "settled final screen matches")
		} else {
			errs = append(errs, fmt.Sprintf(
				"the settled final screen differs between the two passes (%.2f%% of pixels) — "+
					"something time- or random-dependent reached the recording",
				finalDelta*100))
		}
	}

	if a.Bytes != b.Bytes {
		delta := 0.0
		if a.Bytes != 0 {
			delta = 100.0 * float64(b.Bytes-a.Bytes) / float64(a.Bytes)
		}
		notes = append(notes, fmt.Sprintf("file size %s vs %s bytes (%+.1f %%)",
			comma(a.Bytes), comma(b.Bytes), delta))
	}

	return len(errs) == 0, errs, notes
}

// runReport is the `report` subcommand: the envelope report for a directory
// of GIFs.
func runReport(args []string) int {
	dir := ""
	for index := 0; index < len(args); index++ {
		if args[index] == "--dir" {
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --dir needs a path")
				return gate.Broken
			}
			dir = args[index]
			continue
		}
		fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[index])
		return gate.Broken
	}
	if dir == "" {
		fmt.Fprintln(os.Stderr, "demo_repro: report needs --dir")
		return gate.Broken
	}
	lock, err := readLock(lockPath())
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	code, text := report(dir, lock)
	fmt.Println(text)
	path := os.Getenv("GITHUB_STEP_SUMMARY")
	if path != "" {
		appendFile(path, text)
	}
	return code
}

// report is the envelope report for a directory of GIFs.
func report(gifDir string, lock map[string]string) (int, string) {
	var rows, errs, notes []string
	budget, _ := lockInt(lock, "GIF_FRAMES")
	for _, name := range tapes {
		path := filepath.Join(gifDir, name+"-compressed.gif")
		if info, err := os.Stat(path); err != nil || info.IsDir() {
			errs = append(errs, "missing demo GIF: "+path)
			continue
		}
		m, err := measure(path)
		if err != nil {
			errs = append(errs, "unreadable demo asset: "+path+": "+err.Error())
			continue
		}
		// The budget is a ceiling, not a target: a scene legitimately produces
		// fewer frames than GIF_FRAMES.
		if m.Frames > budget {
			errs = append(errs, fmt.Sprintf("`%s` has %d frames, over the %d-frame budget pinned by demo/toolchain.lock",
				path, m.Frames, budget))
		}
		rows = append(rows, fmt.Sprintf("| `%s` | %s | %d | %d ms | %d×%d | %d | `%s…` |",
			name, comma(m.Bytes), m.Frames, m.DurationMs, m.Width, m.Height,
			m.UniqueFrames, m.FramesSha256[:12]))
	}

	lines := []string{
		"## Demo recording determinism",
		"",
		toolchainProvenance(lock),
		"",
		"| gif | bytes | frames | duration | raster | unique frames | content hash |",
		"|---|---|---|---|---|---|---|",
	}
	lines = append(lines, rows...)
	lines = append(lines, "")
	text := strings.Join(lines, "\n")
	if len(notes) > 0 {
		text += strings.Join(notes, "\n") + "\n"
	}
	if len(errs) > 0 {
		text += fmt.Sprintf("**%d finding(s)**\n\n", len(errs)) + strings.Join(errs, "\n") + "\n"
	} else {
		text += "**Every GIF matches the pinned envelope.**\n"
	}
	for _, e := range errs {
		fmt.Printf("::error::%s\n", e)
	}
	for _, n := range notes {
		fmt.Printf("::notice::%s\n", n)
	}
	if len(errs) > 0 {
		return gate.Red, text
	}
	return gate.Green, text
}

// toolchainProvenance is the list of the pinned tools.
func toolchainProvenance(lock map[string]string) string {
	lines := []string{
		fmt.Sprintf("- vhs **%s** (sha256 `%s…`)", lock["VHS_VERSION"], short(lock["VHS_SHA256"])),
		fmt.Sprintf("- ttyd **%s** (sha256 `%s…`)", lock["TTYD_VERSION"], short(lock["TTYD_SHA256"])),
	}
	if path, err := exec.LookPath("ffmpeg"); err == nil {
		out, err := exec.Command(path, "-version").Output()
		if err == nil {
			fields := strings.Fields(string(out))
			reported := "?"
			if len(fields) > 2 {
				reported = fields[2]
			}
			pinned := lock["FFMPEG_VERSION"]
			suffix := ""
			if !strings.HasPrefix(reported, pinned) {
				suffix = " — does not match the pinned " + pinned
			}
			lines = append(lines, fmt.Sprintf("- ffmpeg **%s**%s", reported, suffix))
		}
	}
	lines = append(lines, fmt.Sprintf("- %s **%s** (sha256 `%s…`)",
		lock["FONT_FAMILY"], lock["FONT_VERSION"], short(lock["FONT_SHA256"])))
	lines = append(lines, fmt.Sprintf("- envelope **%s frames/screen @ %s fps**, %s×%s, %s-frame settled tail, %s-frame ceiling",
		lock["GIF_STATE_FRAMES"], lock["GIF_FPS"], lock["GIF_WIDTH"], lock["GIF_HEIGHT"],
		lock["GIF_TAIL_FRAMES"], lock["GIF_FRAMES"]))
	return strings.Join(lines, "\n")
}

// short is the first twelve characters of a digest.
func short(value string) string {
	if len(value) > 12 {
		return value[:12]
	}
	return value
}

// The fields the drift gate reads out of one baseline entry.
var (
	requiredFields = []string{"bytes", "width", "height", "frames", "durationMs",
		"uniqueFrames", "fileSha256", "framesSha256"}
	directIndexed = []string{"bytes", "frames", "durationMs"}
	hexFields     = []string{"fileSha256", "framesSha256"}
	countFields   = []string{"bytes", "width", "height", "frames", "durationMs", "uniqueFrames"}
)

// runCheckManifest is the `check-manifest` subcommand.
func runCheckManifest(args []string) int {
	manifest := filepath.Join(repoRoot(), "assets", "demo", "baseline.json")
	for index := 0; index < len(args); index++ {
		if args[index] == "--manifest" {
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --manifest needs a path")
				return gate.Broken
			}
			manifest = args[index]
			continue
		}
		fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[index])
		return gate.Broken
	}
	code, text := checkManifest(manifest)
	for _, line := range strings.Split(text, "\n") {
		if strings.HasPrefix(line, "::") {
			fmt.Println(line)
		}
	}
	fmt.Println(text)
	path := os.Getenv("GITHUB_STEP_SUMMARY")
	if path != "" {
		appendFile(path, text)
	}
	return code
}

// checkManifest is `assets/demo/baseline.json` well-formed enough for the
// drift gate to trust?
func checkManifest(manifestPath string) (int, string) {
	var errs []string

	if info, err := os.Stat(manifestPath); err != nil || info.IsDir() {
		return gate.Red, fmt.Sprintf("::error::missing baseline manifest: %s\n"+
			"The drift gate fails on this too, but only on a run that got as far as "+
			"recording something.\n", manifestPath)
	}

	data, err := os.ReadFile(manifestPath)
	if err != nil {
		return gate.Red, fmt.Sprintf("::error::unreadable baseline manifest %s: %v\n", manifestPath, err)
	}
	var document map[string]any
	if err := json.Unmarshal(data, &document); err != nil {
		return gate.Red, fmt.Sprintf("::error::unreadable baseline manifest %s: %v\n", manifestPath, err)
	}

	gifs, ok := document["gifs"].(map[string]any)
	if !ok {
		return gate.Red, fmt.Sprintf("::error::%s has no `gifs` object — every comparison "+
			"the drift gate makes is driven from it, so this file currently authorises "+
			"nothing and nothing says so\n", manifestPath)
	}

	known := map[string]bool{}
	for _, name := range tapes {
		known[name] = true
	}
	var unknown []string
	for name := range gifs {
		if !known[name] {
			unknown = append(unknown, name)
		}
	}
	sort.Strings(unknown)
	for _, name := range unknown {
		errs = append(errs, fmt.Sprintf("describes an unknown GIF `%s`; the recorded set is %s. "+
			"An entry the gate never reads is a claim nothing checks.",
			name, strings.Join(tapes, ", ")))
	}

	// Compared, not merely iterated: an empty map has to be an error in its
	// own right, because "nothing disagrees" is what a vacuous pass looks
	// like.
	compared := 0
	for _, name := range tapes {
		entry, ok := gifs[name]
		if !ok {
			errs = append(errs, fmt.Sprintf("has no entry for `%s` (assets/demo/%s-compressed.gif) "+
				"— the drift gate falls back to 'no baseline' and downgrades that GIF from a "+
				"comparison to a warning, so one dropped key silently removes that GIF from "+
				"drift detection", name, name))
			continue
		}
		fields, ok := entry.(map[string]any)
		if !ok {
			errs = append(errs, fmt.Sprintf("entry `%s` is %s, not an object", name, jsonTypeName(entry)))
			continue
		}

		compared++
		for _, field := range requiredFields {
			if _, present := fields[field]; present {
				continue
			}
			how := "makes the drift gate report the content as changed on every run, " +
				"because it reads that key with .get()"
			if contains(directIndexed, field) {
				how = fmt.Sprintf("crashes the drift gate step on b[%s]", pycompat.Repr(field))
			}
			errs = append(errs, fmt.Sprintf("`%s` has no `%s` — %s", name, field, how))
		}

		for _, field := range countFields {
			value, present := fields[field]
			if !present {
				continue
			}
			number, ok := value.(float64)
			if !ok || number <= 0 || number != float64(int64(number)) {
				errs = append(errs, fmt.Sprintf("`%s.%s` is %s; the gate does arithmetic and "+
					"a percentage on it, so it has to be a positive integer",
					name, field, pycompat.Repr(jsonReprValue(value))))
			}
		}

		for _, field := range hexFields {
			value, present := fields[field]
			if !present {
				continue
			}
			text, ok := value.(string)
			if !ok || len(text) != 64 || !isLowerHex(text) {
				errs = append(errs, fmt.Sprintf("`%s.%s` is not a 64-character lowercase sha256 "+
					"(%s); it is read and compared verbatim, so a truncated digest would never "+
					"match anything", name, field, pycompat.Repr(jsonReprValue(value))))
			}
		}

		rel, _ := fields["path"].(string)
		if rel == "" {
			rel = fmt.Sprintf("assets/demo/%s-compressed.gif", name)
		}
		if info, err := os.Stat(filepath.Join(repoRoot(), rel)); err != nil || info.IsDir() {
			errs = append(errs, fmt.Sprintf("describes `%s`, which is not in the tree — the gate's "+
				"`path` has nothing to open", rel))
		}
	}

	if compared == 0 {
		errs = append(errs, "no entry could be examined at all; agreement was not reached, "+
			"it was unavailable")
	}

	shown := manifestPath
	if relative, err := filepath.Rel(repoRoot(), manifestPath); err == nil {
		shown = relative
	}

	lines := []string{
		"## Baseline manifest integrity",
		"",
		fmt.Sprintf("Manifest: `%s` — %d/%d entries well-formed. This is a check that the drift "+
			"gate's comparisons are *defined*, not that the manifest matches the committed GIFs: "+
			"`record_baseline` defaults to `false`, so the manifest is expected to lag the artifacts.",
			shown, compared, len(tapes)),
		"",
	}
	text := strings.Join(lines, "\n")
	if len(errs) > 0 {
		text += "\n" + fmt.Sprintf("**%d finding(s) — the drift gate's comparisons are not all "+
			"well-defined.** Each one is a way this gate can pass, crash, or report something "+
			"meaningless without saying so.\n\n", len(errs))
		text += strings.Join(errs, "\n") + "\n"
		for _, e := range errs {
			fmt.Printf("::error::%s\n", e)
		}
	} else {
		text += "\n**Every entry is well-formed.** The drift gate's comparisons are all defined, " +
			"and a finding it reports will be about a rendering change rather than about the manifest.\n"
	}
	if len(errs) > 0 {
		return gate.Red, text
	}
	return gate.Green, text
}

// runMeasure is the `measure` subcommand: the shape of a single GIF.
func runMeasure(args []string) int {
	input := ""
	for index := 0; index < len(args); index++ {
		if args[index] == "--input" {
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "demo_repro: --input needs a path")
				return gate.Broken
			}
			input = args[index]
			continue
		}
		fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[index])
		return gate.Broken
	}
	if input == "" {
		fmt.Fprintln(os.Stderr, "demo_repro: measure needs --input")
		return gate.Broken
	}
	m, err := measure(input)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	data, err := pycompat.JSONDumpASCII(m.asJSON(), "  ")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	fmt.Println(string(data))
	return gate.Green
}

// comma renders an integer with the thousands separators python's `:,`
// format puts in.
func comma(value int) string {
	text := strconv.Itoa(value)
	negative := strings.HasPrefix(text, "-")
	if negative {
		text = text[1:]
	}
	var out strings.Builder
	for index, r := range text {
		if index > 0 && (len(text)-index)%3 == 0 {
			out.WriteByte(',')
		}
		out.WriteRune(r)
	}
	if negative {
		return "-" + out.String()
	}
	return out.String()
}

// sameInts reports whether two int lists hold the same values.
func sameInts(a, b []int) bool {
	if len(a) != len(b) {
		return false
	}
	for index := range a {
		if a[index] != b[index] {
			return false
		}
	}
	return true
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

// isLowerHex reports whether the text is a lowercase hexadecimal digest.
func isLowerHex(text string) bool {
	for _, r := range text {
		if !((r >= '0' && r <= '9') || (r >= 'a' && r <= 'f')) {
			return false
		}
	}
	return true
}

// jsonTypeName is the name python would print for a JSON value's type.
func jsonTypeName(value any) string {
	switch value.(type) {
	case nil:
		return "NoneType"
	case bool:
		return "bool"
	case float64:
		return "int"
	case string:
		return "str"
	case []any:
		return "list"
	case map[string]any:
		return "dict"
	}
	return "object"
}

// jsonReprValue renders a JSON value the way python's repr() would.
func jsonReprValue(value any) string {
	switch typed := value.(type) {
	case nil:
		return "None"
	case bool:
		if typed {
			return "True"
		}
		return "False"
	case float64:
		if typed == float64(int64(typed)) {
			return strconv.FormatInt(int64(typed), 10)
		}
		return strconv.FormatFloat(typed, 'g', -1, 64)
	case string:
		return pycompat.Repr(typed)
	}
	data, err := json.Marshal(value)
	if err != nil {
		return ""
	}
	return string(data)
}

// appendFile appends to a file, creating it when it is not there.
func appendFile(path string, text string) {
	handle, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	defer handle.Close()
	handle.WriteString(text)
}
