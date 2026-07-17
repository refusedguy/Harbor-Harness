using System.Runtime.InteropServices;
using System.Text;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Sessions;
/// <summary>
///     Default system prompt builder. Implements Builder pattern (GOF).
///     Assembles: identity + tool policy + constraints + env + agent + tools + MCP + skills + context files.
///     Uses a pooled <see cref="StringBuilder" /> to avoid per-call allocation.
/// </summary>
public sealed class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly ILogger<SystemPromptBuilder> _logger;

    public SystemPromptBuilder() : this(NullLogger<SystemPromptBuilder>.Instance) { }

    public SystemPromptBuilder(ILogger<SystemPromptBuilder> logger)
    {
        _logger = logger;
    }

    private const string DefaultBasePrompt = """
                                             You are Harbor, a coding agent. Solve tasks with tools. Be precise and minimal.
                                             Rules: read before edit; small targeted diffs; verify after change; prefer dedicated tools over bash.
                                             """;

    private const string ToolUsePolicy = """
                                         ## Tool Use
                                         - Call tools only via the provided function-calling interface. Args must match each tool's schema.
                                         - Never invent tool names, arguments, paths, or results.
                                         - Parallelize independent reads (read/glob/grep). Keep writes and bash sequential.
                                         - After edits: re-read or run a focused check before claiming success.
                                         - Prefer read/glob/grep/edit over bash. Use bash for build, test, git, package managers.
                                         """;

    private const string Constraints = """
                                       ## Constraints
                                       - Stay inside the working directory unless the user asks otherwise.
                                       - Do not exfiltrate secrets (.env, keys, tokens) into chat or tool arguments.
                                       - No destructive ops without explicit user intent (rm -rf, git push --force, drop db, format).
                                       """;

    /// <inheritdoc />
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        using var sb = StringBuilderPool.Rent(4096);
        var builder = sb.Builder;

        // 1. Identity
        builder.AppendLine(DefaultBasePrompt);
        builder.AppendLine();

        // 2. Tool policy + constraints (high priority — before long context)
        builder.AppendLine(ToolUsePolicy);
        builder.AppendLine(Constraints);
        builder.AppendLine();

        // 3. Environment (compact)
        builder.AppendLine("## Environment");
        builder.Append("- Working directory: `").Append(context.WorkingDirectory).AppendLine("`");
        builder.Append("- Platform: ").Append(GetOsShort()).AppendLine();
        builder.Append("- Today: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")).AppendLine();
        builder.Append("- Model: ").Append(context.Model.ProviderId).Append('/').Append(context.Model.Id).AppendLine();
        builder.AppendLine();

        // 4. Agent-specific instructions
        if (!string.IsNullOrEmpty(context.Agent.SystemPromptAppend))
        {
            builder.AppendLine("## Additional Instructions");
            builder.AppendLine(context.Agent.SystemPromptAppend);
            builder.AppendLine();
        }

        // 5. Tools (descriptors already permission-filtered; schema lives in the API tool defs)
        if (context.Tools.Count == 0)
        {
            builder.AppendLine("## Tools");
            builder.AppendLine("No tools available this turn. Answer from knowledge only.");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("## Tools");
            foreach (var tool in context.Tools.OrderBy(t => t.Name.Value, StringComparer.Ordinal))
            {
                builder.Append("- `").Append(tool.Name.Value).Append("`: ")
                    .AppendLine(tool.PromptSnippet ?? tool.Description);

                if (tool.PromptGuidelines.Count > 0)
                {
                    int n = 0;
                    foreach (string g in tool.PromptGuidelines)
                    {
                        if (n >= 3) break;
                        if (string.IsNullOrWhiteSpace(g) || g.Length > 160) continue;
                        builder.Append("  - ").AppendLine(g);
                        n++;
                    }
                }
            }
            builder.AppendLine();
        }

        // 6. MCP
        if (!string.IsNullOrEmpty(context.McpInstructions))
        {
            builder.AppendLine("## MCP Servers");
            builder.AppendLine(context.McpInstructions);
            builder.AppendLine();
        }

        // 7. Skills (flat — cheaper than nested XML)
        if (context.Skills.Count > 0)
        {
            builder.AppendLine("## Skills");
            builder.AppendLine("Specialized instruction packs. Use `read` on the skill path when the task matches.");
            foreach (var skill in context.Skills.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                builder.Append("- **").Append(skill.Name).Append("** — ")
                    .Append(skill.Description)
                    .Append(" (`").Append(skill.FilePath).AppendLine("`)");
            }
            builder.AppendLine();
        }

        // 8. Project context (last — long, lower priority for attention)
        if (context.ContextFiles.Count > 0)
        {
            builder.AppendLine("## Project Context");
            builder.AppendLine();
            builder.AppendLine("<project_context>");
            foreach (var file in context.ContextFiles)
            {
                builder.Append("<file path=\"").Append(file.Path).AppendLine("\">");
                builder.AppendLine(file.Content);
                builder.AppendLine("</file>");
            }
            builder.AppendLine("</project_context>");
            builder.AppendLine();
        }

        _logger.LogDebug(
            "System prompt built: {Length} chars, {ToolCount} tools, {SkillCount} skills, {FileCount} files",
            builder.Length,
            context.Tools.Count,
            context.Skills.Count,
            context.ContextFiles.Count);

        return Task.FromResult(builder.ToString());
    }

    private static string GetOsShort()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return RuntimeInformation.OSDescription;
    }
}
