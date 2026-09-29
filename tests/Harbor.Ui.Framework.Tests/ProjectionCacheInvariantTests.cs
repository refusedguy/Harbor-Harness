// ProjectionCacheInvariantTests.cs — the behavioural half of the `null!` wave.
//
// DefaultUiProjector.ProjectionCache declared six members as `= null!`. They are
// now `required`, so the compiler enforces the invariant at the single
// construction site. These tests pin the invariant from the outside: every cache
// reuse site must observe a populated model, and none of them may hand back a
// null through a non-nullable return type.
//
// Each test targets one reuse site in DefaultUiProjector.Project, so a future
// edit that makes one of the six models optional fails here by name rather than
// as a NullReferenceException in a render loop:
//
//   Assert_SameStateTwice_ReusesScreen        -> cache.State, cache.Screen   (:70-73)
//   Assert_StreamingDelta_ReusesChrome       -> cache.Header/StatusBar/Input (:208,218,220)
//   Assert_ViewportChange_ReusesTranscript   -> cache.Transcript             (:112)
//
// The textual gate that stops the `null!` coming back lives in
// tests/Harbor.Architecture.Tests/UiFrameworkNullabilityRules.cs.

using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Asserts every model <see cref="DefaultUiProjector" /> caches is populated on
///     each projection path.
/// </summary>
public class ProjectionCacheInvariantTests
{
    /// <summary>
    ///     A cold projector on an empty store: the first <c>Project</c> builds every
    ///     model from scratch and publishes the cache.
    /// </summary>
    [Test]
    public async Task Assert_ColdProject_PopulatesEveryModel()
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();

        UiScreenModel screen = projector.Project(store.State);

        await Assert.That(screen).IsNotNull();
        await Assert.That(screen.Header).IsNotNull();
        await Assert.That(screen.Transcript).IsNotNull();
        await Assert.That(screen.StatusBar).IsNotNull();
        await Assert.That(screen.Input).IsNotNull();
        await Assert.That(screen.StateRevision).IsNotEqualTo(string.Empty);
    }

    /// <summary>
    ///     Same state twice: the fast path returns the cached screen itself, so a
    ///     <c>cache.Screen</c> that was never populated would surface here as null.
    /// </summary>
    [Test]
    public async Task Assert_SameStateTwice_ReusesScreen()
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();
        UiState state = store.State;

        UiScreenModel first = projector.Project(state);
        UiScreenModel second = projector.Project(state);

        await Assert.That(second).IsNotNull();
        await Assert.That(second).IsSameReferenceAs(first);
    }

    /// <summary>
    ///     A streaming delta changes the tail but not the chrome fingerprint, so
    ///     the header, status bar and input models must be the very same instances
    ///     the previous frame published.
    /// </summary>
    [Test]
    public async Task Assert_StreamingDelta_ReusesChrome()
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();
        var partial = AssistantMessage.Empty("s", "m");

        store.Dispatch(new ChatAppMsg.Agent(new MessageStartEvent(partial)));
        UiScreenModel first = projector.Project(store.State);

        store.Dispatch(new ChatAppMsg.Agent(
            new MessageUpdateEvent(new TextDeltaEvent("m", new string('x', 32)), partial)));
        UiScreenModel second = projector.Project(store.State);

        await Assert.That(second).IsNotNull();
        await Assert.That(second.Header).IsSameReferenceAs(first.Header);
        await Assert.That(second.StatusBar).IsSameReferenceAs(first.StatusBar);
        await Assert.That(second.Input).IsSameReferenceAs(first.Input);
        await Assert.That(second.Transcript).IsNotSameReferenceAs(first.Transcript);
    }

    /// <summary>
    ///     A viewport report touches neither the transcript nor the chat domain, so
    ///     the cached transcript must survive into the freshly assembled screen.
    /// </summary>
    [Test]
    public async Task Assert_ViewportChange_ReusesTranscript()
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();

        UiScreenModel first = projector.Project(store.State);
        store.Dispatch(new AppMsg.Viewport(40));
        UiScreenModel second = projector.Project(store.State);

        await Assert.That(second).IsNotNull();
        await Assert.That(second.Transcript).IsSameReferenceAs(first.Transcript);
        await Assert.That(second.Header).IsNotNull();
        await Assert.That(second.StatusBar).IsNotNull();
        await Assert.That(second.Input).IsNotNull();
    }
}
