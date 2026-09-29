// Session-broadcast CS-source plugin for Harbor (issue #352).
//
// Drop this file into ~/.harbor/plugins/ (or <project>/.harbor/plugins/) and Harbor will
// compile it at startup via Roslyn and register the `session_broadcast` + `session_inbox`
// tools. No .csproj, no DLL — just C# source. See docs/PLUGIN_DEVELOPMENT.md
// for the full reference and samples/plugins-cs/HelloWorldPlugin.cs for the minimal shape.
//
// Model (follow-up to #165 peer supervision): sessions are PEERS that run in parallel.
// Delivery is quiet — a broadcast lands in each sibling's inbox queue and is picked up
// when the neighbor drains it with `session_inbox`. A live run is never interrupted.
//
// The hub is in-process and dependency-free on purpose: the production ToolDispatcher
// passes Services=null into ToolContext, and IToolRegistryBuilder has no
// service-provider overload, so a CS plugin cannot resolve ISessionStore — the same
// constraint the TodoWrite sample works around with static per-session state.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Sample.SessionBroadcast;

/// <summary>
///     Peer-broadcast plugin: one message to all sibling sessions (issue #352).
/// </summary>
public sealed class SessionBroadcastPlugin : IToolPlugin
{
    /// <inheritdoc />
    public string Name => "session-broadcast";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version RequiredHarborVersion => new(0, 4, 0);

    /// <inheritdoc />
    public string Description => "Peer broadcast: one message to all sibling sessions via a quiet inbox queue.";

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        context.CreateLogger<SessionBroadcastPlugin>().LogInformation("SessionBroadcast plugin initialized");
    }

    /// <inheritdoc />
    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<SessionBroadcastTool>();
        builder.AddTool<SessionInboxTool>();
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
///     One queued broadcast: who sent it, the text, and when. Sessions that drain the
///     same loaded plugin assembly share one hub; reloading the plugin starts empty.
/// </summary>
/// <param name="FromSessionId">Id of the session that broadcast.</param>
/// <param name="Text">The broadcast text.</param>
/// <param name="SentAt">UTC timestamp of the broadcast.</param>
public sealed record BroadcastEnvelope(string FromSessionId, string Text, DateTimeOffset SentAt);

/// <summary>
///     Fan-out report for one <c>session_broadcast</c> call.
/// </summary>
/// <param name="DeliveredTo">Sibling session ids that received the envelope, sorted.</param>
/// <param name="SkippedByFilter">Known siblings excluded by <c>filter</c>.</param>
/// <param name="DroppedOverflow">Oldest envelopes evicted to respect the per-session cap.</param>
public sealed record BroadcastReport(IReadOnlyList<string> DeliveredTo, int SkippedByFilter, int DroppedOverflow);

/// <summary>
///     In-process neighbor queue for peer broadcasts. Thread-safe for concurrent tool calls.
///     A session becomes a known sibling the first time it calls <c>session_broadcast</c> or
///     <c>session_inbox</c> — sessions that never touch either tool cannot receive.
/// </summary>
internal static class SessionBroadcastHub
{
    /// <summary>Maximum broadcast text length (bounds the prompt-injection surface).</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Maximum <c>filter</c> length.</summary>
    public const int MaxFilterLength = 128;

    /// <summary>Broadcasts allowed per sender session per window.</summary>
    public const int MaxBroadcastsPerWindow = 5;

    /// <summary>Rate-limit window in seconds, per sender session.</summary>
    public const int WindowSeconds = 60;

    /// <summary>Queued envelopes per session; oldest are evicted past the cap.</summary>
    public const int MaxQueuedPerSession = 100;

    /// <summary>Machine-readable broadcast-provenance trailer: <c>[broadcast-from:&lt;sessionId&gt;]</c>.</summary>
    public const string BroadcastMarkerPrefix = "[broadcast-from:";

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<BroadcastEnvelope>> Inboxes =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> SendWindows =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, long> SentCount =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, long> ReceivedCount =
        new(StringComparer.Ordinal);

