// Package all runs every gate of this repository that needs nothing but the
// tree, in one command.
//
// # WHAT THIS IS
//
// The gates of this repository come in three kinds, and the kind decides
// where the gate can run:
//
//   - a gate that reads what the BUILD produced (a TRX, a publish log, a
//     .nupkg, obj/project.assets.json) — it runs in the job that built it;
//   - a gate that runs dotnet itself — it runs where there is an SDK;
//   - a gate that reads the TREE — it runs anywhere, including on the pull
//     request that breaks the build.
//
// The third kind is this package. It is the set of gates that need no
// artifact and no SDK, and the point of running them together is that one
// command answers "is the tree sound?" — locally, on a developer machine
// with no SDK, and in CI, with the same binary and the same flags.
//
// # THE LIST IS HERE, AND ONLY HERE
//
// tools/gates/all/gates.go holds the one list of the tree gates: the name,
// what the gate reads, and the flags the workflow passes it. A gate that is
// not in this list is not a tree gate, and a gate that is in this list and
// needs an artifact is a bug in this file — the self-test proves it by
// reading the workflows and looking at the arguments the workflows really
// pass (see workflow.go).
//
// The list is deliberately NOT a copy of the policy table in
// docs/CI_GATES.md: that table is about what CI runs and where, this list
// is about what needs nothing but the tree. A gate can be in both, in
// neither, or in one of them.
//
// THE CONTRACT
//
//	harbor-gates all [flags]
//
//	exit 0  green   - every gate ran and found nothing
//	exit 1  red     - a gate ran and found something
//	exit 2  broken  - a gate could not run at all
//
// A gate that could not run is NOT a gate that found nothing, and the
// difference is the difference between a red build that says what is wrong
// and a green build that says nothing is wrong when nothing ran.
package all

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// treeGate is one gate of the list.
//
// The fields are the gate's CONTRACT with this runner, and each one is
// there because the runner cannot guess it.
type treeGate struct {
	// name is the gate's name: the directory under tools/gates/ that holds
	// its sources, and the subcommand that runs it. One name, one gate.
	name string
	// needs says, in one line, what the gate reads. It is what the report
	// prints, so a reader can tell at a glance which gates want the git
	// history and which want nothing but files.
	needs string
	// args are the flags the workflow passes the gate.
	//
	// They are the WORKFLOW's flags and not ours on purpose: the floor a
	// gate runs with in CI is the floor it must run with here, or the local
	// command and the CI command check different things. A floor that is
	// missing in one of the two is a gate that is green because it scanned
	// nothing.
	args []string
	// git says the gate reads the git history. Such a gate blocks on I/O
	// and opens a git process, and the pool keeps them apart so that a run
	// of many gates does not open many git processes at once.
	git bool
	// writer says the gate REWRITES the tree. `all` is a reader: a command
	// that checks the tree must not change it, and a developer running the
	// local command must not get a different working tree afterwards. Such
	// a gate stays in the list (it IS a tree gate) and is reported as not
	// run.
	writer bool
}

// The placeholders in the arguments. They are resolved when the gate runs,
// not when the list is written, because the base commit depends on the tree
// the command runs against.
const (
	// placeholderBase is the commit the range of the git gates starts at.
	placeholderBase = "$BASE"
	// placeholderRepo is the root of the repository.
	placeholderRepo = "$REPO"
)

