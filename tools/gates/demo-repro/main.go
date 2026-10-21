// Package demorepro is the compiled port of tools/demo_repro.py.
//
// It holds the README demo GIFs to a deterministic envelope: the frame
// pacing is re-emitted from the toolchain lock, every tape is recorded
// twice and the two passes are compared, and the baseline manifest is
// checked for the fields the drift gate is about to read.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package demorepro

import (
	"crypto/sha256"
	"encoding/hex"
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

// The tapes the repository ships, and the scene each one records.
var (
	tapes      = []string{"hero", "markdown", "approval", "plain"}
	tapeScenes = map[string]string{"hero": "hero", "markdown": "markdown", "approval": "approval", "plain": "hero"}
)

// Run is the tool's entry point.
//
// The python original had SUBCOMMANDS (`normalize`, `compare`, `report`,
// `measure`, `check-manifest`), and the subcommand is the first argument.
func Run(args []string) int {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "demo_repro: a subcommand is required: normalize, compare, report, measure, check-manifest")
		return gate.Broken
	}
	command := args[0]
	rest := args[1:]

	switch command {
	case "--self-test", "self-test":
		return selfTestRun()
	case "normalize":
		return runNormalize(rest)
	case "compare":
		return runCompare(rest)
	case "report":
		return runReport(rest)
	case "check-manifest":
		return runCheckManifest(rest)
	case "measure":
		return runMeasure(rest)
	default:
		fmt.Fprintf(os.Stderr, "demo_repro: unknown subcommand: %s\n", command)
		return gate.Broken
	}
}

// readLock parses the KEY=VALUE toolchain lock (the same file the shell
// scripts source).
func readLock(path string) (map[string]string, error) {
	if info, err := os.Stat(path); err != nil || info.IsDir() {
		return nil, fmt.Errorf("demo_repro: missing %s", path)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("demo_repro: missing %s", path)
	}
	values := map[string]string{}
	for _, raw := range pycompat.SplitLines(string(data)) {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") || !strings.Contains(line, "=") {
			continue
		}
		key, raw, _ := strings.Cut(line, "=")
		value := strings.TrimSpace(raw)
		if len(value) >= 2 && value[0] == value[len(value)-1] && (value[0] == '"' || value[0] == '\'') {
			value = value[1 : len(value)-1]
		}
		// The lock escapes a literal `$` in the prompt so sourcing it is safe.
		values[strings.TrimSpace(key)] = strings.ReplaceAll(value, "\\$", "$")
	}
	return values, nil
}

// lockPath is the toolchain lock of the repository.
func lockPath() string {
	root, err := gate.BinaryRoot()
	if err != nil {
		return filepath.Join("demo", "toolchain.lock")
	}
	return filepath.Join(root, "demo", "toolchain.lock")
}

// repoRoot is the repository the tool works on.
func repoRoot() string {
	root, err := gate.BinaryRoot()
	if err != nil {
		return "."
	}
	return root
}

// ------------------------------------------------------------------------ the GIF

// subBlocks reads a GIF sub-block chain: the size byte, then that many
// bytes, until the zero size byte.
func subBlocks(buf []byte, i int) ([]byte, int, error) {
	out := []byte{}
	for {
		if i >= len(buf) {
			return nil, 0, fmt.Errorf("truncated GIF: sub-block chain runs past end of file")
		}
		size := int(buf[i])
		i++
		if size == 0 {
			return out, i, nil
		}
		if i+size > len(buf) {
			return nil, 0, fmt.Errorf("truncated GIF: sub-block runs past end of file")
		}
		out = append(out, buf[i:i+size]...)
		i += size
	}
}

// measurement is what the walk of a GIF found.
type measurement struct {
	Path         string `json:"path"`
	Bytes        int    `json:"bytes"`
	Width        int    `json:"width"`
	Height       int    `json:"height"`
	Frames       int    `json:"frames"`
	DurationMs   int    `json:"durationMs"`
	Delays       []int  `json:"delays"`
	UniqueFrames int    `json:"uniqueFrames"`
	FileSha256   string `json:"fileSha256"`
	FramesSha256 string `json:"framesSha256"`
	digests      []string
}

