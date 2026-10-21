using System.Collections.Concurrent;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Agents;

/// <summary>
///     Default <see cref="IBackgroundTaskRegistry" />: detached sub-agent runs
///     keyed to the launching session, capped so one chatty orchestrator
///     cannot fork-bomb the provider bill.
/// </summary>
/// <remarks>
///     <para>
///         Each run owns a <see cref="CancellationTokenSource" /> linked to the
///         launching token (#407): the link preserves the old "abort the parent,
///         abort the run" behaviour, while <see cref="CancelSession" /> fires the
///         owned sources directly — so a parent cancel stops children whose launch
///         token is already gone. Sources are dropped when their run drains.
///     </para>
///     <para>
///         A detached run gets its OWN thread for its synchronous prologue
///         (#2093), not a slot on the thread pool. The pool hands a queued work
///         item to whichever worker picks it up, and a worker that is busy — or
///         blocked — holds the item until it comes back; the run's first line
///         then waits for that worker, and on a loaded runner the wait was long
///         enough for the parent to believe the child never started. The
///         dedicated thread runs the prologue (up to the run's first await) and
///         is released the moment the run yields: the run itself stays
///         asynchronous, and no thread is held for its lifetime.
///     </para>
/// </remarks>
public sealed class BackgroundTaskRegistry : IBackgroundTaskRegistry
{
    private readonly ConcurrentDictionary<string, Task<Result<SubAgentRunResult>>> _runs = new();
    private readonly ConcurrentDictionary<string, (string AgentName, string SessionId)> _meta =
        new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new();
    private readonly ILogger<BackgroundTaskRegistry> _logger;
    private int _seq;

    /// <inheritdoc />
    public int MaxBackgroundTasks => 8;

    /// <inheritdoc />
    public int PendingCount => _runs.Count;

    /// <summary>Create a registry (optionally with a typed logger).</summary>
    public BackgroundTaskRegistry(ILogger<BackgroundTaskRegistry>? logger = null)
    {
        _logger = logger ?? NullLogger<BackgroundTaskRegistry>.Instance;
    }

    /// <inheritdoc />
    public Result<string> Start(
        string agentName,
        string sessionId,
        Func<CancellationToken, Task<Result<SubAgentRunResult>>> run,
        CancellationToken ct
    )
    {
        Ensure.NotNull(run);
        if (_runs.Count >= MaxBackgroundTasks)
        {
            return Result.Failure<string>(
                $"Too many background tasks ({MaxBackgroundTasks}). Wait for one to finish."
            );
        }

        string id = "task_" + Interlocked.Increment(ref _seq);
        // Detached scheduling (own token None): the RUN is still bound to the
        // launcher's token via a linked source the registry owns (#407), so
        // aborting the parent run cancels the background run while normal
        // turn boundaries leave it alive — and CancelSession can fire the
        // owned source even after the launcher's token is gone.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // #1105: a detached run is a NEW logical chain — suppress the
        // ExecutionContext flow so it does not inherit the launcher's
        // AsyncLocal SubAgentRunner depth (a background launch from inside a
        // sub-run would otherwise trip the nesting guard, or vice versa).
        Task<Result<SubAgentRunResult>> task;
        AsyncFlowControl flow = ExecutionContext.SuppressFlow();
        try
        {
            // The run starts on a DEDICATED thread (#2093), not on the pool: the
            // pool's queue is drained by whichever worker comes back first, so
            // on a loaded runner the run's first line waited for a pool slot
            // that could take minutes — and a background run that has not
            // started looks exactly like a background run that never will. The
            // dedicated thread covers the run's synchronous prologue only; the
            // run's own awaits still resume on the pool, as before.
            task = Task.Factory
                .StartNew<Task<Result<SubAgentRunResult>>>(
                    () => run(cts.Token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
        }
        finally
        {
            flow.Undo();
        }
        _runs[id] = task;
        _sources[id] = cts;
        _meta[id] = (agentName, sessionId);
        BackgroundTaskRegistryLog.BackgroundTaskStarted(_logger, id, agentName, sessionId);
        return Result.Success(id);
    }

    /// <inheritdoc />
    public int CancelSession(string sessionId)
    {
        int cancelled = 0;
        foreach (var (id, cts) in _sources)
        {
            if (!_meta.TryGetValue(id, out var meta) || meta.SessionId != sessionId)
                continue;
            try
            {
                cts.Cancel();
                cancelled++;
            }
            catch (ObjectDisposedException ex)
            {
                // Raced with DrainCompletedAsync dropping this run's source —
                // the run is already observed, nothing left to stop.
                BackgroundTaskRegistryLog.BackgroundTaskSourceDrained(_logger, ex, id);
            }
        }

        if (cancelled > 0)
        {
            BackgroundTaskRegistryLog.BackgroundTasksCancelled(_logger, sessionId, cancelled);
        }

        return cancelled;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackgroundTaskCompletion>> DrainCompletedAsync(string sessionId)
    {
        var done = new List<BackgroundTaskCompletion>();
        foreach (var (id, task) in _runs)
        {
            if (
                !_meta.TryGetValue(id, out var meta)
                || meta.SessionId != sessionId
                || !task.IsCompleted
            )
            {
                continue;
            }

            if (_runs.TryRemove(id, out _) && _meta.TryRemove(id, out _))
            {
                if (_sources.TryRemove(id, out var cts))
                    cts.Dispose();
                // #1099: async read — IsCompleted guards the wait,
                // not a blocking take.
                done.Add(new BackgroundTaskCompletion(id, meta.AgentName, await ObserveAsync(task).ConfigureAwait(false)));
            }
        }

        return done;
    }

    /// <inheritdoc />
    public async Task<bool> WaitForIdleAsync(
        string? sessionId = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        TimeSpan deadline = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (IsIdle(sessionId))
                return true;
            if (sw.Elapsed >= deadline)
                return false;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private bool IsIdle(string? sessionId)
    {
        foreach (var (id, task) in _runs)
        {
            if (task.IsCompleted)
                continue;
            if (sessionId is null)
                return false;
            if (_meta.TryGetValue(id, out var meta) && meta.SessionId == sessionId)
                return false;
        }

        return true;
    }

    // Observe without throwing: faulted/cancelled runs surface as error
    // completions. Awaited, never a blocking .Result take (#1099).
    private static async Task<Result<SubAgentRunResult>> ObserveAsync(Task<Result<SubAgentRunResult>> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<SubAgentRunResult>("Background run was cancelled.");
        }
        catch (Exception ex)
        {
            return Result.Failure<SubAgentRunResult>($"Background run crashed: {ex.GetBaseException().Message}");
        }
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="BackgroundTaskRegistry" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class BackgroundTaskRegistryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Background task started: id={Id} agent={Agent} session={Session}")]
    public static partial void BackgroundTaskStarted(ILogger logger, string id, string agent, string session);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Background task source already drained: id={Id}")]
    public static partial void BackgroundTaskSourceDrained(ILogger logger, Exception ex, string id);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Background tasks cancelled: session={Session} count={Count}")]
    public static partial void BackgroundTasksCancelled(ILogger logger, string session, int count);

}
