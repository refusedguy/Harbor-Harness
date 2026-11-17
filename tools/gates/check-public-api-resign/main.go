// Package checkpublicapiresign is the compiled port of
// tools/check-public-api-resign.py.
//
// The gate reads the PR's COMMIT RANGE and asks the question the analyzer
// cannot: did the frozen surface change, and was it re-signed — in the same
// commit, in the same diff?
//
// The order of the commits is the order git gives them, oldest first, and the
// order of the findings is the order the commits are read in: the gate is
// read by a reviewer who reads the log the same way.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checkpublicapiresign

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// frozen is the two frozen assemblies. The analyzer package is referenced by
// these two projects and by no others, so the freeze is exactly these two
// surfaces.
var frozen = []struct {
	directory string
	assembly  string
}{
	{"src/Harbor.Abstractions.Contracts", "Harbor.Abstractions.Contracts"},
	{"src/Harbor.Abstractions", "Harbor.Abstractions"},
}

// baselineNames are the public-API baseline files of a frozen assembly.
var baselineNames = []string{"PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"}

// declRe is a declaration line, for the RESIGN-002 exemption: after comments
// and whitespace are stripped, a line that begins with `public` or
// `protected`.
var declRe = regexp.MustCompile(`^(public|protected)\b`)

// lineCommentRe is a trailing comment.
var lineCommentRe = regexp.MustCompile(`//.*$`)

// blockCommentRe is a block comment.
var blockCommentRe = regexp.MustCompile(`(?s)/\*.*?\*/`)

// gateError is a condition that stops the gate from running at all.
//
// It is raised instead of returning findings, because a gate that cannot see
// the tree must not report a clean bill of health.
type gateError struct {
	message string
}

// Error is the message of the error.
func (e gateError) Error() string { return e.message }

// Run is the gate entry point.
func Run(args []string) int {
	base := ""
	head := "HEAD"
	var minRows []string
	selfTest := false
	hasBase := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--base":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--base needs a commit")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			base = value
			hasBase = true
		case "--head":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--head needs a commit")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			head = value
		case "--min-rows":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-rows needs ASSEMBLY=N")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			minRows = append(minRows, value)
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-public-api-resign [--base COMMIT] [--head COMMIT] [--min-rows ASSEMBLY=N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	repo, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	floors := map[string]int{}
	for _, item := range minRows {
		idx := strings.LastIndexByte(item, '=')
		key, value := "", ""
		if idx >= 0 {
			key, value = item[:idx], item[idx+1:]
		}
		if key == "" || !isDigits(value) {
			fmt.Printf("FAIL --min-rows wants ASSEMBLY=N, got %s\n", gatekit.PyRepr(item))
			return gate.Red
		}
		number, err := strconv.Atoi(value)
		if err != nil {
			fmt.Printf("FAIL --min-rows wants ASSEMBLY=N, got %s\n", gatekit.PyRepr(item))
			return gate.Red
		}
		floors[key] = number
	}

	if !hasBase {
		out, err := runGit(repo, "merge-base", "HEAD", "origin/dev")
		if err != nil {
			fmt.Printf("FAIL %s\n", err.Error())
			fmt.Println("FAIL no base commit to compare against — pass --base")
			return gate.Red
		}
		base = strings.TrimSpace(out)
	}
	if base == "" {
		fmt.Println("FAIL no base commit to compare against — pass --base")
		return gate.Red
	}

	findings, count, err := runGate(repo, base, head, floors)
	if err != nil {
		fmt.Printf("FAIL %s\n", err.Error())
		return gate.Red
	}

	fmt.Printf("compared %d commit(s) in %s..%s\n", count, shortSHA(base), head)
	for _, text := range findings {
		fmt.Printf("FAIL %s\n", text)
	}
	if len(findings) > 0 {
		fmt.Printf("\n%d finding(s) — red\n", len(findings))
		return gate.Red
	}
	fmt.Println("OK: every change to the frozen surface is re-signed in its own commit")
	return gate.Green
}

