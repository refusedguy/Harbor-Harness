// DefaultAgentBoundStateResultTests.cs — the behavioural + shape half of issue #1583.
//
// THE DEFECT
// ----------
// `DefaultAgent` kept its bound state behind a private accessor that THREW when
// the state was absent:
//
//     private AgentState BoundState
//     {
//         get => _state ?? throw new InvalidOperationException(
//             "DefaultAgent.State was read before Initialize. Bind a session with Initialize(session, agent) first.");
//         set => _state = value;
//     }
//
// The throw sat in the middle of the agent-run corridor. "Not bound yet" is an
// EXPECTED answer — a host or a plugin that prompts before the session is bound
// gets a named reason — but the accessor made it an exception unwound through
// the layers, instead of a `Result.Failure` the caller can read. The same
// corridor also spelled the reason two ways ("Agent is not initialized. Call
// InitializeAsync first." and "Agent is not initialized."), and the first of
// those named a method no caller can invoke: the only entry point is
// `IAgent.Initialize`, and it is not async.
//
// THE FIX
// -------
// The read seam is Result-shaped (`Result<AgentState>`), so "not bound" travels
// as a value and the run boundary hands the reason to the caller; the write
// side advances the same field and no longer throws on the way.
//
// WHAT IS PINNED HERE
// -------------------
//   * The not-initialized read path returns a Failure and does NOT throw — for
//     BOTH `PromptAsync` overloads, which are the two ways into the corridor.
//     (A throw fails the test: the assertion never runs.)
//   * Both overloads answer with the SAME reason: one named diagnostic, not two
//     near-synonyms, and it names the method that actually binds a session.
//   * The failure happens BEFORE anything is persisted, so no fabricated session
//     id reaches the store.
//   * The initialized path still runs the loop and still flips the state back
//     to idle — the fix changes the unbound answer, not the bound one.
//   * The SHAPE: `DefaultAgent` hands the bound state out only as `Maybe` or as
//     `Result`. A bare `AgentState` is a value that may not exist, and the
//     accessor that had to throw was exactly such a bare value. This is the
//     half that catches a re-introduced throwing accessor: the behavioural
//     half above cannot, because the old accessor was unreachable through the
//     public API (the guards ran first), so the behaviour it produced was
//     already correct. The shape is what was wrong.
//
// The reflection in the shape test is the same technique the abort-surface
// guard in this project already uses, and reflection is allowed in tests.

using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakeEventBus = Harbor.TestKit.FakeEventBus;
using FakeSessionStore = Harbor.Application.Tests.Fakes.FakeSessionStore;

namespace Harbor.Application.Tests;

/// <summary>
///     #1583: a read of the agent's bound state before <c>Initialize</c> must
///     answer with a named <c>Result.Failure</c>, not an exception.
/// </summary>
public sealed class DefaultAgentBoundStateResultTests
{
    private static Session NewSession() =>
        Session.Create("/tmp/harbor-agent-bound-state-tests", "code", "test", "test-model");

    private static DefaultAgent NewAgent(Session session, out FakeSessionStore store)
    {
        store = new FakeSessionStore(session);
        return new DefaultAgent(store, new FakeAgentLoop(), new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
    }

    /// <summary>
    ///     The text overload — the way a host or a REPL enters the corridor.
    /// </summary>
    [Test]
    public async Task PromptAsync_Text_BeforeInitialize_ReturnsFailure_AndDoesNotThrow()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out FakeSessionStore store);

