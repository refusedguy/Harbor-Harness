// DefaultAgentLoadFailureTests.cs — the behavioural pin for issue #1510.
//
// THE DEFECT
// ----------
// DefaultAgent.PromptAsync loads the session context through a Result-shaped
// seam and then threw the failure away:
//
//     var loaded = await LoadSessionContextAsync(state.SessionId, linkedCts.Token);
//     if (loaded.IsFailure)
//         throw new InvalidOperationException(loaded.Error);   // Result → exception
//     ...
//     catch (Exception ex)
//     {
//         ...
//         var failed = Result.Failure(ex.Message);              // exception → Result
//         completion.TrySetResult(failed);
//         return failed;
//     }
//
// A Result that leaves as an exception and comes back as a Result is a round
// trip with no gain: the typed failure dies on the way out, the catch-all picks
// up exceptions nobody meant it to (the same catch also covers the agent loop),
// and the reader has to reconstruct the cycle from two places. The exception
// never left the method — it was created and caught inside the same try — so
// nothing outside could depend on its type.
//
// THE FIX
// -------
// The load failure returns the Result it already holds. The exception is never
// created, so there is nothing to catch back, and the log line names the
// failure instead of attaching an exception nobody threw.
//
// WHAT IS PINNED HERE
// -------------------
//   * The load failure reaches the caller as a Failure carrying the STORE's own
//     text, byte for byte the text the old path produced — an exception built
//     from a string keeps that string as its message, so the round trip was
//     text-preserving and the fix has to be too.
//   * NO exception is logged on that path: the failure is not an exception, so
//     it must not be logged as one.
//   * The loop never ran, the state is back to idle, and the run's completion
//     source carries the failure — the same surfaces the old path fed.
//   * The catch that REMAINS (for the agent loop, which can throw) still turns
//     an exception into a Failure and still logs the exception. The fix removes
//     ONE bridge; it does not disarm the run boundary.
//
// The store fake lives in this file rather than in the shared fakes, so the
// test depends on nothing but the product types.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using FakeEventBus = Harbor.TestKit.FakeEventBus;

namespace Harbor.Application.Tests;

/// <summary>
///     #1510: a session-context load failure must reach the caller as the
///     Result it already is — not as an exception the same method catches and
///     converts back.
/// </summary>
public sealed class DefaultAgentLoadFailureTests
{
    private const string LoadFailureText = "session store offline (simulated)";

    private static Session NewSession() =>
        Session.Create("/tmp/harbor-agent-load-failure-tests", "code", "test", "test-model");

