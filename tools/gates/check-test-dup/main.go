// Package testdup is the compiled port of tools/check-test-dup.py.
//
// The duplicate-test-body detector and CI gate for the tests/ tree.
//
// It asks one question: is a test body a copy of another test body? The
// answer is a REGRESSION gate: it has to be true on the tree as it stands
// today and false the moment a duplicate is added.
//
// The detection method: lex each file into tokens (comments dropped, string,
// char and numeric literals kept as distinct kinds), find method bodies by
// brace matching, normalise each body into a token stream (literals -> S/C/N,
// identifiers -> I), group the bodies by that fingerprint, and keep only the
// groups whose members ALSO share at least `--min-similarity` of their
// identifier vocabulary.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could
// not run.
package testdup

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pyfmt"
)

// options are the gate's flags.
type options struct {
	root          string
	report        bool
	update        bool
	jsonOut       string
	newSince      string
	minFiles      int
	minMethods    int
	minLines      int
	minSimilarity float64
	warnOnly      bool
}

// Run is the gate entry point.
func Run(args []string) int {
	opts := options{minLines: minBodyLines, minSimilarity: minSimilarity}
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--root needs a path")
				return gate.Broken
			}
			i++
			opts.root = args[i]
		case arg == "--report":
			opts.report = true
		case arg == "--update":
			opts.update = true
		case arg == "--json-out":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--json-out needs a path")
				return gate.Broken
			}
			i++
			opts.jsonOut = args[i]
		case arg == "--new-since":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--new-since needs a ref")
				return gate.Broken
			}
			i++
			opts.newSince = args[i]
		case arg == "--min-files":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			opts.minFiles = value
		case arg == "--min-methods":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-methods needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-methods needs a number")
				return gate.Broken
			}
			opts.minMethods = value
		case arg == "--min-lines":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-lines needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.Atoi(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-lines needs a number")
				return gate.Broken
			}
			opts.minLines = value
		case arg == "--min-similarity":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-similarity needs a number")
				return gate.Broken
			}
			i++
			value, err := strconv.ParseFloat(args[i], 64)
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-similarity needs a number")
				return gate.Broken
			}
			opts.minSimilarity = value
		case arg == "--warn-only":
			opts.warnOnly = true
		case arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-test-dup [--root PATH] [--report] [--update] [--json-out PATH] [--new-since REF] [--min-files N] [--min-methods N] [--min-lines N] [--min-similarity F] [--warn-only] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	repoRoot, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}
	if opts.root != "" {
		repoRoot = opts.root
	}
	return runWith(repoRoot, opts)
}

// runWith runs the gate over a tree, with the flags already read.
func runWith(repoRoot string, opts options) int {
	if info, err := os.Stat(filepath.Join(repoRoot, scopeDir)); err != nil || !info.IsDir() {
		fmt.Fprintf(os.Stderr, "error: %s/ not found under %s\n", scopeDir, repoRoot)
		return gate.Broken
	}

	if opts.update {
		methods, scan := scanTree(repoRoot)
		groups, _ := buildGroups(methods, opts.minLines, minMembers, opts.minSimilarity)
		path, err := writeBaseline(repoRoot, groups, scan, opts.minLines, opts.minSimilarity)
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		measured := census(groups, scan, 0)
		fmt.Printf("wrote %d group(s) / %d duplicate lines to %s\n",
			len(groups), measured.duplicateLines, path)
		return gate.Green
	}

	var changed map[string]bool
	if opts.newSince != "" {
		changed = changedFilesSince(repoRoot, opts.newSince)
		if changed == nil {
			fmt.Fprintf(os.Stderr,
				"error: cannot resolve '%s' with git — the comparison needs a ref this checkout knows\n",
				opts.newSince)
			return gate.Broken
		}
	}

	result := runGate(repoRoot, opts.minLines, minMembers, opts.minSimilarity, opts.minFiles, opts.minMethods, changed)
	measured := result.measured

	fmt.Printf("scanned %d .cs files, %d method bodies (%d [Test]); %d duplicate group(s), %d removable code lines\n",
		measured.files, measured.methods, measured.testMethods, measured.groups, measured.duplicateLines)

	if opts.report {
		printReport(result.groups, measured)
		for _, warning := range result.warnings {
			fmt.Printf("  warn  %s\n", warning)
		}
		if opts.jsonOut != "" {
			if err := writeArtifact(opts.jsonOut, result.groups, measured); err != nil {
				fmt.Fprintln(os.Stderr, err.Error())
				return gate.Broken
			}
			fmt.Printf("\nwrote %s\n", opts.jsonOut)
		}
		return gate.Green
	}

	if len(result.offenders) > 0 {
		fmt.Println()
		for _, group := range result.offenders {
			fmt.Println(describeGroup(group))
		}
	}
	prefix := "FAIL"
	if opts.warnOnly {
		prefix = "WARN"
	}
	for _, finding := range result.findings {
		fmt.Printf("%s %s\n", prefix, finding)
	}
	for _, warning := range result.warnings {
		fmt.Printf("warn  %s\n", warning)
	}

	if opts.jsonOut != "" {
		if err := writeArtifact(opts.jsonOut, result.groups, measured); err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		fmt.Printf("wrote %s\n", opts.jsonOut)
	}

	if len(result.findings) > 0 && opts.warnOnly {
		fmt.Printf("\n%d finding(s) - WARN ONLY, the gate is not blocking (the blocking default is unchanged, this flag only changes the exit code)\n",
			len(result.findings))
		return gate.Green
	}
	if len(result.findings) > 0 {
		fmt.Printf("\n%d finding(s) — red\n", len(result.findings))
		return gate.Red
	}
	fmt.Println("OK: no duplication beyond the committed baseline")
	return gate.Green
}

