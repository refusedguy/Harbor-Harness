// Package gate holds what every compiled gate in this repository shares:
// the exit codes, the repository root, the tracked-file list, and the small
// helpers that read a file without copying it more than once.
package gate

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

// The exit codes. The contract is fixed for every gate, and the workflows
// read it:
//
//	0  green  - the gate ran and found nothing
//	1  red    - the gate ran and found something
//	2  broken - the gate could not run: a bad invocation, a missing input,
//	            an unreadable file, a broken toolchain
//
// "Could not run" is NOT "found nothing". A gate that cannot start must not
// look like a gate that found nothing, because a red build that says "I
// could not run" is a fixable signal and a green build that says "I could
// not run" is a lie.
const (
	Green  = 0
	Red    = 1
	Broken = 2
)

// RepoRoot is the repository the gate runs in.
//
// The gates run from the repository root in every workflow, and the root is
// found by walking up from the working directory until a directory holds the
// marker the repository always has. The binary lives in the repository
// (tools/bin/), so walking up from the binary's own path would also work,
// but the working directory is what the python originals used and what the
// workflows set.
func RepoRoot() (string, error) {
	dir, err := os.Getwd()
	if err != nil {
		return "", err
	}
	for {
		if isRepoRoot(dir) {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", fmt.Errorf("no repository root above %s", dir)
		}
		dir = parent
	}
}

func isRepoRoot(dir string) bool {
	for _, marker := range []string{"Harbor.slnx", "tools", "docs"} {
		if _, err := os.Stat(filepath.Join(dir, marker)); err != nil {
			return false
		}
	}
	return true
}

// TrackedFiles lists the paths git tracks, filtered by suffix.
//
// The INDEX, not a glob: an untracked scratch file must not be able to
// break the gate, and a tracked file must not be able to hide from it.
// This is the same reason tools/md_gate.py asks git rather than walking
// the tree, and the same reason the gate is portable: it asks the same
// question the python originals ask.
//
// An empty suffix list means every tracked file.
func TrackedFiles(root string, suffixes ...string) ([]string, error) {
	cmd := exec.Command("git", "ls-files")
	cmd.Dir = root
	cmd.Env = gitEnv()
	out, err := cmd.Output()
	if err != nil {
		return nil, fmt.Errorf("git ls-files failed: %w", err)
	}
	var paths []string
	for _, line := range strings.Split(string(out), "\n") {
		if line == "" {
			continue
		}
		if len(suffixes) == 0 || hasAnySuffix(line, suffixes) {
			paths = append(paths, line)
		}
	}
	sort.Strings(paths)
	return paths, nil
}

func hasAnySuffix(path string, suffixes []string) bool {
	for _, suffix := range suffixes {
		if strings.HasSuffix(path, suffix) {
			return true
		}
	}
	return false
}

// gitEnv is the environment for the gate's own git calls: never prompt, and
// never inherit a terminal. A CI runner and an interactive shell get the
// same behaviour.
func gitEnv() []string {
	env := os.Environ()
	out := make([]string, 0, len(env)+1)
	for _, kv := range env {
		if strings.HasPrefix(kv, "GIT_TERMINAL_PROMPT=") {
			continue
		}
		out = append(out, kv)
	}
	return append(out, "GIT_TERMINAL_PROMPT=0")
}

// ReadLines reads a file and returns its lines without their line endings.
//
// The python originals read the file as text and split it; this reads the
// bytes and splits on \n, which is the same for every file in this
// repository (they are UTF-8 with LF endings, and .editorconfig says so).
// A trailing \r is kept, because a file with CRLF endings is a fact about
// that file and the gate must see it.
func ReadLines(path string) ([]string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	text := string(data)
	if text == "" {
		return nil, nil
	}
	lines := strings.Split(text, "\n")
	if lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	return lines, nil
}

// ReadText reads a file as TEXT, the way the python originals read it with
// `open(path, encoding="utf-8", errors="replace")`.
//
// Three things happen that ReadLines does not do, and the gates that read
// documents rather than source code depend on all three:
//
//   - invalid UTF-8 becomes U+FFFD instead of an error;
//   - CRLF and a lone CR become LF, because python's text mode translates
//     them (universal newlines) and the line numbers a gate reports are
//     counted on the translated text;
//   - the file is read as one string, so a caller can match across lines.
func ReadText(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	text := strings.ToValidUTF8(string(data), "�")
	text = strings.ReplaceAll(text, "\r\n", "\n")
	text = strings.ReplaceAll(text, "\r", "\n")
	return text, nil
}

// SplitLinesKeepEnds splits text into lines the way python's
// `str.splitlines(keepends=True)` does.
//
// The boundaries are python's, not Go's: LF, CR, CRLF (as one), VT, FF,
// FS, GS, RS, NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR. The line ENDING
// is kept with the line.
//
// Go's `strings.SplitAfter(text, "\n")` knows only LF, and the markdown
// gates count lines and report them, so the difference is a difference in
// the verdict.
func SplitLinesKeepEnds(text string) []string {
	var out []string
	start := 0
	runes := []rune(text)
	for i := 0; i < len(runes); i++ {
		width := 1
		switch runes[i] {
		case '\r':
			if i+1 < len(runes) && runes[i+1] == '\n' {
				width = 2
			}
		case '\n', '\v', '\f', 0x1c, 0x1d, 0x1e, 0x85, 0x2028, 0x2029:
		default:
			continue
		}
		out = append(out, string(runes[start:i+width]))
		start = i + width
	}
	if start < len(runes) {
		out = append(out, string(runes[start:]))
	}
	return out
}

// SplitLines splits text into lines the way python's `str.splitlines()`
// does: the same boundaries as SplitLinesKeepEnds, without the endings.
//
// A text that ends with a line ending does NOT produce a trailing empty
// line, which is where this differs from `strings.Split(text, "\n")`.
func SplitLines(text string) []string {
	ends := SplitLinesKeepEnds(text)
	out := make([]string, 0, len(ends))
	for _, line := range ends {
		out = append(out, trimLineEnd(line))
	}
	return out
}

// trimLineEnd removes the line ending from a line.
func trimLineEnd(line string) string {
	runes := []rune(line)
	end := len(runes)
	for end > 0 {
		switch runes[end-1] {
		case '\n', '\r', '\v', '\f', 0x1c, 0x1d, 0x1e, 0x85, 0x2028, 0x2029:
			end--
			continue
		}
		break
	}
	return string(runes[:end])
}

// ScanLines calls fn for every line of a file, with the line number.
//
// The line number is 1-based, the way a file:line citation counts.
func ScanLines(path string, fn func(lineNo int, line string) error) error {
	fh, err := os.Open(path)
	if err != nil {
		return err
	}
	defer fh.Close()
	reader := bufio.NewReaderSize(fh, 1<<16)
	lineNo := 0
	for {
		line, err := reader.ReadString('\n')
		if len(line) > 0 {
			lineNo++
			trimmed := strings.TrimSuffix(line, "\n")
			if ferr := fn(lineNo, trimmed); ferr != nil {
				return ferr
			}
		}
		if err != nil {
			if err == io.EOF {
				return nil
			}
			return err
		}
	}
}