    /// <summary>Register a session as a known sibling (idempotent).</summary>
    public static void Touch(string sessionId)
    {
        Inboxes.GetOrAdd(sessionId, _ => new ConcurrentQueue<BroadcastEnvelope>());
        SendWindows.GetOrAdd(sessionId, _ => new Queue<DateTimeOffset>());
    }

    /// <summary>How many sessions are currently known (including the caller).</summary>
    public static int KnownCount => Inboxes.Count;

    /// <summary>Broadcasts sent by a session (rate-limited attempts included).</summary>
    public static long SentBy(string sessionId) =>
        SentCount.TryGetValue(sessionId, out long n) ? n : 0;

    /// <summary>Envelopes ever queued for a session.</summary>
    public static long ReceivedBy(string sessionId) =>
        ReceivedCount.TryGetValue(sessionId, out long n) ? n : 0;

    /// <summary>
    ///     Sliding-window rate check for one sender. Records the attempt on success;
    ///     rejected attempts do not consume budget.
    /// </summary>
    /// <returns>True when the broadcast may proceed; otherwise false with <paramref name="error" /> set.</returns>
    public static bool TryCheckRateLimit(string senderId, out string? error)
    {
        Queue<DateTimeOffset> window = SendWindows.GetOrAdd(senderId, _ => new Queue<DateTimeOffset>());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cutoff = now.AddSeconds(-WindowSeconds);
        lock (window)
        {
            while (window.Count > 0 && window.Peek() <= cutoff)
                window.Dequeue();
            if (window.Count >= MaxBroadcastsPerWindow)
            {
                error = $"Rate limit: max {MaxBroadcastsPerWindow} broadcasts per {WindowSeconds}s per session. Try again later.";
                return false;
            }

            window.Enqueue(now);
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Fan out one envelope to every known sibling except the sender, honoring
    ///     <paramref name="filter" /> (case-insensitive session-id substring).
    /// </summary>
    public static BroadcastReport Broadcast(string senderId, string text, string? filter)
    {
        Touch(senderId);
        var envelope = new BroadcastEnvelope(senderId, text, DateTimeOffset.UtcNow);
        var delivered = new List<string>();
        int skipped = 0;
        int dropped = 0;
        foreach (KeyValuePair<string, ConcurrentQueue<BroadcastEnvelope>> pair in Inboxes)
        {
            if (string.Equals(pair.Key, senderId, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrEmpty(filter) && pair.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                skipped++;
                continue;
            }

            ConcurrentQueue<BroadcastEnvelope> queue = pair.Value;
            while (queue.Count >= MaxQueuedPerSession && queue.TryDequeue(out _))
                dropped++;
            queue.Enqueue(envelope);
            delivered.Add(pair.Key);
            ReceivedCount.AddOrUpdate(pair.Key, 1, (_, n) => n + 1);
        }

        delivered.Sort(StringComparer.Ordinal);
        SentCount.AddOrUpdate(senderId, 1, (_, n) => n + 1);
        return new BroadcastReport(delivered, skipped, dropped);
    }

    /// <summary>Drain and return every envelope queued for a session, oldest first.</summary>
    public static IReadOnlyList<BroadcastEnvelope> Drain(string sessionId)
    {
        Touch(sessionId);
        ConcurrentQueue<BroadcastEnvelope> queue = Inboxes[sessionId];
        var result = new List<BroadcastEnvelope>();
        while (queue.TryDequeue(out BroadcastEnvelope? envelope) && envelope is not null)
            result.Add(envelope);
        return result;
    }

    /// <summary>Re-queue envelopes at the back, preserving their relative order (no counter bump).</summary>
    public static void Requeue(string sessionId, IReadOnlyList<BroadcastEnvelope> envelopes)
    {
        Touch(sessionId);
        ConcurrentQueue<BroadcastEnvelope> queue = Inboxes[sessionId];
        for (int i = 0; i < envelopes.Count; i++)
            queue.Enqueue(envelopes[i]);
    }

    /// <summary>Render one envelope as a human line plus the machine-readable provenance trailer.</summary>
    public static string FormatEnvelope(BroadcastEnvelope envelope) =>
        $"[broadcast from {envelope.FromSessionId} @ {envelope.SentAt:u}]: {envelope.Text} {BroadcastMarkerPrefix}{envelope.FromSessionId}]";
}

/// <summary>
///     Send side of peer broadcast (issue #352): one message to all sibling sessions.
///     Quiet delivery — envelopes wait in each neighbor's inbox queue for <c>session_inbox</c>;
///     a live run is never interrupted. Approved by default (Allow rule), rate-limited per session.
/// </summary>
public sealed class SessionBroadcastTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "Message for all sibling sessions (max 4000 chars)" },
            "filter": { "type": "string", "description": "Optional case-insensitive session-id substring; only matching siblings receive" }
          },
          "required": ["text"]
        }
        """);

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("session_broadcast");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <inheritdoc />
    public string DisplayName => "Session Broadcast";

    /// <inheritdoc />
    public string Description =>
        "Send one message to all sibling sessions. Quiet delivery into each neighbor's inbox queue " +
        "(they read it with session_inbox on their next turn) — a live run is never interrupted. " +
        "Rate-limited per session.";

    /// <inheritdoc />
    public JsonDocument ParameterSchema => Schema;

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "session_broadcast: send one message to all sibling sessions (quiet inbox queue)";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "keep broadcasts short and actionable: context, a result, or a redirect",
        "siblings read it via session_inbox on their next turn — broadcast never blocks or interrupts",
        "use filter to target one session by id substring; omit it to reach every sibling",
    ];

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'text'.");
        string text = textProp.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure("'text' cannot be empty.");
        if (text.Length > SessionBroadcastHub.MaxTextLength)
            return Result.Failure($"'text' is too long ({text.Length} chars, max {SessionBroadcastHub.MaxTextLength}).");

        if (args.TryGetProperty("filter", out var filterProp))
        {
            if (filterProp.ValueKind != JsonValueKind.String)
                return Result.Failure("Optional argument 'filter' must be a string.");
            string filter = filterProp.GetString() ?? string.Empty;
            if (filter.Length > SessionBroadcastHub.MaxFilterLength)
                return Result.Failure($"'filter' is too long ({filter.Length} chars, max {SessionBroadcastHub.MaxFilterLength}).");
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string text = args.GetProperty("text").GetString() ?? string.Empty;
        string? filter = null;
        if (args.TryGetProperty("filter", out var filterProp)
            && filterProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(filterProp.GetString()))
        {
            filter = filterProp.GetString();
        }

        if (!SessionBroadcastHub.TryCheckRateLimit(context.SessionId, out string? limited))
            return Task.FromResult(ToolResult.Error(limited ?? "Rate limit exceeded."));

        BroadcastReport report = SessionBroadcastHub.Broadcast(context.SessionId, text, filter);
        int knownOthers = Math.Max(0, SessionBroadcastHub.KnownCount - 1);

        if (report.DeliveredTo.Count == 0)
        {
            string reason = report.SkippedByFilter > 0
                ? $"filter '{filter}' matched no known siblings (known: {knownOthers})."
                : "no other sessions have used broadcast/inbox yet, so no siblings are known.";
            return Task.FromResult(ToolResult.Success(
                $"[broadcast queued] {reason} Nothing delivered.",
                new { delivered = 0, skippedByFilter = report.SkippedByFilter }));
        }

        var sb = new StringBuilder();
        sb.Append("[broadcast delivered to ").Append(report.DeliveredTo.Count).Append(" sibling(s): ");
        for (int i = 0; i < report.DeliveredTo.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(report.DeliveredTo[i]);
        }

        sb.Append(']');
        if (report.SkippedByFilter > 0)
            sb.Append(" (skipped ").Append(report.SkippedByFilter).Append(" by filter)");
        if (report.DroppedOverflow > 0)
            sb.Append(" (dropped ").Append(report.DroppedOverflow).Append(" oldest overflow)");
        sb.Append(" They read it with session_inbox on their next turn.");

        return Task.FromResult(ToolResult.Success(
            sb.ToString(),
            new
            {
                delivered = report.DeliveredTo.Count,
                skippedByFilter = report.SkippedByFilter,
                droppedOverflow = report.DroppedOverflow,
            }));
    }
}

/// <summary>
///     Receive side of peer broadcast (issue #352): drain your session's inbox queue.
///     Quiet by design — the agent decides when to check; nothing pushes into a live turn.
/// </summary>
public sealed class SessionInboxTool : ITool
{
    /// <summary>Default envelopes rendered per drain (the rest stay queued, oldest first).</summary>
    public const int DefaultLimit = 20;

    /// <summary>Hard cap for the optional <c>limit</c> argument.</summary>
    public const int MaxLimit = 50;

    private static readonly JsonDocument Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": 50, "description": "Max envelopes to show (default 20); the rest stay queued" }
          },
          "required": []
        }
        """);

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("session_inbox");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <inheritdoc />
    public string DisplayName => "Session Inbox";

    /// <inheritdoc />
    public string Description =>
        "Drain your session's broadcast inbox: messages peer sessions sent with session_broadcast. " +
        "Check at turn start; nothing interrupts a live run.";

    /// <inheritdoc />
    public JsonDocument ParameterSchema => Schema;

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "session_inbox: read messages broadcast by sibling sessions";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "check the inbox at turn start when collaborating with peer sessions",
        "each entry carries a [broadcast-from:<sessionId>] trailer with its author",
    ];

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (args.TryGetProperty("limit", out var limitProp))
        {
            if (limitProp.ValueKind != JsonValueKind.Number || !limitProp.TryGetInt32(out int limit))
                return Result.Failure("Optional argument 'limit' must be an integer.");
            if (limit < 1 || limit > MaxLimit)
                return Result.Failure($"Optional argument 'limit' must be between 1 and {MaxLimit}.");
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int limit = DefaultLimit;
        if (args.TryGetProperty("limit", out var limitProp)
            && limitProp.ValueKind == JsonValueKind.Number
            && limitProp.TryGetInt32(out int parsed)
            && parsed >= 1 && parsed <= MaxLimit)
        {
            limit = parsed;
        }

        IReadOnlyList<BroadcastEnvelope> all = SessionBroadcastHub.Drain(context.SessionId);
        if (all.Count == 0)
            return Task.FromResult(ToolResult.Success("Inbox empty — no broadcasts waiting."));

        int shown = Math.Min(limit, all.Count);
        var sb = new StringBuilder();
        sb.Append("Inbox (").Append(all.Count).Append(" new):").AppendLine();
        for (int i = 0; i < shown; i++)
        {
            sb.Append("  ").Append(SessionBroadcastHub.FormatEnvelope(all[i])).AppendLine();
        }

        if (all.Count > shown)
        {
            var rest = new List<BroadcastEnvelope>(all.Count - shown);
            for (int i = shown; i < all.Count; i++)
                rest.Add(all[i]);
            SessionBroadcastHub.Requeue(context.SessionId, rest);
            sb.Append("  …+").Append(rest.Count).Append(" kept queued (re-run with a higher limit).").AppendLine();
        }

        sb.Append("— end of inbox (you sent ").Append(SessionBroadcastHub.SentBy(context.SessionId))
            .Append(", received ").Append(SessionBroadcastHub.ReceivedBy(context.SessionId)).Append(')');

        return Task.FromResult(ToolResult.Success(
            sb.ToString(),
            new { count = all.Count, shown }));
    }
}
