using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;

namespace Harbor.Abstractions.Sessions;

/// <summary>
/// Builds system prompt from agent definition, tools, context files, etc.
/// Implements Builder pattern (GOF).
/// </summary>
/// <remarks>
/// <para>
/// The system prompt builder is responsible for assembling the prompt that anchors every
/// LLM call in a turn. The default <c>SystemPromptBuilder</c> in <c>Harbor.Core</c> produces
/// a Markdown document with sections for environment, agent instructions, available tools,
/// MCP instructions, skills, and project context files.
/// </para>
/// <para>
/// Implementations MUST be thread-safe.
/// </para>
/// </remarks>
public interface ISystemPromptBuilder
{
    /// <summary>
    /// Build the system prompt string from the supplied context.
    /// </summary>
    /// <param name="context">The prompt context (agent, model, tools, files, skills, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assembled system prompt string.</returns>
    Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default);
}

/// <summary>
/// Context for <see cref="ISystemPromptBuilder.BuildAsync"/>.
/// </summary>
/// <param name="Agent">The agent definition driving this run.</param>
/// <param name="Model">The model being called.</param>
/// <param name="Tools">The tools available to the model this turn.</param>
/// <param name="ContextFiles">Project context files to embed (e.g. AGENTS.md, CLAUDE.md).</param>
/// <param name="Skills">Skills available for the model to load.</param>
/// <param name="McpInstructions">Optional MCP server instructions.</param>
/// <param name="WorkingDirectory">The current working directory.</param>
public sealed record SystemPromptContext(
    Agents.AgentDefinition Agent,
    ModelInfo Model,
    IReadOnlyList<ToolDescriptor> Tools,
    IReadOnlyList<ContextFile> ContextFiles,
    IReadOnlyList<SkillDescriptor> Skills,
    string? McpInstructions,
    string WorkingDirectory);

/// <summary>
/// A project context file to embed in the system prompt.
/// </summary>
/// <param name="Path">The file path (for display).</param>
/// <param name="Content">The file's contents.</param>
public sealed record ContextFile(string Path, string Content);

/// <summary>
/// A skill available for the model to load.
/// </summary>
/// <param name="Name">The skill name.</param>
/// <param name="Description">A short description of when to use the skill.</param>
/// <param name="FilePath">The path to the skill file (typically a Markdown file).</param>
public sealed record SkillDescriptor(
    string Name,
    string Description,
    string FilePath);
