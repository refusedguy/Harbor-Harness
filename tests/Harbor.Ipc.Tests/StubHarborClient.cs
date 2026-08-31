using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Scriptable <see cref="IHarborClient" /> stub for the IDE bridge tests:
///     records prompts, gates methods for timeout/concurrency scenarios, and
///     lets tests publish <see cref="HarborEvent" />s into the event stream.
/// </summary>
internal sealed class StubHarborClient : IHarborClient
{
    private readonly Channel<HarborEvent> _events = Channel.CreateUnbounded<HarborEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _promptSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _promptGate;
    private TaskCompletionSource? _listGate;
    private CancellationTokenSource? _runCts;

    public List<Session> Sessions { get; set; } = [];

    public List<string> Prompts { get; } = [];

    public bool Aborted { get; private set; }

    public bool LastPromptAborted { get; private set; }

    public bool IsConnected { get; private set; }

    public Session? BoundSession { get; set; }

    /// <summary>Blocks subsequent <see cref="SendPromptAsync" /> calls until released.</summary>
    public void GatePrompt() => _promptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleasePrompt() => _promptGate?.TrySetResult();

    /// <summary>Blocks subsequent <see cref="ListSessionsAsync" /> calls until released.</summary>
    public void GateListSessions() => _listGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseListSessions() => _listGate?.TrySetResult();

    public Task WaitPromptAsync(TimeSpan timeout) => _promptSignal.Task.WaitAsync(timeout);

    public Task WaitListSessionsStartedAsync(TimeSpan timeout) => _listStarted.Task.WaitAsync(timeout);

    public async ValueTask PublishEventAsync(HarborEvent evt)
    {
        await _events.Writer.WriteAsync(evt).ConfigureAwait(false);
    }

    public Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public async Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default)
    {
        Prompts.Add(prompt);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runCts = runCts;
        _promptSignal.TrySetResult();
        try
        {
            TaskCompletionSource? gate = _promptGate;
            if (gate is not null)
            {
                await gate.Task.WaitAsync(runCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            LastPromptAborted = true;
            return Result.Failure("run aborted");
        }
        finally
        {
            if (ReferenceEquals(_runCts, runCts)) _runCts = null;
            runCts.Dispose();
        }

        return Result.Success();
    }

    public Task<Result> AbortAgentAsync(CancellationToken ct = default)
    {
        Aborted = true;
        _runCts?.Cancel();
        return Task.FromResult(Result.Success());
    }

    public Task<Result<Session>> CreateSessionAsync(string dir, string agent, string provider, string model, CancellationToken ct = default)
    {
        Session session = Session.Create(dir, agent, provider, model);
        Sessions.Add(session);
        return Task.FromResult(Result.Success(session));
    }

    public Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default)
    {
        _listStarted.TrySetResult();
        if (_listGate is not null)
        {
            return WaitGateAsync(_listGate, Sessions);
        }

        IReadOnlyList<Session> snapshot = [.. Sessions];
        return Task.FromResult(Result.Success(snapshot));
    }

    private static async Task<Result<IReadOnlyList<Session>>> WaitGateAsync(
        TaskCompletionSource gate, List<Session> sessions)
    {
        await gate.Task.ConfigureAwait(false);
        IReadOnlyList<Session> snapshot = [.. sessions];
        return Result.Success(snapshot);
    }

    public Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        Session? match = Sessions.FirstOrDefault(s => s.Id == sessionId) ?? BoundSession;
        return Task.FromResult(
            match is not null ? Result.Success(match) : Result.Failure<Session>($"Session '{sessionId}' not found."));
    }

    public Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([]));

    public Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ProviderId>>([]));

    public Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(string? providerId = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

    public Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ToolDescriptor>>([]));

    public async IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (HarborEvent evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
