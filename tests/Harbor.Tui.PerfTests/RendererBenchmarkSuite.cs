namespace Harbor.Tui.PerfTests;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tui;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.NickConsoleEx;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using Microsoft.Extensions.Logging.Abstractions;
using SharpConsoleUI.Drivers;

/// <summary>
///     Synthetic-load benchmark harness (renderer-unification sprint Phase
///     6.1): every backend renders the SAME synthetic stream on a 24×80
///     surface; throughput, p99 latency and allocation rate are measured and
///     checked against <see cref="RendererPerformanceContract.Defaults"/>.
/// </summary>
/// <remarks>
///     Locally reproducible: <c>dotnet run --project tests/Harbor.Tui.PerfTests -- --renderer all</c>
///     (the MTP host runs every test; the report prints per-backend metrics).
/// </remarks>
public static class RendererBenchmarkSuite
{
    public const int EventCount = 1000;
    public const int WarmupEvents = 100;
    public const int Cols = 80;
    public const int Rows = 24;

    /// <summary>Runs the suite for every registered backend (id "all").</summary>
    public static async Task<IReadOnlyList<RendererBenchmarkResult>> RunAllAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<RendererBenchmarkResult> results = await RunAsync("all", ct);
        WriteReport(results);
        return results;
    }

    /// <summary>
    ///     Writes the machine-readable report consumed by the CI gate
    ///     (renderer-perf-gate.yml uploads it; BENCHMARKS_RENDERERS.md diffs
    ///     against it). Enabled via HARBOR_PERF_REPORT=/path/to/report.
    /// </summary>
    public static void WriteReport(IReadOnlyList<RendererBenchmarkResult> results)
    {
        string? path = Environment.GetEnvironmentVariable("HARBOR_PERF_REPORT");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# backend;events;events_per_sec;p99_ms;mb_per_1k_events");
        foreach (RendererBenchmarkResult r in results)
        {
            sb.Append(r.BackendId).Append(';')
                .Append(r.EventsRendered).Append(';')
                .Append(r.EventsPerSec.ToString("F0", CultureInfo.InvariantCulture)).Append(';')
                .Append(r.P99Latency.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(';')
                .Append(r.AllocatedMbPerThousandEvents.ToString("F4", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    ///     Runs the suite for the given backend id ("all", "ansi", "plain",
    ///     "cellforge", "nickconsoleex").
    /// </summary>
    public static async Task<IReadOnlyList<RendererBenchmarkResult>> RunAsync(
        string renderer,
        CancellationToken ct = default)
    {
        var results = new List<RendererBenchmarkResult>();
        if (renderer is "all" or "ansi")
        {
            results.Add(await MeasureAsync("ansi", () => new AnsiTuiRenderer(
                NullLogger<AnsiTuiRenderer>.Instance, new StringWriter()), ct));
        }

        if (renderer is "all" or "plain")
        {
            results.Add(await MeasureAsync("plain", () => new PlainTuiRenderer(new StringWriter()), ct));
        }

        if (renderer is "all" or "cellforge")
        {
            results.Add(await MeasureAsync("cellforge", () => new CellForgeTuiRenderer(
                NullLogger<CellForgeTuiRenderer>.Instance, new NullTerminalBackend()), ct));
        }

        if (renderer is "all" or "nickconsoleex")
        {
            results.Add(await MeasureAsync("nickconsoleex", () => new NickConsoleExTuiRenderer(
                NullLogger<NickConsoleExTuiRenderer>.Instance,
                driverOverride: new HeadlessConsoleDriver(Cols, Rows)), ct));
        }

        return results;
    }

    private static async Task<RendererBenchmarkResult> MeasureAsync(
        string backendId,
        Func<ITuiRenderer> factory,
        CancellationToken ct)
    {
        // Warmup: absorb JIT/dispatch cost (docs/BENCHMARKS.md methodology).
        ITuiRenderer warm = factory();
        try
        {
            await StreamAsync(warm, WarmupEvents, "warm", ct);
        }
        finally
        {
            warm.Dispose();
        }

        ITuiRenderer renderer = factory();
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            List<long> ticks = await StreamAsync(renderer, EventCount, "tok", ct);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            ticks.Sort();
            long p99 = ticks[Math.Min(ticks.Count - 1, (int)(ticks.Count * 0.99))];
            double perThousand = allocated * 1000.0 / EventCount;

            return new RendererBenchmarkResult(
                backendId,
                EventCount,
                EventCount / sw.Elapsed.TotalSeconds,
                TimeSpan.FromTicks(p99),
                perThousand / (1024.0 * 1024.0));
        }
        finally
        {
            renderer.Dispose();
        }
    }

    private static async Task<List<long>> StreamAsync(ITuiRenderer renderer, int events, string token, CancellationToken ct)
    {
        var ticks = new List<long>(events);
        var partial = AssistantMessage.Empty("perf-session", "perf-msg");
        var start = new MessageStartEvent(partial);

        for (int i = 0; i < events; i++)
        {
            AgentEvent evt = i % 20 == 0
                ? start
                : new MessageUpdateEvent(new TextDeltaEvent("0", token), partial);

            var sw = Stopwatch.StartNew();
            await renderer.RenderAsync(evt, ct);
            ticks.Add(sw.Elapsed.Ticks);
        }

        return ticks;
    }

    /// <summary>CellForge capture backend: absorbs frames without I/O.</summary>
    private sealed class NullTerminalBackend : ITerminalBackend
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Write(ReadOnlySpan<byte> bytes)
        {
        }
    }
}
