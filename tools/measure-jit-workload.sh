#!/usr/bin/env bash
# JIT vs NativeAOT scripted-workload measurement harness (issue #411).
#
# WHAT: builds apps/Harbor.App.Cli as plain JIT (framework-dependent Release,
# the configuration developers run) and drives the same offline workload N
# times against it: --version / --help / --providers. These three are the only
# CLI verbs that need no provider key and no network, so runs are free and
# reproducible — the same set the aot-publish smoke step (#413) uses.
#
# WHY NOT AOT HERE: the NativeAOT publish of this tree FAILS today (recorded in
# .github/aot-warning-baseline.txt, gated by #413: IL3000 x3 incl. one inside
# Microsoft.CodeAnalysis itself, IL2072, IL2070 — ILC runs warnings-as-errors).
# A failing ILC publish emits no binary, so there is no AOT artifact to drive.
# This script therefore establishes the JIT half of the #411 table (median +
# spread, never a single number — #998 measured 1.43x spread on one unchanged
# job) and prints the verbatim AOT recipe + recorded outcome alongside, so the
# day #413's record flips to published the same workload runs unchanged
# against both binaries. No pass/fail threshold: an absolute wall-clock gate on
# a shared runner is a wrong metric, not a bad number (#998, #410).
#
# RUN: locally `./tools/measure-jit-workload.sh [N]` (default N=7, odd so the
# median is a measured run), or in CI via workflow_dispatch on
# .github/workflows/meas-jit-aot.yml, which runs exactly this file and uploads
# the log. Needs: .NET 10 SDK, python3 (stdlib only).
set -euo pipefail
cd "$(dirname "$0")/.."

N="${1:-7}"
OUT="meas/jit-fx"
LOG="meas/jit-workload.log"
mkdir -p meas

echo "== provenance =="
echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "commit: $(git rev-parse HEAD)"
echo "sdk: $(dotnet --list-sdks | tr '\n' '; ')"
echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs) ($(nproc) vCPU, $(uname -m))"
echo "os: $(uname -srm) / $(grep -m1 PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '\"')"

echo "== build (JIT, framework-dependent Release) =="
dotnet publish apps/Harbor.App.Cli -c Release \
  -p:ContinuousIntegrationBuild=true \
  -o "$OUT" 2>&1 | tail -3
echo "publish dir: $(du -sh "$OUT" | cut -f1) ($(du -sb "$OUT" | cut -f1) bytes)"
# Framework-dependent publish layouts differ (apphost `Harbor.App.Cli` and/or
# `Harbor.App.Cli.dll`): drive the apphost when it exists — that is the true
# JIT cold start with no `dotnet` muxer in the path — else `dotnet <dll>`.
if [ -x "$OUT/Harbor.App.Cli" ]; then
  echo "binary: $(du -h "$OUT/Harbor.App.Cli" | cut -f1) (apphost, framework-dependent)"
  RUNNER="app"
else
  echo "binary: $(du -h "$OUT/Harbor.App.Cli.dll" | cut -f1) (dll via dotnet muxer)"
  RUNNER="muxer"
fi

{
  echo "== workload: --version / --help / --providers x$N =="
  DOTNET_BIN="$(pwd)/$OUT/Harbor.App.Cli.dll"
  APPHOST="$(pwd)/$OUT/Harbor.App.Cli"
  export DOTNET_BIN APPHOST RUNNER HARBOR_TUI=plain
  python3 - "$N" <<'EOF'
import os, statistics as s, subprocess, sys, time, resource
n = int(sys.argv[1])
dll = os.environ["DOTNET_BIN"]
apphost = os.environ["APPHOST"]
runner = os.environ["RUNNER"]
wl = [["--version"], ["--help"], ["--providers"]]
print(f"runner: {'apphost-direct' if runner == 'app' else 'dotnet-muxer'}")
print(f"{'cmd':<12}{'median_ms':>10}{'min_ms':>10}{'max_ms':>10}{'spread':>8}{'rss_med_kb':>12}{'rss_max_kb':>12}")
for args in wl:
    walls, rss = [], []
    for _ in range(n):
        t0 = time.perf_counter()
        cmd = [apphost, *args] if runner == "app" else ["dotnet", dll, *args]
        r = subprocess.run(cmd, capture_output=True, text=True,
                           env={**os.environ, "DOTNET_NOLOGO": "1",
                                 "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
        dt = (time.perf_counter() - t0) * 1000
        assert r.returncode == 0, (args, r.returncode, r.stderr[-500:])
        assert r.stdout.strip(), args
        walls.append(dt)
        rss.append(resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss)
    med = s.median(walls)
    print(f"{args[0]:<12}{med:>10.0f}{min(walls):>10.0f}{max(walls):>10.0f}"
          f"{max(walls) / min(walls):>7.2f}x{s.median(rss):>12.0f}{max(rss):>12.0f}")
print("wall = spawn-to-exit per verb; rss = ru_maxrss peak (KB).")
EOF
  echo "== AOT side (not runnable — recorded outcome, #413) =="
  echo "recipe: dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 --self-contained true -p:HarborWithAot=true -o publish/aot"
  echo "outcome: failed — IL3000 (x3, one inside Microsoft.CodeAnalysis), IL2072, IL2070 as errors under ILC warnings-as-errors; no binary emitted."
  echo "record: .github/aot-warning-baseline.txt (aot-publish job compares every run)."
} | tee "$LOG"
echo "log: $LOG"