// treeGates is THE list of the gates that need nothing but the tree.
//
// It is sorted by name, and the self-test says so: the order of the report
// is the order of this list, and a list that is not sorted makes the report
// of two runs differ for a reason that has nothing to do with the tree.
//
// The list is the policy, and the policy is measured against the workflows:
// every gate here is invoked by a workflow, and the arguments that workflow
// passes it name no artifact and no SDK. The self-test checks both
// directions — a gate in this list that a workflow runs with a TRX on the
// command line is a gate that does not belong here, and a gate in this
// list that no workflow runs at all is a gate nobody reviews.
//
// The flags are the ones the workflows pass. Where a workflow's flags carry
// a value that only the workflow knows (the base commit of the pull
// request), the value is a placeholder this runner resolves.
var treeGates = []treeGate{
	{
		name:  "changelog-assemble",
		needs: "the changelog fragments and CHANGELOG.md",
		args:  []string{"--check"},
	},
	{
		name:  "check-abstractions-contract",
		needs: "the tree",
		args:  []string{"--min-packages", "19", "--min-namespaces", "7", "--min-files", "141"},
	},
	{
		name:  "check-analyzer-scope",
		needs: "the tree and tools/analyzer-scope.json",
		args:  []string{"--min-files", "150"},
	},
	{
		name:  "check-arch-anchors",
		needs: "the tree and the anchor exceptions",
		args:  []string{"--min-files", "40"},
	},
	{
		name:  "check-arch-suite-once",
		needs: "the tree and the shard lists",
		args:  []string{"--min-shards", "4"},
	},
	{
		name:  "check-doc-cites",
		needs: "the tree and the documents that cite it",
		args: []string{
			"--min-files", "2", "--min-cites", "100", "--min-types", "50",
			"--min-api", "12", "--min-counts", "1", "--min-test-cites", "70",
			"--min-guide-usings", "30", "--min-base-lists", "2",
		},
	},
	{
		name:  "check-doc-facts",
		needs: "the tree",
		args:  []string{"--min-facts", "5"},
	},
	{
		name:  "check-gate-policy",
		needs: "the tree and docs/CI_GATES.md",
		args:  []string{"--min-gates", "42"},
	},
	{
		name:  "check-local-merge-loss",
		needs: "the tree and the git history",
		args:  []string{"--range", "origin/dev", "--tip", "HEAD", "--min-merges", "1", "--allow-missing-heads"},
		git:   true,
	},
	{
		name:  "check-md-links",
		needs: "the tree",
		args:  []string{"--min-files", "250", "--min-refs", "600"},
	},
	{
		name:  "check-no-test-sdk",
		needs: "the tree",
		args:  []string{"--min-files", "150"},
	},
	{
		name:  "check-public-api-resign",
		needs: "the tree and the git history",
		args: []string{
			"--base", placeholderBase,
			"--min-rows", "src/Harbor.Abstractions.Contracts=1896",
			"--min-rows", "src/Harbor.Abstractions=1269",
		},
		git: true,
	},
	{
		name:  "check-result-chains",
		needs: "the tree and the ratchet baseline",
		args:  []string{"--check", "--baseline", "tools/result-chains-baseline.json"},
	},
	{
		name:  "check-shard-balance",
		needs: "the tree and the recorded shard costs",
		args:  []string{"--min-projects", "37", "--max-skew", "4.0"},
	},
	{
		name:  "check-string-switch",
		needs: "the tree and the exceptions list",
		args:  []string{"--check", "--min-labels", "280"},
	},
	{
		name:  "check-test-dup",
		needs: "the tree",
		args:  []string{"--min-files", "800", "--min-methods", "9000", "--json-out", "test-dup-groups.json", "--warn-only"},
	},
	{
		name:  "check-test-json-reflection",
		needs: "the tree",
		args:  []string{},
	},
	{
		name:  "check-test-projects",
		needs: "the tree and the project lists",
		args: []string{
			"--min-projects", "43", "--min-tested", "38", "--min-lists", "7",
			"--min-claims", "37", "--min-contexts", "41",
		},
	},
	{
		name:  "check-tool-schema-pin",
		needs: "the tree and the pinned schemas",
		args:  []string{"--min-tools", "25"},
	},
	{
		name:  "check-xml-doc-coverage",
		needs: "the tree",
		args:  []string{"--min-members", "493", "--min-documented", "478", "--min-coverage", "0.96"},
	},
	{
		name:  "ci-shard-slices",
		needs: "the tree and the CI project lists",
		args:  []string{"--min-shards", "4", "--min-projects", "37"},
	},
	{
		name:  "gen-plugin-inventory",
		needs: "the tree and the plugin registries",
		args:  []string{"--check", "--min-axes", "7", "--min-routes", "7", "--min-plugins", "6"},
	},
	{
		name:  "gen-seam-graph",
		needs: "the tree",
		args:  []string{"--check", "--min-files", "2523", "--min-interfaces", "191"},
	},
	{
		name:  "md-lint",
		needs: "the tree",
		args:  []string{"--min-files", "250", "--min-lines", "60000"},
	},
	{
		name:   "normalize-mtimes",
		needs:  "the tree and the git history; it REWRITES the mtimes of the tree",
		args:   []string{"--repo", placeholderRepo, "--base", placeholderBase},
		git:    true,
		writer: true,
	},
}

