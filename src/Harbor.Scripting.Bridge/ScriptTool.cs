// Bridge layer — ScriptTool: an ITool whose execute() body is a script function.
//
// Layering rule (see ScriptGlobals.cs):
//   This file depends ONLY on Harbor.Abstractions types. It accepts an
//   engine-supplied delegate to invoke the script function — the Bridge layer
//   never references an IScriptEngine directly. That keeps the layer pure and
//   lets any engine (SharpTS subprocess, Jint in-process, future engines)
//   supply tools without circular dependencies.
namespace Harbor.Scripting.Bridge;
/// <summary>
///     <see cref="ITool" /> implementation backed by a script function
///     registered via <c>Harbor.registerTool</c>.
/// </summary>
/// <remarks>
///     <para>
///         The script <c>execute</c> function is captured at registration time
///         and re-evaluated on each invocation (per-call engine instances are
///         required for thread safety in the Jint path; the SharpTS subprocess
///         path is naturally isolated). The engine that registered the tool
///         supplies an execute delegate that knows how to invoke the script
///         function — ScriptTool itself stays engine-agnostic.
///     </para>
///     <para>
///         <b>Async limitation:</b> the PoC supports synchronous
///         <c>execute</c> functions only. If the function returns a Promise,
///         the result is treated as an opaque object — Promise microtask
///         draining is on the roadmap.
///     </para>
/// </remarks>
public sealed class ScriptTool : ITool
{
    private readonly Func<JsonElement, CancellationToken, Task<ToolResult>> _execute;

    /// <summary>
    ///     Construct a script-backed tool.
    /// </summary>
    /// <param name="name">Lowercase tool name (will be wrapped in <see cref="ToolName" />).</param>
    /// <param name="displayName">Human-readable name shown in <c>/tools</c>.</param>
    /// <param name="description">One-line description shown to the model.</param>
    /// <param name="schema">JSON Schema describing the tool's input arguments.</param>
    /// <param name="executionMode">Whether the tool can run in parallel with others.</param>
    /// <param name="execute">
    ///     Delegate that invokes the script's <c>execute</c> function with the
    ///     supplied args and returns a <see cref="ToolResult" />. Supplied by
    ///     the engine at registration time.
    /// </param>
    public ScriptTool(
        string name,
        string displayName,
        string description,
        JsonDocument schema,
        ExecutionMode executionMode,
        Func<JsonElement, CancellationToken, Task<ToolResult>> execute)
    {
        Name = ToolName.Create(name);
        DisplayName = displayName;
        Description = description;
        ParameterSchema = schema;
        ExecutionMode = executionMode;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <inheritdoc />
    public ToolName Name { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; }

    /// <inheritdoc />
    public ExecutionMode ExecutionMode { get; }

    /// <inheritdoc />
    public string? PromptSnippet => $"[script] {DisplayName}";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // Link the caller-supplied token with the context abort token so either
        // cancels the script. The engine's own timeout (in ScriptEngineOptions)
        // is the hard backstop.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Abort);
        return _execute(args, linkedCts.Token);
    }

    /// <summary>
    ///     Convert a script-returned <see cref="JsonElement" /> into a
    ///     <see cref="ToolResult" />. Shared between engine implementations.
    /// </summary>
    /// <remarks>
    ///     Convention: <c>execute()</c> returns
    ///     <c>{ output: string, isError?: boolean }</c>. Non-object returns
    ///     are coerced to a string and treated as success output.
    /// </remarks>
    public static ToolResult ConvertToToolResult(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            string output = el.TryGetProperty("output", out var outEl) && outEl.ValueKind == JsonValueKind.String
                ? outEl.GetString() ?? string.Empty
                : el.GetRawText();
            bool isError = el.TryGetProperty("isError", out var errEl) && errEl.ValueKind == JsonValueKind.True;
            return new ToolResult(output, isError);
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            return ToolResult.Success(el.GetString() ?? string.Empty);
        }

        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
        {
            return ToolResult.Success(string.Empty);
        }

        return ToolResult.Success(el.GetRawText());
    }
}
