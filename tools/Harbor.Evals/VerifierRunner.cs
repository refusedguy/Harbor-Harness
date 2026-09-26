using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Harbor.Evals;

/// <summary>Runs verifier.sh/ps1 with timeout; separates verifier-error from check-fail.</summary>
internal static class VerifierRunner
{
    public sealed record VerifyResult(
        bool Ran, // false = verifier itself errored (infra)
        List<CheckResult> Checks,
        string Stdout,
        string Stderr,
        int ExitCode);

    public sealed record CheckResult(string Id, string Kind, string Outcome);

    public static async Task<VerifyResult> RunAsync(
        EvalTask task,
        string verifierDir,
        string workspaceDir,
        string verificationOutputPath,
        int timeoutSeconds,
        CancellationToken ct)
    {
        bool windows = OperatingSystem.IsWindows();
        var plat = windows ? task.Verifier.Windows : task.Verifier.Unix;
        if (string.IsNullOrWhiteSpace(plat.File))
            return new VerifyResult(false, [], string.Empty, "no verifier for this OS", -1);

        var psi = new ProcessStartInfo(plat.File)
        {
            WorkingDirectory = workspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in plat.Args)
            psi.ArgumentList.Add(a.Replace("{verifier}", verifierDir)
                .Replace("{workspace}", workspaceDir)
                .Replace("{verificationOutput}", verificationOutputPath));

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return new VerifyResult(false, [], string.Empty, "start failed", -1);
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    // Best-effort kill: process already gone is the normal case.
                    _ = ex;
                }
                return new VerifyResult(false, [], await outTask, await errTask + "\nHARNESS: verifier timeout", -1);
            }

            stdout.Append(await outTask);
            stderr.Append(await errTask);
            if (proc.ExitCode != 0 || !File.Exists(verificationOutputPath))
                return new VerifyResult(false, [], stdout.ToString(), stderr.ToString(), proc.ExitCode);

            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(verificationOutputPath, ct));
                var checks = new List<CheckResult>();
                foreach (var c in doc.RootElement.GetProperty("checks").EnumerateArray())
                    checks.Add(new CheckResult(
                        c.GetProperty("id").GetString() ?? "?",
                        c.TryGetProperty("kind", out var k) ? k.GetString() ?? "objective" : "objective",
                        c.GetProperty("outcome").GetString() ?? "inconclusive"));
                return new VerifyResult(true, checks, stdout.ToString(), stderr.ToString(), 0);
            }
            catch (Exception ex)
            {
                return new VerifyResult(false, [], stdout.ToString(), stderr.ToString() + "\nHARNESS: bad verification.json: " + ex.Message, proc.ExitCode);
            }
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, [], stdout.ToString(), stderr.ToString() + "\nHARNESS: " + ex.Message, -1);
        }
    }
}
