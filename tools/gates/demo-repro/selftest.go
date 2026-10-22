package demorepro

// selftest.go -- the tool's own proof that it can fail.
//
// The python original had no self-test: the tool runs ffmpeg and reads real
// recordings, and the drift gate is the reader that catches a wrong answer.
// The port adds one, because a tool that stops reading a recording reports a
// green demo for a recording it never looked at.
//
// The fixtures are small and hand-made:
//   1. the lock parser reads a KEY=VALUE file the way the shell sources it;
//   2. the GIF walk reads a one-pixel GIF: its shape, its frame count and
//      its digests;
//   3. a file that is not a GIF is refused;
//   4. the frame collapse turns a digest sequence into runs;
//   5. the manifest checker names the field a baseline entry is missing.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// onePixelGif is a one-pixel GIF with a single frame.
//
// GIF89a, a 1x1 raster, a six-byte global colour table, a graphic control
// extension with a zero delay, one image descriptor, and the trailer.
var onePixelGif = []byte{
	'G', 'I', 'F', '8', '9', 'a',
	0x01, 0x00, 0x01, 0x00,
	0x80, 0x00, 0x00,
	0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
	0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,
	0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
	0x02,
	0x02, 0x44, 0x01,
	0x00,
	0x3B,
}

func selfTestRun() int {
	var failures []string
	fail := func(message string) {
		failures = append(failures, message)
	}

	root, err := os.MkdirTemp("", "demo-repro-selftest-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	// 1. The lock parser.
	lockPath := filepath.Join(root, "toolchain.lock")
	os.WriteFile(lockPath, []byte(
		"# a comment\n"+
			"GIF_FRAMES=432\n"+
			"GIF_FPS=12\n"+
			"QUOTED=\"a value with spaces\"\n"+
			"SINGLE='single'\n"+
			"PROMPT=echo \\$HOME\n"+
			"NOEQUALS\n"), 0o644)
	lock, err := readLock(lockPath)
	if err != nil {
		fail("the lock could not be read: " + err.Error())
	} else {
		if lock["GIF_FRAMES"] != "432" {
			fail("the lock's GIF_FRAMES is " + lock["GIF_FRAMES"])
		}
		if lock["QUOTED"] != "a value with spaces" {
			fail("the lock's QUOTED is " + lock["QUOTED"])
		}
		if lock["SINGLE"] != "single" {
			fail("the lock's SINGLE is " + lock["SINGLE"])
		}
		if lock["PROMPT"] != "echo $HOME" {
			fail("the lock's escaped dollar was not unescaped: " + lock["PROMPT"])
		}
		if _, present := lock["NOEQUALS"]; present {
			fail("a line without an equals sign became a value")
		}
	}

	// 2. The GIF walk.
	gif := filepath.Join(root, "one.gif")
	os.WriteFile(gif, onePixelGif, 0o644)
	m, err := measure(gif)
	if err != nil {
		fail("the one-pixel GIF could not be measured: " + err.Error())
	} else {
		if m.Width != 1 || m.Height != 1 {
			fail(fmt.Sprintf("the one-pixel GIF measures %dx%d", m.Width, m.Height))
		}
		if m.Frames != 1 {
			fail(fmt.Sprintf("the one-pixel GIF has %d frame(s), expected 1", m.Frames))
		}
		if m.UniqueFrames != 1 {
			fail(fmt.Sprintf("the one-pixel GIF has %d unique frame(s), expected 1", m.UniqueFrames))
		}
		if len(m.Delays) != 1 || m.Delays[0] != 0 {
			fail(fmt.Sprintf("the one-pixel GIF's delays are %v, expected [0]", m.Delays))
		}
		if m.DurationMs != 0 {
			fail(fmt.Sprintf("the one-pixel GIF lasts %d ms, expected 0", m.DurationMs))
		}
		if len(m.FileSha256) != 64 || len(m.FramesSha256) != 64 {
			fail("the digests are not sha256 hex")
		}
		if m.Bytes != len(onePixelGif) {
			fail(fmt.Sprintf("the one-pixel GIF measures %d bytes, expected %d", m.Bytes, len(onePixelGif)))
		}
	}

	// 3. A file that is not a GIF is refused.
	notGif := filepath.Join(root, "not.gif")
	os.WriteFile(notGif, []byte("this is not a GIF at all"), 0o644)
	if _, err := measure(notGif); err == nil {
		fail("a file that is not a GIF was measured")
	} else if !strings.Contains(err.Error(), "not a GIF") {
		fail("the refusal of a non-GIF did not say so: " + err.Error())
	}

	// 4. The frame collapse.
	runs := collapse([]string{"a", "a", "a", "b", "b", "c"})
	if len(runs) != 3 {
		fail(fmt.Sprintf("the collapse produced %d run(s), expected 3", len(runs)))
	} else {
		if runs[0][0] != 0 || runs[0][1] != 3 {
			fail(fmt.Sprintf("the first run is %v, expected [0 3]", runs[0]))
		}
		if runs[1][0] != 3 || runs[1][1] != 2 {
			fail(fmt.Sprintf("the second run is %v, expected [3 2]", runs[1]))
		}
		if runs[2][0] != 5 || runs[2][1] != 1 {
			fail(fmt.Sprintf("the third run is %v, expected [5 1]", runs[2]))
		}
	}

	// 5. The manifest checker names the field a baseline entry is missing.
	manifest := filepath.Join(root, "baseline.json")
	os.WriteFile(manifest, []byte(
		`{"gifs": {"hero": {"bytes": 10, "width": 1, "height": 1, "frames": 2, `+
			`"durationMs": 100, "uniqueFrames": 2, "fileSha256": "`+
			strings.Repeat("a", 64)+`"}}}`), 0o644)
	code, text := checkManifest(manifest)
	if code == 0 {
		fail("a manifest without a framesSha256 was accepted")
	}
	if !strings.Contains(text, "has no `framesSha256`") {
		fail("the manifest finding did not name the missing field")
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the tool reads the lock, walks a GIF, refuses a file that " +
		"is not one, collapses a frame sequence into runs, and names the field a " +
		"baseline entry is missing")
	return gate.Green
}