// measure walks a GIF block by block: size, shape, per-frame delay and
// payload digests.
//
// Note on hashing: the digest is over the *raw LZW payload*, so it reflects
// the image data and the palette the encoder chose — not the container.
func measure(path string) (*measurement, error) {
	buf, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	if len(buf) < 6 || (string(buf[:6]) != "GIF89a" && string(buf[:6]) != "GIF87a") {
		signature := ""
		if len(buf) >= 6 {
			signature = string(buf[:6])
		}
		return nil, fmt.Errorf("not a GIF (bad signature %s)", pycompat.Repr(signature))
	}
	if len(buf) < 13 {
		return nil, fmt.Errorf("truncated GIF: the header runs past end of file")
	}

	packed := buf[10]
	i := 13
	if packed&0x80 != 0 {
		i += 3 * (1 << ((int(packed) & 0x07) + 1))
	}

	var digests []string
	var delays []int
	delayCS := 0

	for i < len(buf) && buf[i] != 0x3B { // 0x3B = trailer
		marker := buf[i]
		if marker == 0x21 { // extension
			if i+2 > len(buf) {
				return nil, fmt.Errorf("truncated GIF: extension introducer runs past end of file")
			}
			if buf[i+1] == 0xF9 { // graphic control -> next frame delay
				if i+6 > len(buf) {
					return nil, fmt.Errorf("truncated GIF: the graphic control runs past end of file")
				}
				delayCS = int(buf[i+4]) | int(buf[i+5])<<8
			}
			_, next, err := subBlocks(buf, i+2)
			if err != nil {
				return nil, err
			}
			i = next
			continue
		}
		if marker != 0x2C { // 0x2C = image descriptor
			return nil, fmt.Errorf("unexpected GIF block 0x%02X at offset %d", marker, i)
		}
		if i+10 > len(buf) {
			return nil, fmt.Errorf("truncated GIF: the image descriptor runs past end of file")
		}
		local := buf[i+9]
		i += 10
		if local&0x80 != 0 {
			i += 3 * (1 << ((int(local) & 0x07) + 1))
		}
		i++
		payload, next, err := subBlocks(buf, i)
		if err != nil {
			return nil, err
		}
		i = next
		digest := sha256.Sum256(payload)
		digests = append(digests, hex.EncodeToString(digest[:]))
		delays = append(delays, delayCS)
		delayCS = 0
	}

	if len(digests) == 0 {
		return nil, fmt.Errorf("truncated GIF: no image frames found")
	}

	rollup := sha256.New()
	for _, digest := range digests {
		rollup.Write([]byte(digest))
	}
	fileDigest := sha256.Sum256(buf)

	unique := map[string]bool{}
	for _, digest := range digests {
		unique[digest] = true
	}
	uniqueDelays := map[int]bool{}
	total := 0
	for _, delay := range delays {
		uniqueDelays[delay] = true
		total += delay
	}
	delaysSorted := make([]int, 0, len(uniqueDelays))
	for delay := range uniqueDelays {
		delaysSorted = append(delaysSorted, delay)
	}
	sort.Ints(delaysSorted)

	return &measurement{
		Path:         path,
		Bytes:        len(buf),
		Width:        int(buf[6]) | int(buf[7])<<8,
		Height:       int(buf[8]) | int(buf[9])<<8,
		Frames:       len(digests),
		DurationMs:   total * 10,
		Delays:       delaysSorted,
		UniqueFrames: len(unique),
		FileSha256:   hex.EncodeToString(fileDigest[:]),
		FramesSha256: hex.EncodeToString(rollup.Sum(nil)),
		digests:      digests,
	}, nil
}

// screenSimilarity is the fraction of pixels that differ by more than
// `threshold` levels (0-255).
//
// A hash is the wrong tool here. Three layers of encoder noise sit on top of
// the actual image, and the innermost one is the dither. Thresholding at 24
// levels ignores the dither entirely while still catching anything actually
// visible.
func screenSimilarity(aPGM, bPGM []byte, threshold int) float64 {
	header := []byte("P5\n")
	if !strings.HasPrefix(string(aPGM), string(header)) || !strings.HasPrefix(string(bPGM), string(header)) {
		return 0
	}
	aBody, aOk := pgmBody(aPGM)
	bBody, bOk := pgmBody(bPGM)
	if !aOk || !bOk {
		return 0
	}
	if len(aBody) != len(bBody) || len(aBody) == 0 {
		return 0
	}
	differing := 0
	for index := range aBody {
		delta := int(aBody[index]) - int(bBody[index])
		if delta < 0 {
			delta = -delta
		}
		if delta > threshold {
			differing++
		}
	}
	return float64(differing) / float64(len(aBody))
}

