package mergeloss

// selftest.go -- the gate's own proof that it can fail.
//
// The fixture is a throwaway repository with one squash-merge: the merge
// base, a pull request branch that adds a line, a `main` that adds a line of
// its own, and the squash-merge that lands the pull request on `main`.
//
// Four states, and the gate must tell them apart:
//   1. the merge takes the pull request's version of the file and drops
//      `main`'s line — the failure this gate exists to catch;
//   2. the merge keeps every line — a clean merge;
//   3. the merge keeps the line and a LATER ORDINARY commit removes it on
//      purpose — not a loss, because somebody asked for it to go;
//   4. the whole gate, on a repository that drops a line — RED.
//
// The third state is the one that exercises the history: the line was
// removed by an ordinary commit, so the gate has to walk the history and
// ask who removed it. The history is read in ONE `git log` for every path
// at once, and the answer must be the same answer the one-log-per-path way
// gave.
//
// The fifth state is the one that exercises the OTHER half of the batching:
// two files, each with a line an ordinary commit removed, and the merge
// drops a line from both. One walk of the history answers for both paths,
// and the answer for one path must not leak into the other.

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// gitIn runs a git command in a directory.
func gitIn(dir string, args ...string) {
	command := exec.Command("git", args...)
	command.Dir = dir
	command.Env = append(os.Environ(), "GIT_CONFIG_GLOBAL=/dev/null", "GIT_CONFIG_SYSTEM=/dev/null")
	command.Run()
}

// gitTextIn runs a git command in a directory and returns its stdout.
func gitTextIn(dir string, args ...string) string {
	command := exec.Command("git", args...)
	command.Dir = dir
	command.Env = append(os.Environ(), "GIT_CONFIG_GLOBAL=/dev/null", "GIT_CONFIG_SYSTEM=/dev/null")
	out, err := command.Output()
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(out))
}

// fixture writes a throwaway repository with one squash-merge.
func fixture(root string, keepDevLine bool) string {
	os.MkdirAll(root, 0o755)
	gitIn(root, "init", "-q", "-b", "main", ".")
	gitIn(root, "config", "user.name", "selftest")
	gitIn(root, "config", "user.email", "selftest@example.invalid")
	gitIn(root, "config", "commit.gpgsign", "false")

	write := func(text string) {
		os.WriteFile(filepath.Join(root, "doc.md"), []byte(text), 0o644)
	}

	write("# doc\n\nbase line\n")
	gitIn(root, "add", "doc.md")
	gitIn(root, "commit", "-q", "-m", "base")

	gitIn(root, "checkout", "-q", "-b", "pr")
	write("# doc\n\nbase line\npull request line\n")
	gitIn(root, "commit", "-q", "-am", "pull request work")
	prHead := gitTextIn(root, "rev-parse", "HEAD")

	gitIn(root, "checkout", "-q", "main")
	write("# doc\n\nbase line\ndev line\n")
	gitIn(root, "commit", "-q", "-am", "dev work")

	if keepDevLine {
		write("# doc\n\nbase line\npull request line\ndev line\n")
	} else {
		write("# doc\n\nbase line\npull request line\n")
	}
	gitIn(root, "commit", "-q", "-am",
		"squash\n\nSquash-merged into dev (PR #4242, head "+prHead+").")
	return prHead
}

// deliberateFixture writes a repository where a line was removed on purpose,
// added back, and then dropped by the squash-merge.
//
// The line is in the file when the merge lands (the merge drops it), the tip
// does not have it, and an ORDINARY commit removed it once: the gate must
// read the history and say that somebody asked for it to go.
func deliberateFixture(root string) string {
	os.MkdirAll(root, 0o755)
	gitIn(root, "init", "-q", "-b", "main", ".")
	gitIn(root, "config", "user.name", "selftest")
	gitIn(root, "config", "user.email", "selftest@example.invalid")
	gitIn(root, "config", "commit.gpgsign", "false")

	write := func(text string) {
		os.WriteFile(filepath.Join(root, "doc.md"), []byte(text), 0o644)
	}

	write("# doc\n\nbase line\n")
	gitIn(root, "add", "doc.md")
	gitIn(root, "commit", "-q", "-m", "base")

	// An ordinary commit removes the base line, and another one adds it
	// back: the history knows the line, and knows that somebody took it
	// away on purpose once.
	write("# doc\n")
	gitIn(root, "commit", "-q", "-am", "an ordinary commit removes the base line")
	write("# doc\n\nbase line\n")
	gitIn(root, "commit", "-q", "-am", "an ordinary commit puts the base line back")

	gitIn(root, "checkout", "-q", "-b", "pr")
	write("# doc\n\nbase line\npull request line\n")
	gitIn(root, "commit", "-q", "-am", "pull request work")
	prHead := gitTextIn(root, "rev-parse", "HEAD")

	gitIn(root, "checkout", "-q", "main")
	// The merge takes the pull request's version of the file and drops the
	// base line with it.
	write("# doc\n\npull request line\n")
	gitIn(root, "commit", "-q", "-am",
		"squash\n\nSquash-merged into dev (PR #4343, head "+prHead+").")
	return prHead
}