// shortSHA is the python `text[:12]`: the first twelve characters, or the
// whole text when it is shorter.
//
// The python original sliced a STRING, and a python slice of a short string
// is the whole string. A Go slice of a short string is a panic, so the port
// has to say what python's slice says: a gate that crashes on an argument
// it accepts is a gate that cannot run, and the crash was found by the
// benchmark, which passes `--base HEAD~1` where CI passes a SHA.
func shortSHA(text string) string {
	if len(text) > 12 {
		return text[:12]
	}
	return text
}

// isDigits is the python str.isdigit.
func isDigits(text string) bool {
	if text == "" {
		return false
	}
	for _, r := range text {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// runGit runs git in the repository and returns stdout.
func runGit(cwd string, args ...string) (string, error) {
	full := append([]string{"-C", cwd}, args...)
	cmd := exec.Command("git", full...)
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	var stderr strings.Builder
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		code := 1
		if exit, ok := err.(*exec.ExitError); ok {
			code = exit.ExitCode()
		}
		return "", gateError{message: fmt.Sprintf("git %s failed (%d): %s",
			strings.Join(args, " "), code, strings.TrimSpace(stderr.String()))}
	}
	return string(out), nil
}

// isBaseline is whether a path is one of the baseline files.
func isBaseline(path string) bool {
	base := filepath.Base(path)
	for _, name := range baselineNames {
		if base == name {
			return true
		}
	}
	return false
}

// inDirectory is whether a repo-relative path lives under `directory`.
func inDirectory(path string, directory string) bool {
	return path == directory || strings.HasPrefix(path, strings.TrimRight(directory, "/")+"/")
}

// stripComments removes comments from a source fragment, keeping line
// structure.
func stripComments(text string) string {
	text = blockCommentRe.ReplaceAllStringFunc(text, func(match string) string {
		return strings.Repeat("\n", strings.Count(match, "\n"))
	})
	lines := strings.Split(text, "\n")
	for index, line := range lines {
		lines[index] = lineCommentRe.ReplaceAllString(line, "")
	}
	return strings.Join(lines, "\n")
}

// declarationChanged is whether a diff carries an added or removed
// declaration line.
//
// The diff is expected in `git diff` unified format: lines that begin with
// '+' or '-' are the changed ones, '+++' and '---' are the file headers.
func declarationChanged(diffText string) bool {
	for _, raw := range strings.Split(diffText, "\n") {
		if strings.HasPrefix(raw, "+++") || strings.HasPrefix(raw, "---") {
			continue
		}
		if raw == "" || (raw[0] != '+' && raw[0] != '-') {
			continue
		}
		body := stripComments(raw[1:])
		for _, line := range strings.Split(body, "\n") {
			if declRe.MatchString(strings.TrimSpace(line)) {
				return true
			}
		}
	}
	return false
}

// commit is one commit of the range.
type commit struct {
	sha     string
	subject string
}

// commitRange is the commits of `base..head`, oldest first, as (sha,
// subject).
//
// Merge commits are skipped: a merge commit's diff against its first parent
// is the merged content, which the individual commits already carry.
func commitRange(base string, head string, cwd string) ([]commit, error) {
	out, err := runGit(cwd, "log", "--no-merges", "--reverse", "--format=%H%x09%s", base+".."+head)
	if err != nil {
		return nil, err
	}
	var commits []commit
	for _, line := range strings.Split(out, "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		sha, subject, _ := strings.Cut(line, "\t")
		commits = append(commits, commit{sha: sha, subject: subject})
	}
	return commits, nil
}

// changedFiles is the repo-relative paths a commit touched.
func changedFiles(sha string, cwd string) ([]string, error) {
	out, err := runGit(cwd, "show", "--name-only", "--format=", "--no-renames", sha)
	if err != nil {
		return nil, err
	}
	var paths []string
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if line != "" {
			paths = append(paths, line)
		}
	}
	return paths, nil
}