// describeGroup is one actionable block per duplicate group, plus its other
// members.
func describeGroup(group *dupGroup) string {
	head := group.members[0]
	lines := []string{fmt.Sprintf("  DUP  %s x %d copies, %d code lines each (%d removable lines)",
		head.where(), group.count, group.lines, group.duplicateLines)}
	for _, member := range group.members[1:] {
		lines = append(lines, fmt.Sprintf("       also %s  (%s)", member.where(), member.name))
	}
	lines = append(lines, fmt.Sprintf(
		"       shape sha1 %s, identifier similarity %s, %d/%d exact copies",
		group.fingerprint[:12], pyfmt.Repr(group.similarity), group.exactCount, group.count))
	return strings.Join(lines, "\n")
}

// printReport prints the census and the groups.
func printReport(groups []*dupGroup, measured censusCounts) {
	fmt.Printf("scanned      %d .cs files under %s/ (%d lines)\n", measured.files, scopeDir, measured.lines)
	fmt.Printf("methods      %d bodies (%d attributed to [Test])\n", measured.methods, measured.testMethods)
	fmt.Printf("groups       %d duplicate groups (%d intra-file, %d cross-file, %d with exact copies)\n",
		measured.groups, measured.intraFileGroups, measured.crossFileGroups, measured.exactGroups)
	fmt.Printf("duplicate    %d removable code lines (%d lines inside duplicated bodies)\n",
		measured.duplicateLines, measured.groupLines)
	fmt.Printf("coincidences %d same-shape groups rejected for sharing too few identifiers\n",
		measured.shapeCoincidences)
	if len(groups) == 0 {
		return
	}
	for _, group := range groups {
		fmt.Println()
		fmt.Println(describeGroup(group))
	}
}

// writeArtifact writes the JSON artifact: the work list the next
// duplication ticket takes.
func writeArtifact(path string, groups []*dupGroup, measured censusCounts) error {
	censusNode := jsonObject()
	censusNode.set("groups", jsonNumber(measured.groups))
	censusNode.set("intra_file_groups", jsonNumber(measured.intraFileGroups))
	censusNode.set("cross_file_groups", jsonNumber(measured.crossFileGroups))
	censusNode.set("exact_groups", jsonNumber(measured.exactGroups))
	censusNode.set("duplicate_lines", jsonNumber(measured.duplicateLines))
	censusNode.set("group_lines", jsonNumber(measured.groupLines))
	censusNode.set("shape_coincidences", jsonNumber(measured.shapeCoincidences))
	censusNode.set("files", jsonNumber(measured.files))
	censusNode.set("lines", jsonNumber(measured.lines))
	censusNode.set("methods", jsonNumber(measured.methods))
	censusNode.set("test_methods", jsonNumber(measured.testMethods))

	groupNodes := jsonArray()
	for _, group := range groups {
		entry := jsonObject()
		entry.set("fingerprint", jsonString(group.fingerprint))
		entry.set("copies", jsonNumber(group.count))
		entry.set("exact_copies", jsonNumber(group.exactCount))
		entry.set("files", jsonNumber(group.fileCount))
		entry.set("lines", jsonNumber(group.lines))
		entry.set("duplicate_lines", jsonNumber(group.duplicateLines))
		entry.set("intra_file", jsonBool(group.intraFile))
		entry.set("similarity", jsonFloat(group.similarity))
		members := jsonArray()
		for _, member := range group.members {
			one := jsonObject()
			one.set("path", jsonString(member.path))
			one.set("name", jsonString(member.name))
			one.set("start_line", jsonNumber(member.startLine))
			one.set("end_line", jsonNumber(member.endLine))
			one.set("lines", jsonNumber(member.lines))
			one.set("is_test", jsonBool(member.isTest))
			members.append(one)
		}
		entry.set("members", members)
		groupNodes.append(entry)
	}

	payload := jsonObject()
	payload.set("generator", jsonString("tools/bin/harbor-gates-linux-x64 check-test-dup"))
	payload.set("census", censusNode)
	payload.set("groups", groupNodes)

	return os.WriteFile(path, []byte(payload.render(0)+"\n"), 0o644)
}

// changedFilesSince returns the repo-relative paths changed between `ref`
// and the working tree.
//
// nil means "cannot tell" -- the caller then says so rather than pretending
// the branch touched nothing.
func changedFilesSince(root string, ref string) map[string]bool {
	command := exec.Command("git", "diff", "--name-only", ref+"...HEAD")
	command.Dir = root
	command.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	out, err := command.Output()
	if err != nil {
		return nil
	}
	changed := map[string]bool{}
	for _, line := range strings.Split(string(out), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		changed[strings.ReplaceAll(line, string(os.PathSeparator), "/")] = true
	}
	return changed
}
