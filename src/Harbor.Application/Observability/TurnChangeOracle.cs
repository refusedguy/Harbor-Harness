using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Observability;

/// <summary>
///     The harness-side oracle of what a turn changed in the workspace
///     (issue #1745).
/// </summary>
/// <remarks>
///     <para>
///         <b>What it is.</b> A projection over the agent event stream that
///         answers one question per turn: which files did the workspace
///         actually change while that turn ran? The answer comes from the
///         filesystem, and the harness takes it by fingerprinting the target
///         of every mutating tool call twice — once before the tool runs and
///         once after it finishes — and comparing the two.
///     </para>
///     <para>
///         <b>Why it is outside the agent.</b> The agent's own words are not an
///         input to any decision here. The oracle never reads message text,
///         and a turn whose assistant message claims three edited files while
///         its tools touched none reports no change. That is the whole point:
///         «the agent says» and «the world says» are two different claims,
///         and only the second one is evidence.
///     </para>
///     <para>
///         <b>What it cannot see.</b> A tool that writes without declaring a
///         path — <c>bash</c>, <c>task</c>, an MCP server, a plugin tool — is
///         recorded in <see cref="TurnChangeReport.UnobservedTools" />, and
///         the report is marked incomplete. The oracle never claims «nothing
///         changed» about a turn whose tools it could not look through.
///     </para>
///     <para>
///         <b>Where it lives.</b> This is a headless core service, not a
///         renderer and not a prompt: it lives in the Application layer beside
///         the diagnostics aggregator, which is the same shape of thing — a
///         stateful projection over the event bus that the hosts read.
///     </para>
/// </remarks>
public sealed class TurnChangeOracle : IDisposable
{
    /// <summary>
    ///     Reports kept in memory. A host that reads the oracle once per turn
    ///     never reaches this; a host that never reads it holds at most this
    ///     many.
    /// </summary>
    public const int MaxRetainedReports = 64;

    /// <summary>
    ///     The largest file the oracle hashes. Above it the oracle compares
    ///     size and modification time instead, and says so: a file it cannot
    ///     hash is a file it cannot prove unchanged.
    /// </summary>
    public const long MaxHashedBytes = 4L * 1024 * 1024;

    /// <summary>
    ///     Tool calls remembered while their turn is open. A long session
    ///     cannot grow this map without bound.
    /// </summary>
    private const int MaxPendingCalls = 512;

    /// <summary>
    ///     Sessions remembered for their turn index. A host that switches
    ///     sessions often must not grow this without bound either.
    /// </summary>
    private const int MaxRememberedSessions = 64;

