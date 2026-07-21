using System.Diagnostics;
using System.Text;
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
    public string DisplayName => "Bash";
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

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'command'.");
        if (string.IsNullOrWhiteSpace(cmdEl.GetString()))
            return Result.Failure("'command' cannot be empty.");
        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string command = args.GetProperty("command").GetString()!;
        string? cwd = args.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        int timeout = args.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 30;
        var env = args.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : null;

        if (timeout is < 1 or > 600) timeout = 30;

        var psi = new ProcessStartInfo
        {
            FileName = GetShell(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        _logger.LogDebug("Executing: {Command} (timeout: {Timeout}s)", command, timeout);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stdout.Builder.Length >= MaxOutputChars)
            {
                stdoutDropped += e.Data.Length + 1;
                return;
            }
            stdout.Builder.Append(e.Data).Append('\n');
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stderr.Builder.Length >= MaxOutputChars)
            {
                stderrDropped += e.Data.Length + 1;
                return;
            }
            stderr.Builder.Append(e.Data).Append('\n');
        };

        if (!process.Start())
            return ToolResult.Error("Failed to start process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch
                { /* ignore */
                }
                _logger.LogWarning(ex, "Command timed out after {Timeout}s", timeout);
                return ToolResult.Error(
                    $"Command timed out after {timeout}s and was killed.\nStdout so far:\n{stdout}\nStderr:\n{stderr}");
            }
        }

        var output = new StringBuilder();
        if (stdout.Builder.Length > 0) output.Append(stdout.Builder).Append('\n');
        if (stderr.Builder.Length > 0) output.Append("[stderr]\n").Append(stderr.Builder).Append('\n');
        output.Append("[exit code: ").Append(process.ExitCode).Append(']').Append('\n');

        if (stdoutDropped > 0 || stderrDropped > 0)
        {
            _logger.LogWarning("Bash output truncated: stdout dropped {StdoutDropped} chars, stderr dropped {StderrDropped} chars (cap={Cap})",
                stdoutDropped, stderrDropped, MaxOutputChars);
        }

        _logger.LogInformation("Command completed: exit={ExitCode}", process.ExitCode);

        if (output.Length > 50_000)
            output.Length = 50_000;

        bool isError = process.ExitCode != 0;
        var result = isError
            ? ToolResult.Error(output.ToString(), new { exitCode = process.ExitCode })
            : ToolResult.Success(output.ToString(), new { exitCode = process.ExitCode });
        return result;
    }

    private static string GetShell() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
}