// commitDiff is the unified diff of one commit restricted to `paths`.
func commitDiff(sha string, paths []string, cwd string) (string, error) {
	if len(paths) == 0 {
		return "", nil
	}
	args := []string{"show", "--format=", "--unified=0", "--no-color", "--no-renames", sha, "--"}
	args = append(args, paths...)
	return runGit(cwd, args...)
}

// existedInParent is whether a path already existed in the commit's parent.
//
// The first commit of a repository has no parent, and `git cat-file -e` says
// so with a non-zero exit; that answer is "no".
func existedInParent(sha string, path string, cwd string) bool {
	cmd := exec.Command("git", "-C", cwd, "cat-file", "-e", sha+"^:"+path)
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	return cmd.Run() == nil
}

// countRows is the number of API rows in a public-API baseline file.
func countRows(path string) int {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0
	}
	rows := 0
	for _, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		rows++
	}
	return rows
}

// checkCommit is RESIGN-001 and RESIGN-002 for one commit.
func checkCommit(sha string, subject string, files []string, cwd string, findings *[]string) {
	for _, entry := range frozen {
		var touched []string
		for _, path := range files {
			if inDirectory(path, entry.directory) {
				touched = append(touched, path)
			}
		}
		if len(touched) == 0 {
			continue
		}

		var sources []string
		var baselines []string
		for _, path := range touched {
			if strings.HasSuffix(path, ".cs") {
				sources = append(sources, path)
			}
			if isBaseline(path) {
				baselines = append(baselines, path)
			}
		}

		// RESIGN-001: a baseline edit arrives with the code it re-signs.
		//
		// Creating the baseline is not covered. There is nothing to re-sign
		// when the file does not exist yet, and the content of a created
		// baseline is checked by the analyzer in the same commit.
		var edited []string
		for _, path := range baselines {
			if existedInParent(sha, path, cwd) {
				edited = append(edited, path)
			}
		}
		// A baseline edit that only REMOVES a row is a CORRECTION, not a
		// re-signing: the declaration it named did not change, so there is
		// no code to arrive with.
		removalOnly := true
		for _, path := range edited {
			if additionsInCommit(sha, path, cwd) {
				removalOnly = false
				break
			}
		}
		if len(edited) > 0 && len(sources) == 0 && !removalOnly {
			*findings = append(*findings,
				"RESIGN-001 "+shortSHA(sha)+" "+subject+
					" — the frozen baseline of "+entry.assembly+" was edited in this commit ("+
					strings.Join(edited, ", ")+") and no source of the assembly was touched with it. "+
					"A re-signing is a diff, not a file edit: the code that changed the surface and "+
					"the row that records it belong in the same commit.")
		}

		// RESIGN-002: the code that changes the surface re-signs it here.
		if len(sources) > 0 && len(baselines) == 0 {
			diff, err := commitDiff(sha, sources, cwd)
			if err == nil && declarationChanged(diff) {
				sorted := append([]string{}, sources...)
				sortStrings(sorted)
				shown := sorted
				if len(shown) > 3 {
					shown = shown[:3]
				}
				tail := ""
				if len(sources) > 3 {
					tail = ", ..."
				}
				*findings = append(*findings,
					"RESIGN-002 "+shortSHA(sha)+" "+subject+
						" — this commit changes a public declaration of "+entry.assembly+" ("+
						strings.Join(shown, ", ")+tail+
						") and does not re-sign the frozen baseline with it. "+
						"The build would still fail on RS0016 at the tip of the branch, but a "+
						"two-commit PR could hide the re-signing from the reviewer. Put the "+
						"baseline row in the same commit as the declaration it records.")
			}
		}
	}
}

// sortStrings sorts in place.
func sortStrings(values []string) {
	for i := 1; i < len(values); i++ {
		for j := i; j > 0 && values[j] < values[j-1]; j-- {
			values[j], values[j-1] = values[j-1], values[j]
		}
	}
}

