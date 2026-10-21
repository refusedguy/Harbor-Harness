// Package normalizemtimes is the compiled port of tools/normalize-mtimes.py.
//
// It makes the source mtimes tell the truth, so MSBuild can skip what did
// not change: a file whose CONTENT differs from the commit the cached
// outputs were built from gets the current time, and a file whose content is
// identical gets that commit's time.
//
// The rule that keeps the cache honest (issue #2100): the outputs are
// stamped ONLY for the projects whose files did not change. A project whose
// files changed is put BEHIND the tree, so MSBuild rebuilds it: a cache
// that decides what does not need compiling must not be able to make a
// compile error invisible.
//
// The text this prints is the text the python original printed. The exit
// codes are the python original's, because the workflow reads them:
//
//	0  the mtimes are normalised
//	1  something went wrong that is not the cache's fault
//	3  the cached base could not be resolved, so the changed set is unknown.
//	   The caller MUST discard the restored outputs and rebuild.
package normalizemtimes

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"harbor.local/gates/internal/gate"
)

const (
	exitOK            = 0
	exitError         = 1
	exitUntrustedBase = 3
	toolName          = "normalize-mtimes"
)

// Run is the gate entry point.
func Run(args []string) int {
	repo := "."
	base := ""
	now := 0.0
	hasNow := false
	dryRun := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch arg {
		case "--repo":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --repo needs a path")
				return gate.Broken
			}
			i++
			repo = args[i]
		case "--base":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --base needs a commit")
				return gate.Broken
			}
			i++
			base = args[i]
		case "--now":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --now needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.ParseFloat(args[i], 64)
			if err != nil {
				fmt.Fprintf(os.Stderr, "unknown argument: --now needs a number: %v\n", err)
				return gate.Broken
			}
			now = value
			hasNow = true
		case "--dry-run":
			dryRun = true
		case "--self-test":
			selfTest = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if base == "" {
		fmt.Fprintln(os.Stderr, "normalize-mtimes: --base is required")
		return exitError
	}

	absolute, err := filepath.Abs(repo)
	if err != nil {
		fmt.Fprintf(os.Stderr, "normalize-mtimes: %v\n", err)
		return exitError
	}
	if !hasNow {
		now = float64(time.Now().UnixNano()) / 1e9
	}

	code, report, fatal := normalise(absolute, base, now, dryRun)
	if fatal != nil {
		// The python original raised here, and its `except` printed the
		// message on stderr and returned EXIT_ERROR — no report line.
		fmt.Fprintf(os.Stderr, "%s: %v\n", toolName, fatal)
		return exitError
	}
	fmt.Printf("[%s] %s\n", toolName, report)
	if code == exitUntrustedBase {
		fmt.Fprintf(os.Stderr, "%s: %s\n", toolName, report)
		fmt.Fprintf(os.Stderr,
			"%s: the restored outputs cannot be trusted — the caller must "+
				"discard them and rebuild from scratch\n", toolName)
	}
	return code
}

// git runs a git command in the repository and returns (code, stdout, stderr).
func git(repo string, args ...string) (int, string, string) {
	cmd := exec.Command("git", args...)
	cmd.Dir = repo
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	code := 0
	if err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			code = exitErr.ExitCode()
		} else {
			code = 1
		}
	}
	return code, stdout.String(), stderr.String()
}

// trackedFiles lists the tracked paths, minus the gitlinks.
//
// A gitlink is a submodule, not a file: there is nothing to stamp.
func trackedFiles(repo string) ([]string, error) {
	code, out, errOut := git(repo, "ls-files", "-s", "-z")
	if code != 0 {
		return nil, fmt.Errorf("git ls-files failed: %s", strings.TrimSpace(errOut))
	}
	var paths []string
	for _, entry := range strings.Split(out, "\x00") {
		if entry == "" {
			continue
		}
		meta, path, _ := cut(entry, "\t")
		if path == "" {
			continue
		}
		mode, _, _ := cut(meta, " ")
		if mode == "160000" {
			continue
		}
		paths = append(paths, path)
	}
	return paths, nil
}

// cut splits a string at the first occurrence of the separator.
func cut(text, sep string) (before, after string, found bool) {
	index := strings.Index(text, sep)
	if index < 0 {
		return text, "", false
	}
	return text[:index], text[index+len(sep):], true
}

// stampOutputs stamps the output trees of the whole repository.
//
// The rule (issue #2100): the outputs of a project whose files CHANGED are
// not blessed. They are put BEHIND the tree - one second before the base
// commit - so that MSBuild rebuilds the project. A cache of COMPILED OUTPUTS
// decides what does not need compiling, and a decision that is wrong hides
// a compile error: the file that broke is not read by the compiler, and the
// green build ships the old assembly.
//
// The outputs of a project whose files did NOT change are stamped with the
// current time: the restored outputs carry the timestamps they had when the
// cache was built, and a restored NuGet folder is extracted at the start of
// the run, so the analyzer assemblies are NEWER than the outputs the cache
// carries. Stamping them with the current time puts them back on top of
// those inputs, and the unchanged projects are skipped.
//
// Returns the number of files stamped with the current time, and the number
// put behind the tree.
func stampOutputs(repo string, when float64, baseTime int64, changed map[string]bool) (int, int) {
	blessed := 0
	stale := 0
	now := time.Unix(int64(when), int64((when-float64(int64(when)))*1e9))
	// One second before the base commit: older than every source of the
	// tree, so the project that owns these outputs is rebuilt.
	behind := time.Unix(baseTime-1, 0)
	_ = filepath.Walk(repo, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if !info.IsDir() {
			return nil
		}
		base := filepath.Base(path)
		if base == ".git" {
			return filepath.SkipDir
		}
		if base != "obj" && base != "bin" {
			return nil
		}
		// The project that owns this output tree is the directory above it.
		project, relErr := filepath.Rel(repo, filepath.Dir(path))
		if relErr != nil {
			project = "."
		}
		project = filepath.ToSlash(project)
		changedProject := projectChanged(changed, project)
		stamp := now
		if changedProject {
			stamp = behind
		}
		// Stamp the whole output tree, then do not walk it again.
		_ = filepath.Walk(path, func(inner string, innerInfo os.FileInfo, innerErr error) error {
			if innerErr != nil || innerInfo.IsDir() {
				return nil
			}
			if os.Chtimes(inner, stamp, stamp) == nil {
				if changedProject {
					stale++
				} else {
					blessed++
				}
			}
			return nil
		})
		return filepath.SkipDir
	})
	return blessed, stale
}

