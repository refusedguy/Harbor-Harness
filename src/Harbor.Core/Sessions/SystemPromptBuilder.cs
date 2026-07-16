using System.Text;
using Harbor.Abstractions.Extensions;
namespace Harbor.Core.Sessions;
/// <summary>
///     Default system prompt builder. Implements Builder pattern (GOF).
///     Assembles prompt from: base template + env + tools + skills + MCP + context files.
///     Uses a pooled <see cref="StringBuilder" /> to avoid per-call allocation.
/// </summary>
public sealed class SystemPromptBuilder : ISystemPromptBuilder
{
    private const string DefaultBasePrompt = """
                                             You are Harbor, an AI coding assistant. You help users with coding tasks by using available tools.

                                             Available tools are listed below. Use them as needed to accomplish tasks.
                                             Always be precise, efficient, and respectful of the user's time.

                                             Guidelines:
                                             - Read files before editing to understand existing structure
                                             - Make minimal, targeted edits
                                             - Verify changes after editing
                                             - Use bash sparingly; prefer dedicated tools when available
                                             - Show file paths clearly when working with files

                                             """;

    /// <summary>
    ///     Build the system prompt for the supplied context.
    /// </summary>
    /// <param name="context">The prompt context (agent, model, tools, files, skills, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assembled system prompt string.</returns>
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
    {
        // Pre-size generously (system prompts typically 1–4 KB).
        using var sb = StringBuilderPool.Rent(4096);
        var builder = sb.Builder;

        // 1. Base prompt
        builder.AppendLine(DefaultBasePrompt);

        // 2. Environment
        builder.AppendLine("## Environment");
        builder.AppendLine($"- Working directory: `{context.WorkingDirectory}`");
        builder.AppendLine($"- Platform: {Environment.OSVersion}");
        builder.AppendLine($"- Today: {DateTimeOffset.Now:yyyy-MM-dd}");
        builder.AppendLine($"- Model: {context.Model.ProviderId}/{context.Model.Id}");
        builder.AppendLine();

        // 3. Agent-specific instructions
        if (!string.IsNullOrEmpty(context.Agent.SystemPromptAppend))
        {
            builder.AppendLine("## Additional Instructions");
            builder.AppendLine(context.Agent.SystemPromptAppend);
            builder.AppendLine();
        }

        // 4. Available tools
        builder.AppendLine("## Available Tools");
        foreach (var tool in context.Tools)
        {
            builder.AppendLine($"- `{tool.Name.Value}`: {tool.PromptSnippet ?? tool.Description}");
            if (tool.PromptGuidelines.Count > 0)
            {
                foreach (string g in tool.PromptGuidelines)
                {
                    builder.AppendLine($"  - {g}");
                }
            }
        }
        builder.AppendLine();

        // 5. MCP instructions
        if (!string.IsNullOrEmpty(context.McpInstructions))
        {
            builder.AppendLine("## MCP Servers");
            builder.AppendLine(context.McpInstructions);
            builder.AppendLine();
        }

        // 6. Skills
        if (context.Skills.Count > 0)
        {
            builder.AppendLine("## Available Skills");
            builder.AppendLine("The following skills provide specialized instructions:");
            builder.AppendLine();
            builder.AppendLine("<available_skills>");
            foreach (var skill in context.Skills)
            {
                builder.AppendLine("  <skill>");
                builder.AppendLine($"    <name>{skill.Name}</name>");
                builder.AppendLine($"    <description>{skill.Description}</description>");
                builder.AppendLine($"    <location>{skill.FilePath}</location>");
                builder.AppendLine("  </skill>");
            }
            builder.AppendLine("</available_skills>");
            builder.AppendLine();
            builder.AppendLine("Use the `read` tool to load a skill file when the task matches its description.");
            builder.AppendLine();
        }

        // 7. Context files (AGENTS.md, CLAUDE.md, etc.)
        if (context.ContextFiles.Count > 0)
        {
            builder.AppendLine("## Project Context");
            builder.AppendLine();
            builder.AppendLine("<project_context>");
            foreach (var file in context.ContextFiles)
            {
                builder.AppendLine($"<file path=\"{file.Path}\">");
                builder.AppendLine(file.Content);
                builder.AppendLine("</file>");
            }
            builder.AppendLine("</project_context>");
            builder.AppendLine();
        }

        return Task.FromResult(builder.ToString());
    }
}