// twoFilesFixture writes a repository with two files, each with a line an
// ordinary commit removed, and a merge that drops a line from both.
//
// The two paths are read by ONE walk of the history, and the answer for one
// path must not leak into the other: a line that only the first file ever
// had is not something the second file lost.
func twoFilesFixture(root string) string {
	os.MkdirAll(root, 0o755)
	gitIn(root, "init", "-q", "-b", "main", ".")
	gitIn(root, "config", "user.name", "selftest")
	gitIn(root, "config", "user.email", "selftest@example.invalid")
	gitIn(root, "config", "commit.gpgsign", "false")

	first := filepath.Join(root, "one.md")
	second := filepath.Join(root, "two.md")
	os.WriteFile(first, []byte("# one\n\none line\n"), 0o644)
	os.WriteFile(second, []byte("# two\n\ntwo line\n"), 0o644)
	gitIn(root, "add", "one.md", "two.md")
	gitIn(root, "commit", "-q", "-m", "base")

	// An ordinary commit removes one line from EACH file.
	os.WriteFile(first, []byte("# one\n"), 0o644)
	os.WriteFile(second, []byte("# two\n"), 0o644)
	gitIn(root, "commit", "-q", "-am", "an ordinary commit removes a line from each file")

	gitIn(root, "checkout", "-q", "-b", "pr")
	os.WriteFile(first, []byte("# one\n\none line\npull request line\n"), 0o644)
	os.WriteFile(second, []byte("# two\n\ntwo line\npull request line\n"), 0o644)
	gitIn(root, "commit", "-q", "-am", "pull request work")
	prHead := gitTextIn(root, "rev-parse", "HEAD")

	gitIn(root, "checkout", "-q", "main")
	// The merge takes the pull request's version of both files.
	os.WriteFile(first, []byte("# one\n\npull request line\n"), 0o644)
	os.WriteFile(second, []byte("# two\n\npull request line\n"), 0o644)
	gitIn(root, "commit", "-q", "-am",
		"squash\n\nSquash-merged into dev (PR #4444, head "+prHead+").")
	return prHead
}

// checkOne runs the checker over one fixture and returns its report.
func checkOne(dir string, item merge, tip string, squashMerges map[string]bool) *mergeReport {
	batch, err := newObjectBatch(dir)
	if err != nil {
		return &mergeReport{skipped: "the object reader could not start: " + err.Error()}
	}
	defer batch.close()
	cache := newCaches(dir, tip, squashMerges, batch)
	report := checkMergeAt(dir, item, cache)
	cache.resolveRestored([]*mergeReport{report})
	return report
}

// captureStdout runs a function with the standard output redirected, and
// returns what it printed.
//
// The self-test calls the whole gate once, on a fixture that must be red,
// and the gate's report would otherwise sit in the self-test's own output
// like a finding in this repository.
func captureStdout(run func() int) (int, string) {
	reader, writer, err := os.Pipe()
	if err != nil {
		code := run()
		return code, ""
	}
	saved := os.Stdout
	os.Stdout = writer
	printed := make(chan string, 1)
	go func() {
		data, _ := io.ReadAll(reader)
		printed <- string(data)
	}()
	code := run()
	os.Stdout = saved
	writer.Close()
	text := <-printed
	reader.Close()
	return code, text
}

