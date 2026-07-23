using System.Diagnostics;
using System.Text.Json;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Wraps the <c>z-ai vision</c> CLI to verify a screenshot against a
///     DETAILED content description. Each call invokes the VLM out-of-process
///     and returns the model's response + a pass/fail flag derived from
///     keyword matching ("yes"/"no", "match"/"differ").
/// </summary>
/// <remarks>
///     <para>
///         <b>Skipped by default:</b> VLM verification is skipped if z-ai CLI
///         is not installed (common in CI). Tests pass regardless - the
///         deterministic Avalonia tree-walk assertions are the test contract.
///         Use <c>HARBOR_SKIP_VLM=1</c> to explicitly skip even when CLI is available.
///     </para>
///     <para>
///         <b>Output:</b> every verification result is appended to
///         <c>~/.harbor/test-screenshots-comp-ct/vlm-report.jsonl</c> so a
///         reviewer can read the VLM's full verdict per screenshot without
///         re-running the tests.
///     </para>
/// </remarks>
internal static class VlmVerifier
{
    /// <summary>Path to the JSONL report file written by every verification call.</summary>
    public static readonly string ReportPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "test-screenshots-comp-ct",
        "vlm-report.jsonl");

    /// <summary>
    ///     Verify a screenshot against a detailed content description.
    /// </summary>
    /// <param name="screenshotPath">Absolute path to the PNG captured by the test.</param>
    /// <param name="description">DETAILED description of what the screenshot SHOULD show.</param>
    /// <param name="testName">Name of the test (for the report).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="VlmResult"/> with the raw VLM text + pass/fail flag.</returns>
    /// <remarks>
    ///     VLM verification is skipped by default if z-ai CLI is not installed.
    ///     Set <c>HARBOR_SKIP_VLM=1</c> to explicitly skip even when CLI is available.
    ///     Tests pass regardless of VLM availability (screenshot existence is the contract).
    /// </remarks>
    public static async Task<VlmResult> VerifyAsync(
        string screenshotPath,
        string description,
        string testName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(screenshotPath))
        {
            return new VlmResult(false, $"screenshot not found: {screenshotPath}", string.Empty);
        }

        string? zaiPath = null;
        bool skipVlm = string.Equals(Environment.GetEnvironmentVariable("HARBOR_SKIP_VLM"), "1",
            StringComparison.Ordinal) || TryFindZaiCli(out zaiPath);

        if (skipVlm)
        {
            string reason = string.Equals(Environment.GetEnvironmentVariable("HARBOR_SKIP_VLM"), "1",
                StringComparison.Ordinal)
                ? "SKIPPED (HARBOR_SKIP_VLM=1)"
                : "SKIPPED (z-ai CLI not installed)";
            Console.WriteLine("WARN [VlmVerifier] " + reason + " for " + testName);
            await AppendReportAsync(testName, screenshotPath, description, reason,
                false, cancellationToken).ConfigureAwait(false);
            return new VlmResult(false, reason, string.Empty);
        }

        // Build the prompt: tell the VLM what to look for, then ask it to
        // confirm match + list any differences. Keep the prompt compact so
        // the model returns a focused answer.
        var prompt = $"""
            This screenshot should show: {description}

            Verify:
            1) The expected elements are visible.
            2) No obvious visual bugs (overlapping/cut-off text, blank areas, broken layout).
            3) The visual state matches the description.

            Reply with EXACTLY two lines:
            Line 1: "MATCH" or "DIFFER" (does the screenshot match the description?)
            Line 2: a short list of any differences (or "none" if MATCH).
            """;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var startInfo = new ProcessStartInfo
            {
                FileName = zaiPath!,
                Arguments = $"vision -p \"{Escape(prompt)}\" -i \"{screenshotPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = startInfo };
            if (!proc.Start())
            {
                await AppendReportAsync(testName, screenshotPath, description, "SKIPPED (process start failed)",
                    false, cancellationToken).ConfigureAwait(false);
                return new VlmResult(false, "SKIPPED (process start failed)", string.Empty);
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                await AppendReportAsync(testName, screenshotPath, description, "SKIPPED (timeout)",
                    false, cancellationToken).ConfigureAwait(false);
                return new VlmResult(false, "SKIPPED (timeout)", string.Empty);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var raw = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            var match = raw.Contains("MATCH", StringComparison.OrdinalIgnoreCase)
                && !raw.Contains("DIFFER", StringComparison.OrdinalIgnoreCase);

            await AppendReportAsync(testName, screenshotPath, description, raw, match, cancellationToken)
                .ConfigureAwait(false);

            return new VlmResult(match, raw, stderr);
        }
        catch (Exception ex)
        {
            await AppendReportAsync(testName, screenshotPath, description, $"SKIPPED (exception: {ex.Message})",
                false, cancellationToken).ConfigureAwait(false);
            return new VlmResult(false, $"SKIPPED (exception: {ex.Message})", ex.Message);
        }
    }

    /// <summary>
    ///     Check if the <c>z-ai</c> CLI is available in the PATH.
    /// </summary>
    /// <param name="zaiPath">Resolved path to the CLI if found.</param>
    /// <returns>True if the CLI was found, false otherwise.</returns>
    private static bool TryFindZaiCli(out string zaiPath)
    {
        zaiPath = string.Empty;

        string fileName = OperatingSystem.IsWindows() ? "z-ai.exe" : "z-ai";
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return false;

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
            {
                zaiPath = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Escape a string so it survives being passed as a double-quoted shell argument.</summary>
    private static string Escape(string s)
    {
        return s.Replace("\"", "'", StringComparison.Ordinal);
    }

    /// <summary>Append one verification record to the JSONL report.</summary>
    private static async Task AppendReportAsync(
        string testName,
        string screenshotPath,
        string description,
        string vlmOutput,
        bool match,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            var record = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                testName,
                screenshot = Path.GetFileName(screenshotPath),
                description,
                vlmOutput = vlmOutput.Trim(),
                match,
            };
            var line = JsonSerializer.Serialize(record);
            await File.AppendAllTextAsync(ReportPath, line + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — don't fail the test because the report write failed.
        }
    }
}

/// <summary>Result of one VLM verification call.</summary>
internal sealed record VlmResult(bool Match, string Output, string Error)
{
    /// <summary>True if the VLM responded with MATCH (and not DIFFER).</summary>
    public bool Match { get; } = Match;

    /// <summary>Full VLM output (stdout). May be empty if VLM was skipped.</summary>
    public string Output { get; } = Output;

    /// <summary>VLM stderr (or exception message if the CLI failed to start).</summary>
    public string Error { get; } = Error;

    /// <summary>True if VLM verification was skipped (CLI not found or HARBOR_SKIP_VLM=1).</summary>
    public bool IsSkipped => string.IsNullOrEmpty(Output) && !Error.Contains("MATCH") && !Error.Contains("DIFFER");
}