// projectChanged reports whether any changed path lives under the project
// directory.
//
// The unit is the project DIRECTORY, not the project file: the outputs of a
// project live under it (obj/ and bin/), and a change to any file of that
// directory can change what the project compiles. The test is deliberately
// coarse: a project that did not really change is rebuilt (a cost in
// seconds), and a project that did change is never skipped (correctness).
func projectChanged(changed map[string]bool, project string) bool {
	prefix := ""
	if project != "" && project != "." {
		prefix = project + "/"
	}
	for path := range changed {
		if strings.HasPrefix(path, prefix) {
			return true
		}
	}
	return false
}

// normalise sets every tracked file's mtime and returns (exit code, report).
//
// The third value is the error that stopped it, when the gate could not
// finish at all: the python original raised there, and the message went to
// stderr without a report line.
func normalise(repo, base string, now float64, dryRun bool) (int, string, error) {
	code, out, _ := git(repo, "rev-parse", "--verify", "--quiet", base+"^{commit}")
	if code != 0 || strings.TrimSpace(out) == "" {
		// The commit the cache was built from is not in this clone — the
		// branch that produced it was deleted, or the cache outlived it.
		// GitHub serves a commit by SHA, so one shallow fetch brings back
		// the only thing that is needed: the TREE.
		_, _, fetchErr := git(repo, "fetch", "--no-tags", "--depth=1", "origin", base)
		code, out, _ = git(repo, "rev-parse", "--verify", "--quiet", base+"^{commit}")
		if code != 0 || strings.TrimSpace(out) == "" {
			detail := strings.TrimSpace(fetchErr)
			if detail == "" {
				detail = "fetch failed"
			}
			return exitUntrustedBase, fmt.Sprintf(
				"the cached base %s is not a commit in this clone (%s) — the changed set "+
					"is unknown, so the restored outputs cannot be trusted", base, detail), nil
		}
	}

	code, out, _ = git(repo, "log", "-1", "--format=%ct", base)
	if code != 0 || strings.TrimSpace(out) == "" {
		return exitUntrustedBase, fmt.Sprintf("cannot read the committer time of %s", base), nil
	}
	baseTime, err := strconv.ParseInt(strings.TrimSpace(out), 10, 64)
	if err != nil {
		return exitUntrustedBase, fmt.Sprintf("cannot read the committer time of %s", base), nil
	}

	code, out, errOut := git(repo, "diff", "--name-only", "-z", "--no-renames", base, "HEAD")
	if code != 0 {
		detail := strings.TrimSpace(errOut)
		if detail == "" {
			detail = "no output"
		}
		return exitUntrustedBase, fmt.Sprintf("git diff %s HEAD failed: %s", base, detail), nil
	}
	changed := map[string]bool{}
	for _, path := range strings.Split(out, "\x00") {
		if path != "" {
			changed[path] = true
		}
	}

	touchedNow := 0
	touchedBase := 0
	missing := 0

	// The outputs first, and the changed sources a second later than them:
	//
	//   * the outputs of a project that did NOT change are stamped NOW, so
	//     they are newer than every other input MSBuild compares them
	//     against (the analyzer assemblies in the NuGet folder above all)
	//     and the unchanged projects are skipped;
	//   * the outputs of a project that DID change are put BEHIND the tree
	//     (one second before the base commit), so the project is rebuilt:
	//     a file that changed must be compiled again, and a compile error
	//     in it must be seen (issue #2100);
	//   * a CHANGED source is stamped one second after the current time, so
	//     it is newer than the output that was built from the old version
	//     of it — otherwise MSBuild would skip it and the build would be
	//     stale;
	//   * an UNCHANGED source is stamped with the base commit's time, which
	//     is older than the outputs, so MSBuild skips it.
	outputsStamped := 0
	outputsStale := 0
	if !dryRun {
		outputsStamped, outputsStale = stampOutputs(repo, now, baseTime, changed)
	}

	tracked, err := trackedFiles(repo)
	if err != nil {
		return exitError, "", err
	}
	for _, path := range tracked {
		full := filepath.Join(repo, path)
		if _, err := os.Lstat(full); err != nil {
			missing++
			continue
		}
		stampForFile := float64(baseTime)
		if changed[path] {
			stampForFile = now + 1.0
		}
		if !dryRun {
			when := time.Unix(int64(stampForFile), int64((stampForFile-float64(int64(stampForFile)))*1e9))
			os.Chtimes(full, when, when)
		}
		if changed[path] {
			touchedNow++
		} else {
			touchedBase++
		}
	}

	report := fmt.Sprintf(
		"base=%s base_time=%d changed=%d rebuilt=%d skipped=%d missing=%d "+
			"outputs_stamped=%d outputs_stale=%d now=%d",
		base, baseTime, len(changed), touchedNow, touchedBase, missing,
		outputsStamped, outputsStale, int64(now))
	return exitOK, report, nil
}
