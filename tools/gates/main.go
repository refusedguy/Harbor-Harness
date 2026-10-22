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

	aggregateanalyzercost "harbor.local/gates/aggregate-analyzer-cost"
	aggregatebuildvcpu "harbor.local/gates/aggregate-build-vcpu"
	aotpublish "harbor.local/gates/aot-publish-gate"
	changelog "harbor.local/gates/changelog-assemble"
	checkabstractionscontract "harbor.local/gates/check-abstractions-contract"
	checkanalyzerscope "harbor.local/gates/check-analyzer-scope"
	registrygate "harbor.local/gates/check-coverage-registry"
	docfacts "harbor.local/gates/check-doc-facts"
	dupjscpd "harbor.local/gates/check-dup-jscpd"
	eventtopology "harbor.local/gates/check-event-topology"
	mergeloss "harbor.local/gates/check-local-merge-loss"
	checknotestsdk "harbor.local/gates/check-no-test-sdk"
	checkpublicapiresign "harbor.local/gates/check-public-api-resign"
	checkreferencetrimmer "harbor.local/gates/check-reference-trimmer"
	shardbalance "harbor.local/gates/check-shard-balance"
	testdup "harbor.local/gates/check-test-dup"
	jsonreflection "harbor.local/gates/check-test-json-reflection"
	testpackages "harbor.local/gates/check-test-packages"
	testprojects "harbor.local/gates/check-test-projects"
	pinschema "harbor.local/gates/check-tool-schema-pin"
	tunitanalyzer "harbor.local/gates/check-tunit-analyzer-gate"
	checkworkflows "harbor.local/gates/check-workflows"
	checkxmldoccoverage "harbor.local/gates/check-xml-doc-coverage"
	codemodtestevents "harbor.local/gates/codemod-test-events"
	codemodtoolnames "harbor.local/gates/codemod-tool-names"
	codemodschemas "harbor.local/gates/codemod-tool-schemas"
	coveragereport "harbor.local/gates/coverage-report"
	gene2eshards "harbor.local/gates/gen-e2e-shards"
	genseamgraph "harbor.local/gates/gen-seam-graph"
	goquality "harbor.local/gates/go-quality"
	gate "harbor.local/gates/internal/gate"
	measurebuildcpu "harbor.local/gates/measure-build-cpu"
	measureprojectgraph "harbor.local/gates/measure-project-graph"
	msbuildevaluationcost "harbor.local/gates/msbuild-evaluation-cost"
	normalizemtimes "harbor.local/gates/normalize-mtimes"
	packvalidate "harbor.local/gates/pack-validate"
	prcost "harbor.local/gates/pr-cost"
	rune2eshards "harbor.local/gates/run-e2e-avalonia-shards"
	testcensus "harbor.local/gates/test-census"
	testmeasurements "harbor.local/gates/test-measurements"
	testskips "harbor.local/gates/test-skips"
)

// registered is the gate table. Every entry is one gate, one subcommand,
// and one python original that no longer exists.
//
// The gate's name is the name of its directory under tools/gates/, and the
// name of the subcommand that runs it: one directory, one gate, one
// workflow line.
//
// The table is in alphabetical order, so two porters who add a gate at the
// same time do not fight over one place in the file.
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
		name:    "aot-publish-gate",
		summary: "the AOT publish's outcome and diagnostics match the committed record",
		run:     aotpublish.Run,
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
		name:    "check-dup-jscpd",
		summary: "the second duplication detector, against the committed baseline",
		run:     dupjscpd.Run,
	},
	{
		name:    "check-event-topology",
		summary: "the event-topology document's producer rows name the tree's real PublishAsync call sites",
		run:     eventtopology.Run,
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
		name:    "check-test-dup",
		summary: "a test body is a copy of another test body",
		run:     testdup.Run,
	},
	{
		name:    "check-test-json-reflection",
		summary: "no test serializes through the reflection resolver",
		run:     jsonreflection.Run,
	},
	{
		name:    "check-test-packages",
		summary: "which packages a test or sample project may reference, and whether a banned one is among them",
		run:     testpackages.Run,
	},
	{
		name:    "check-test-projects",
		summary: "every test project in the solution is discovered by a CI run",
		run:     testprojects.Run,
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
		name:    "check-workflows",
		summary: "no workflow file declares a key twice, which is a file GitHub refuses to read",
		run:     checkworkflows.Run,
	},
	{
		name:    "check-xml-doc-coverage",
		summary: "every public member of the frozen contract assembly carries an XML doc",
		run:     checkxmldoccoverage.Run,
	},
	{
		name:    "codemod-test-events",
		summary: "the hand-built test events and messages move onto the shared builders",
		run:     codemodtestevents.Run,
	},
	{
		name:    "codemod-tool-names",
		summary: "the tool-name literals move onto the WellKnownTools catalog",
		run:     codemodtoolnames.Run,
	},
	{
		name:    "codemod-tool-schemas",
		summary: "the one-shot migration of the tool schemas, and the writer of the pin",
		run:     codemodschemas.Run,
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
		name:    "go-quality",
		summary: "the Go sources of the gates: formatted, vet-clean, and registered",
		run:     goquality.Run,
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
		name:    "pr-cost",
		summary: "what one pull request costs: the runner-minutes, the wall, the critical path and the peak width",
		run:     prcost.Run,
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
