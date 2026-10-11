#!/usr/bin/env bash
# NativeAOT side of the JIT vs AOT scripted-workload measurement (issue #411).
#
# WHAT: publishes apps/Harbor.App.Cli with the verbatim #413 gate recipe and
# drives the SAME offline workload as tools/measure-jit-workload.sh N times:
# --version / --help / providers (bare `providers`: Program.cs matches the verb
# without dashes — `--providers` falls through to interactive mode and exits 1
# with no TTY). Same verbs, same N, same table shape, so the two logs are
# comparable cell by cell and the AOT column of docs/BENCHMARKS.md §4.4 is
# traceable to a CI run, not typed by hand.
#
# RECIPE (verbatim #413 `fulltree`, .github/workflows/ci.yml `aot-publish`):
#   dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 \
#     --self-contained true -p:HarborWithAot=true \
#     -p:ContinuousIntegrationBuild=true -o meas/aot-fx
# Publishable since #1055s3 (record: .github/aot-warning-baseline.txt): the
# IL3000/IL2072/IL2070 errors left with the in-process plugin pipeline, no
# suppression added. RID is pinned to linux-x64 — cross-RID numbers are not
# comparable, so they are not taken here.
#
# NEEDS: .NET 10 SDK, python3 (stdlib only), the AOT native toolchain
# (clang, lld, binutils, zlib1g-dev — installed by the CI step in
# .github/workflows/meas-jit-aot.yml, `apt-get install` locally).
#
# RUN: locally `./tools/measure-aot-workload.sh [N]` (default N=7, odd so the
# median is a measured run), or in CI via workflow_dispatch on
# .github/workflows/meas-jit-aot.yml (`measure-aot` job), which runs exactly
# this file and uploads the log. No pass/fail threshold: an absolute
# wall-clock gate on a shared runner is a wrong metric, not a bad number
# (#998, #410).
set -euo pipefail
cd "$(dirname "$0")/.."

N="${1:-7}"
OUT="meas/aot-fx"
LOG="meas/aot-workload.log"
mkdir -p meas

echo "== provenance =="
echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "commit: $(git rev-parse HEAD)"
echo "sdk: $(dotnet --list-sdks | tr '\n' '; ')"
echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs) ($(nproc) vCPU, $(uname -m))"
echo "os: $(uname -srm) / $(grep -m1 PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '\"')"

echo "== build (NativeAOT, self-contained linux-x64, verbatim #413 recipe) =="
dotnet publish apps/Harbor.App.Cli \
  -c Release -r linux-x64 \
  --self-contained true \
  -p:HarborWithAot=true \
  -p:ContinuousIntegrationBuild=true \
  -o "$OUT" 2>&1 | tail -3
echo "publish dir: $(du -sh "$OUT" | cut -f1) ($(du -sb "$OUT" | cut -f1) bytes)"
BIN="$OUT/Harbor.App.Cli"
test -x "$BIN" || { echo "no AOT executable at $BIN — the publish did not complete; see the record in .github/aot-warning-baseline.txt"; exit 1; }
echo "binary: $(du -h "$BIN" | cut -f1) (self-contained NativeAOT)"

{
  echo "== workload: --version / --help / providers x$N =="
  export BIN HARBOR_TUI=plain
  python3 - "$N" <<'EOF'
import os, statistics as s, subprocess, sys, time, resource
n = int(sys.argv[1])
binfile = os.path.abspath(os.environ["BIN"])
wl = [["--version"], ["--help"], ["providers"]]
print("runner: native-aot")
print(f"{'cmd':<12}{'median_ms':>10}{'min_ms':>10}{'max_ms':>10}{'spread':>8}{'rss_med_kb':>12}{'rss_max_kb':>12}")
for args in wl:
    walls, rss = [], []
    for _ in range(n):
        t0 = time.perf_counter()
        r = subprocess.run([binfile, *args], capture_output=True, text=True,
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
  echo "== recipe (verbatim, for reproducibility) =="
  echo "dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 --self-contained true -p:HarborWithAot=true -p:ContinuousIntegrationBuild=true -o meas/aot-fx"
  echo "record: .github/aot-warning-baseline.txt (status=published since #1055s3)."
} | tee "$LOG"
echo "log: $LOG"
