using System.Collections.Generic;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="BashTool" /> through the Proxy seam (#1590): the
///     process is replaced by a scripted <see cref="FakeProcessHandle" />, so
///     every test here runs on ANY OS with NO live <c>/bin/bash</c>. What stays
///     with real processes (tree-kill against /proc, real shell pipelines)
///     lives in <see cref="BashToolBehaviorTests" />, Linux-gated as before.
/// </summary>
/// <remarks>
///     Pinned with the fake, portable:
///     <list type="bullet">
///       <item>start-failure surfaces the proxy's error text as a ToolResult;</item>
///       <item>output composition: <c>stdout · [stderr] · [exit code: N]</c>, error on non-zero;</item>
///       <item>the per-stream 100_000-char builder cap drops later lines (logged) and the
///           combined transcript hard-cuts at 50_000 with a trailing note;</item>
///       <item>timeout / caller-cancel kill the tree exactly once with the known messages;</item>
///       <item>command/args/env/cwd flow into <see cref="ProcessSpec" /> verbatim.</item>
///     </list>
/// </remarks>
public class BashToolProxyTests
{
    // ── 1. Start failure comes back through the seam ─────────────────────

    [Test]
    public async Task StartFailure_ProxyError_IsSurfaced_Verbatim()
    {
        FakeProcessProxy proxy = new()
        {
            StartFailure = "Failed to start process: no such file",
            Build = _ => throw new InvalidOperationException("unreachable: Start failed")
        };
        BashTool tool = new(proxy, NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse("""{"command":"echo hi","timeout":5}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).IsEqualTo("Failed to start process: no such file");
    }

    // ── 2. Success: composition order and exit code ──────────────────────

    [Test]
    public async Task ExitZero_StdoutThenStderrThenExitCode_ExactShape()
    {
        FakeProcessProxy proxy = StderrProxy(out _);
        BashTool tool = new(proxy, NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse("""{"command":"echo hi","timeout":5}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsFalse();
        // Composition appends a '\n' separator after each non-empty block, on
        // top of the newline-terminated builders — the empty lines are the tool's
        // committed shape (real-shell tests only assert Contains).
        await Assert.That(result.Output).IsEqualTo("out-1\nout-2\n\n[stderr]\nerr-1\n\n[exit code: 0]\n");
        // No `env` argument → the spec carries a null environment override.
        await Assert.That(proxy.LastSpec!.Environment).IsNull();
    }

    [Test]
    public async Task ExitNonZero_IsError_ResultCarriesExitCode()
    {
        BashTool tool = new(StderrProxy(out FakeProcessProxy proxy), NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse("""{"command":"exit 2","timeout":5}""").RootElement;
        proxy.ExitCodeOverride = 2;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("[exit code: 2]");
    }

    // ── 3. The per-stream cap drops lines past 100_000 chars ─────────────

    /// <summary>
    ///     First line lands the builder exactly past the 100_000-char cap, so the
    ///     next line is DROPPED (counter + Warning log). The remaining transcript
    ///     still passes through the 50_000-char hard cut, so 'b' never survives —
    ///     dropped-before-logging is observable only through the log.
    /// </summary>
    [Test]
    public async Task StdoutPastBuilderCap_LineDropped_TruncationLogged()
    {
        CapturingLogger<BashTool> logger = new();
        FakeProcessProxy proxy = new()
        {
            // L1 lands the builder just past the 100_000-char cap; L2 is dropped.
            Build = _ => new FakeProcessHandle(["a" + new string('a', 100_000), "KIL-DROPPED"], [], 0)
        };
        BashTool tool = new(proxy, logger);
        var args = JsonDocument.Parse("""{"command":"cat huge.log","timeout":5}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsFalse();
        // Combined transcript exceeds the hard cut → truncated to exactly 50_000.
        await Assert.That(result.Output.Length).IsEqualTo(50_000);
        // #1587: the tail note's wording moved to the unified output-limit seam.
        await Assert.That(result.Output).Contains("truncated at 50000 chars");
        // The dropped line's marker survives nowhere in the visible output...
        await Assert.That(result.Output).DoesNotContain("KIL-DROPPED");
        // ...but the drop itself is surfaced in the log (12 = line + '\n').
        await Assert.That(logger.Entries).Contains(
            (LogLevel.Warning, "Bash output truncated: stdout dropped 12 chars, stderr dropped 0 chars (cap=100000)"));
    }

    // ── 4. Timeout: kill once, exact message ─────────────────────────────

    [Test]
    public async Task Timeout_NeverExits_ProcessKilled_TimedOutMessage()
    {
        FakeProcessProxy proxy = new() { Build = _ => new FakeProcessHandle([], [], 0, hang: true) };
        BashTool tool = new(proxy, NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse("""{"command":"sleep 100","timeout":1}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Command timed out after 1s and was killed.");
        await Assert.That(proxy.Kills).IsEqualTo(1);
    }

    // ── 5. Caller cancellation (not timeout) ─────────────────────────────

    [Test]
    public async Task CallerCancelled_KillsProcess_ReportsCancelled()
    {
        // The fake exits on its own only after 3s; the tool's own timeout is
        // 30s, so the only cancel source is the caller's token at 400ms.
        FakeProcessProxy proxy = new() { Build = _ => new FakeProcessHandle([], [], 0, exitDelayMs: 3_000) };
        BashTool tool = new(proxy, NullLogger<BashTool>.Instance);
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var args = JsonDocument.Parse("""{"command":"sleep 100","timeout":30}""").RootElement;

        ToolResult result = await tool
            .ExecuteAsync(args, TestToolContext.Create(), callerCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Command was cancelled.");
        await Assert.That(proxy.Kills).IsEqualTo(1);
    }

    // ── 6. Arguments flow into the spec verbatim ─────────────────────────

    [Test]
    public async Task Args_PassedThroughSpec_CommandArgsEnvCwd()
    {
        string workDir = Path.Combine(Path.GetTempPath(), "kilo-proxy-cwd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        FakeProcessProxy proxy = new() { Build = _ => new FakeProcessHandle([], [], 0) };
        BashTool tool = new(proxy, NullLogger<BashTool>.Instance);
        string argsJson = """{"command":"pwd-marker-1590","cwd":"@CWD@","env":{"KIL":"peace"}}"""
            .Replace("@CWD@", workDir);
        var args = JsonDocument.Parse(argsJson).RootElement;
        string shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

        try
        {
            await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

            await Assert.That(proxy.LastSpec!.FileName).IsEqualTo(shell);
            await Assert.That(proxy.LastSpec.Arguments.Count).IsEqualTo(2);
            await Assert.That(proxy.LastSpec.Arguments[^1]).IsEqualTo("pwd-marker-1590");
            await Assert.That(proxy.LastSpec.WorkingDirectory).IsEqualTo(workDir);
            await Assert.That(proxy.LastSpec.Environment!["KIL"]).IsEqualTo("peace");
        }
        finally
        {
            try { Directory.Delete(workDir); }
            catch { /* temp dir best-effort cleanup */ }
        }
    }

    // ── 7. Missing cwd fails closed BEFORE the seam is touched ───────────

    [Test]
    public async Task MissingCwd_FailsClosed_ProxyNeverCalled()
    {
        FakeProcessProxy proxy = new()
        {
            Build = _ => throw new InvalidOperationException("unreachable: cwd validated first")
        };
        BashTool tool = new(proxy, NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse(
            """{"command":"pwd","cwd":"/definitely/missing/kilo-1590"}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create()).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Working directory does not exist");
        await Assert.That(proxy.LastSpec).IsNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Two stdout lines and one stderr line, exit 0 (unless overridden per test).</summary>
    private static FakeProcessProxy StderrProxy(out FakeProcessProxy proxy) => proxy = new()
    {
        Build = _ => new FakeProcessHandle(["out-1", "out-2"], ["err-1"], 0)
    };
}

/// <summary>
///     Scripted <see cref="IProcessProxy" /> for <see cref="BashToolProxyTests" />.
///     Records the <see cref="ProcessSpec" /> it was called with and how often the
///     tool killed a process; behaviour is programmed per test through
///     <see cref="Build" />.
/// </summary>
internal sealed class FakeProcessProxy : IProcessProxy
{
    /// <summary>The spec of the last <see cref="Start" /> call; null when never called.</summary>
    public ProcessSpec? LastSpec { get; private set; }

    /// <summary>Non-null → <see cref="Start" /> returns this failure instead of building a handle.</summary>
    public string? StartFailure { get; init; }

    /// <summary>Builds the scripted handle for a successful start.</summary>
    public required Func<ProcessSpec, FakeProcessHandle> Build { get; init; }

    /// <summary>How often <see cref="IProcessHandle.KillTree" /> ran across all started handles.</summary>
    public int Kills { get; private set; }

    /// <summary>Shared exit-code override — hacky but keeps one proxy per single-handle test simple.</summary>
    public int ExitCodeOverride { get; set; } = int.MinValue;

    public Result<IProcessHandle> Start(ProcessSpec spec)
    {
        LastSpec = spec;
        if (StartFailure is not null)
            return Result.Failure<IProcessHandle>(StartFailure);

        FakeProcessHandle handle = Build(spec);
        handle.KillCounted = () => Kills++;
        if (ExitCodeOverride != int.MinValue)
            handle.ExitCodeOverride = ExitCodeOverride;
        return Result.Success<IProcessHandle>(handle);
    }
}

/// <summary>
///     Scripted <see cref="IProcessHandle" />: replays buffered stdout/stderr
///     lines when <see cref="BeginReading" /> is called (like the BCL buffers
///     until BeginOutputReadLine), signals one null end-of-stream per stream,
///     then exits after <paramref name="exitDelayMs" /> — or never, with
///     <paramref name="hang" />, so a kill is the only way out.
/// </summary>
internal sealed class FakeProcessHandle(
    string[] stdout,
    string[] stderr,
    int exitCode,
    bool hang = false,
    int exitDelayMs = 20) : IProcessHandle
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _terminations; // 0 = running, 1 = EoS+fired exactly once

    /// <inheritdoc />
    public event EventHandler<ProcessLineEventArgs>? StdoutLine;

    /// <inheritdoc />
    public event EventHandler<ProcessLineEventArgs>? StderrLine;

    /// <summary>Exit code the tool reads; real kills report non-zero, tests default to <see cref="exitCode" />.</summary>
    public int ExitCodeOverride { get; set; } = int.MinValue;

    /// <summary>Wired by <see cref="FakeProcessProxy.Start" /> to roll up kill statistics.</summary>
    public Action? KillCounted { get; set; }

    /// <inheritdoc />
    public bool HasExited => Volatile.Read(ref _terminations) > 0;

    /// <inheritdoc />
    public int ExitCode => ExitCodeOverride == int.MinValue ? exitCode : ExitCodeOverride;

    /// <inheritdoc />
    public void BeginReading()
    {
        _ = EmitAsync();
        async Task EmitAsync()
        {
            if (stdout.Length + stderr.Length > 0)
                await Task.Delay(5).ConfigureAwait(false);
            if (HasExited) { EmitEndOfStreams(); return; }
            foreach (string line in stdout)
            {
                if (HasExited) break;
                StdoutLine?.Invoke(this, new ProcessLineEventArgs(line));
            }
            foreach (string line in stderr)
            {
                if (HasExited) break;
                StderrLine?.Invoke(this, new ProcessLineEventArgs(line));
            }
            if (HasExited) { EmitEndOfStreams(); return; }
            if (hang) return; // never exits on its own — KillTree() must drive it
            await Task.Delay(exitDelayMs).ConfigureAwait(false);
            if (!HasExited) EmitEndOfStreams();
        }
    }

    /// <inheritdoc />
    public void CancelReading() { /* nothing async is ever started in the fake */ }

    /// <inheritdoc />
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _exit.Task.IsCompleted ? Task.CompletedTask : _exit.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void KillTree()
    {
        KillCounted?.Invoke();
        // A real `Kill(entireProcessTree: true)` terminates the child: streams
        // reach end-of-stream and the process reaches HasExited. The fake does
        // the same, so the tool's drain does not hang on the timeout path.
        EmitEndOfStreams();
    }

    /// <inheritdoc />
    public void Dispose() { /* no OS resources to release in the fake */ }

    /// <summary>One null per stream (end-of-stream sentinels), then HasExited + exit completion.</summary>
    private void EmitEndOfStreams()
    {
        if (Interlocked.Exchange(ref _terminations, 1) > 0)
            return;
        StdoutLine?.Invoke(this, new ProcessLineEventArgs(null));
        StderrLine?.Invoke(this, new ProcessLineEventArgs(null));
        _exit.TrySetResult();
    }
}
