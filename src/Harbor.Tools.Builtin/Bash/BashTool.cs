using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

/// <summary>
/// Executes shell commands. Captures stdout/stderr/exit code.
/// </summary>
public sealed class BashTool : ITool
{
    public ToolName Name => ToolName.Create("bash");
    public string DisplayName => "Bash";
    public string Description => "Execute a shell command. Output is captured and returned. Commands run in the current working directory. Use `cwd` to override.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "bash: Execute shell commands";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Prefer dedicated tools (read, edit, glob, grep) for file operations",
        "Use `bash` for compilation, testing, git, and other shell tasks",
        "Specify `timeout` for long-running commands (default: 30s)",
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
        var command = args.GetProperty("command").GetString()!;
        var cwd = args.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var timeout = args.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 30;
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
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
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
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            return ToolResult.Error("Failed to start process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return ToolResult.Error(
                    $"Command timed out after {timeout}s and was killed.\nStdout so far:\n{stdout}\nStderr:\n{stderr}");
            }
        }

        var output = new StringBuilder();
        if (stdout.Length > 0) output.AppendLine(stdout.ToString());
        if (stderr.Length > 0) output.AppendLine($"[stderr]\n{stderr}");
        output.AppendLine($"[exit code: {process.ExitCode}]");

        if (output.Length > 50_000)
            output.Length = 50_000;

        var isError = process.ExitCode != 0;
        var result = isError
            ? ToolResult.Error(output.ToString(), new { exitCode = process.ExitCode })
            : ToolResult.Success(output.ToString(), new { exitCode = process.ExitCode });
        return result;
    }

    private static string GetShell() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
}
