using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
namespace Harbor.Abstractions.Tools;
/// <summary>
///     Strategy interface for tools (Strategy pattern, GOF).
///     Each tool (read, write, bash, etc.) implements this.
/// </summary>
/// <remarks>
///     <para>
///         Tools are the agent's hands: every action the model takes beyond emitting text goes
///         through a tool implementation. Each <see cref="ITool" /> exposes a JSON Schema for its
///         arguments, an <see cref="ExecutionMode" /> (parallel vs. sequential), and optional
///         prompt-snippet/guideline text that gets injected into the system prompt.
///     </para>
///     <para>
///         Implementations MUST be thread-safe for concurrent <see cref="ExecuteAsync" /> calls.
///     </para>
/// </remarks>
public interface ITool
{
    /// <summary>
    ///     The tool's stable, lowercase name.
    /// </summary>
    public ToolName Name { get; }

    /// <summary>
    ///     Human-readable name shown in <c>/tools</c>.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    ///     One-line description shown to the model in the tool definition.
    /// </summary>
    public string Description { get; }

    /// <summary>
    ///     JSON Schema describing the tool's input arguments.
    /// </summary>
    public JsonDocument ParameterSchema { get; }

    /// <summary>
    ///     Whether this tool can run in parallel with other tool calls in the same turn.
    /// </summary>
    public ExecutionMode ExecutionMode { get; }

    /// <summary>
    ///     Optional one-line snippet injected into the system prompt's "Available Tools" list.
    /// </summary>
    public string? PromptSnippet { get; }

    /// <summary>
    ///     Optional longer-form guidelines injected under the tool's entry.
    /// </summary>
    public IReadOnlyList<string> PromptGuidelines { get; }

    /// <summary>
    ///     Execute the tool with the given arguments.
    /// </summary>
    /// <param name="args">The raw JSON arguments validated against <see cref="ParameterSchema" />.</param>
    /// <param name="context">The execution context (session, services, helpers).</param>
    /// <param name="cancellationToken">Cancellation token used to abort the tool mid-execution.</param>
    /// <returns>The tool's result (success or error, with optional attachments/metadata).</returns>
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validate arguments before execution (optional). The default implementation accepts any input.
    /// </summary>
    /// <param name="args">The raw JSON arguments.</param>
    /// <returns>Success if arguments are valid, or failure with an error message.</returns>
    public Result ValidateArguments(JsonElement args) => Result.Success();
}

/// <summary>
///     Execution mode for a tool.
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    ///     Can run in parallel with other tool calls.
    /// </summary>
    Parallel,

    /// <summary>
    ///     Must run sequentially (e.g. <c>bash</c> with side effects).
    /// </summary>
    Sequential
}

/// <summary>
///     Context passed to tool execution. Provides access to session, services, and helpers.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="MessageId">The assistant message id that emitted this tool call.</param>
/// <param name="CallId">The unique tool-call id.</param>
/// <param name="Agent">The agent name running this tool.</param>
/// <param name="Abort">Cancellation token used to abort the tool mid-execution.</param>
/// <param name="Messages">A snapshot of the current conversation messages.</param>
/// <param name="ReportProgress">Callback to report progress updates.</param>
/// <param name="Ask">Callback to ask the user for a permission decision.</param>
/// <param name="Services">The DI service provider for resolving tool-specific services.</param>
public sealed record ToolContext(
    string SessionId,
    string MessageId,
    string? CallId,
    string Agent,
    CancellationToken Abort,
    IReadOnlyList<AgentMessage> Messages,
    Func<ToolProgressUpdate, CancellationToken, Task> ReportProgress,
    Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> Ask,
    IServiceProvider Services);

/// <summary>
///     Progress update from a tool execution.
/// </summary>
/// <param name="Status">Optional status message (e.g. <c>"Downloading..."</c>).</param>
/// <param name="PercentComplete">Optional 0–100 progress percentage.</param>
/// <param name="PartialResult">Optional partial result preview.</param>
public sealed record ToolProgressUpdate(
    string? Status = null,
    int? PercentComplete = null,
    object? PartialResult = null);
