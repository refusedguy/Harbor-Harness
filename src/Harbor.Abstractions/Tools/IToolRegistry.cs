using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Tools;

/// <summary>
/// Registry of tools. Implements Registry pattern.
/// </summary>
/// <remarks>
/// <para>
/// The tool registry is the canonical lookup table for <see cref="ITool"/> instances.
/// Builtin tools are registered at startup; plugins add additional tools via
/// <see cref="IToolRegistryBuilder"/> during initialization.
/// </para>
/// <para>
/// Implementations MUST be thread-safe. The default <c>ToolRegistry</c> in <c>Harbor.Core</c>
/// uses a <c>NonBlocking.ConcurrentDictionary</c> for lock-free scaling and an optional
/// <see cref="FrozenDictionary{TKey, TValue}"/> snapshot for O(1) reads after
/// <see cref="ToolRegistry.Freeze"/> is called.
/// </para>
/// </remarks>
public interface IToolRegistry
{
    /// <summary>
    /// Get all registered tools.
    /// </summary>
    /// <returns>A read-only list of <see cref="ToolDescriptor"/> snapshots.</returns>
    IReadOnlyList<ToolDescriptor> GetAllTools();

    /// <summary>
    /// Get tools available for a given agent (filtered by permissions).
    /// </summary>
    /// <param name="agentName">The agent requesting tools.</param>
    /// <param name="sessionPermission">Optional session-level ruleset override.</param>
    /// <returns>The descriptors for every tool the agent is allowed to call.</returns>
    IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, Permissions.PermissionRuleset? sessionPermission = null);

    /// <summary>
    /// Get a specific tool by name.
    /// </summary>
    /// <param name="name">The tool name to look up.</param>
    /// <returns>Success with the tool, or failure if not registered.</returns>
    Result<ITool> GetTool(ToolName name);

    /// <summary>
    /// Register a tool. Fails if a tool with the same name is already registered.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Result Register(ITool tool);

    /// <summary>
    /// Try to unregister a tool.
    /// </summary>
    /// <param name="name">The tool name to remove.</param>
    /// <returns>Success, or failure if the tool is not registered.</returns>
    Result Unregister(ToolName name);
}

/// <summary>
/// Builder for tool registration (used by plugins).
/// </summary>
/// <remarks>
/// Plugins receive an <see cref="IToolRegistryBuilder"/> during initialization and call one
/// of the <see cref="AddTool"/> overloads for each tool they wish to contribute.
/// </remarks>
public interface IToolRegistryBuilder
{
    /// <summary>
    /// Register a fully-constructed tool instance.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    void AddTool(ITool tool);

    /// <summary>
    /// Register a tool by type. The tool is constructed via <c>new T()</c>.
    /// </summary>
    /// <typeparam name="T">The tool type. Must have a public parameterless constructor.</typeparam>
    void AddTool<T>() where T : ITool, new();

    /// <summary>
    /// Register a tool via a factory function.
    /// </summary>
    /// <param name="factory">Factory producing the tool instance.</param>
    void AddTool(Func<ITool> factory);
}

/// <summary>
/// Descriptor for a tool (immutable snapshot for serialization/LLM).
/// </summary>
/// <param name="Name">The tool's stable name.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Schema">JSON Schema describing input arguments.</param>
/// <param name="ExecutionMode">Whether the tool can run in parallel.</param>
/// <param name="PromptSnippet">Optional one-line snippet for the system prompt.</param>
/// <param name="PromptGuidelines">Optional longer-form guidelines.</param>
public sealed record ToolDescriptor(
    ToolName Name,
    string DisplayName,
    string Description,
    JsonDocument Schema,
    ExecutionMode ExecutionMode,
    string? PromptSnippet,
    IReadOnlyList<string> PromptGuidelines);
