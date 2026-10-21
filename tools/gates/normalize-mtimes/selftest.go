package normalizemtimes

// selftest.go -- the gate's own proof that it tells a changed file from an
// unchanged one, and a changed PROJECT from an unchanged one.
//
// The fixture is a throwaway git repository with TWO projects, because the
// rule the cache depends on is about the project and not only about the
// file:
//
//   changed/  a project with a CHANGED source, and the restored output the
//             cache carries for it. The output must NOT be blessed: it has
//             to end up BEHIND the tree, or MSBuild skips the project and
//             the compile error in it is never seen (issue #2100).
//   same/     a project with an UNCHANGED source, and the restored output
//             the cache carries for it. The output must be stamped with the
//             current time, or MSBuild rebuilds the whole solution and the
//             cache buys nothing.
//
// The mtimes the fixture ends up with:
//
//   - the CHANGED source, one second after the outputs, so MSBuild rebuilds
//     it and nothing else;
//   - the UNCHANGED source, at the base commit's time, so MSBuild skips it;
//   - the output of the UNCHANGED project, at the current time, so it is
//     newer than every input MSBuild compares it against;
//   - the output of the CHANGED project, one second BEFORE the base
//     commit's time, so it is older than every input of that project and
//     the project is rebuilt.
//
// The second fixture is the unknown base: a base that is not in the clone
// must NOT be silently accepted, because a cache whose base is unknown
// cannot be told apart from a stale one.

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

// gitIn runs a git command in a fixture repository.
//
// The fixture is a throwaway repository of the self-test's own making, so it
// carries its own identity: that is the fixture's identity, not the
// repository's, and it never reaches a commit of this repository.
func gitIn(root string, args ...string) string {
	cmd := exec.Command("git", args...)
	cmd.Dir = root
	cmd.Env = append(os.Environ(),
		"GIT_CONFIG_GLOBAL=/dev/null",
		"GIT_CONFIG_SYSTEM=/dev/null",
	)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(out))
}

func selfTestRun() int {
	root, err := os.MkdirTemp("", "harbor-mtimes-")
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	gitIn(root, "init", "-q", root)
	gitIn(root, "-C", root, "config", "user.email", "t@example.com")
	gitIn(root, "-C", root, "config", "user.name", "T")

	// Two projects: one whose file changes, one whose file does not.
	changedDir := filepath.Join(root, "changed")
	sameDir := filepath.Join(root, "same")
	for _, dir := range []string{changedDir, sameDir} {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
			return gate.Red
		}
	}
	unchanged := filepath.Join(sameDir, "Same.cs")
	changed := filepath.Join(changedDir, "Changed.cs")
	if err := os.WriteFile(unchanged, []byte("// unchanged\n"), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
		return gate.Red
	}
	if err := os.WriteFile(changed, []byte("// v1\n"), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
		return gate.Red
	}
	gitIn(root, "-C", root, "add", "-A")
	gitIn(root, "-C", root, "commit", "-qm", "base")
	base := gitIn(root, "-C", root, "rev-parse", "HEAD")

	if err := os.WriteFile(changed, []byte("// v2\n"), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
		return gate.Red
	}
	gitIn(root, "-C", root, "commit", "-qam", "change")

	// The restored outputs the cache carries: one per project.
	changedOutput := filepath.Join(changedDir, "obj", "Release", "net10.0", "Changed.dll")
	unchangedOutput := filepath.Join(sameDir, "obj", "Release", "net10.0", "Same.dll")
	for _, output := range []string{changedOutput, unchangedOutput} {
		if err := os.MkdirAll(filepath.Dir(output), 0o755); err != nil {
			fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
			return gate.Red
		}
		if err := os.WriteFile(output, []byte("binary"), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "self-test: %v\n", err)
			return gate.Red
		}
	}

	baseTime, err := strconv.ParseInt(gitIn(root, "-C", root, "log", "-1", "--format=%ct", base), 10, 64)
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test: cannot read the base time: %v\n", err)
		return gate.Red
	}
	// "now" stands in for the moment of the checkout, which is later than
	// the commit the cached outputs were built from.
	now := float64(baseTime + 1000)
	code, report, fatal := normalise(root, base, now, false)
	if code != exitOK {
		if fatal != nil {
			fmt.Fprintf(os.Stderr, "self-test: normalise failed: %v\n", fatal)
		} else {
			fmt.Fprintf(os.Stderr, "self-test: normalise failed: %s\n", report)
		}
		return gate.Red
	}

	gotChanged := mtimeOf(changed)
	gotUnchanged := mtimeOf(unchanged)
	gotChangedOutput := mtimeOf(changedOutput)
	gotUnchangedOutput := mtimeOf(unchangedOutput)
	if int64(gotChanged) != int64(now)+1 {
		fmt.Fprintf(os.Stderr,
			"self-test: the changed file has mtime %d, expected %d (one second after the outputs)\n",
			int64(gotChanged), int64(now)+1)
		return gate.Red
	}
	if int64(gotUnchanged) != baseTime {
		fmt.Fprintf(os.Stderr,
			"self-test: the unchanged file has mtime %d, expected the base commit's time %d\n",
			int64(gotUnchanged), baseTime)
		return gate.Red
	}
	if int64(gotUnchangedOutput) != int64(now) {
		fmt.Fprintf(os.Stderr,
			"self-test: the output of the UNCHANGED project has mtime %d, expected %d\n",
			int64(gotUnchangedOutput), int64(now))
		return gate.Red
	}
	// The heart of the rule: the output of a project that CHANGED is not
	// blessed. It ends up behind the tree, so MSBuild rebuilds the project
	// and a compile error in it cannot hide behind the cache.
	if int64(gotChangedOutput) != baseTime-1 {
		fmt.Fprintf(os.Stderr,
			"self-test: the output of the CHANGED project has mtime %d, expected %d "+
				"(one second before the base commit, so the project is rebuilt)\n",
			int64(gotChangedOutput), baseTime-1)
		return gate.Red
	}
	if gotUnchangedOutput <= gotUnchanged {
		fmt.Fprintln(os.Stderr,
			"self-test: the output of the unchanged project is not newer than its source")
		return gate.Red
	}
	if gotChangedOutput >= gotChanged {
		fmt.Fprintln(os.Stderr,
			"self-test: the output of the changed project is not older than its changed source")
		return gate.Red
	}
	if gotChangedOutput >= gotUnchanged {
		fmt.Fprintln(os.Stderr,
			"self-test: the output of the changed project is not older than its unchanged source")
		return gate.Red
	}
	if !strings.Contains(report, "outputs_stale=1") {
		fmt.Fprintf(os.Stderr,
			"self-test: the report does not count the output of the changed project: %s\n", report)
		return gate.Red
	}

	// An unknown base must NOT be silently accepted.
	code, _, _ = normalise(root, strings.Repeat("0", 40), now, true)
	if code != exitUntrustedBase {
		fmt.Fprintf(os.Stderr, "self-test: an unknown base returned %d, expected %d\n",
			code, exitUntrustedBase)
		return gate.Red
	}

	fmt.Println("self-test: ok")
	return gate.Green
}

// mtimeOf is a file's modification time, in seconds.
func mtimeOf(path string) float64 {
	info, err := os.Stat(path)
	if err != nil {
		return 0
	}
	return float64(info.ModTime().Unix())
}
