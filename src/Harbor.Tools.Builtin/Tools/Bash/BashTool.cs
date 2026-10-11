using System.Diagnostics;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
namespace Harbor.Tools.Builtin;
/// <summary>
///     Executes shell commands. Captures stdout/stderr/exit code.
/// </summary>
public sealed class BashTool : ITool
{
    private readonly ILogger<BashTool> _logger;

    public BashTool(ILogger<BashTool> logger) { _logger = logger; }

    public ToolName Name => ToolName.Create("bash");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Command();

    public string DisplayName => "Bash";

    /// <summary>Glyph beside the tool name in every UI surface (#680).</summary>
    public string Glyph => "$";

    public string Description => "Execute a shell command. Output is captured and returned. Commands run in the current working directory. Use `cwd` to override.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "bash: Execute shell commands";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Prefer dedicated tools (read, edit, glob, grep) for file operations",
        "Use `bash` for compilation, testing, git, and other shell tasks",
        "Specify `timeout` for long-running commands (default: 30s)"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "command": { "type": "string", "description": "Shell command to execute" },
                                                                          "cwd": { "type": "string", "description": "Working directory (default: current)" },
                                                                          "timeout": { "type": "integer", "description": "Timeout in seconds (default: 30, max: 600)" },
                                                                          "env": { "type": "object", "description": "Additional environment variables" }
                                                                        },
                                                                        "required": ["command"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args) =>
        JsonArgValidator.RequiredNonBlankString(
            args, "command", "Missing required argument 'command'.", "'command' cannot be empty.");

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string command = args.GetProperty("command").GetString()!;
        string? cwd = JsonArgs.GetString(args, "cwd");
        int timeout = JsonArgs.GetInt(args, "timeout") ?? 30;
        var env = args.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().ToDictionary(
                p => p.Name,
                // GetString() would throw for non-string JSON kinds; fall back
                // to the raw token ("123", "true") instead of crashing.
                p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText())
            : null;

        if (timeout is < 1 or > 600) timeout = 30;

        // S2 (#376): an explicit `cwd` wins; otherwise the context working
        // directory (the isolated worktree for sub-agent runs), else the
        // process directory. A missing effective directory fails closed.
        string baseDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory!
            : Environment.CurrentDirectory;
        if (cwd is not null && !Directory.Exists(cwd))
            return ToolResult.Error($"Working directory does not exist: '{cwd}'.");
        if (cwd is null && !Directory.Exists(baseDir))
            return ToolResult.Error($"Working directory does not exist: '{baseDir}'.");

        var psi = new ProcessStartInfo
        {
            FileName = GetShell(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? baseDir
        };

        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        if (env is not null)
        {
            foreach ((string k, string v) in env)
                psi.Environment[k] = v;
        }

        // §PERF-006 (RESOLVED): stdout/stderr are accumulated in two StringBuilders
        // rented from StringBuilderPool (no per-call allocation), and each is capped
        // at MaxOutputChars so a runaway `find /` or `cat huge.log` can't OOM the
        // process. Once the cap is hit, further lines are silently dropped (the
        // dropped-bytes counter is kept for diagnostic logging) — partial output is
        // strictly better than crashing the agent. Append('\n') is used instead of
        // AppendLine() to keep the separator platform-independent (the rendered
        // transcript already normalises line endings).
        using var process = new Process { StartInfo = psi };
        const int MaxOutputChars = 100_000;
        using var stdout = StringBuilderPool.Rent(4096);
        using var stderr = StringBuilderPool.Rent(1024);
        long stdoutDropped = 0;
        long stderrDropped = 0;
        // End-of-stream sentinels: the async output callbacks can still fire
        // AFTER WaitForExitAsync returns (and after a kill). Every code path
        // below drains both sentinels (bounded 2s, non-fatal on timeout) and
        // detaches the handlers in the finally before the pooled builders are
        // read or returned, so no late callback can write into a returned slot.
        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        BashToolLog.Executing(_logger, command, timeout);

        process.OutputDataReceived += OnStdout;
        process.ErrorDataReceived += OnStderr;

        try
        {
            if (!process.Start())
                return ToolResult.Error("Failed to start process.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start process: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Bounded drain: wait for the reader callbacks to signal end-of-stream
        // so the builders are quiescent before they are read or returned.
        // #53 audit: a drain timeout no longer propagates — throwing out of
        // here would dispose the pooled builders while callbacks may still be
        // in flight (use-after-return). Handlers are detached in the finally
        // below regardless of outcome.
        async Task DrainAsync()
        {
            try
            {
                await Task.WhenAll(stdoutEof.Task, stderrEof.Task)
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                BashToolLog.OutputDrainTimeout(_logger, ex);
            }
        }

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            if (!process.HasExited)
            {
                // ROP-A Z1 п.14: kill semantics live in one shared helper.
                ToolErrors.KillQuietly(process);
                bool timedOut = !cancellationToken.IsCancellationRequested;
                await DrainAsync().ConfigureAwait(false);
                if (timedOut)
                {
                    BashToolLog.CommandTimedOut(_logger, ex, timeout);
                    return ToolResult.Error(
                        $"Command timed out after {timeout}s and was killed.\nStdout so far:\n{stdout}\nStderr:\n{stderr}");
                }

                BashToolLog.CommandCancelled(_logger, ex);
                return ToolResult.Error(
                    $"Command was cancelled.\nStdout so far:\n{stdout}\nStderr:\n{stderr}");
            }
        }
        finally
        {
            // Detach BEFORE the pooled builders are read or returned, so no
            // late process callback can append into a recycled builder.
            process.OutputDataReceived -= OnStdout;
            process.ErrorDataReceived -= OnStderr;
            try { process.CancelOutputRead(); }
            catch (InvalidOperationException) { /* never started reading */
            }
            try { process.CancelErrorRead(); }
            catch (InvalidOperationException) { /* never started reading */
            }
        }

        // §PERF-006 (tail): the composed transcript was the last per-call
        // `new StringBuilder()` in this method — rented now. Safe: built
        // synchronously after the drain+detach in the finally above, so no
        // process callback can touch it; read into the ToolResult string
        // before the `using` returns it.
        using var output = StringBuilderPool.Rent(8192);
        if (stdout.Builder.Length > 0) output.Builder.Append(stdout.Builder).Append('\n');
        if (stderr.Builder.Length > 0) output.Builder.Append("[stderr]\n").Append(stderr.Builder).Append('\n');
        output.Builder.Append("[exit code: ").Append(process.ExitCode).Append(']').Append('\n');
        if (stdoutDropped > 0 || stderrDropped > 0)
        {
            // Surface truncation to the model, not just to the logs — the
            // agent must be able to tell that output was incomplete.
            output.Builder.Append("[output truncated: ").Append(stdoutDropped + stderrDropped)
                .Append(" chars dropped (cap=").Append(MaxOutputChars).Append(")]\n");
            BashToolLog.OutputTruncated(_logger, stdoutDropped, stderrDropped, MaxOutputChars);
        }

        BashToolLog.CommandCompleted(_logger, process.ExitCode);

        if (output.Builder.Length > 50_000)
        {
            const string HardCutNote = "\n[output truncated to 50000 chars]\n";
            output.Builder.Length = 50_000 - HardCutNote.Length;
            output.Builder.Append(HardCutNote);
        }

        bool isError = process.ExitCode != 0;
        var result = isError
            ? ToolResult.Error(output.ToString(), new { exitCode = process.ExitCode })
            : ToolResult.Success(output.ToString(), new { exitCode = process.ExitCode });
        return result;

        // #53 audit: named locals (not inline lambdas) so they can be
        // detached in the finally above. The async output callbacks can still
        // fire AFTER WaitForExitAsync returns, after a kill, or after the 2s
        // drain gives up — without detach, a late callback would append into
        // a pooled builder already returned to StringBuilderPool
        // (use-after-return corrupting another renter's buffer).
        void OnStdout(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                stdoutEof.TrySetResult();
                return;
            }
            if (stdout.Builder.Length >= MaxOutputChars)
            {
                stdoutDropped += e.Data.Length + 1;
                return;
            }
            stdout.Builder.Append(e.Data).Append('\n');
        }

        void OnStderr(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                stderrEof.TrySetResult();
                return;
            }
            if (stderr.Builder.Length >= MaxOutputChars)
            {
                stderrDropped += e.Data.Length + 1;
                return;
            }
            stderr.Builder.Append(e.Data).Append('\n');
        }
    }

    private static string GetShell() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="BashTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class BashToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Executing: {Command} (timeout: {Timeout}s)")]
    public static partial void Executing(ILogger logger, string command, int timeout);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Timed out waiting for process output drains; continuing with output so far")]
    public static partial void OutputDrainTimeout(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Command timed out after {Timeout}s")]
    public static partial void CommandTimedOut(ILogger logger, Exception ex, int timeout);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Command cancelled before completion")]
    public static partial void CommandCancelled(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Bash output truncated: stdout dropped {StdoutDropped} chars, stderr dropped {StderrDropped} chars (cap={Cap})")]
    public static partial void OutputTruncated(ILogger logger, long stdoutDropped, long stderrDropped, int cap);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Command completed: exit={ExitCode}")]
    public static partial void CommandCompleted(ILogger logger, int exitCode);
}