// pgmBody is the pixels of a P5 image, past the header.
//
// The header is the magic, the whitespace-separated width and height, and a
// single space.
func pgmBody(blob []byte) ([]byte, bool) {
	rest := blob[len("P5\n"):]
	var fields [][]byte
	index := 0
	for len(fields) < 2 {
		for index < len(rest) && isSpace(rest[index]) {
			index++
		}
		start := index
		for index < len(rest) && !isSpace(rest[index]) {
			index++
		}
		fields = append(fields, rest[start:index])
	}
	if index+1 > len(rest) {
		return nil, false
	}
	return rest[index+1:], true
}

// isSpace reports whether the byte is whitespace, the way python's
// bytes.isspace() sees it.
func isSpace(b byte) bool {
	switch b {
	case ' ', '\t', '\n', '\r', '\v', '\f':
		return true
	}
	return false
}

// decodedScreens are the decoded greyscale frames, one per distinct screen
// shown.
//
// The GIF palette and the dither are dropped by decoding to greyscale: the
// result is a function of what is on screen, not of the encoder's choices.
// Consecutive duplicates are collapsed, so the caller compares *screens*, not
// frame counts.
func decodedScreens(gif, work string, width, height int) ([][]byte, error) {
	ffmpeg := ffmpegPath()
	framesDir := filepath.Join(work, "px-"+strings.ReplaceAll(gif, "/", "_"))
	if err := os.MkdirAll(framesDir, 0o755); err != nil {
		return nil, err
	}
	command := exec.Command(ffmpeg, "-y", "-loglevel", "error", "-i", gif,
		"-vf", fmt.Sprintf("scale=%d:%d:flags=neighbor,format=gray", width, height),
		"-f", "image2", "-c:v", "pgm", filepath.Join(framesDir, "f%05d.pgm"))
	var stderr strings.Builder
	command.Stderr = &stderr
	if err := command.Run(); err != nil {
		return nil, fmt.Errorf("demo_repro: could not decode %s:\n%s", gif, strings.TrimSpace(stderr.String()))
	}

	entries, err := os.ReadDir(framesDir)
	if err != nil {
		return nil, err
	}
	var names []string
	for _, entry := range entries {
		if !entry.IsDir() && strings.HasSuffix(entry.Name(), ".pgm") {
			names = append(names, entry.Name())
		}
	}
	sort.Strings(names)

	var screens [][]byte
	var previous []byte
	for _, name := range names {
		pixels, err := os.ReadFile(filepath.Join(framesDir, name))
		if err != nil {
			continue
		}
		if previous == nil || screenSimilarity(previous, pixels, 24) > 0.0005 {
			screens = append(screens, pixels)
			previous = pixels
		}
	}
	os.RemoveAll(framesDir)
	return screens, nil
}

// ffmpegPath is the ffmpeg the tool invokes.
func ffmpegPath() string {
	if path, err := exec.LookPath("ffmpeg"); err == nil {
		return path
	}
	return "ffmpeg"
}

// ffmpeg runs ffmpeg and fails loudly when it does.
func ffmpeg(args []string, what string) error {
	ffmpeg := ffmpegPath()
	command := exec.Command(ffmpeg, append([]string{"-y", "-loglevel", "error"}, args...)...)
	var stderr strings.Builder
	command.Stderr = &stderr
	if err := command.Run(); err != nil {
		return fmt.Errorf("demo_repro: ffmpeg failed while %s:\n%s", what, strings.TrimSpace(stderr.String()))
	}
	return nil
}

// ------------------------------------------------------------------------ normalize

// collapse collapses a frame sequence into (representative index, run length)
// runs.
//
// A screen recording repeats each visible screen for an arbitrary number of
// frames — that count is wall-clock and cannot be pinned. The *sequence of
// screens* can be, because it is a function of the text that was written.
func collapse(digests []string) [][2]int {
	var runs [][2]int
	for index, digest := range digests {
		if len(runs) > 0 && digests[runs[len(runs)-1][0]] == digest {
			runs[len(runs)-1][1]++
			continue
		}
		runs = append(runs, [2]int{index, 1})
	}
	return runs
}

