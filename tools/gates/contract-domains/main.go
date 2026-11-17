// Package contractdomains is the compiled instrument of issue #2113: the
// map of who uses the contract roots, the domains the map falls into, and
// the price of each of them.
//
// # WHAT IT MEASURES
//
// The two contract roots of this tree -- Harbor.Abstractions.Contracts and
// Harbor.Abstractions -- are the bottom of the pyramid, and almost every
// project of the solution depends on them: a change in one file of a root
// rebuilds almost the whole tree. The issue asks whether the answer is to
// CUT the root into pieces (one assembly per kind of type) or to NARROW the
// contract (move out what almost nobody uses, and leave one root).
//
// The instrument answers it with numbers instead of taste:
//
//   - the CENSUS: for every type of the two roots, the number of projects
//     of the solution whose code names it. The number is a fact about the
//     sources: a project uses a type when its code names it.
//   - the DOMAINS: the types grouped by the set of projects that use them.
//     A domain is a group with a consumer set of its own. A set of types
//     everybody uses is NOT a domain: it cannot be separated, because the
//     assembly it would move into has the same consumers as the one it
//     left.
//   - the RADIUS: the number of projects a change in a domain rebuilds,
//     measured on the re-wired graph, and its price in the seconds the
//     compile-cost baseline recorded for those projects.
//   - the COST: what one more assembly costs -- a row in the layer matrix,
//     a row in the layer rules, the frozen public-API rows that move with
//     it, one more compilation in the architecture sweep, and the number of
//     consumer files that would need a new `using`.
//
// # WHY IT IS AN INSTRUMENT AND NOT A GATE
//
// The decision the issue asks for is taken by the numbers of #2112 (the
// compile-cost instrument), not by this one. This instrument produces the
// numbers the decision needs, and it is re-runnable: the same command
// before a split and after it says what the split bought.
//
// The exit code is the contract of the binary: 0 green, 1 red, 2 the gate
// could not run. It goes red when the census it just measured does not
// match the committed snapshot (--check), and the self-test proves that
// path on a planted fixture.
package contractdomains

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"strings"

	"harbor.local/gates/internal/gate"
)

// The contract roots the census covers.
const (
	contractsRoot    = "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj"
	abstractionsRoot = "src/Harbor.Abstractions/Harbor.Abstractions.csproj"
)

// The defaults of the flags.
const (
	defaultSolution = "Harbor.slnx"
	defaultBaseline = "tools/compile-cost-baseline.txt"
	// defaultCensus is the committed snapshot of the census: the
	// "before" of a split, and the thing `--check` compares against.
	defaultCensus     = "tools/contract-domains-baseline.txt"
	defaultJaccard    = 0.75
	defaultUniversal  = 0.80
	defaultDomainPlan = "3,5,8,12"
	// The cost of the architecture sweep: the number of compilings it
	// makes, the seconds a pass costs and the number of passes a run
	// makes. The numbers are the measured ones (changelog fragment
	// 2167: "the sweep Roslyn -- 93.3 s per test, four analyzer passes
	// over the same 64 compilations, 73.0 s per pass"), and the flags
	// let a reader put their own measurement in.
	defaultSweepCompilations = 64
	defaultSweepSeconds      = 73.0
	defaultSweepPasses       = 4
)

// options are the flags of the instrument.
type options struct {
	solution     string
	baseline     string
	jsonOut      string
	checkPath    string
	writePath    string
	jaccard      float64
	universal    float64
	plan         []int
	roots        []string
	selfTest     bool
	showConsumer bool
	// The cost of the architecture sweep: how many compilings it makes,
	// what a pass costs and how many passes a run makes. The defaults are
	// the measured numbers of the sweep (the ci.yml comment on the arch
	// shard: "it compiles 61 product assemblies to check the layering
	// rules"), and the flags let a reader put their own measurement in.
	sweepCompilations int
	sweepSeconds      float64
	sweepPasses       int
}

