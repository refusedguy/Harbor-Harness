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
	"harbor.local/gates/aggregate-analyzer-cost"
	"harbor.local/gates/aggregate-build-vcpu"
	changelog "harbor.local/gates/changelog-assemble"
	"harbor.local/gates/check-abstractions-contract"
	"harbor.local/gates/check-analyzer-scope"
	registrygate "harbor.local/gates/check-coverage-registry"
	docfacts "harbor.local/gates/check-doc-facts"
	mergeloss "harbor.local/gates/check-local-merge-loss"
	"harbor.local/gates/check-no-test-sdk"
	"harbor.local/gates/check-public-api-resign"
	"harbor.local/gates/check-reference-trimmer"
	shardbalance "harbor.local/gates/check-shard-balance"
	"harbor.local/gates/check-xml-doc-coverage"
	coveragereport "harbor.local/gates/coverage-report"
	gene2eshards "harbor.local/gates/gen-e2e-shards"
	"harbor.local/gates/gen-seam-graph"
	"harbor.local/gates/internal/gate"
	"harbor.local/gates/measure-build-cpu"
	"harbor.local/gates/measure-project-graph"
	"harbor.local/gates/msbuild-evaluation-cost"
	normalizemtimes "harbor.local/gates/normalize-mtimes"
	packvalidate "harbor.local/gates/pack-validate"
	rune2eshards "harbor.local/gates/run-e2e-avalonia-shards"
	"harbor.local/gates/test-census"
	"harbor.local/gates/test-measurements"
	"harbor.local/gates/test-skips"
	"os"
	"sort"
	"strings"
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
		name:    "aggregate-analyzer-cost",
		summary: "the analyzer cost table: what each analyzer pack costs the compiler",
		run:     aggregateanalyzercost.Run,
	},
	{
		name:    "aggregate-build-vcpu",
		summary: "the measurement table: the wall time, the CPU utilisation and the ratios",
		run:     aggregatebuildvcpu.Run,
	},
	{
		name:    "changelog-assemble",
		summary: "the changelog fragments are not already in CHANGELOG.md, and the assembly is idempotent",
		run:     changelog.Run,
	},
	{
		name:    "check-abstractions-contract",
		summary: "every dependency the five guarded src/ projects declare is load-bearing",
		run:     checkabstractionscontract.Run,
	},
	{
		name:    "check-analyzer-scope",
		summary: "where each analyzer package is allowed to run",
		run:     checkanalyzerscope.Run,
	},
	{
		name:    "check-coverage-registry",
		summary: "the registry of consciously uncovered files still matches the measurement",
		run:     registrygate.Run,
	},
	{
		name:    "check-doc-facts",
		summary: "a count a document states still equals the count of tracked files",
		run:     docfacts.Run,
	},
	{
		name:    "check-doc-facts",
		summary: "a count a document states still equals the count of tracked files",
		run:     docfacts.Run,
	},
	{
		name:    "check-local-merge-loss",
		summary: "a local squash-merge into dev dropped no content",
		run:     mergeloss.Run,
	},
	{
		name:    "check-no-test-sdk",
		summary: "no project file declares the VSTest host that stops TUnit's discovery",
		run:     checknotestsdk.Run,
	},
	{
		name:    "check-public-api-resign",
		summary: "a change to the frozen surface is re-signed in the same commit",
		run:     checkpublicapiresign.Run,
	},
	{
		name:    "check-reference-trimmer",
		summary: "does any project in the tree reference a package the compiler never uses",
		run:     checkreferencetrimmer.Run,
	},
	{
		name:    "check-shard-balance",
		summary: "are the CI test shards balanced by what their projects cost",
		run:     shardbalance.Run,
	},
	{
		name:    "check-xml-doc-coverage",
		summary: "every public member of the frozen contract assembly carries an XML doc",
		run:     checkxmldoccoverage.Run,
	},
	{
		name:    "coverage-report",
		summary: "the per-project and per-file coverage report of a run",
		run:     coveragereport.Run,
	},
	{
		name:    "gen-e2e-shards",
		summary: "the process plan of the headless Avalonia E2E suite",
		run:     gene2eshards.Run,
	},
	{
		name:    "gen-seam-graph",
		summary: "the committed seam graph still matches the tree",
		run:     genseamgraph.Run,
	},
	{
		name:    "gen-seam-graph",
		summary: "the committed seam graph still matches the tree",
		run:     genseamgraph.Run,
	},
	{
		name:    "measure-build-cpu",
		summary: "what a build command costs: the wall time and the CPU utilisation",
		run:     measurebuildcpu.Run,
	},
	{
		name:    "measure-project-graph",
		summary: "the shape of the project dependency graph",
		run:     measureprojectgraph.Run,
	},
	{
		name:    "msbuild-evaluation-cost",
		summary: "what a Release compile spends on MSBuild project evaluation",
		run:     msbuildevaluationcost.Run,
	},
	{
		name:    "normalize-mtimes",
		summary: "the source mtimes tell the truth, so MSBuild can skip what did not change",
		run:     normalizemtimes.Run,
	},
	{
		name:    "pack-validate",
		summary: "every packed Harbor.* package carries its metadata, its README and its lib",
		run:     packvalidate.Run,
	},
	{
		name:    "run-e2e-avalonia-shards",
		summary: "the headless Avalonia E2E plan, one process per shard",
		run:     rune2eshards.Run,
	},
	{
		name:    "test-census",
		summary: "the per-test and per-project wall census of a CI run",
		run:     testcensus.Run,
	},
	{
		name:    "test-measurements",
		summary: "the per-test measurements a CI run printed",
		run:     testmeasurements.Run,
	},
	{
		name:    "test-skips",
		summary: "the tests a run skipped, and the reason each declared",
		run:     testskips.Run,
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
