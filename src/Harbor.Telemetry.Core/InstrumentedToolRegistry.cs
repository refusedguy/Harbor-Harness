using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Diagnostics;

namespace Harbor.Telemetry;

/// <summary>
///     Wraps every <see cref="ITool" /> handed out by the inner registry so ANY
///     consumer (dispatcher, plugins, future runners) is instrumented at the
///     boundary — not just one call site (sprint3-C C1).
/// </summary>
public sealed class InstrumentedToolRegistry(IToolRegistry inner, IMetrics metrics, ITracer tracer) : IToolRegistry
{
    public IReadOnlyList<ToolDescriptor> GetAllTools() => inner.GetAllTools();

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        => inner.ResolveTools(agentName, sessionPermission);

    public Result<ITool> GetTool(ToolName name)
    {
        Result<ITool> resolved = inner.GetTool(name);
        return resolved.IsSuccess
            ? Result.Success<ITool>(new TelemetryToolDecorator(resolved.Value, metrics, tracer))
            : Result.Failure<ITool>(resolved.Error);
    }

    public Result Register(ITool tool) =>
        inner.Register(tool is TelemetryToolDecorator ? tool : new TelemetryToolDecorator(tool, metrics, tracer));

    public Result Unregister(ToolName name) => inner.Unregister(name);
}

/// <summary>
///     Per-tool telemetry: span tool.execute{tool.name}, counter
///     tool.calls{tool.name,tool.status}, histogram tool.duration.ms{tool.name}.
/// </summary>
public sealed class TelemetryToolDecorator(ITool inner, IMetrics metrics, ITracer tracer) : ITool
{
    public ToolName Name => inner.Name;

    public string DisplayName => inner.DisplayName;

    public string Description => inner.Description;

    public ExecutionMode ExecutionMode => inner.ExecutionMode;

    public string? PromptSnippet => inner.PromptSnippet;

    public IReadOnlyList<string> PromptGuidelines => inner.PromptGuidelines;

    public JsonDocument ParameterSchema => inner.ParameterSchema;

    public Result ValidateArguments(JsonElement args) => inner.ValidateArguments(args);

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        using Harbor.Diagnostics.ITelemetrySpan? span = tracer.StartSpan(
            "tool.execute",
            new KeyValuePair<string, object?>(TelemetryTagNames.ToolName, inner.Name.Value));
        long start = Stopwatch.GetTimestamp();
        try
        {
            ToolResult result = await inner.ExecuteAsync(args, context, cancellationToken).ConfigureAwait(false);
            Record(span, result.IsError ? "error" : "ok", start);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(span, "cancelled", start);
            throw;
        }
        catch (Exception ex)
        {
            Record(span, "exception", start);
            span?.SetError(ex.Message);
            throw;
        }
    }

    private void Record(Harbor.Diagnostics.ITelemetrySpan? span, string status, long start)
    {
        double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        metrics.Counter(
            TelemetryTagNames.ToolCalls, 1,
            new KeyValuePair<string, object?>(TelemetryTagNames.ToolName, inner.Name.Value),
            new KeyValuePair<string, object?>(TelemetryTagNames.ToolStatus, status));
        metrics.Histogram(
            TelemetryTagNames.ToolDurationMs, elapsedMs,
            new KeyValuePair<string, object?>(TelemetryTagNames.ToolName, inner.Name.Value));
        span?.SetTag(TelemetryTagNames.ToolStatus, status);
        span?.SetTag("tool.duration.ms", Math.Round(elapsedMs, 3));
        if (status == "error")
        {
            span?.SetError(status);
        }
    }
}
