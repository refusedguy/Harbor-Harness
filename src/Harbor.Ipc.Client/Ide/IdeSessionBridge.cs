using System.Text.Json;

namespace Harbor.Ipc.Ide;

/// <summary>Tunables for <see cref="IdeSessionBridge" />.</summary>
public sealed record IdeSessionBridgeOptions
{
    /// <summary>
    ///     Budget for a scheduled prompt run. Runs are lifetime-scoped (not
    ///     request-scoped) because <c>inject_prompt</c> answers immediately —
    ///     this is the safety net, not the UX contract.
    /// </summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Per-request budget for bridge requests themselves (delegate to the framing server).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
///     Binds the NDJSON JSON-RPC framing server to a live Harbor host through the
///     hexagonal <see cref="IHarborClient"/> seam, implementing the four editor
///     methods: <c>list_sessions</c>, <c>inject_prompt</c>, <c>read_stream</c>,
///     <c>stop_stream</c> and <c>abort</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Non-blocking guarantee:</b> <c>inject_prompt</c> schedules the agent
///         run on a tracked background task and answers
///         <c>{"accepted":true}</c> immediately — the run never blocks the read
///         loop, and the read loop never blocks the agent. Progress reaches the
///         editor as <c>stream</c> notifications pushed by a single event pump
///         consuming <see cref="IHarborClient.SubscribeToEventsAsync"/>; pump
///         writes share the framing server's write lock, so frames never
///         interleave.
///     </para>
///     <para>
///         <b>Session binding:</b> the bridge serves exactly one session
///         (<see cref="SessionId"/>, bound by the host glue via
///         <c>StartAgentAsync</c> before <see cref="RunAsync"/>).
///         <c>inject_prompt</c> with a foreign <c>session_id</c> is rejected with
///         -32602 instead of silently rerouting the host's agent.
///     </para>
/// </remarks>
public sealed class IdeSessionBridge : IAsyncDisposable
{
    private readonly IHarborClient _client;
    private readonly IdeJsonRpcServer _server;
    private readonly IdeSessionBridgeOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _sync = new();
    private readonly List<(Task Run, CancellationTokenSource Cts)> _runs = [];
    private int _streamSubscribed;
    private Task? _eventPump;
    private int _disposed;

    /// <summary>
    ///     Create a bridge over the editor-facing stdio pair. The host glue is
    ///     responsible for connecting the client and calling
    ///     <c>StartAgentAsync</c> for <paramref name="sessionId"/> before
    ///     <see cref="RunAsync"/>.
    /// </summary>
    public IdeSessionBridge(
        IHarborClient client,
        TextReader input,
        TextWriter output,
        string? sessionId,
        ILogger logger,
        IdeSessionBridgeOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new IdeSessionBridgeOptions();
        SessionId = sessionId;
        _server = new IdeJsonRpcServer(
            input,
            output,
            HandleAsync,
            logger,
            new IdeJsonRpcServerOptions { RequestTimeout = _options.RequestTimeout });
    }

    /// <summary>Session the bridge is bound to (null until the glue binds one).</summary>
    public string? SessionId { get; private set; }

    /// <summary>
    ///     Serve requests until the editor closes stdin. Returns the framing
    ///     server's completion; in-flight runs are cancelled by <see cref="DisposeAsync"/>.
    /// </summary>
    public Task RunAsync(CancellationToken ct) => _server.RunAsync(ct);

    // ── Request dispatch ───────────────────────────────────────────────────

    private async Task<JsonElement?> HandleAsync(string method, JsonElement? parameters, CancellationToken requestCt)
    {
        return method switch
        {
            IdeMethods.ListSessions => await ListSessionsAsync(requestCt).ConfigureAwait(false),
            IdeMethods.InjectPrompt => InjectPrompt(parameters),
            IdeMethods.ReadStream => StartStream(),
            IdeMethods.StopStream => StopStream(),
            IdeMethods.Abort => await AbortAsync(requestCt).ConfigureAwait(false),
            _ => throw new IdeRpcException(IdeRpcException.MethodNotFound, $"Unknown method '{method}'.")
        };
    }

