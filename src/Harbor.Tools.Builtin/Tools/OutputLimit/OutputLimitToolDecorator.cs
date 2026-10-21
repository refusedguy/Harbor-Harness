using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The truncating <see cref="ITool" /> decorator (#1587): one cap + the
///     unified marker over <see cref="ITool.ExecuteAsync" />, applied at the
///     composition root rather than re-implemented per tool. The wrapped tool
///     executes untouched — arguments, permission handling and the result's
///     metadata pass verbatim; only an <see cref="ToolResult.Output" /> longer
///     than the cap is head-kept and closed with the unified note.
/// </summary>
/// <remarks>
///     <para>
///         A third sibling of the established decorators:
///         <c>TelemetryToolDecorator</c> (Harbor.Telemetry.Core) instruments,
///         <c>SandboxedPluginTool</c> (Harbor.Plugins.Runtime) sandboxes — this
///         one bounds output. Same forwarding shape: every contract member is
///         the inner tool's, so descriptors, the #557 safety-policy derivation
///         and registry name resolution see nothing new.
///     </para>
///     <para>
///         Thread-safety mirrors the inner tool's (interface contract applies to
///         concurrent <see cref="ExecuteAsync" />): no mutable state here, so
///         the decorator cannot lower the inner tool's guarantee.
///     </para>
/// </remarks>
public sealed class OutputLimitToolDecorator : ITool
{
    private readonly ITool _inner;
    private readonly ILogger _logger;
    private readonly int _cap;

    /// <summary>
    ///     Wrap <paramref name="inner" /> with the unified output cap.
    /// </summary>
    public OutputLimitToolDecorator(ITool inner, ILogger logger, int cap = ToolOutputLimit.DecoratorDefaultCap)
    {
        _inner = inner;
        _logger = logger;
        _cap = cap;
    }

    /// <inheritdoc />
    public ToolName Name => _inner.Name;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public string Glyph => _inner.Glyph;

    /// <inheritdoc />
    public string Description => _inner.Description;

    /// <inheritdoc />
    public JsonDocument ParameterSchema => _inner.ParameterSchema;

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => _inner.ExecutionMode;

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => _inner.SafetyProfile;

    /// <inheritdoc />
    public string? PromptSnippet => _inner.PromptSnippet;

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines => _inner.PromptGuidelines;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args) => _inner.ValidateArguments(args);

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        ToolResult result = await _inner.ExecuteAsync(args, context, cancellationToken).ConfigureAwait(false);
        string capped = ToolOutputLimit.Apply(
            result.Output,
            _cap,
            hint: $"{result.Output.Length - _cap} more chars dropped");
        if (ReferenceEquals(capped, result.Output))
            return result; // within budget: verbatim pass-through, zero allocation

        OutputLimitDecoratorLog.OutputLimited(_logger, _inner.Name.Value, result.Output.Length, _cap);
        // #1763: the cap rewrites the OUTPUT, not the rest of the result. A
        // deferred context the tool handed over rides along untouched — the
        // decorator is not allowed to swallow the tool's second exit.
        return new ToolResult(capped, result.IsError, result.Metadata, result.Attachments)
        {
            DeferredContexts = result.DeferredContexts
        };
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for the output-limit decorator.
/// </summary>
internal static partial class OutputLimitDecoratorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Output limited: {Tool} produced {Length} chars, cap={Cap}")]
    public static partial void OutputLimited(ILogger logger, string tool, int length, int cap);
}