// normalizeResult is what the normalize subcommand prints when it re-encodes.
type normalizeResult struct {
	Input        string   `json:"input"`
	Output       string   `json:"output"`
	SourceFrames int      `json:"sourceFrames"`
	Screens      int      `json:"screens"`
	Frames       int      `json:"frames"`
	Gif          *gifJSON `json:"gif"`
}

// normalizeDryRun is what the normalize subcommand prints with --dry-run.
type normalizeDryRun struct {
	Input        string `json:"input"`
	States       []int  `json:"states"`
	FramesNeeded int    `json:"framesNeeded"`
	Budget       int    `json:"budget"`
	SourceFrames int    `json:"sourceFrames"`
}

// gifJSON is the shape of a measured GIF, as the report prints it.
type gifJSON struct {
	Path         string `json:"path"`
	Bytes        int    `json:"bytes"`
	Width        int    `json:"width"`
	Height       int    `json:"height"`
	Frames       int    `json:"frames"`
	DurationMs   int    `json:"durationMs"`
	Delays       []int  `json:"delays"`
	UniqueFrames int    `json:"uniqueFrames"`
	FileSha256   string `json:"fileSha256"`
	FramesSha256 string `json:"framesSha256"`
}

// asJSON is the measured GIF without the digests, the way python pops them.
func (m *measurement) asJSON() *gifJSON {
	return &gifJSON{
		Path:         m.Path,
		Bytes:        m.Bytes,
		Width:        m.Width,
		Height:       m.Height,
		Frames:       m.Frames,
		DurationMs:   m.DurationMs,
		Delays:       m.Delays,
		UniqueFrames: m.UniqueFrames,
		FileSha256:   m.FileSha256,
		FramesSha256: m.FramesSha256,
	}
}

