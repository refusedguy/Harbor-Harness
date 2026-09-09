using System.Diagnostics;
using System.Text;

namespace Harbor.Evals;

/// <summary>Drives one Harbor CLI run: single prompt, stdout capture, timeout kill.</summary>
internal static class ProcessDriver
{
    public static string[] PassthroughNames() =>
        Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(k => k.EndsWith("_API_KEY", StringComparison.Ordinal))
            .OrderBy(k => k).ToArray();

    public sealed record DriveResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        string Outcome, // completed | timed_out | crashed | harness_error
        DateTimeOffset StartedUtc,
        DateTimeOffset EndedUtc);

    public static async Task<DriveResult> RunAsync(
        EvalProfile profile,
        string prompt,
        string workdir,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        string cliDll = Environment.ExpandEnvironmentVariables(profile.Harbor.CliDll);
        if (!Path.IsPathRooted(cliDll))
            cliDll = Path.GetFullPath(Path.Combine(profile.RepoRoot, cliDll));

        var psi = new ProcessStartInfo(profile.Harbor.Dotnet)
        {
            WorkingDirectory = workdir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        // Prompt travels as an argv element (harbor ask <prompt>), never stdin.
        psi.ArgumentList.Add(cliDll);
        psi.ArgumentList.Add("ask");
        psi.ArgumentList.Add(prompt);
        foreach (var (k, v) in profile.Harbor.Env)
            psi.Environment[k] = Environment.ExpandEnvironmentVariables(v);
        // API keys flow from the runner's own env (CI secrets) into the child.
        // Never from files, never into artifacts (see ArtifactWriter env_names).
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v && k.EndsWith("_API_KEY", StringComparison.Ordinal))
                psi.Environment[k] = v;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            proc.Exited += (_, _) => exited.TrySetResult(true);
            if (!proc.Start())
                return new DriveResult(-1, string.Empty, "start failed", "harness_error", started, DateTimeOffset.UtcNow);

            proc.StandardInput.Close();

            var stdoutTask = ReadAllAsync(proc.StandardOutput, stdout, ct);
            var stderrTask = ReadAllAsync(proc.StandardError, stderr, ct);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var done = await Task.WhenAny(exited.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(false);
            if (!ReferenceEquals(done, exited.Task))
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
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new DriveResult(-1, stdout.ToString(), stderr.ToString(), "timed_out", started, DateTimeOffset.UtcNow);
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var ended = DateTimeOffset.UtcNow;
            string outcome = proc.ExitCode == 0 && stdout.ToString().Contains("[agent_end]", StringComparison.Ordinal)
                ? "completed"
                : "crashed";
            return new DriveResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), outcome, started, ended);
        }
        catch (Exception ex)
        {
            return new DriveResult(-1, stdout.ToString(), stderr.ToString() + "\nHARNESS: " + ex.Message, "harness_error", started, DateTimeOffset.UtcNow);
        }
    }

    private static async Task ReadAllAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        char[] buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            sink.Append(buf, 0, n);
    }
}