    private async Task<JsonElement?> ListSessionsAsync(CancellationToken requestCt)
    {
        Result<IReadOnlyList<Session>> result = await _client.ListSessionsAsync(requestCt).ConfigureAwait(false);
        if (result.IsFailure)
            throw new IdeRpcException(IdeRpcException.HandlerError, result.Error);

        var sessions = new List<IdeSessionInfo>(result.Value.Count);
        foreach (Session s in result.Value)
        {
            sessions.Add(new IdeSessionInfo(s.Id, s.Title, s.Agent, s.ProviderId, s.Model, s.Directory, s.UpdatedAt));
        }

        return JsonSerializer.SerializeToElement(new IdeListSessionsResult(sessions), IdeJsonContext.Default.IdeListSessionsResult);
    }

    private JsonElement? InjectPrompt(JsonElement? parameters)
    {
        IdeInjectPromptParams p = ParseInjectParams(parameters);
        if (string.IsNullOrWhiteSpace(p.Prompt))
            throw new IdeRpcException(IdeRpcException.InvalidParams, "'prompt' (non-empty string) is required.");
        if (!string.IsNullOrEmpty(p.SessionId) && !string.Equals(p.SessionId, SessionId, StringComparison.Ordinal))
            throw new IdeRpcException(IdeRpcException.InvalidParams, $"Bridge is bound to session '{SessionId}'; refusing to inject into '{p.SessionId}'.");
        if (SessionId is null)
            throw new IdeRpcException(IdeRpcException.InvalidParams, "No session bound — start the bridge with --session <id>.");

        SchedulePromptRun(p.Prompt);

        return JsonSerializer.SerializeToElement(
            new IdeInjectPromptResult(Accepted: true, SessionId: SessionId),
            IdeJsonContext.Default.IdeInjectPromptResult);
    }

    private JsonElement? StartStream()
    {
        Volatile.Write(ref _streamSubscribed, 1);
        EnsurePumpStarted();
        _logger.LogDebug("IDE bridge: editor subscribed to the stream for session {SessionId}", SessionId);
        return JsonSerializer.SerializeToElement(new IdeReadStreamResult(Subscribed: true), IdeJsonContext.Default.IdeReadStreamResult);
    }

    private JsonElement? StopStream()
    {
        Volatile.Write(ref _streamSubscribed, 0);
        _logger.LogDebug("IDE bridge: editor unsubscribed from the stream");
        return JsonSerializer.SerializeToElement(new IdeReadStreamResult(Subscribed: false), IdeJsonContext.Default.IdeReadStreamResult);
    }

    private async Task<JsonElement?> AbortAsync(CancellationToken requestCt)
    {
        Result result = await _client.AbortAgentAsync(requestCt).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new IdeAbortResult(Requested: result.IsSuccess), IdeJsonContext.Default.IdeAbortResult);
    }

    // ── Background prompt runs ─────────────────────────────────────────────

    private void SchedulePromptRun(string prompt)
    {
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        runCts.CancelAfter(_options.RunTimeout);

        Task run = Task.Run(() => RunPromptAsync(prompt, runCts), CancellationToken.None);
        lock (_sync)
        {
            _runs.RemoveAll(static r => r.Run.IsCompleted);
            _runs.Add((run, runCts));
        }

        _ = AwaitRunAsync(run, runCts); // RunPromptAsync is catch-all — the task never faults
    }

    private async Task RunPromptAsync(string prompt, CancellationTokenSource runCts)
    {
        Result result;
        try
        {
            result = await _client.SendPromptAsync(prompt, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            result = Result.Failure("run aborted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IDE bridge: prompt run failed");
            result = Result.Failure(ex.Message);
        }

        if (result.IsFailure && Volatile.Read(ref _disposed) == 0)
        {
            await PushNotificationAsync(IdeNotifications.PromptError,
                new IdePromptErrorNotification(SessionId, result.Error)).ConfigureAwait(false);
        }
    }

    private async Task AwaitRunAsync(Task run, CancellationTokenSource runCts)
    {
        try
        {
            await run.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _runs.RemoveAll(r => ReferenceEquals(r.Run, run));
            }

            runCts.Dispose();
        }
    }

