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
// The suffix becomes a PATHSPEC, the way the python originals wrote it:
// `git ls-files "*.cs"` asks git for the `.cs` files and lets git do the
// filtering. Asking git for everything and filtering here is the same
// answer and more work — and the pathspec is what git is fast at.
func TrackedFiles(root string, suffixes ...string) ([]string, error) {
	args := []string{"ls-files"}
	for _, suffix := range suffixes {
		args = append(args, "*."+strings.TrimPrefix(suffix, "."))
	}
	cmd := exec.Command("git", args...)
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
	if !strings.ContainsRune(text, '\r') {
		return text, nil
	}
	// The line endings in ONE pass: CRLF is one break, and a lone CR is a
	// break too. Two passes would copy the text twice for nothing.
	var b strings.Builder
	b.Grow(len(text))
	for i := 0; i < len(text); i++ {
		if text[i] == '\r' {
			if i+1 < len(text) && text[i+1] == '\n' {
				i++
			}
			b.WriteByte('\n')
			continue
		}
		b.WriteByte(text[i])
	}
	return b.String(), nil
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
	for i := 0; i < len(text); {
		width := 0
		switch text[i] {
		case '\n', '\v', '\f', 0x1c, 0x1d, 0x1e:
			width = 1
		case '\r':
			width = 1
			if i+1 < len(text) && text[i+1] == '\n' {
				width = 2
			}
		case 0xc2:
			// U+0085 NEXT LINE.
			if i+1 < len(text) && text[i+1] == 0x85 {
				width = 2
			}
		case 0xe2:
			// U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR.
			if i+2 < len(text) && text[i+1] == 0x80 &&
				(text[i+2] == 0xa8 || text[i+2] == 0xa9) {
				width = 3
			}
		}
		if width == 0 {
			i++
			continue
		}
		out = append(out, text[start:i+width])
		i += width
		start = i
	}
	if start < len(text) {
		out = append(out, text[start:])
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
