package mergeloss

// selftest.go -- the gate's own proof that it can fail.
//
// The fixture is a throwaway repository with one squash-merge: the merge
// base, a pull request branch that adds a line, a `main` that adds a line of
// its own, and the squash-merge that lands the pull request on `main`.
//
// Three states, and the gate must tell them apart:
//   1. the merge takes the pull request's version of the file and drops
//      `main`'s line — the failure this gate exists to catch;
//   2. the merge keeps every line — a clean merge;
//   3. the merge keeps the line and a LATER ORDINARY commit removes it on
//      purpose — not a loss, because somebody asked for it to go.

import (
	"fmt"
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
		report := checkMergeAt(broken, item, item.sha, map[string]bool{}, true, newCaches(broken, item.sha, map[string]bool{}))
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
		report := checkMergeAt(clean, item, item.sha, map[string]bool{}, true, newCaches(clean, item.sha, map[string]bool{}))
		if len(report.losses) > 0 {
			messages := make([]string, 0, len(report.losses))
			for _, candidate := range report.losses {
				messages = append(messages, candidate.path+": "+candidate.line)
			}
			fail("the checker reported a loss on a merge that kept every line: " +
				strings.Join(messages, "; "))
		}
	}

	// 3. A line a normal commit removed on purpose is not a loss.
	deliberate := filepath.Join(root, "deliberate")
	fixture(deliberate, true)
	os.WriteFile(filepath.Join(deliberate, "doc.md"),
		[]byte("# doc\n\nbase line\npull request line\n"), 0o644)
	gitIn(deliberate, "commit", "-q", "-am", "an ordinary commit removes the dev line")
	deliberateMerges := findMergesAt(deliberate, deliberate)
	if len(deliberateMerges) != 1 {
		fail("the marker scan found no merge in the deliberate fixture")
	} else {
		item := deliberateMerges[0]
		report := checkMergeAt(deliberate, item, item.sha, map[string]bool{item.sha: true}, true, newCaches(deliberate, item.sha, map[string]bool{item.sha: true}))
		if len(report.losses) > 0 {
			messages := make([]string, 0, len(report.losses))
			for _, candidate := range report.losses {
				messages = append(messages, candidate.path+": "+candidate.line)
			}
			fail("the checker reported a line that an ordinary commit removed on purpose: " +
				strings.Join(messages, "; "))
		}
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the checker reports a dropped line, stays quiet on a " +
		"clean merge, and ignores a line a normal commit removed on purpose")
	return gate.Green
}
