using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Skip unless running on Linux: these tests execute real <c>/bin/bash</c>
///     processes and (for the orphan check) scan <c>/proc</c>.
/// </summary>
internal sealed class SkipWhenNotLinuxAttribute : SkipAttribute
{
    public SkipWhenNotLinuxAttribute() : base(
        "BashTool process-behaviour tests require Linux (/bin/bash + /proc).") { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!OperatingSystem.IsLinux());
}

/// <summary>
///     Behavioural tests for <see cref="BashTool" /> that run real processes. Each test
///     is kept fast (&lt;5s): tool-level timeouts of ~1s, unique markers so concurrent
///     runs never collide, and Linux-only guards via <see cref="SkipWhenNotLinuxAttribute" />.
/// </summary>
/// <remarks>
///     Documented contract being pinned here (from committed BashTool):
///     <list type="bullet">
///       <item>Timeout → <c>Kill(entireProcessTree: true)</c> + error result containing
///           "timed out".</item>
///       <item>Output cap is a hardcoded const (<c>MaxOutputChars = 100_000</c> chars per
///           stream, drop-silently past the cap) followed by a final hard truncate of the
///           combined output to 50_000 chars. Dropped/truncated counters are logged only —
///           they are NOT surfaced in <see cref="ToolResult" />, so tests assert the
///           observable length/content instead.</item>
///       <item><c>env</c> entries are merged over the inherited environment
///           (<c>psi.Environment[k] = v</c>).</item>
///       <item><c>cwd</c> overrides the working directory.</item>
///     </list>
/// </remarks>
public class BashToolBehaviorTests
{
    // ── 1. Timeout kills the whole process tree ───────────────────────────

    /// <summary>
    ///     A command whose background child carries a unique marker as its argv[0]
    ///     (<c>exec -a MARKER sleep 100 &amp; wait</c>) must be killed on tool timeout,
    ///     and afterwards NO process with that marker may survive anywhere in /proc —
    ///     proving <c>Kill(entireProcessTree: true)</c> reaped the child, not just the
    ///     direct shell.
    /// </summary>
    [Test]
    [SkipWhenNotLinux]
    public async Task Timeout_KillsProcessTree_NoOrphanChildSurvives()
    {
        string marker = $"kilo-orphan-{Guid.NewGuid().ToString("N")[..16]}";
        var args = JsonDocument.Parse(
            $$"""{"command":"exec -a {{marker}} sleep 100 & wait","timeout":1}""").RootElement;

        var result = await ExecuteAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("timed out");

        // Poll-retry: give the kernel a moment to reap the killed children.
        bool markerGone = await WaitForAsync(
            () => Task.FromResult(!CmdlineScanContains(marker)),
            deadline: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(100));

        await Assert.That(markerGone).IsTrue();
    }

    // ── 2. Output cap ─────────────────────────────────────────────────────

    /// <summary>
    ///     ~200KB of stdout against the 100K-char per-stream cap must complete without
    ///     error/OOM, and the returned output must respect the final 50_000-char
    ///     truncation the tool applies to its combined output.
    /// </summary>
    [Test]
    [SkipWhenNotLinux]
    public async Task OversizedStdout_CompletesWithoutError_OutputRespectsCap()
    {
        var args = JsonDocument.Parse(
            """{"command":"yes X | head -c 200000","timeout":10}""").RootElement;

        var result = await ExecuteAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsFalse();
        // Final combined-output truncate at 50_000 chars (BashTool hard limit).
        await Assert.That(result.Output.Length).IsLessThanOrEqualTo(50_000);
        // Content actually came through stdout before truncation kicked in.
        await Assert.That(result.Output).Contains("X");
    }

    // ── 3. Env override sanity ────────────────────────────────────────────

    /// <summary>
    ///     Overriding PATH to an empty directory and invoking a nonexistent binary must
    ///     fail cleanly (bash exit code 127) — no hang, no exception escaping the tool.
    ///     Documents current behaviour of env merging.
    /// </summary>
    [Test]
    [SkipWhenNotLinux]
    public async Task EnvOverride_EmptyPath_MissingBinary_FailsCleanly()
    {
        string emptyPathDir = Path.Combine(Path.GetTempPath(), "kilo-empty-path-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyPathDir);
        try
        {
            var args = JsonDocument.Parse(
                $$$"""{"command":"definitely-missing-binary-kilo-xyz --version","env":{"PATH":"{{{emptyPathDir}}}"}}""").RootElement;

            var result = await ExecuteAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("[exit code: 127]");
        }
        finally
        {
            try { Directory.Delete(emptyPathDir); }
            catch { /* temp dir best-effort cleanup */ }
        }
    }

    // ── 4. Working directory respected ────────────────────────────────────

    /// <summary>The cwd argument must become the child shell's working directory.</summary>
    [Test]
    [SkipWhenNotLinux]
    public async Task WorkingDirectory_CwdArg_Respected()
    {
        string workDir = Path.Combine(Path.GetTempPath(), "kilo-cwd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDir));
            var args = JsonDocument.Parse(
                $$"""{"command":"pwd","cwd":"{{normalized}}"}""").RootElement;

            var result = await ExecuteAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains(normalized);
        }
        finally
        {
            try { Directory.Delete(workDir); }
            catch { /* temp dir best-effort cleanup */ }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static BashTool NewTool() => new(NullLogger<BashTool>.Instance);

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    private static Task<ToolResult> ExecuteAsync(JsonElement args)
        => NewTool().ExecuteAsync(args, CreateContext());

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan deadline, TimeSpan pollInterval)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(pollInterval);
        }

        return await condition();
    }

    /// <summary>
    ///     Scan all /proc/[0-9]*/cmdline files for a substring. Cheap enough for a few
    ///     hundred processes and robust against processes vanishing mid-scan.
    /// </summary>
    private static bool CmdlineScanContains(string needle)
    {
        byte[] needleBytes = System.Text.Encoding.UTF8.GetBytes(needle);
        foreach (string procDir in Directory.EnumerateDirectories("/proc"))
        {
            string pid = Path.GetFileName(procDir);
            if (!pid.All(char.IsAsciiDigit)) continue;
            try
            {
                if (File.ReadAllBytes(Path.Combine(procDir, "cmdline")).AsSpan().IndexOf(needleBytes) >= 0)
                    return true;
            }
            catch (IOException)
            {
                // Process exited between enumeration and read — ignore.
            }
        }

        return false;
    }
}
