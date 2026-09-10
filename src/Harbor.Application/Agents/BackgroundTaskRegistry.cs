using System.Collections.Concurrent;
using Harbor.Abstractions.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Agents;

/// <summary>
///     Default <see cref="IBackgroundTaskRegistry" />: detached sub-agent runs
///     on the thread pool, keyed to the launching session, capped so one
///     chatty orchestrator cannot fork-bomb the provider bill.
/// </summary>
public sealed class BackgroundTaskRegistry : IBackgroundTaskRegistry
{
    private readonly ConcurrentDictionary<string, Task<Result<SubAgentRunResult>>> _runs = new();
    private readonly ConcurrentDictionary<string, (string AgentName, string SessionId)> _meta =
        new();
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
        ArgumentNullException.ThrowIfNull(run);
        if (_runs.Count >= MaxBackgroundTasks)
        {
            return Result.Failure<string>(
                $"Too many background tasks ({MaxBackgroundTasks}). Wait for one to finish."
            );
        }

        string id = "task_" + Interlocked.Increment(ref _seq);
        // Detached scheduling (own token None): the RUN is still bound to the
        // launcher's token, so aborting the parent run cancels the background
        // run while normal turn boundaries leave it alive.
        var task = Task.Run(() => run(ct), CancellationToken.None);
        _runs[id] = task;
        _meta[id] = (agentName, sessionId);
        _logger.LogInformation(
            "Background task started: id={Id} agent={Agent} session={Session}",
            id,
            agentName,
            sessionId
        );
        return Result.Success(id);
    }

    /// <inheritdoc />
    public IReadOnlyList<BackgroundTaskCompletion> DrainCompleted(string sessionId)
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
                done.Add(new BackgroundTaskCompletion(id, meta.AgentName, Observe(task)));
            }
        }

        return done;
    }

    // Observe without throwing: faulted/cancelled runs surface as error
    // completions (reading .Exception observes the task).
    private static Result<SubAgentRunResult> Observe(Task<Result<SubAgentRunResult>> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        if (task.IsCanceled)
        {
            return Result.Failure<SubAgentRunResult>("Background run was cancelled.");
        }

        string detail = task.Exception?.GetBaseException().Message ?? "unknown crash";
        return Result.Failure<SubAgentRunResult>($"Background run crashed: {detail}");
    }
}