    /// <summary>
    ///     The builtin tools that write into the workspace. Each of them
    ///     declares the path it writes in its <c>path</c> argument, which is
    ///     what makes its effect attributable.
    /// </summary>
    private static readonly FrozenSet<string> WorkspaceMutators =
        new[] { "write", "edit", "patch" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The builtin tools that cannot write into the workspace. A tool on
    ///     this list contributes nothing to a turn's report; a tool on NEITHER
    ///     list is <i>unobserved</i> — the oracle cannot look through it, so
    ///     the turn is marked incomplete rather than clean.
    /// </summary>
    private static readonly FrozenSet<string> WorkspaceReadOnlyTools =
        new[]
        {
            "read", "glob", "grep", "ls", "tree", "lsp", "ripgrep",
            "notebook", "skill", "webfetch", "session_read", "session_steer",
            "mcp_prompt", "read_mcp_resource",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Path comparison. Windows paths are case-insensitive; POSIX paths are
    ///     not, and treating them as if they were would let a sibling
    ///     directory pass for a child.
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly Queue<string> _pendingOrder = new();
    private readonly Dictionary<string, int> _turnIndexBySession = new(StringComparer.Ordinal);
    private readonly List<TurnChangeReport> _reports = [];
    private readonly IDisposable[] _subscriptions;
    private readonly ILogger<TurnChangeOracle>? _logger;
    private long _sequence;
    private bool _disposed;

    /// <summary>
    ///     Create an oracle and start observing.
    /// </summary>
    /// <param name="eventBus">
    ///     The agent events. Null observes nothing — a host with no bus gets an
    ///     oracle that reports nothing, which is honest rather than broken.
    /// </param>
    /// <param name="logger">Sink for the per-turn summary line.</param>
    public TurnChangeOracle(IEventBus? eventBus, ILogger<TurnChangeOracle>? logger = null)
    {
        _logger = logger;

        if (eventBus is null)
        {
            _subscriptions = [];
            return;
        }

        _subscriptions =
        [
            eventBus.Subscribe<ToolExecutionStartEvent>(OnToolStarted),
            eventBus.Subscribe<ToolExecutionEndEvent>(OnToolEnded),
            eventBus.Subscribe<TurnStartEvent>(OnTurnStarted),
            eventBus.Subscribe<TurnEndEvent>(OnTurnEnded),
        ];
    }

    /// <summary>
    ///     The directory relative tool paths resolve against — the session's
    ///     working directory in every shipped host. A path outside it is not
    ///     read: the oracle reports it as unverified instead.
    /// </summary>
    public string Root { get; init; } = Environment.CurrentDirectory;

    /// <summary>
    ///     The reports sealed so far, oldest first.
    /// </summary>
    public IReadOnlyList<TurnChangeReport> Reports
    {
        get
        {
            lock (_sync)
            {
                return [.. _reports];
            }
        }
    }

    /// <summary>
    ///     The most recently sealed report, or null when no turn has ended
    ///     since the oracle started.
    /// </summary>
    public TurnChangeReport? LastReport
    {
        get
        {
            lock (_sync)
            {
                return _reports.Count == 0 ? null : _reports[^1];
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    // ── The event stream ───────────────────────────────────────────────────

    private ValueTask OnTurnStarted(TurnStartEvent evt, CancellationToken _)
    {
        lock (_sync)
        {
            _turnIndexBySession[SessionKey(evt.SessionId)] = evt.TurnIndex;
            if (_turnIndexBySession.Count > MaxRememberedSessions)
            {
                // A host that switches sessions faster than this would grow the
                // map without bound. Dropping it costs a turn index, not a
                // report: the report says «turn 0» instead of «turn 7».
                _turnIndexBySession.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnToolStarted(ToolExecutionStartEvent evt, CancellationToken _)
    {
        if (!WorkspaceMutators.Contains(evt.ToolName))
        {
            return ValueTask.CompletedTask;
        }

        string? target = ResolveTarget(PathArgument(evt.Args));
        if (target is null)
        {
            // No path to watch. The turn-end pass reports the call as
            // unverified rather than dropping it.
            return ValueTask.CompletedTask;
        }

        // Fingerprinted OUTSIDE the lock: this reads the file, and the lock
        // is held by every other writer of this state.
        Fingerprint before = Fingerprint(target);

        lock (_sync)
        {
            _pending[evt.ToolCallId] = new PendingCall(target, before);
            _pendingOrder.Enqueue(evt.ToolCallId);
            while (_pendingOrder.Count > MaxPendingCalls)
            {
                _pending.Remove(_pendingOrder.Dequeue());
            }
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnToolEnded(ToolExecutionEndEvent evt, CancellationToken _)
    {
        // The end event names the call but not the tool, so the target comes
        // from the start event. A call that never started has no target and
        // no verdict.
        string target;
        lock (_sync)
        {
            if (!_pending.TryGetValue(evt.ToolCallId, out PendingCall call))
            {
                return ValueTask.CompletedTask;
            }

            target = call.Target;
        }

        Fingerprint after = Fingerprint(target);

        lock (_sync)
        {
            if (_pending.TryGetValue(evt.ToolCallId, out PendingCall current))
            {
                _pending[evt.ToolCallId] = current with { After = after, HasAfter = true };
            }
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnTurnEnded(TurnEndEvent evt, CancellationToken _)
    {
        string key = SessionKey(evt.SessionId);
        TurnChangeReport report = BuildReport(evt, key);

        lock (_sync)
        {
            _reports.Add(report);
            while (_reports.Count > MaxRetainedReports)
            {
                _reports.RemoveAt(0);
            }
        }

        if (_logger is not null)
        {
            TurnChangeOracleLog.TurnChanges(_logger, report.TurnIndex, Describe(report));
        }

        return ValueTask.CompletedTask;
    }

    // ── The report ─────────────────────────────────────────────────────────

    private TurnChangeReport BuildReport(TurnEndEvent evt, string sessionKey)
    {
        int turnIndex;
        lock (_sync)
        {
            _turnIndexBySession.TryGetValue(sessionKey, out turnIndex);
        }

        List<WorkspaceChange> files = [];
        List<string> unobserved = [];
        Dictionary<string, PathWatch> watched = new(StringComparer.Ordinal);
        List<string> order = [];

        foreach (ToolCallPart call in ToolCallsOf(evt.AssistantMessage))
        {
            if (WorkspaceMutators.Contains(call.ToolName))
            {
                WatchPath(call, watched, order, files);
                continue;
            }

            if (WorkspaceReadOnlyTools.Contains(call.ToolName))
            {
                continue;
            }

            // A tool the oracle cannot look through. The turn is incomplete
            // from here on: what it changed is not in the report, and the
            // report says so.
            if (!unobserved.Contains(call.ToolName))
            {
                unobserved.Add(call.ToolName);
            }
        }

        foreach (string target in order)
        {
            PathWatch watch = watched[target];
            files.Add(new WorkspaceChange(
                RelativeToRoot(watch.Target),
                Compare(watch.Before, watch.After),
                watch.ToolName));
        }

        long sequence;
        lock (_sync)
        {
            sequence = ++_sequence;
        }

        return new TurnChangeReport(sequence, turnIndex, evt.SessionId, files, unobserved);
    }

    private void WatchPath(
        ToolCallPart call,
        Dictionary<string, PathWatch> watched,
        List<string> order,
        List<WorkspaceChange> files)
    {
        string raw = PathArgument(call.Args) ?? string.Empty;
        string? target = ResolveTarget(raw);
        if (target is null)
        {
            files.Add(new WorkspaceChange(
                raw.Length == 0 ? "(no path)" : raw,
                WorkspaceChangeKind.Unverified,
                call.ToolName));
            return;
        }

        if (watched.TryGetValue(target, out PathWatch existing))
        {
            // The net change of the turn: the FIRST call's «before» and the
            // LAST call's «after». A file written twice and reverted reads as
            // unchanged, which is what the workspace says.
            watched[target] = existing with { After = AfterOf(call.Id), ToolName = call.ToolName };
            return;
        }

        watched[target] = new PathWatch(target, BeforeOf(call.Id), AfterOf(call.Id), call.ToolName);
        order.Add(target);
    }

    private Fingerprint BeforeOf(string callId)
    {
        lock (_sync)
        {
            return _pending.TryGetValue(callId, out PendingCall call) ? call.Before : Fingerprint.Unknown;
        }
    }

    private Fingerprint AfterOf(string callId)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(callId, out PendingCall call) && call.HasAfter)
            {
                return call.After;
            }

            return Fingerprint.Unknown;
        }
    }

    // ── Fingerprints ───────────────────────────────────────────────────────

    /// <summary>
    ///     Read what the filesystem says about one path right now.
    /// </summary>
    private Fingerprint Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Fingerprint.Missing;
            }

            long length = info.Length;
            long modified = info.LastWriteTimeUtc.Ticks;
            if (length > MaxHashedBytes)
            {
                // Too large to hash: size and modification time are what the
                // oracle has, and it says so rather than pretending.
                return new Fingerprint(true, true, length, modified, null);
            }

            using FileStream stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(stream));
            return new Fingerprint(true, true, length, modified, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A path that cannot be read is a path the oracle says nothing
            // about. It is not an error the caller has to handle, but it is
            // worth a line in the log: the report will say «unverified» and
            // the log says why.
            if (_logger is not null)
            {
                TurnChangeOracleLog.FingerprintFailed(_logger, ex, path);
            }

            return Fingerprint.Unknown;
        }
    }

    /// <summary>
    ///     The verdict for one path, from the two fingerprints alone.
    /// </summary>
    private static WorkspaceChangeKind Compare(Fingerprint before, Fingerprint after)
    {
        if (!before.Readable || !after.Readable)
        {
            return WorkspaceChangeKind.Unverified;
        }

        if (!before.Exists)
        {
            return after.Exists ? WorkspaceChangeKind.Created : WorkspaceChangeKind.Unchanged;
        }

        if (!after.Exists)
        {
            return WorkspaceChangeKind.Deleted;
        }

        if (before.Hash is not null && after.Hash is not null)
        {
            return before.Hash == after.Hash ? WorkspaceChangeKind.Unchanged : WorkspaceChangeKind.Modified;
        }

        // One of the two is too large to hash: the size and the modification
        // time are all the oracle has. Equal means «cannot prove», which is
        // not the same claim as «unchanged».
        if (before.Length == after.Length && before.ModifiedTicks == after.ModifiedTicks)
        {
            return WorkspaceChangeKind.Unverified;
        }

        return WorkspaceChangeKind.Modified;
    }

    // ── Paths ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>path</c> argument of a tool call, when it has one.
    /// </summary>
    private static string? PathArgument(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!args.TryGetProperty("path", out JsonElement path))
        {
            return null;
        }

        return path.ValueKind == JsonValueKind.String ? path.GetString() : null;
    }

    /// <summary>
    ///     Resolve a declared path against the workspace root. Null when the
    ///     path is empty, unusable, or OUTSIDE the root — the oracle does not
    ///     read outside the workspace it was pointed at.
    /// </summary>
    private string? ResolveTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string combined = Path.IsPathRooted(raw) ? raw : Path.Combine(Root, raw);
        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string root;
        try
        {
            root = Path.GetFullPath(Root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, PathComparison) ? full : null;
    }

    /// <summary>
    ///     The path as the user would read it: relative to the workspace root
    ///     when it is inside it.
    /// </summary>
    private string RelativeToRoot(string path)
    {
        try
        {
            return Path.GetRelativePath(Root, path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // ── The turn's tool calls ──────────────────────────────────────────────

    /// <summary>
    ///     The tool calls the turn's assistant message carries, in order.
    ///     The turn-end event is the only place the harness learns which calls
    ///     belonged to the turn AND which session they belonged to, so the
    ///     attribution is read from here rather than guessed from the order
    ///     the tool events arrived in.
    /// </summary>
    private static List<ToolCallPart> ToolCallsOf(AssistantMessage message)
    {
        List<ToolCallPart> calls = [];
        foreach (ContentPart part in message.Parts)
        {
            if (part is ToolCallPart call)
            {
                calls.Add(call);
            }
        }

        return calls;
    }

    private static string SessionKey(string? sessionId) => sessionId ?? string.Empty;

    /// <summary>
    ///     One line describing a report, for the log.
    /// </summary>
    private static string Describe(TurnChangeReport report)
    {
        int changed = 0;
        foreach (WorkspaceChange file in report.Files)
        {
            if (file.Kind is WorkspaceChangeKind.Created or WorkspaceChangeKind.Modified or WorkspaceChangeKind.Deleted)
            {
                changed++;
            }
        }

        string summary = changed == 0
            ? "no file changes"
            : $"{changed} file(s) changed";
        if (report.UnobservedTools.Count > 0)
        {
            summary += $"; not observable through: {string.Join(", ", report.UnobservedTools)}";
        }

        return summary;
    }

    // ── The state ──────────────────────────────────────────────────────────

    /// <summary>
    ///     What the harness remembers about one tool call while its turn is
    ///     open.
    /// </summary>
    private readonly record struct PendingCall(
        string Target,
        Fingerprint Before,
        Fingerprint After = default,
        bool HasAfter = false);

    /// <summary>
    ///     One watched path inside a turn.
    /// </summary>
    private readonly record struct PathWatch(string Target, Fingerprint Before, Fingerprint After, string ToolName);

    /// <summary>
    ///     What the filesystem said about a path at one moment.
    /// </summary>
    /// <param name="Exists">The path was there.</param>
    /// <param name="Readable">The oracle could read what it needed to say anything.</param>
    /// <param name="Length">The path's size in bytes.</param>
    /// <param name="ModifiedTicks">The path's last modification time, in ticks.</param>
    /// <param name="Hash">The content hash, or null when the file was too large to hash.</param>
    private readonly record struct Fingerprint(
        bool Exists,
        bool Readable,
        long Length,
        long ModifiedTicks,
        string? Hash)
    {
        /// <summary>The path was not there, and that is a fact the oracle can state.</summary>
        internal static Fingerprint Missing { get; } = new(false, true, 0, 0, null);

        /// <summary>The oracle could not read the path, so it says nothing about it.</summary>
        internal static Fingerprint Unknown { get; } = new(false, false, 0, 0, null);
    }
}

/// <summary>
///     BCL <c>[LoggerMessage]</c> delegates for <see cref="TurnChangeOracle" />.
/// </summary>
internal static partial class TurnChangeOracleLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Turn {TurnIndex}: {Summary}")]
    public static partial void TurnChanges(ILogger logger, int turnIndex, string summary);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Turn change oracle: cannot read '{Path}' — the path is reported as unverified")]
    public static partial void FingerprintFailed(ILogger logger, Exception exception, string path);
}