    // ── Event pump ─────────────────────────────────────────────────────────

    private void EnsurePumpStarted()
    {
        lock (_sync)
        {
            if (_eventPump is not null) return;
            _eventPump = Task.Run(() => PumpAsync(_lifetimeCts.Token), CancellationToken.None);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (HarborEvent evt in _client.SubscribeToEventsAsync(ct).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _streamSubscribed) == 0) continue;
                if (MapEvent(evt) is not { } notification) continue;
                await PushNotificationAsync(IdeNotifications.Stream, notification, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IDE bridge event pump stopped unexpectedly");
        }
    }

    private IdeStreamNotification? MapEvent(HarborEvent evt) => evt switch
    {
        HarborEvent.AgentStarted s => new(SessionId: s.SessionId, Kind: IdeStreamKinds.AgentStart),
        HarborEvent.MessageUpdate u => new(u.Partial.SessionId, IdeStreamKinds.MessageDelta, Delta: u.Delta),
        HarborEvent.MessageEnd e => new(e.Final.SessionId, IdeStreamKinds.MessageEnd, Text: ConcatText(e.Final)),
        HarborEvent.ToolStart t => new(SessionId, IdeStreamKinds.ToolStart, ToolCallId: t.ToolCallId, ToolName: t.ToolName),
        HarborEvent.ToolEnd t => new(SessionId, IdeStreamKinds.ToolEnd, ToolCallId: t.ToolCallId, Ok: !t.Result.IsError),
        HarborEvent.TurnStart t => new(SessionId, IdeStreamKinds.TurnStart, Turn: t.Turn),
        HarborEvent.TurnEnd t => new(SessionId, IdeStreamKinds.TurnEnd, Turn: t.Turn),
        HarborEvent.AgentEnded s => new(s.SessionId, IdeStreamKinds.AgentEnd),
        HarborEvent.AgentError e => new(SessionId, IdeStreamKinds.AgentError, Error: e.Message),
        _ => null // compaction events stay TUI-internal; not part of the editor stream
    };

    private static string ConcatText(AssistantMessage message)
    {
        var sb = new System.Text.StringBuilder();
        foreach (ContentPart part in message.Parts)
        {
            if (part is TextPart text) _ = sb.Append(text.Text);
        }

        return sb.ToString();
    }

    private async Task PushNotificationAsync(string method, object payload, CancellationToken ct = default)
    {
        try
        {
            JsonElement element = payload switch
            {
                IdeStreamNotification n => JsonSerializer.SerializeToElement(n, IdeJsonContext.Default.IdeStreamNotification),
                IdePromptErrorNotification n => JsonSerializer.SerializeToElement(n, IdeJsonContext.Default.IdePromptErrorNotification),
                _ => throw new InvalidOperationException($"Unhandled notification payload type {payload.GetType().Name}.")
            };
            await _server.WriteNotificationAsync(method, element, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "IDE bridge: failed to push '{Method}' notification", method);
        }
    }

    // ── Params parsing ─────────────────────────────────────────────────────

    private static IdeInjectPromptParams ParseInjectParams(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
            throw new IdeRpcException(IdeRpcException.InvalidParams, "'params' object is required.");

        try
        {
            return p.Deserialize(IdeJsonContext.Default.IdeInjectPromptParams)
                   ?? throw new IdeRpcException(IdeRpcException.InvalidParams, "Params deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new IdeRpcException(IdeRpcException.InvalidParams, $"Malformed params: {ex.Message}");
        }
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        Task[] pending;
        lock (_sync)
        {
            pending = [.. _runs.Select(r => r.Run), _eventPump ?? Task.CompletedTask];
            _runs.Clear();
        }

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IDE bridge: in-flight work did not drain within the grace window");
            }
        }

        _lifetimeCts.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
