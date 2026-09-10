using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Reducers;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Pure-reducer tests for <see cref="ChromeReducer" />.
/// </summary>
public class ChromeReducerTests
{
    [Test]
    public async Task SessionChangedEvent_SetsActiveSessionId()
    {
        var state = new ChromeViewState();
        var result = ChromeReducer.Reduce(new SessionChangedEvent("session-123"), state);
        await Assert.That(result.ActiveSessionId).IsEqualTo(SessionId.Create("session-123"));
    }

    [Test]
    public async Task SessionChangedEvent_PreservesNavigationStack()
    {
        var navStack = ImmutableStack<ChromeViewState.Route>.Empty.Push(
            new ChromeViewState.Route.Chat(SessionId.Create("s1")));
        var state = new ChromeViewState { NavigationStack = navStack };
        var result = ChromeReducer.Reduce(new SessionChangedEvent("session-456"), state);
        await Assert.That(result.NavigationStack.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SessionChangedEvent_PreservesToasts()
    {
        var toast = new ChromeViewState.Toast(
            "hello",
            ChromeViewState.ToastSeverity.Info,
            DateTimeOffset.UtcNow,
            "t1");
        var state = new ChromeViewState { Toasts = [toast] };
        var result = ChromeReducer.Reduce(new SessionChangedEvent("session-456"), state);
        await Assert.That(result.Toasts.Length).IsEqualTo(1);
        await Assert.That(result.Toasts[0].Message).IsEqualTo("hello");
    }

    [Test]
    public async Task UnknownEvent_ReturnsSameStateReference()
    {
        var state = new ChromeViewState { ActiveSessionId = SessionId.Create("s1") };
        var result = ChromeReducer.Reduce(new TurnStartEvent(1), state);
        await Assert.That(result.ActiveSessionId).IsEqualTo(SessionId.Create("s1"));
        await Assert.That(ReferenceEquals(state, result)).IsTrue();
    }

    [Test]
    public async Task MultipleSessionChanges_UpdatesToLatest()
    {
        var state = new ChromeViewState();
        var r1 = ChromeReducer.Reduce(new SessionChangedEvent("s1"), state);
        var r2 = ChromeReducer.Reduce(new SessionChangedEvent("s2"), r1);
        await Assert.That(r2.ActiveSessionId).IsEqualTo(SessionId.Create("s2"));
    }

    [Test]
    public async Task SessionChangedEvent_PreservesActiveModal()
    {
        var modal = new ChromeViewState.Modal.Confirm("title", "msg", "confirm");
        var state = new ChromeViewState { ActiveModal = modal };
        var result = ChromeReducer.Reduce(new SessionChangedEvent("session-456"), state);
        await Assert.That(result.ActiveModal).IsEqualTo(modal);
    }
}
