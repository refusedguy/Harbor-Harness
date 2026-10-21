// harbor-gates -- the compiled gates of this repository, in one binary.
//
// The gates in tools/ used to be python scripts. They are now subcommands
// of this binary, and the binary is committed to the repository so a gate
// can run where there is no SDK, no build of Harbor, and no python.
//
// THE CONTRACT (fixed, and every gate honours it)
//
//	harbor-gates <gate> [flags]
//
//	exit 0  green   - the gate ran and found nothing
//	exit 1  red     - the gate ran and found something
//	exit 2  broken  - the gate could not run at all
//
// The text a gate prints is the text its python original printed: the
// workflows read those lines and the self-tests compare them. The flag
// names and the floors are the ones docs/CI_GATES.md lists.
//
// # WHY ONE BINARY AND NOT FORTY-FIVE
//
// Forty-five binaries would be forty-five files in git, forty-five rebuild
// jobs, and forty-five ways for a reviewer to lose track of which artifact
// belongs to which source. One binary with subcommands is one file per
// architecture, one rebuild, one manifest.
//
// # WHY THE BINARY IS IN GIT
//
// A gate has to fire on the pull request that breaks the build, and on the
// documentation-only pull request that never starts the .NET build. Both of
// those are places where a compile step is not available. A committed binary
// is. The price is that a reviewer cannot read it, and the answer is a
// machine check: the gates-binary workflow rebuilds this binary from the
// sources in the same tree and compares the bytes with the committed ones.
//
// See tools/gates/README.md for the build, and docs/CI_GATES.md for the
// policy the gates obey.
package main

import (
	"fmt"
	"os"
	"sort"
	"strings"

	docfacts "harbor.local/gates/check-doc-facts"
	dupjscpd "harbor.local/gates/check-dup-jscpd"
	jsonreflection "harbor.local/gates/check-test-json-reflection"
	pinschema "harbor.local/gates/check-tool-schema-pin"
	tunitanalyzer "harbor.local/gates/check-tunit-analyzer-gate"
	codemodschemas "harbor.local/gates/codemod-tool-schemas"
	"harbor.local/gates/gen-seam-graph"
	"harbor.local/gates/internal/gate"
)

// registered is the gate table. Every entry is one gate, one subcommand,
// and one python original that no longer exists.
//
// The gate's name is the name of its directory under tools/gates/, and the
// name of the subcommand that runs it: one directory, one gate, one
// workflow line.
type registered struct {
	name    string
	summary string
	run     func(args []string) int
}

var registry = []registered{
	{
		name:    "check-doc-facts",
		summary: "a count a document states still equals the count of tracked files",
		run:     docfacts.Run,
	},
	{
		name:    "check-dup-jscpd",
		summary: "the second duplication detector, against the committed baseline",
		run:     dupjscpd.Run,
	},
	{
		name:    "check-test-json-reflection",
		summary: "no test serializes through the reflection resolver",
		run:     jsonreflection.Run,
	},
	{
		name:    "check-tool-schema-pin",
		summary: "the parameter schema of every builtin tool still is the one that was pinned",
		run:     pinschema.Run,
	},
	{
		name:    "check-tunit-analyzer-gate",
		summary: "the analyzer must report a planted offender, and a waiver must suppress exactly its own site",
		run:     tunitanalyzer.Run,
	},
	{
		name:    "codemod-tool-schemas",
		summary: "the one-shot migration of the tool schemas, and the writer of the pin",
		run:     codemodschemas.Run,
	},
	{
		name:    "gen-seam-graph",
		summary: "the committed seam graph still matches the tree",
		run:     genseamgraph.Run,
	},
}

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	if len(args) == 0 {
		usage(os.Stderr)
		return gate.Broken
	}
	switch args[0] {
	case "--list":
		for _, g := range sortedRegistry() {
			fmt.Printf("%-24s %s\n", g.name, g.summary)
		}
		return gate.Green
	case "--help", "-h":
		usage(os.Stdout)
		return gate.Green
	}
	for _, g := range registry {
		if g.name == args[0] {
			return g.run(args[1:])
		}
	}
	fmt.Fprintf(os.Stderr, "unknown gate: %s\n\n", args[0])
	usage(os.Stderr)
	return gate.Broken
}

func sortedRegistry() []registered {
	out := make([]registered, len(registry))
	copy(out, registry)
	sort.Slice(out, func(i, j int) bool { return out[i].name < out[j].name })
	return out
}

func usage(w *os.File) {
	fmt.Fprintln(w, "harbor-gates <gate> [flags]")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "The gates of this repository, compiled. Exit codes: 0 green, 1 red, 2 the gate could not run.")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "gates:")
	for _, g := range sortedRegistry() {
		fmt.Fprintf(w, "  %-24s %s\n", g.name, g.summary)
	}
	fmt.Fprintln(w)
	fmt.Fprintln(w, "  --list               print this table")
	fmt.Fprintln(w, "  --help               print this text")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "Each gate takes its own flags; run `harbor-gates <gate> --help` for them.")
	fmt.Fprintln(w, strings.TrimSpace(""))
}
