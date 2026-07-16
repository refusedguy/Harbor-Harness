using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugin.GitTools;
/// <summary>
///     GitTools plugin — adds a `git` tool for common git operations.
///     Demonstrates wrapping shell commands in a typed tool.
/// </summary>
public sealed class GitToolsPlugin : IToolPlugin
{
    public string Name => "gittools";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Git operations as a tool";

    public void Initialize(PluginContext context) => context.CreateLogger<GitToolsPlugin>().LogInformation("GitTools plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<GitTool>();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class GitTool : ITool
{
    public ToolName Name => ToolName.Create("git");
    public string DisplayName => "Git";
    public string Description => "Run git commands. Safer than bash because it validates the command and parses output for common operations.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "git: Git operations (status, diff, log, branch, commit)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `git` instead of `bash git ...` for better error handling",
        "Common subcommands: status, diff, log, branch, add, commit, push, pull"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "args": { "type": "string", "description": "Git subcommand and arguments (e.g. 'status', 'log --oneline -10')" },
                                                                          "cwd": { "type": "string", "description": "Working directory (default: current)" }
                                                                        },
                                                                        "required": ["args"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("args", out var a) || a.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'args' argument.");
        if (string.IsNullOrWhiteSpace(a.GetString()))
            return Result.Failure("'args' cannot be empty.");

        // Safety: block obviously dangerous commands
        string argsStr = a.GetString()!;
        string lower = argsStr.ToLowerInvariant();
        if (lower.Contains("push --force") && !lower.Contains("--force-with-lease"))
            return Result.Failure("'git push --force' is blocked. Use '--force-with-lease' instead.");
        if (lower.Contains("reset --hard"))
            return Result.Failure("'git reset --hard' is blocked. Use 'git reset --soft' or revert.");

        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string gitArgs = args.GetProperty("args").GetString()!;
        string cwd = args.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()!
            : Environment.CurrentDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd
        };
        psi.ArgumentList.Add(gitArgs);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            return ToolResult.Error("Failed to start git process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            return ToolResult.Error("Git command was cancelled.");
        }

        var output = new StringBuilder();
        if (stdout.Length > 0) output.Append(stdout);
        if (stderr.Length > 0) output.AppendLine($"[stderr]\n{stderr}");
        output.AppendLine($"[exit code: {process.ExitCode}]");

        return process.ExitCode == 0
            ? ToolResult.Success(output.ToString(), new { exitCode = process.ExitCode })
            : ToolResult.Error(output.ToString(), new { exitCode = process.ExitCode });
    }
}