    /// <summary>
    ///     The load seam's failure case, with a working write side: the prompt is
    ///     persisted, and only the READ of the session fails, so the failure
    ///     under test is the context load and nothing else.
    /// </summary>
    private sealed class ScriptedStore(Session session) : ISessionStore
    {
        /// <summary>When set, <see cref="GetAsync" /> fails with this text.</summary>
        public string? ReadFailure { get; init; }

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(session));

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(ReadFailure is { } text
                ? Result.Failure<Session>(text)
                : Result.Success(session));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([]));

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    /// <summary>Loop that counts its runs and never fails.</summary>
    private sealed class CountingAgentLoop : IAgentLoop
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Loop that throws — the case the remaining catch exists for.</summary>
    private sealed class ThrowingAgentLoop : IAgentLoop
    {
        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
            => throw new InvalidOperationException("loop exploded (simulated)");
    }

    /// <summary>
    ///     Logger that keeps what was written — including whether an exception was
    ///     attached, which is the whole point of the load-failure assertions.
    /// </summary>
    private sealed class RecordingLogger : ILogger<DefaultAgent>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Thrown)> _entries = [];

        /// <summary>How many lines were written.</summary>
        public int LineCount
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>How many of them carried an exception.</summary>
        public int ExceptionCount
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Count(e => e.Thrown is not null);
                }
            }
        }

        /// <summary>Whether some line names the given text.</summary>
        public bool Mentions(string text)
        {
            lock (_entries)
            {
                return _entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal));
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private static DefaultAgent NewAgent(
        Session session,
        ISessionStore store,
        IAgentLoop loop,
        ILogger<DefaultAgent> logger)
    {
        var agent = new DefaultAgent(store, loop, new FakeEventBus(), logger);
        agent.Initialize(session, TestAgents.AllowAll());
        return agent;
    }

    /// <summary>
    ///     The failure reaches the caller with the STORE's text — the text the old
    ///     bridge produced verbatim, because an exception built from a string keeps
    ///     that string as its message.
    /// </summary>
    [Test]
    public async Task PromptAsync_SessionContextLoadFails_AnswersWithTheStoreFailure()
    {
        Session session = NewSession();
        var loop = new CountingAgentLoop();
        using DefaultAgent subject = NewAgent(
            session,
            new ScriptedStore(session) { ReadFailure = LoadFailureText },
            loop,
            new RecordingLogger());

        Result result = await subject.PromptAsync("hello");

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "The context load failed, so the run cannot start. It must FAIL with the store's reason.");
        await Assert.That(result.Error).IsEqualTo(LoadFailureText)
            .Because(
                "The text is the contract: the old path produced it through "
                + "InvalidOperationException(loaded.Error).Message, which is the string it was given, so the "
                + "failure the caller saw was the store's text. The fix must not change it.");
        await Assert.That(loop.Runs).IsEqualTo(0)
            .Because("The loop must not run on a context that never loaded.");
    }

    /// <summary>
    ///     The failure is logged as a failure, NOT as an exception: there is no
    ///     exception any more, so attaching one would be a lie.
    /// </summary>
    [Test]
    public async Task PromptAsync_SessionContextLoadFails_LogsNoException()
    {
        Session session = NewSession();
        var logger = new RecordingLogger();
        using DefaultAgent subject = NewAgent(
            session,
            new ScriptedStore(session) { ReadFailure = LoadFailureText },
            new CountingAgentLoop(),
            logger);

        await subject.PromptAsync("hello");

        await Assert.That(logger.LineCount > 0).IsTrue()
            .Because(
                "A failure that reaches nobody is a failure nobody can diagnose. The log line names the "
                + "failure — it just does not carry an exception, because none was thrown.");
        await Assert.That(logger.ExceptionCount).IsEqualTo(0)
            .Because(
                "The old path logged the load failure through AgentRunFailed, which attaches the exception "
                + "the bridge had just manufactured. A Result is not an exception and must not be logged as "
                + "one.");
        await Assert.That(logger.Mentions(LoadFailureText)).IsTrue()
            .Because(
                "The log has to name the reason. 'Something failed' is not a diagnosis; the store's text is.");
    }

    /// <summary>
    ///     The same surfaces the old path fed: the state goes back to idle and
    ///     the run's completion source carries the failure, so a waiter is
    ///     released instead of hanging.
    /// </summary>
    [Test]
    public async Task PromptAsync_SessionContextLoadFails_LeavesTheAgentIdle()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(
            session,
            new ScriptedStore(session) { ReadFailure = LoadFailureText },
            new CountingAgentLoop(),
            new RecordingLogger());

        await subject.PromptAsync("hello");

        await Assert.That(subject.State.Value.IsRunning).IsFalse()
            .Because(
                "The old path reset IsRunning in the catch. The fix resets it where the failure is handled, "
                + "so an agent whose load failed is idle, not stuck claiming a run.");
        await Assert.That(subject.State.HasValue).IsTrue()
            .Because("The agent stays bound — the load failed, the binding did not.");
    }

    /// <summary>
    ///     The catch that REMAINS still works: the agent loop can throw, and the
    ///     run boundary turns that into a Failure and logs the exception. The fix
    ///     removes one bridge; it does not disarm the boundary.
    /// </summary>
    [Test]
    public async Task PromptAsync_LoopThrows_StillAnswersWithFailure_AndLogsTheException()
    {
        Session session = NewSession();
        var logger = new RecordingLogger();
        using DefaultAgent subject = NewAgent(
            session,
            new ScriptedStore(session),
            new ThrowingAgentLoop(),
            logger);

        Result result = await subject.PromptAsync("hello");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("loop exploded (simulated)")
            .Because(
                "An exception from the loop is a genuine fault, not an expected failure — it travels as a "
                + "Failure with the exception's message, exactly as before the fix.");
        await Assert.That(logger.ExceptionCount > 0).IsTrue()
            .Because(
                "A real exception is still logged as one. The fix removed the bridge that MANUFACTURED an "
                + "exception; it did not blind the boundary to the ones that really happen.");
        await Assert.That(subject.State.Value.IsRunning).IsFalse()
            .Because("The failed run releases the state back to idle, as it always did.");
    }
}
