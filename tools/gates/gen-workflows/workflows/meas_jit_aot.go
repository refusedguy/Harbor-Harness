// Package workflows holds the model of every workflow file this repository
// generates.
//
// # ONE FILE PER WORKFLOW
//
// Every file in this package describes exactly ONE workflow file, and the
// name of the file is the name of the workflow. That is the whole point of
// the layout: two agents that edit two different workflows edit two
// different Go files, and their changes cannot overlap in a merge. The
// shared machinery — the YAML writer, the shapes of a step and of a job —
// lives in the yamlout package, and the gate that writes and checks the
// files lives one package up.
//
// # WHAT A MODEL IS
//
// A model is a tree: a header of comments, a root mapping, jobs, steps.
// It is not a template with holes, and it is not the YAML with the keys
// renamed. The difference matters for the reason the generator exists: a
// merge conflict is a disagreement about TEXT, and a tree is a smaller and
// better-typed thing to disagree about. The tree is also what lets the gate
// answer questions the YAML cannot be asked, such as "which jobs does this
// file declare".
package workflows

import (
	"harbor.local/gates/gen-workflows/yamlout"
)

// MeasJitAot is the model of .github/workflows/meas-jit-aot.yml.
//
// It is the FIRST file to be generated, and it is the smallest one that
// still has two jobs, a flow sequence, a block scalar and per-step
// environment. It is not a merge gate and it is in no required-checks
// list, so a generator that is wrong here cannot block a pull request.
func MeasJitAot() *yamlout.Doc {
	return &yamlout.Doc{
		Header: []string{
			"JIT vs AOT scripted-workload measurement (issue #411) — manual only.",
			"",
			"WHY DISPATCH-ONLY: this is a measurement, not a gate (#413 owns the AOT",
			"gate; #998 forbids absolute wall-clock thresholds on shared runners). It",
			"runs only when a human asks for numbers, costs nothing on PRs, and uploads",
			"the logs so the table in docs/BENCHMARKS.md §4.4 is traceable to CI runs.",
			"The workloads live in tools/measure-jit-workload.sh (JIT half) and",
			"tools/measure-aot-workload.sh (AOT half, verbatim #413 recipe) — this file",
			"only provides the runner + SDK. Not in any required-checks list.",
		},
		Root: yamlout.Map(
			yamlout.P("name", yamlout.Str("meas-jit-aot")),
			yamlout.PB("on", yamlout.Map(
				yamlout.P("workflow_dispatch", yamlout.Map(
					yamlout.P("inputs", yamlout.Map(
						yamlout.P("runs", yamlout.Map(
							yamlout.P("description", yamlout.Quoted("Iterations per verb (odd; median is a measured run)")),
							yamlout.P("required", yamlout.Bool(false)),
							yamlout.P("default", yamlout.Quoted("7")),
						)),
					)),
				)),
				yamlout.P("push", yamlout.Map(
					yamlout.P("branches", yamlout.FlowSeq(yamlout.Str("meas/411-jit-aot-workload"))),
				)),
			)),
			yamlout.PB("permissions", yamlout.Map(
				yamlout.P("contents", yamlout.Str("read")),
			)),
			yamlout.PB("jobs", yamlout.Map(
				yamlout.P("measure", yamlout.Map(
					yamlout.P("runs-on", yamlout.Str("ubuntu-latest")),
					yamlout.P("timeout-minutes", yamlout.Int(30)),
					yamlout.P("steps", yamlout.Seq(
						yamlout.IC(yamlout.Map(
							yamlout.P("uses", yamlout.Str("actions/checkout@v7")),
						), "No `submodules: recursive`: the vendored ConsoleEx submodule left the",
							"product (ADR-019); nothing in this workflow builds contrib/external/."),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Setup .NET 10")),
							yamlout.P("uses", yamlout.Str("actions/setup-dotnet@v6")),
							yamlout.P("with", yamlout.Map(
								yamlout.P("dotnet-version", yamlout.Quoted("10.0.x")),
								yamlout.P("cache", yamlout.Bool(true)),
								yamlout.P("cache-dependency-path", yamlout.Quoted("Directory.Packages.props")),
							)),
						)),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Drive the scripted workload (JIT)")),
							yamlout.P("env", yamlout.Map(
								yamlout.P("DOTNET_NOLOGO", yamlout.Bool(true)),
								yamlout.P("DOTNET_CLI_TELEMETRY_OPTOUT", yamlout.Bool(true)),
								yamlout.P("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", yamlout.Bool(true)),
							)),
							yamlout.P("run", yamlout.Str(`bash tools/measure-jit-workload.sh "${{ inputs.runs }}"`)),
						)),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Upload measurement log (JIT)")),
							yamlout.P("if", yamlout.Str("always()")),
							yamlout.P("uses", yamlout.Str("actions/upload-artifact@v7")),
							yamlout.P("with", yamlout.Map(
								yamlout.P("name", yamlout.Str("jit-workload-log")),
								yamlout.P("path", yamlout.Str("meas/jit-workload.log")),
								yamlout.P("retention-days", yamlout.Int(30)),
								yamlout.P("if-no-files-found", yamlout.Str("error")),
							)),
						)),
					)),
				)),
				yamlout.PB("measure-aot", yamlout.Map(
					yamlout.P("runs-on", yamlout.Str("ubuntu-latest")),
					yamlout.P("timeout-minutes", yamlout.Int(45)),
					yamlout.P("steps", yamlout.Seq(
						yamlout.IC(yamlout.Map(
							yamlout.P("uses", yamlout.Str("actions/checkout@v7")),
						), "No `submodules: recursive`: the vendored ConsoleEx submodule left the",
							"product (ADR-019); nothing in this workflow builds contrib/external/."),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Setup .NET 10")),
							yamlout.P("uses", yamlout.Str("actions/setup-dotnet@v6")),
							yamlout.P("with", yamlout.Map(
								yamlout.P("dotnet-version", yamlout.Quoted("10.0.x")),
								yamlout.P("cache", yamlout.Bool(true)),
								yamlout.P("cache-dependency-path", yamlout.Quoted("Directory.Packages.props")),
							)),
						)),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Install the AOT native toolchain")),
							yamlout.P("run", yamlout.Block(
								"sudo apt-get update -qq\n"+
									"sudo apt-get install -y -qq clang lld binutils zlib1g-dev")),
						)),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Drive the scripted workload (AOT)")),
							yamlout.P("env", yamlout.Map(
								yamlout.P("DOTNET_NOLOGO", yamlout.Bool(true)),
								yamlout.P("DOTNET_CLI_TELEMETRY_OPTOUT", yamlout.Bool(true)),
								yamlout.P("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", yamlout.Bool(true)),
							)),
							yamlout.P("run", yamlout.Str(`bash tools/measure-aot-workload.sh "${{ inputs.runs }}"`)),
						)),
						yamlout.IB(yamlout.Map(
							yamlout.P("name", yamlout.Str("Upload measurement log (AOT)")),
							yamlout.P("if", yamlout.Str("always()")),
							yamlout.P("uses", yamlout.Str("actions/upload-artifact@v7")),
							yamlout.P("with", yamlout.Map(
								yamlout.P("name", yamlout.Str("aot-workload-log")),
								yamlout.P("path", yamlout.Str("meas/aot-workload.log")),
								yamlout.P("retention-days", yamlout.Int(30)),
								yamlout.P("if-no-files-found", yamlout.Str("error")),
							)),
						)),
					)),
				)),
			)),
		),
	}
}
