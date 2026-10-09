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
    ///     Glyph shown next to the tool's name in every UI surface (#680).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Declared HERE, beside <see cref="DisplayName" />, because the glyph
    ///         used to live in hand-written tables inside the UI layer — one per
    ///         rendering path, two of them disagreeing, and all three keyed the
    ///         web-fetch tool as <c>"web_fetch"</c> while the tool is
    ///         <c>"webfetch"</c>. Adding a tool meant editing those tables.
    ///     </para>
    ///     <para>
    ///         It travels with the call: <see cref="Abstractions.Contracts.Events.ToolExecutionStartEvent" />
    ///         carries it into the UI state, and every renderer reads it from there.
    ///         No renderer owns a tool-name-keyed glyph map any more, and
    ///         <c>ToolGlyphTableRule</c> fails the build if one reappears.
    ///     </para>
    ///     <para>
    ///         The default is deliberately CONSERVATIVE: a tool that declares none
    ///         renders <see cref="ToolGlyphs.Default" />, which is visibly unspecific
    ///         rather than silently wrong — the direction docs/PATTERNS.md requires a
    ///         default interface member to fail in.
    ///     </para>
    /// </remarks>
    public string Glyph => ToolGlyphs.Default;

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
    ///     How the permission system must treat this tool's arguments (#557).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Required, with no default.</b> The path-traversal guard that stops
    ///         <c>new("write", "src/*", Allow)</c> from authorising
    ///         <c>src/../../../etc/passwd</c> used to be gated on membership of a
    ///         hand-maintained name list inside <c>PathGuardSafetyPolicy</c>. A new
    ///         path-taking write tool that failed to join the list was not merely
    ///         unlisted — the guard reported <c>AppliesTo == false</c>, the
    ///         suppression never ran, and the rule authorised the traversal. Nothing
    ///         warned and nothing failed.
    ///     </para>
    ///     <para>
    ///         The list is gone: the guard set is assembled by
    ///         <c>ToolSafetyPolicies.Build</c> from what tools actually registered.
    ///         Declaring here is what feeds it, and because the member has no default
    ///         implementation the compiler refuses a tool that has not declared — the
    ///         one enforcement point that cannot drift.
    ///     </para>
    /// </remarks>
    public ToolSafetyProfile SafetyProfile { get; }

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
/// <param name="WorkingDirectory">
///     Working directory tools resolve relative paths and process cwd against
///     (epic #42, stage S2: the isolated worktree). Null falls back to the
///     process directory — trailing optional, existing call sites unchanged.
/// </param>
/// <remarks>
///     #470 — this record deliberately carries <b>no</b> <c>IServiceProvider</c>.
///     It used to declare one that both production call sites
///     (<c>ToolDispatcher</c> / <c>McpStdioServer</c>) passed as <c>null!</c>, so
///     the signature promised a container that never existed and every consumer
///     of it was one guard away from a <see cref="NullReferenceException" />.
///     Tool dependencies are constructor-injected by the composition root
///     (<c>ToolsCatalog.CreateToolRegistry</c>) — an absent dependency is now a
///     visible <c>null</c> on the tool, not a trap in the context.
/// </remarks>
public sealed record ToolContext(
    string SessionId,
    string MessageId,
    string? CallId,
    string Agent,
    CancellationToken Abort,
    IReadOnlyList<AgentMessage> Messages,
    Func<ToolProgressUpdate, CancellationToken, Task> ReportProgress,
    Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> Ask,
    string? WorkingDirectory = null);

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