        Result result = await subject.PromptAsync("hello");

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "An unbound agent has no session to append to and no definition to drive, so the call "
                + "cannot succeed. It must FAIL with a reason, not throw: a throw unwinds through the "
                + "layers and the caller cannot read a message out of it.");
        await Assert.That(result.Error).Contains("not initialized")
            .Because("The reason must name the condition, so the operator knows what to do instead of guessing.");
        await Assert.That(result.Error).Contains("Initialize")
            .Because("The reason must name the FIX. The old wording said \"Call InitializeAsync first\", "
                + "and no caller can invoke that — the only entry point is Initialize, and it is not async.");
        await Assert.That(store.Appends).IsEqualTo(0)
            .Because(
                "The guard has to fire BEFORE persistence. A fabricated session id reaching "
                + "ISessionStore.AppendMessageAsync is the concrete argument against an AgentState.Uninitialized "
                + "sentinel, and it is the argument against answering with a value instead of a reason.");
    }

    /// <summary>
    ///     The message overload — the way the image path and the IPC path enter the corridor.
    /// </summary>
    [Test]
    public async Task PromptAsync_PreBuiltMessage_BeforeInitialize_ReturnsFailure_AndDoesNotThrow()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out FakeSessionStore store);

        Result result = await subject.PromptAsync(TestMessages.User("hi", sessionId: session.Id, agent: "user"));

        await Assert.That(result.IsFailure).IsTrue()
            .Because("The UserMessage overload must refuse too — it is the overload the image path and the IPC path use.");
        await Assert.That(result.Error).Contains("not initialized");
        await Assert.That(store.Appends).IsEqualTo(0)
            .Because("No fabricated session id may reach the store.");
    }

    /// <summary>
    ///     One condition, one reason: the two overloads must not drift into two
    ///     near-synonyms again.
    /// </summary>
    [Test]
    public async Task BothPromptOverloads_AnswerWithTheSameNamedReason()
    {
        Session session = NewSession();
        using DefaultAgent fromText = NewAgent(session, out _);
        using DefaultAgent fromMessage = NewAgent(session, out _);

        Result text = await fromText.PromptAsync("hello");
        Result message = await fromMessage.PromptAsync(TestMessages.User("hi", sessionId: session.Id, agent: "user"));

        await Assert.That(text.Error).IsEqualTo(message.Error)
            .Because(
                "The two overloads used to answer \"Agent is not initialized. Call InitializeAsync first.\" "
                + "and \"Agent is not initialized.\" — the same condition in two dialects, one of them "
                + "naming a method that does not exist. A caller grepping for the reason must find one "
                + "answer, and both overloads must find it.");
    }

    /// <summary>
    ///     The bound path is untouched: the loop runs, the message is persisted,
    ///     and the state comes back to idle.
    /// </summary>
    [Test]
    public async Task PromptAsync_AfterInitialize_StillRunsTheLoop()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out FakeSessionStore store);
        subject.Initialize(session, TestAgents.AllowAll());

        Result result = await subject.PromptAsync("hello");

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("The fix changes the UNBOUND answer. A bound agent must still run its loop.");
        await Assert.That(store.Appends).IsEqualTo(1)
            .Because("The bound path still persists the prompt.");
        await Assert.That(subject.State.Value.IsRunning).IsFalse()
            .Because(
                "The write side still advances the state: the run finished, so the snapshot must be back "
                + "to idle. The read seam became Result-shaped; advancing the state did not stop working.");
    }

    /// <summary>
    ///     The shape guard: the bound state is handed out as <c>Maybe</c> or as
    ///     <c>Result</c>, and as nothing else. A bare <see cref="AgentState" /> is
    ///     a value that may not exist — and the accessor that had to throw was
    ///     exactly such a bare value.
    /// </summary>
    [Test]
    public async Task DefaultAgent_ExposesNoBareAgentStateMember()
    {
        // Properties and methods only — the members that HAND the state out. A
        // private field is not handed out, and the nullable backing field is
        // precisely the absence the Maybe projection reads.
        string[] bare = typeof(DefaultAgent)
            .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m switch
            {
                PropertyInfo p => p.PropertyType == typeof(AgentState),
                MethodInfo mi => mi.ReturnType == typeof(AgentState),
                _ => false,
            })
            .Select(m => m.Name)
            .ToArray();

        await Assert.That(bare).IsEmpty()
            .Because(
                "A member that hands the bound state out as a bare AgentState cannot say \"not bound yet\" — "
                + "it either throws (the #1583 accessor) or hands back a null the compiler promised was a "
                + "value (the #559 lie). Maybe<AgentState> and Result<AgentState> are the two shapes that can "
                + "say it. Only the members that HAND OUT are checked: the private nullable backing field is "
                + "the absence the Maybe projection reads, not a value anyone can get. Found: "
                + string.Join(", ", bare));
    }
}