// additionsInCommit is whether the commit ADDED lines to the given file.
func additionsInCommit(sha string, path string, cwd string) bool {
	cmd := exec.Command("git", "-C", cwd, "diff", sha+"^", sha, "--", path)
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	out, err := cmd.Output()
	if err != nil {
		return false
	}
	for _, line := range strings.Split(string(out), "\n") {
		if strings.HasPrefix(line, "+++") || strings.HasPrefix(line, "---") {
			continue
		}
		if !strings.HasPrefix(line, "+") {
			continue
		}
		body := strings.TrimSpace(stripComments(line[1:]))
		if body != "" {
			return true
		}
	}
	return false
}

// checkDuplicates is RESIGN-DUP: no row of a frozen baseline appears twice.
//
// The analyzer reads a baseline as a SET of declarations, so a row written
// twice is not "the same row twice" — it is one row and a build that stops
// (RS0025). This check is a file read, and it runs in the lane where a build
// may not happen at all.
func checkDuplicates(repo string, findings *[]string) {
	for _, entry := range frozen {
		for _, name := range baselineNames {
			path := filepath.Join(repo, entry.directory, name)
			info, err := os.Stat(path)
			if err != nil || info.IsDir() {
				continue
			}
			data, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			seen := map[string]int{}
			for number, line := range strings.Split(string(data), "\n") {
				row := strings.TrimSpace(line)
				if row == "" || strings.HasPrefix(row, "#") {
					continue
				}
				if first, ok := seen[row]; ok {
					*findings = append(*findings,
						"RESIGN-DUP "+entry.assembly+" — the row on line "+strconv.Itoa(number+1)+
							" of "+path+" is written twice: it already stands on line "+
							strconv.Itoa(first)+". A baseline is a set: a row written twice is a "+
							"build that stops with RS0025, and which of the two rows is meant to "+
							"be there is ambiguous.")
					continue
				}
				seen[row] = number + 1
			}
		}
	}
}

// checkFloors is RESIGN-FLOOR: the frozen baseline still holds at least N
// rows.
func checkFloors(repo string, floors map[string]int, findings *[]string) {
	for _, entry := range frozen {
		floor, ok := floors[entry.directory]
		if !ok {
			continue
		}
		shipped := filepath.Join(repo, entry.directory, baselineNames[0])
		info, err := os.Stat(shipped)
		if err != nil || info.IsDir() {
			*findings = append(*findings,
				"RESIGN-FLOOR "+entry.assembly+" — "+shipped+" does not exist. A gate that "+
					"passes on a deleted baseline is decorative.")
			continue
		}
		rows := countRows(shipped)
		if rows < floor {
			*findings = append(*findings,
				"RESIGN-FLOOR "+entry.assembly+" — "+shipped+" holds "+strconv.Itoa(rows)+
					" rows, below the floor of "+strconv.Itoa(floor)+". The frozen baseline was "+
					"truncated, emptied or rewritten. A removal from the frozen surface is a "+
					"breaking change and is recorded as a `*REMOVED*` row in "+
					"PublicAPI.Unshipped.txt, not as a deletion from the shipped baseline.")
		}
	}
}

// runGate is the gate itself.
func runGate(repo string, base string, head string, floors map[string]int) ([]string, int, error) {
	gitPath := filepath.Join(repo, ".git")
	if _, err := os.Stat(gitPath); err != nil {
		return nil, 0, gateError{message: "no git checkout at " + repo +
			" — the re-signing gate reads commit history, so it has nothing to read here"}
	}
	commits, err := commitRange(base, head, repo)
	if err != nil {
		return nil, 0, err
	}
	var findings []string
	checkDuplicates(repo, &findings)
	for _, item := range commits {
		files, err := changedFiles(item.sha, repo)
		if err != nil {
			return nil, 0, err
		}
		checkCommit(item.sha, item.subject, files, repo, &findings)
	}
	checkFloors(repo, floors, &findings)
	return findings, len(commits), nil
}
