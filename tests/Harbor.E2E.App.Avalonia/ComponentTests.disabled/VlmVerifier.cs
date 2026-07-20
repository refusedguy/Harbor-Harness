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
///         <b>Why out-of-process:</b> the VLM SDK is a Node CLI; running it as
///         a subprocess keeps the test process unencumbered and lets tests run
///         even when the CLI is unavailable (verification is logged but doesn't
///         fail the test — the deterministic Avalonia tree-walk assertions are
///         the test contract; VLM verification is a supplementary visual audit).
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

        // HARBOR_SKIP_VLM=1 short-circuits the VLM call entirely — the test
        // still asserts that the screenshot file exists (the deterministic
        // tree-walk assertions are the contract), but skips the slow out-of-
        // process VLM call. Use this when running tests in CI / under a tight
        // budget, then run `Harbor.E2E.App.Avalonia.VlmBatch` (or the
        // standalone verify script) over the screenshots afterwards.
        if (string.Equals(Environment.GetEnvironmentVariable("HARBOR_SKIP_VLM"), "1",
            StringComparison.Ordinal))
        {
            await AppendReportAsync(testName, screenshotPath, description, "SKIPPED (HARBOR_SKIP_VLM=1)",
                false, cancellationToken).ConfigureAwait(false);
            return new VlmResult(false, "SKIPPED (HARBOR_SKIP_VLM=1)", string.Empty);
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
            // Hard 25-second timeout per VLM call. Without this, a slow VLM
            // response (or a hung z-ai subprocess) blocks the test forever.
            // 25s is enough for the model to respond on a warm cache while
            // keeping total test runtime bounded.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var (exitCode, stdout, stderr) = await RunCaptureAsync(
                "z-ai", $"vision -p \"{Escape(prompt)}\" -i \"{screenshotPath}\"", cts.Token)
                .ConfigureAwait(false);

            var raw = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            var match = raw.Contains("MATCH", StringComparison.OrdinalIgnoreCase)
                && !raw.Contains("DIFFER", StringComparison.OrdinalIgnoreCase);

            // Append to the JSONL report so a reviewer can read the full verdict.
            await AppendReportAsync(testName, screenshotPath, description, raw, match, cancellationToken)
                .ConfigureAwait(false);

            return new VlmResult(match, raw, stderr);
        }
        catch (Exception ex)
        {
            // VLM CLI not installed / network down — record the failure but
            // don't fail the test (VLM is supplementary; the deterministic
            // tree-walk assertions are the contract).
            await AppendReportAsync(testName, screenshotPath, description, $"EXCEPTION: {ex.Message}", false, cancellationToken)
                .ConfigureAwait(false);
            return new VlmResult(false, $"VLM unavailable: {ex.Message}", ex.Message);
        }
    }

    /// <summary>Escape a string so it survives being passed as a double-quoted shell argument.</summary>
    private static string Escape(string s)
    {
        // Replace any embedded double-quotes with single quotes (we can't
        // easily escape " inside the shell's double-quoted string without
        // shelling out to bash with $'…' — keep it simple).
        return s.Replace("\"", "'", StringComparison.Ordinal);
    }

    /// <summary>Run a process, capturing stdout + stderr.</summary>
    private static async Task<(int exitCode, string stdout, string stderr)> RunCaptureAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            return (-1, string.Empty, "Process.Start returned false");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, string.Empty, "VLM call timed out after 25s");
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
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

    /// <summary>Full VLM output (stdout).</summary>
    public string Output { get; } = Output;

    /// <summary>VLM stderr (or exception message if the CLI failed to start).</summary>
    public string Error { get; } = Error;
}