// Run is the gate entry point.
func Run(args []string) int {
	opts := options{
		solution:          defaultSolution,
		baseline:          defaultBaseline,
		jaccard:           defaultJaccard,
		universal:         defaultUniversal,
		sweepCompilations: defaultSweepCompilations,
		sweepSeconds:      defaultSweepSeconds,
		sweepPasses:       defaultSweepPasses,
	}
	for i := 0; i < len(args); i++ {
		name, value, hasValue := splitFlag(args[i])
		switch name {
		case "--solution":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--solution needs a path")
					return gate.Broken
				}
				value = args[i]
			}
			opts.solution = value
		case "--baseline":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--baseline needs a path")
					return gate.Broken
				}
				value = args[i]
			}
			opts.baseline = value
		case "--json":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--json needs a path")
					return gate.Broken
				}
				value = args[i]
			}
			opts.jsonOut = value
		case "--check":
			if !hasValue {
				// The value is optional: `--check` alone compares
				// against the committed census snapshot.
				if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
					i++
					value = args[i]
				} else {
					value = defaultCensus
				}
			}
			opts.checkPath = value
		case "--write":
			if !hasValue {
				if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
					i++
					value = args[i]
				} else {
					value = defaultCensus
				}
			}
			opts.writePath = value
		case "--jaccard":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--jaccard needs a number")
					return gate.Broken
				}
				value = args[i]
			}
			parsed, err := parseFloat(value)
			if err != nil {
				fmt.Fprintf(os.Stderr, "--jaccard is not a number: %s\n", value)
				return gate.Broken
			}
			opts.jaccard = parsed
		case "--universal":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--universal needs a number")
					return gate.Broken
				}
				value = args[i]
			}
			parsed, err := parseFloat(value)
			if err != nil {
				fmt.Fprintf(os.Stderr, "--universal is not a number: %s\n", value)
				return gate.Broken
			}
			opts.universal = parsed
		case "--plan":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--plan needs a list of numbers")
					return gate.Broken
				}
				value = args[i]
			}
			for _, part := range strings.Split(value, ",") {
				parsed, err := parseInt(strings.TrimSpace(part))
				if err != nil {
					fmt.Fprintf(os.Stderr, "--plan is not a list of numbers: %s\n", value)
					return gate.Broken
				}
				opts.plan = append(opts.plan, parsed)
			}
		case "--sweep-compilations":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--sweep-compilations needs a number")
					return gate.Broken
				}
				value = args[i]
			}
			parsed, err := parseInt(value)
			if err != nil {
				fmt.Fprintf(os.Stderr, "--sweep-compilations is not a number: %s\n", value)
				return gate.Broken
			}
			opts.sweepCompilations = parsed
		case "--sweep-seconds":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--sweep-seconds needs a number")
					return gate.Broken
				}
				value = args[i]
			}
			parsed, err := parseFloat(value)
			if err != nil {
				fmt.Fprintf(os.Stderr, "--sweep-seconds is not a number: %s\n", value)
				return gate.Broken
			}
			opts.sweepSeconds = parsed
		case "--sweep-passes":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--sweep-passes needs a number")
					return gate.Broken
				}
				value = args[i]
			}
			parsed, err := parseInt(value)
			if err != nil {
				fmt.Fprintf(os.Stderr, "--sweep-passes is not a number: %s\n", value)
				return gate.Broken
			}
			opts.sweepPasses = parsed
		case "--roots":
			if !hasValue {
				i++
				if i >= len(args) {
					fmt.Fprintln(os.Stderr, "--roots needs a comma-separated list")
					return gate.Broken
				}
				value = args[i]
			}
			for _, part := range strings.Split(value, ",") {
				opts.roots = append(opts.roots, strings.TrimSpace(part))
			}
		case "--self-test":
			opts.selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates contract-domains [--solution Harbor.slnx]")
			fmt.Println("                                            [--baseline tools/compile-cost-baseline.txt]")
			fmt.Println("                                            [--jaccard 0.75] [--universal 0.80]")
			fmt.Println("                                            [--plan 3,5,8,12] [--roots a,b]")
			fmt.Println("                                            [--json out.json] [--check [snapshot]] [--write [snapshot]]")
			fmt.Println("                                            [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", args[i])
			return gate.Broken
		}
	}
	if opts.selfTest {
		return selfTestRun()
	}
	if len(opts.plan) == 0 {
		for _, part := range strings.Split(defaultDomainPlan, ",") {
			parsed, _ := parseInt(part)
			opts.plan = append(opts.plan, parsed)
		}
	}
	roots := opts.roots
	if len(roots) == 0 {
		roots = []string{contractsRoot, abstractionsRoot}
	}
	root, err := gate.RepoRoot()
	if err != nil {
		root = "."
	}
	return measureAndReport(root, opts, roots)
}

// measureAndReport measures the tree and prints the report.
func measureAndReport(root string, opts options, roots []string) int {
	measured, err := loadTree(root, opts.solution)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	census := measureTypes(measured, roots)
	costs := newBaseline()
	if opts.baseline != "" {
		path := opts.baseline
		if !filepath.IsAbs(path) {
			path = filepath.Join(root, path)
		}
		costs, err = readBaseline(path)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}
	baseline := costs
	report := buildReport(root, measured, census, baseline, opts, roots)
	printReport(report)
	if opts.jsonOut != "" {
		path := opts.jsonOut
		if !filepath.IsAbs(path) {
			path = filepath.Join(root, path)
		}
		data, err := json.MarshalIndent(report, "", " ")
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		if err := os.WriteFile(path, data, 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}
	snapshot := report.snapshot()
	if opts.writePath != "" {
		path := opts.writePath
		if !filepath.IsAbs(path) {
			path = filepath.Join(root, path)
		}
		if err := os.WriteFile(path, []byte(snapshot), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		fmt.Printf("\nwrote the census snapshot to %s\n", opts.writePath)
	}
	if opts.checkPath != "" {
		code := checkSnapshot(opts.checkPath, snapshot)
		if code != gate.Green {
			return code
		}
	}
	return gate.Green
}

// checkSnapshot compares the committed snapshot with what was measured.
//
// The snapshot is the census: the types of the roots, their consumer sets
// and the domains they fall into. A tree that moved a type, added one or
// changed who uses it changes the snapshot, and the gate says so.
func checkSnapshot(path string, snapshot string) int {
	committed, err := os.ReadFile(path)
	if err != nil {
		fmt.Fprintf(os.Stderr, "cannot read the snapshot %s: %v\n", path, err)
		return gate.Broken
	}
	if string(committed) == snapshot {
		fmt.Printf("\nGREEN: the census matches %s\n", path)
		return gate.Green
	}
	fmt.Printf("\nRED: the census moved.\n")
	fmt.Printf("  committed: %s\n", path)
	fmt.Printf("  fix      : re-run with --write %s\n", path)
	return gate.Red
}

// splitFlag splits a flag into its name and its value.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	name, value, found := strings.Cut(arg, "=")
	return name, value, found
}

// parseFloat parses a float.
func parseFloat(value string) (float64, error) {
	var out float64
	_, err := fmt.Sscanf(value, "%g", &out)
	return out, err
}

// parseInt parses an int.
func parseInt(value string) (int, error) {
	var out int
	_, err := fmt.Sscanf(value, "%d", &out)
	return out, err
}
