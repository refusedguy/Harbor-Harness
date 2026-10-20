using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Git;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugin.GitTools;
/// <summary>
///     GitTools plugin — adds a `git` tool for common git operations plus a
///     `git_status` tool for structured branch/PR facts and a `git-status`
///     TUI panel. Showcase slice-2 (#1434): branch/PR status panel over the
///     existing axes only (tools + <c>ITuiPanelPlugin</c> — no new axis, so
///     nothing new to declare in <c>PluginAxisRegistry</c>).
/// </summary>
public sealed class GitToolsPlugin : IToolPlugin, ITuiPanelPlugin
{
    public string Name => "gittools";
    public Version Version => new(1, 1, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Git operations as a tool (with branch/PR status + panel)";

    public void Initialize(PluginContext context) => context.CreateLogger<GitToolsPlugin>().LogInformation("GitTools plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<GitTool>();
        builder.AddTool<GitStatusTool>();
    }

    public void RegisterPanels(IPanelRegistry registry) => registry.Register(new GitStatusPanel());

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class GitTool : ITool
{
    public ToolName Name => ToolName.Create("git");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Command("args");

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

/// <summary>
///     Last-known PR summary written by the <c>pr</c> action of
///     <see cref="GitStatusTool" /> and read by <see cref="GitStatusPanel" />.
///     The panel never forks <c>gh</c> per frame; PR state refreshes only when
///     the agent calls the tool, which is the same no-per-frame-spawn
///     discipline the jump palette keeps for <c>git worktree list</c> (#666).
/// </summary>
internal static class GitPrCache
{
    private static readonly object Gate = new();
    private static string _summary = "PR: unknown (run git_status pr to refresh)";

    public static string Summary
    {
        get { lock (Gate) return _summary; }
    }

    public static void Update(string summary)
    {
        lock (Gate)
        {
            _summary = string.IsNullOrWhiteSpace(summary) ? _summary : summary;
        }
    }
}

/// <summary>
///     Structured, read-only git facts: <c>status</c> (branch + dirty count +
///     last commit), <c>branch</c> (name only), <c>pr</c> (best-effort
///     <c>gh pr status</c>, also refreshes the panel cache). Complements the
///     free-form <c>git</c> tool with parsed output the model can rely on.
/// </summary>
public sealed class GitStatusTool : ITool
{
    public ToolName Name => ToolName.Create("git_status");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => "Git Status";
    public string Description => "Read-only git facts: branch, working-tree status, last commit, and PR status (via gh, best-effort).";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "git_status: branch/PR status facts (read-only)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `git_status` instead of `bash git ...` when you only need branch, dirtiness, or PR state",
        "Actions: status, branch, pr. `pr` needs the `gh` CLI and degrades to a plain message without it"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "action": {
                                                                            "type": "string",
                                                                            "enum": ["status", "branch", "pr"],
                                                                            "description": "Which fact to report"
                                                                          },
                                                                          "cwd": { "type": "string", "description": "Working directory (default: session worktree)" }
                                                                        },
                                                                        "required": ["action"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'action' argument.");
        string? action = actionEl.GetString();
        if (action is not ("status" or "branch" or "pr"))
            return Result.Failure($"Unknown action: '{action}'.");
        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string action = args.GetProperty("action").GetString()!;
        string cwd = args.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(c.GetString())
            ? c.GetString()!
            : context.WorkingDirectory ?? Environment.CurrentDirectory;

        switch (action)
        {
            case "branch":
                string? branch = await RunGit(cwd, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
                if (branch is null)
                    return ToolResult.Error($"Not a git repository: {cwd}");
                return ToolResult.Success($"Branch: {branch.Trim()}", new { branch = branch.Trim() });

            case "status":
                string? sb = await RunGit(cwd, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
                if (sb is null)
                    return ToolResult.Error($"Not a git repository: {cwd}");
                string branchName = sb.Trim();
                string? porcelain = await RunGit(cwd, cancellationToken, "status", "--porcelain").ConfigureAwait(false) ?? "";
                int dirty = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                string? last = await RunGit(cwd, cancellationToken, "log", "-1", "--format=%h %cr %s").ConfigureAwait(false);
                var text = new StringBuilder();
                text.AppendLine($"Branch: {branchName}");
                text.AppendLine(dirty == 0 ? "Clean working tree." : $"Dirty: {dirty} file(s).");
                if (!string.IsNullOrWhiteSpace(last))
                    text.AppendLine($"Last commit: {last.Trim()}");
                return ToolResult.Success(text.ToString(), new { branch = branchName, dirtyFileCount = dirty });

            case "pr":
                string? prBranch = await RunGit(cwd, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
                if (prBranch is null)
                    return ToolResult.Error($"Not a git repository: {cwd}");
                string? prJson = await RunTool("gh", cwd, cancellationToken, "pr", "status", "--json", "number,title,state,url").ConfigureAwait(false);
                if (prJson is null)
                {
                    const string noGh = "PR: gh CLI unavailable (install gh or no PR for this branch).";
                    GitPrCache.Update($"{noGh} [branch {prBranch.Trim()}]");
                    return ToolResult.Success(noGh, new { branch = prBranch.Trim() });
                }
                string summary = SummarizePr(prJson, prBranch.Trim());
                GitPrCache.Update(summary);
                return ToolResult.Success(summary, new { branch = prBranch.Trim() });

            default:
                return ToolResult.Error($"Unknown action: {action}");
        }
    }

    private static string SummarizePr(string json, string branch)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var found = new List<string>();
            CollectPrs(doc.RootElement, found, 5);
            if (found.Count > 0)
                return $"PR for {branch}: {string.Join("; ", found)}";
            return $"PR for {branch}: none.";
        }
        catch
        {
            string flat = json.Trim();
            return $"PR for {branch}: {(flat.Length > 200 ? flat.Substring(0, 200) + "…" : flat)}";
        }
    }

    private static void CollectPrs(JsonElement el, List<string> found, int cap)
    {
        if (found.Count >= cap)
            return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectPrs(item, found, cap);
                break;
            case JsonValueKind.Object:
                if (el.TryGetProperty("number", out var n) && el.TryGetProperty("title", out var t))
                {
                    string state = el.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
                    found.Add($"#{n} {t.GetString()}{(string.IsNullOrEmpty(state) ? "" : $" [{state}]")}");
                }
                else
                {
                    foreach (var prop in el.EnumerateObject())
                        CollectPrs(prop.Value, found, cap);
                }
                break;
        }
    }

    private static async Task<string?> RunGit(string cwd, CancellationToken ct, params string[] gitArgs) =>
        await RunTool("git", cwd, ct, gitArgs).ConfigureAwait(false);

    private static async Task<string?> RunTool(string file, string cwd, CancellationToken ct, params string[] toolArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd
        };
        foreach (string a in toolArgs)
            psi.ArgumentList.Add(a);
        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            if (!process.Start())
                return null;
            process.BeginOutputReadLine();
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch
                { /* ignore */
                }
                return null;
            }
            return process.ExitCode == 0 ? stdout.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
///     Dockable <c>git-status</c> panel: branch/dirty/last-commit rows from the
///     daemon's <see cref="IGitQuery" /> port plus the last-known PR line from
///     <see cref="GitPrCache" />. Side-effect free; returns strings only
///     (CellForge flattens anything else to a type name). Never forks a process:
///     branch facts arrive through <c>PanelServices.Git</c> (daemon data over
///     IPC in the two-process layout), PR facts refresh on
///     <c>git_status pr</c> tool calls. Degrades to placeholders, never throws.
/// </summary>
public sealed class GitStatusPanel : IPanelProvider
{
    public string Id => "git-status";
    public string Title => "Git";
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;
    public int DefaultSize => 8;

    /// <summary>
    ///     Directory the panel reports on. The host may rebind it per session
    ///     (same pattern as the jump palette's <c>WorktreeDirectory</c>);
    ///     defaults to the process directory.
    /// </summary>
    public string WorktreeDirectory { get; set; } = Environment.CurrentDirectory;

    public object? Build(PanelContext ctx)
    {
        string branch = "?";
        string dirtyLine = "git: unavailable";
        string commitLine = string.Empty;
        if (ctx.Deps.Git is { } queries)
        {
            try
            {
                GitWorkspaceStatus status = queries.GetStatus(WorktreeDirectory);
                if (!string.IsNullOrEmpty(status.Branch))
                {
                    branch = status.Branch;
                    dirtyLine = status.IsDirty ? $"Dirty: {status.DirtyFileCount} file(s)." : "Clean working tree.";
                    if (!string.IsNullOrEmpty(status.LastCommitRelative))
                        commitLine = $"Last commit: {status.LastCommitRelative}";
                }
                else
                {
                    dirtyLine = "not a git repository";
                }
            }
            catch
            {
                dirtyLine = "git: unavailable";
            }
        }

        var rows = new List<string>
        {
            "Git",
            $"  branch {branch}  {dirtyLine}",
        };
        if (!string.IsNullOrEmpty(commitLine))
            rows.Add($"  {commitLine}");
        rows.Add($"  {GitPrCache.Summary}");

        int width = Math.Max(ctx.Width, 1);
        int height = Math.Max(ctx.Height, 1);
        var clipped = new List<string>(Math.Min(rows.Count, height));
        for (int i = 0; i < rows.Count && clipped.Count < height; i++)
        {
            string row = rows[i];
            clipped.Add(row.Length > width ? row.Substring(0, width) : row);
        }
        return clipped;
    }

    public bool OnKey(UiKey key, PanelContext ctx) => false;
}