// normalize re-emits a capture at the pinned cadence, rebuilding it from its
// screen sequence.
func normalize(inputPath, outputPath string, lock map[string]string, dryRun bool) (any, error) {
	frames, err := lockInt(lock, "GIF_FRAMES")
	if err != nil {
		return nil, err
	}
	fps, err := lockInt(lock, "GIF_FPS")
	if err != nil {
		return nil, err
	}
	width, err := lockInt(lock, "GIF_WIDTH")
	if err != nil {
		return nil, err
	}
	height, err := lockInt(lock, "GIF_HEIGHT")
	if err != nil {
		return nil, err
	}
	stateFrames, err := lockInt(lock, "GIF_STATE_FRAMES")
	if err != nil {
		return nil, err
	}
	tailFrames, err := lockInt(lock, "GIF_TAIL_FRAMES")
	if err != nil {
		return nil, err
	}

	before, err := measure(inputPath)
	if err != nil {
		return nil, err
	}
	runs := collapse(before.digests)
	states := make([]int, 0, len(runs))
	for _, run := range runs {
		states = append(states, run[0])
	}
	needed := len(states)*stateFrames + tailFrames
	if needed > frames {
		return nil, fmt.Errorf(
			"demo_repro: %s has %d distinct screens, which needs %d frames at %d frames/screen "+
				"but the budget is %d — raise GIF_FRAMES/GIF_STATE_FRAMES in demo/toolchain.lock, "+
				"or the scene grew",
			inputPath, len(states), needed, stateFrames, frames)
	}
	if dryRun {
		return &normalizeDryRun{
			Input:        inputPath,
			States:       states,
			FramesNeeded: needed,
			Budget:       frames,
			SourceFrames: before.Frames,
		}, nil
	}

	work, err := os.MkdirTemp("", "demo-repro-")
	if err != nil {
		return nil, err
	}
	defer os.RemoveAll(work)
	framesDir := filepath.Join(work, "frames")
	if err := os.MkdirAll(framesDir, 0o755); err != nil {
		return nil, err
	}

	// Materialise the paced frame list: one PNG per distinct screen, then a
	// hard link per frame that screen is held for. Doing this by hand is what
	// makes the frame count and duration exact.
	parts := make([]string, 0, len(states))
	for _, index := range states {
		parts = append(parts, fmt.Sprintf("eq(n\\,%d)", index))
	}
	selectExpr := strings.Join(parts, "+")
	if err := ffmpeg([]string{
		"-i", inputPath,
		"-vf", fmt.Sprintf("select='%s',scale=%d:%d:flags=lanczos", selectExpr, width, height),
		"-fps_mode", "passthrough", "-c:v", "png", "-f", "image2",
		filepath.Join(framesDir, "screen-%04d.png"),
	}, "extracting the screen sequence of "+inputPath); err != nil {
		return nil, err
	}

	entries, err := os.ReadDir(framesDir)
	if err != nil {
		return nil, err
	}
	var screens []string
	for _, entry := range entries {
		if !entry.IsDir() && strings.HasPrefix(entry.Name(), "screen-") && strings.HasSuffix(entry.Name(), ".png") {
			screens = append(screens, filepath.Join(framesDir, entry.Name()))
		}
	}
	sort.Strings(screens)
	if len(screens) != len(states) {
		return nil, fmt.Errorf("demo_repro: expected %d screens from %s, ffmpeg produced %d",
			len(states), inputPath, len(screens))
	}

	position := 0
	for index, screen := range screens {
		held := stateFrames
		if index == len(screens)-1 {
			held = stateFrames + tailFrames
		}
		for count := 0; count < held; count++ {
			target := filepath.Join(framesDir, fmt.Sprintf("frame-%05d.png", position))
			if err := os.Link(screen, target); err != nil {
				return nil, err
			}
			position++
		}
	}

	// A fixed input framerate over an explicitly sized frame list is the only
	// way to land on an exact frame count *and* an exact duration.
	if err := ffmpeg([]string{
		"-framerate", strconv.Itoa(fps), "-i", filepath.Join(framesDir, "frame-%05d.png"),
		"-vf", "split[a][b];[a]palettegen=max_colors=256:stats_mode=diff[p];" +
			"[b][p]paletteuse=dither=bayer:bayer_scale=3",
		"-loop", "0", outputPath,
	}, "re-pacing "+inputPath); err != nil {
		return nil, err
	}

	result, err := measure(outputPath)
	if err != nil {
		return nil, err
	}
	expectedFrames := len(states)*stateFrames + tailFrames
	// The GIF muxer quantises per-frame delays to centiseconds, so 12 fps lands
	// on 8.3 cs and the total is only defined to within a frame's worth of
	// rounding.
	if result.Frames != expectedFrames {
		return nil, fmt.Errorf(
			"demo_repro: %s has %d frames, expected %d (%d screens x %d + %d tail) — "+
				"the re-pacing is off",
			outputPath, result.Frames, expectedFrames, len(states), stateFrames, tailFrames)
	}
	if result.Frames > frames {
		return nil, fmt.Errorf("demo_repro: %s has %d frames, over the %d-frame budget",
			outputPath, result.Frames, frames)
	}
	expectedDuration := float64(expectedFrames) * 1000 / float64(fps)
	if float64(abs(result.DurationMs-int(expectedDuration))) > float64(1000/fps)+1 {
		return nil, fmt.Errorf(
			"demo_repro: %s is %d ms, expected ~%.0f ms at %d fps — the re-pacing is off",
			outputPath, result.DurationMs, expectedDuration, fps)
	}
	return &normalizeResult{
		Input:        inputPath,
		Output:       outputPath,
		SourceFrames: before.Frames,
		Screens:      len(states),
		Frames:       result.Frames,
		Gif:          result.asJSON(),
	}, nil
}

// abs is the absolute value of an int.
func abs(value int) int {
	if value < 0 {
		return -value
	}
	return value
}

// lockInt reads an integer out of the lock.
func lockInt(lock map[string]string, key string) (int, error) {
	text, ok := lock[key]
	if !ok {
		return 0, fmt.Errorf("demo_repro: the toolchain lock has no %s", key)
	}
	value, err := strconv.Atoi(strings.TrimSpace(text))
	if err != nil {
		return 0, fmt.Errorf("demo_repro: the toolchain lock's %s is %s, not a number",
			key, pycompat.Repr(text))
	}
	return value, nil
}