func selfTestRun() int {
	var failures []string
	fail := func(message string) {
		failures = append(failures, message)
	}

	root, err := os.MkdirTemp("", "harbor-merge-loss-selftest-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	// 1. A merge that dropped a line: the gate must report it.
	broken := filepath.Join(root, "broken")
	fixture(broken, false)
	merges := findMergesAt(broken, broken)
	if len(merges) != 1 {
		fail(fmt.Sprintf("the marker scan found %d merges, expected 1", len(merges)))
	} else {
		item := merges[0]
		report := checkOne(broken, item, item.sha, map[string]bool{})
		lost := false
		for _, candidate := range report.losses {
			if normalize(candidate.line) == "dev line" {
				lost = true
			}
		}
		if !lost {
			fail("the checker did not report the line the merge dropped " +
				"(merge=" + shortSHA(item.sha) + ", findings=" +
				fmt.Sprint(len(report.losses)) + ")")
		}
		named := false
		for _, candidate := range report.losses {
			if candidate.pr == 4242 {
				named = true
			}
		}
		if !named {
			fail("the checker did not name the pull request")
		}
	}

	// 2. A merge that kept every line: the gate must stay quiet.
	clean := filepath.Join(root, "clean")
	fixture(clean, true)
	cleanMerges := findMergesAt(clean, clean)
	if len(cleanMerges) != 1 {
		fail(fmt.Sprintf("the marker scan found %d merges in the clean fixture, expected 1", len(cleanMerges)))
	} else {
		item := cleanMerges[0]
		report := checkOne(clean, item, item.sha, map[string]bool{})
		if len(report.losses) > 0 {
			messages := make([]string, 0, len(report.losses))
			for _, candidate := range report.losses {
				messages = append(messages, candidate.path+": "+candidate.line)
			}
			fail("the checker reported a loss on a merge that kept every line: " +
				strings.Join(messages, "; "))
		}
	}

	// 3. A line an ordinary commit removed on purpose is not a loss.
	//
	// The loss IS there — the merge dropped the line — and it is marked as
	// restored, because the history says that somebody took it away on
	// purpose. The tip does not have the line either, so only the history
	// can settle it.
	deliberate := filepath.Join(root, "deliberate")
	deliberateFixture(deliberate)
	deliberateMerges := findMergesAt(deliberate, deliberate)
	if len(deliberateMerges) != 1 {
		fail("the marker scan found no merge in the deliberate fixture")
	} else {
		item := deliberateMerges[0]
		report := checkOne(deliberate, item, item.sha, map[string]bool{item.sha: true})
		found := false
		restored := false
		for _, candidate := range report.losses {
			if normalize(candidate.line) == "base line" {
				found = true
				restored = candidate.restored
			}
		}
		if !found {
			fail("the checker did not report the line the merge dropped " +
				"(merge=" + shortSHA(item.sha) + ", findings=" +
				fmt.Sprint(len(report.losses)) + ")")
		} else if !restored {
			fail("the checker called a line an ordinary commit removed on purpose " +
				"a live loss")
		}
	}

	// 4. Two files, one walk of the history: the answers must not mix.
	two := filepath.Join(root, "two")
	twoFilesFixture(two)
	twoMerges := findMergesAt(two, two)
	if len(twoMerges) != 1 {
		fail("the marker scan found no merge in the two-files fixture")
	} else {
		item := twoMerges[0]
		report := checkOne(two, item, item.sha, map[string]bool{item.sha: true})
		seen := map[string]bool{}
		for _, candidate := range report.losses {
			key := candidate.path + "|" + normalize(candidate.line)
			seen[key] = true
		}
		for _, want := range []string{"one.md|one line", "two.md|two line"} {
			if !seen[want] {
				fail("the checker did not report the line the merge dropped: " + want)
			}
		}
		for _, unwanted := range []string{"one.md|two line", "two.md|one line"} {
			if seen[unwanted] {
				fail("the checker mixed the two files: " + unwanted)
			}
		}
	}

	// 5. The whole gate, on a repository that drops a line: it must be RED.
	//
	// The states above call the checker. This one calls the gate, so the
	// exit code -- the thing a workflow reads -- is proved too.
	red := filepath.Join(root, "red")
	fixture(red, false)
	before, err := os.Getwd()
	if err != nil {
		fail("the working directory could not be read: " + err.Error())
	} else if err := os.Chdir(red); err != nil {
		fail("the fixture could not be entered: " + err.Error())
	} else {
		code, _ := captureStdout(func() int {
			return runGate([]string{
				"--range", "HEAD",
				"--tip", "HEAD",
				"--exceptions", filepath.Join(red, "no-such-exceptions.txt"),
				"--min-merges", "1",
			}, true)
		})
		os.Chdir(before)
		if code != gate.Red {
			fail(fmt.Sprintf("the gate answered %d on a merge that dropped a line, "+
				"expected %d (red)", code, gate.Red))
		}
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the checker reports a dropped line, stays quiet on a " +
		"clean merge, ignores a line a normal commit removed on purpose, keeps two " +
		"files apart, and the gate itself is red on a merge that dropped a line")
	return gate.Green
}
