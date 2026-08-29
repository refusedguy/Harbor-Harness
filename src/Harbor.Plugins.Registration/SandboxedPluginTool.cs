using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Registration;

/// <summary>
///     Sandbox guard wrapped around every tool a plugin contributes to the registry.
///     Defence in depth beyond the trust gate: a trusted-but-buggy (or malicious) plugin
///     tool cannot hang the agent loop or balloon the heap.
/// </summary>
/// <remarks>
///     <para>
///         Enforcement:
///         <list type="bullet">
///             <item><b>Timeout</b> — a linked <see cref="CancellationTokenSource" /> fires
///             after <see cref="DefaultTimeout" /> (30s). The plugin's
///             <see cref="ITool.ExecuteAsync" /> races a <c>WaitAsync</c>; when the timer
///             wins, the abandoned execution is left to die on its own (a synchronous
///             loop cannot be thread-aborted in .NET) and the agent loop receives an
///             error result immediately.</item>
///             <item><b>Memory guard</b> — a process-wide allocated-bytes delta is sampled
///             around the call; over the budget, the result is converted to an error and
///             a <c>memory</c> block event is published.</item>
///             <item><b>Audit + events</b> — every block publishes
///             <see cref="PluginBlockedEvent" /> and appends an audit line, so the agent
///             loop sees <see cref="ToolResult.IsError" /> and the operator sees why.</item>
///         </list>
///     </para>
/// </remarks>
public sealed class SandboxedPluginTool : ITool
{
    /// <summary>Default wall-clock budget per plugin tool execution.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default per-call allocation budget (10 MB).</summary>
    public const long DefaultMemoryBudgetBytes = 10 * 1024 * 1024;

    private readonly ITool _inner;
    private readonly string _pluginName;
    private readonly IEventBus _eventBus;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;
    private readonly long _memoryBudgetBytes;
    private readonly IPluginAuditLog? _audit;
    private readonly IReadOnlySet<PluginCapability> _capabilities;

    /// <summary>
    ///     Wrap a plugin-contributed tool with the execution sandbox.
    /// </summary>
    /// <param name="inner">The plugin's tool.</param>
    /// <param name="pluginName">Stable plugin id (for events/audit).</param>
    /// <param name="eventBus">Host event bus — <see cref="PluginBlockedEvent" /> target.</param>
    /// <param name="logger">Diagnostics logger.</param>
    /// <param name="timeout">Execution budget; defaults to 30s.</param>
    /// <param name="memoryBudgetBytes">Allocation budget per call; defaults to 10 MB.</param>
    /// <param name="capabilities">
    ///     Capabilities granted to the owning plugin (audited per call). Empty set when
    ///     unknown — nothing is audited as granted, blocks still are.
    /// </param>
    /// <param name="audit">
    ///     Optional audit sink: each call appends one entry per exercised capability
    ///     (allow) and one per block (deny).
    /// </param>
    public SandboxedPluginTool(
        ITool inner,
        string pluginName,
        IEventBus eventBus,
        ILogger logger,
        TimeSpan? timeout = null,
        long memoryBudgetBytes = DefaultMemoryBudgetBytes,
        IReadOnlySet<PluginCapability>? capabilities = null,
        IPluginAuditLog? audit = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pluginName = pluginName ?? throw new ArgumentNullException(nameof(pluginName));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout ?? DefaultTimeout;
        _memoryBudgetBytes = memoryBudgetBytes;
        _capabilities = capabilities ?? new HashSet<PluginCapability>();
        _audit = audit;
    }

    /// <inheritdoc />
    public ToolName Name => _inner.Name;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public string Description => _inner.Description;

    /// <inheritdoc />
    public JsonDocument ParameterSchema => _inner.ParameterSchema;

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => _inner.ExecutionMode;

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
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        ToolResult result;
        try
        {
            result = await _inner
                .ExecuteAsync(args: args, context, cts.Token)
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await BlockAsync(
                "timeout",
                $"Plugin tool '{_inner.Name.Value}' exceeded its {_timeout.TotalSeconds:0}s execution budget.",
                args,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Sandbox budget fired (the agent-loop token is still live).
            cts.Cancel(); // belt-and-braces: make sure the abandoned execution also observes the token
            return await BlockAsync(
                "timeout",
                $"Plugin tool '{_inner.Name}' was cancelled by the {_timeout.TotalSeconds:0}s sandbox budget.",
                args,
                cancellationToken).ConfigureAwait(false);
        }
        // An OperationCanceledException from the agent-loop token is NOT caught here:
        // the filter above fails and it propagates untouched to the caller.

        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        long delta = allocatedAfter - allocatedBefore;
        if (delta > _memoryBudgetBytes)
        {
            return await BlockAsync(
                "memory",
                $"Plugin tool '{_inner.Name}' exceeded its allocation budget: {delta / 1024.0 / 1024.0:F1} MB > {_memoryBudgetBytes / 1024.0 / 1024.0:0} MB.",
                args,
                cancellationToken).ConfigureAwait(false);
        }

        if (result.IsError)
            return result;

        await AuditCallAsync(args, "allow", detail: null, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    ///     Publish <see cref="PluginBlockedEvent" /> and convert to an error
    ///     <see cref="ToolResult" /> so the agent loop treats it as a failed tool call.
    /// </summary>
    private async Task<ToolResult> BlockAsync(string reason, string detail, JsonElement args, CancellationToken ct)
    {
        _logger.LogWarning(
            "Plugin sandbox blocked {Plugin} tool {Tool}: {Reason} — {Detail}",
            _pluginName,
            _inner.Name,
            reason,
            detail);

        await AuditCallAsync(args, "deny", detail: $"{reason}: {detail}", ct).ConfigureAwait(false);

        await _eventBus.PublishAsync(
            new PluginBlockedEvent(_pluginName, reason, detail),
            ct).ConfigureAwait(false);

        return ToolResult.Error($"[sandbox:{reason}] {detail}");
    }

    /// <summary>
    ///     Append one audit line per granted capability for this tool call. The target
    ///     is extracted from the call arguments when a well-known key matches the
    ///     capability (url/path/command); otherwise the tool name is used.
    /// </summary>
    private async Task AuditCallAsync(JsonElement args, string result, string? detail, CancellationToken ct)
    {
        if (_audit is null || _capabilities.Count == 0)
            return;

        string rawArgs = args.ValueKind is JsonValueKind.String
            ? args.GetString() ?? string.Empty
            : args.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? args.GetRawText()
                : string.Empty;

        foreach (var capability in _capabilities)
        {
            await _audit.WriteAsync(
                _pluginName,
                capability,
                ExtractTarget(capability, args, rawArgs),
                result,
                detail,
                ct).ConfigureAwait(false);
        }
    }

    private static string ExtractTarget(PluginCapability capability, JsonElement args, string rawArgs)
    {
        string? Pick(params string[] keys)
        {
            if (args.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in keys)
                {
                    if (args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (!string.IsNullOrEmpty(s))
                            return s;
                    }
                }
            }
            return null;
        }

        return capability switch
        {
            PluginCapability.HttpRequests => Pick("url", "endpoint", "query", "search", "q") is { } url
                ? url
                : Truncate(rawArgs),
            PluginCapability.ReadFiles or PluginCapability.WriteFiles => Pick("path", "file", "filename") is { } path
                ? path
                : Truncate(rawArgs),
            PluginCapability.RunProcesses => Pick("command", "process", "exe") is { } cmd
                ? cmd
                : Truncate(rawArgs),
            _ => Truncate(rawArgs),
        };
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200];
}