// state is what a gate of the list turned out to be.
//
// The four states are the four answers to "can this gate run here?", and
// they are the reason the list is checked against the tree and not trusted.
type state int

const (
	// stateCompiled: the gate has a package under tools/gates/ and is
	// registered in the binary. It runs.
	stateCompiled state = iota
	// statePython: the gate is still a python script. It is not part of
	// `all` — the compiled binary must not need python — and the local
	// runner runs it with python3.
	statePython
	// stateMissing: the gate has neither a package nor a script. It cannot
	// run, and the run fails with the code that says so.
	stateMissing
	// stateWriter: the gate rewrites the tree. It is a tree gate, and it is
	// not run by a command whose job is to look at the tree.
	stateWriter
)

func (s state) String() string {
	switch s {
	case stateCompiled:
		return "compiled"
	case statePython:
		return "python"
	case stateMissing:
		return "missing"
	case stateWriter:
		return "writer"
	}
	return "unknown"
}

// resolve says what a gate of the list turned out to be in this tree.
//
// The tree is the truth: a directory under tools/gates/ is a compiled gate,
// and a script in tools/ is a python gate. A gate that is neither is a gate
// that cannot run, and the run says so by name instead of skipping it —
// a gate that quietly disappears from the list is a gate that stops
// checking anything without anybody noticing.
func resolve(root string, g treeGate) (state, string) {
	if info, err := os.Stat(filepath.Join(root, "tools", "gates", g.name)); err == nil && info.IsDir() {
		if g.writer {
			return stateWriter, "it rewrites the tree, and a checker does not"
		}
		return stateCompiled, ""
	}
	if info, err := os.Stat(filepath.Join(root, "tools", g.name+".py")); err == nil && !info.IsDir() {
		if g.writer {
			return stateWriter, "it rewrites the tree, and a checker does not"
		}
		return statePython, "the gate is still a python script"
	}
	return stateMissing, "there is no tools/gates/" + g.name + " and no tools/" + g.name + ".py"
}

// expand replaces the placeholders in a gate's arguments.
//
// The base commit is resolved once per run and passed in, because resolving
// it costs a git process and the gates that want it all want the same one.
func expand(g treeGate, root, base string) ([]string, error) {
	out := make([]string, 0, len(g.args))
	for _, arg := range g.args {
		switch arg {
		case placeholderBase:
			if base == "" {
				return nil, fmt.Errorf("the gate needs a base commit and none could be resolved")
			}
			out = append(out, base)
		case placeholderRepo:
			out = append(out, root)
		default:
			out = append(out, arg)
		}
	}
	return out, nil
}

// sortedGates returns the list in the order the report prints it: by name.
//
// The order is a contract, not a convenience. The report of two runs of two
// machines has to be comparable line by line, and a list that is sorted
// only sometimes is a list whose report cannot be compared.
func sortedGates() []treeGate {
	out := make([]treeGate, len(treeGates))
	copy(out, treeGates)
	sort.Slice(out, func(i, j int) bool { return out[i].name < out[j].name })
	return out
}

// pendingLine renders a gate that is still python as the line the local
// runner executes: the script and its flags, tab-separated from the name.
//
// The tab is the separator because the arguments carry spaces (the paths of
// the baseline files) and a space-separated line cannot be split without a
// quoting convention that two programs would have to agree on.
func pendingLine(g treeGate, root, base string) (string, error) {
	args, err := expand(g, root, base)
	if err != nil {
		return "", err
	}
	parts := append([]string{"python3", "tools/" + g.name + ".py"}, args...)
	return g.name + "\t" + strings.Join(parts, " "), nil
}
