// UiTestDoubles.cs — the shared UI test doubles (#1549), moved here by #1889.
//
// WHY THE MOVE
// ------------
// This file was ONE of eleven files in `Harbor.TestKit`, and the only one
// that needed the UI stack. The four `<ProjectReference>`s it cost were
// therefore paid by every test project that referenced TestKit for an
// unrelated reason — 11 of the 14 consumers never touched a double, and each
// of them dragged eleven UI/Desktop assemblies into its build.
//
// The doubles are worth keeping (#1549 replaced five hand-rolled private
// duplicates with these). What was not worth keeping was the arrangement:
// the files that need the UI stack now live in the project that is allowed
// to need it, and `Harbor.TestKit` itself is core-only — a closure of four
// projects, of which only two are product assemblies, instead of nineteen.
// `tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs` pins that
// split.
//
// THE NAMESPACE DID NOT MOVE. The TestKit family is ONE namespace
// (`Harbor.TestKit`) spread over several assemblies (#1772/#1773), so a
// consumer needs `using Harbor.TestKit;` and nothing else, whichever
// assembly the type happens to live in.

using Harbor.Desktop.ViewModels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.TestKit;

/// <summary>
///     Test double for <see cref="IDispatcherAdapter" /> — no-ops so tests stay
///     synchronous, and lets a test drive a store transition by hand through
///     <see cref="Raise" />.
/// </summary>
/// <remarks>
///     <para>
///         This is the ONE dispatcher double for the whole repository. Before
///         #1549 the same shape was copy-pasted: a private
///         <c>TestDispatcherAdapter</c> in
///         <c>tests/Harbor.Ui.Framework.Tests/ChatViewModelBaseTests.cs</c>
///         (used by five files) and a private <c>FakeDispatcherAdapter</c> in
///         <c>tests/Harbor.App.Avalonia.Tests/AvaloniaWorkspaceCommandsTests.cs</c>.
///     </para>
///     <para>
///         <see cref="Post" /> and <see cref="Invoke{T}" /> run the action
///         inline, so a test that pushes a state through the dispatcher observes
///         the projection synchronously — no waiting, no pumping a UI loop.
///     </para>
/// </remarks>
public sealed class TestDispatcherAdapter : IDispatcherAdapter
{
    /// <summary>Raised when a test asks for a store transition.</summary>
    public event EventHandler<UiState>? StateChanged;

    /// <summary>Publish a state transition to every subscriber.</summary>
    public void Raise(UiState state) => StateChanged?.Invoke(this, state);

    /// <summary>Runs the action inline — the point of the double.</summary>
    public void Post(Action action) => action();

    /// <summary>Runs the function inline — the point of the double.</summary>
    public T Invoke<T>(Func<T> func) => func();

    /// <summary>No-op: the double is not bound to a real store.</summary>
    public void Bind(UiStore store)
    {
    }

    /// <summary>No-op: the double is not bound to a real store.</summary>
    public void Unbind(UiStore store)
    {
    }
}

/// <summary>
///     A <see cref="StoreSubscriberViewModel" /> double that COUNTS the hook
///     calls a test asserts on, and exposes the protected selector API the
///     base keeps to itself.
/// </summary>
/// <remarks>
///     <para>
///         Three hand-rolled doubles used to sit around this one hierarchy
///         (#1549): <c>TestableHookTrackingViewModel</c> and
///         <c>TestableStoreSubscriberViewModel</c> in
///         <c>tests/Harbor.Ui.Framework.Tests/</c>, plus the chat-shaped
///         sibling <see cref="CountingChatViewModelStub" />. This is the shared
///         shape they all had: count <c>OnStoreChanged</c>, count
///         <c>OnAfterSelectorsApplied</c>, keep the states that arrived, and
///         let the test register/apply/reset selectors directly.
///     </para>
///     <para>
///         The class is not sealed: a test that needs a different record of
///         the hooks (call ORDER, a flag, an applied selector) overrides the
///         two hooks and calls <c>base</c> — the count stays consistent for
///         free.
///     </para>
/// </remarks>
public class CountingStoreSubscriberStub : StoreSubscriberViewModel
{
    private readonly TestDispatcherAdapter _dispatcher;

    /// <summary>Creates the stub with its own dispatcher and a null logger.</summary>
    public CountingStoreSubscriberStub()
        : this(new TestDispatcherAdapter())
    {
    }

    /// <summary>Creates the stub with the given dispatcher and a null logger.</summary>
    public CountingStoreSubscriberStub(TestDispatcherAdapter dispatcher)
        : base(dispatcher, NullLogger.Instance)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Creates the stub with an explicit dispatcher and logger.</summary>
    public CountingStoreSubscriberStub(TestDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>The dispatcher this stub was built with.</summary>
    public TestDispatcherAdapter DispatcherDouble => _dispatcher;

    /// <summary>Every state that reached <c>OnStoreChanged</c>, in order.</summary>
    public List<UiState> OnStoreChangedStates { get; } = new();

    /// <summary>Every state that reached <c>OnAfterSelectorsApplied</c>, in order.</summary>
    public List<UiState> OnAfterSelectorsAppliedStates { get; } = new();

    /// <summary>How many times <c>OnStoreChanged</c> ran.</summary>
    public int OnStoreChangedCount { get; private set; }

    /// <summary>How many times <c>OnAfterSelectorsApplied</c> ran.</summary>
    public int OnAfterSelectorsAppliedCount { get; private set; }

    /// <inheritdoc />
    protected override void OnStoreChanged(UiState state)
    {
        OnStoreChangedCount++;
        OnStoreChangedStates.Add(state);
    }

    /// <inheritdoc />
    protected override void OnAfterSelectorsApplied(UiState state)
    {
        OnAfterSelectorsAppliedCount++;
        OnAfterSelectorsAppliedStates.Add(state);
    }

    /// <summary>Registers a state→property projection.</summary>
    public void RegisterSelector<T>(Func<UiState, T> read, Action<T> apply, IEqualityComparer<T>? cmp = null)
        => Select(read, apply, cmp);

    /// <summary>Applies every registered selector against a state snapshot.</summary>
    public new void ApplySelectors(UiState state) => base.ApplySelectors(state);

    /// <summary>Resets the selector caches so the next apply always fires.</summary>
    public new void ResetSelectors() => base.ResetSelectors();

    /// <summary>Publishes a state transition through the dispatcher.</summary>
    public void SimulateStateChange(UiState state) => _dispatcher.Raise(state);
}

/// <summary>
///     A <see cref="ChatViewModelBase" /> double that COUNTS the hook calls and
///     exposes the protected selector API — the chat-shaped sibling of
///     <see cref="CountingStoreSubscriberStub" />.
/// </summary>
/// <remarks>
///     Replaces the per-file <c>TestChatViewModel</c> that lived in
///     <c>tests/Harbor.Ui.Framework.Tests/ChatViewModelBaseTests.cs</c> and was
///     reused by <c>ToolCallStructureReachesUiTests.cs</c> (#1549). The
///     selectors the base itself registers (status, streaming, input) are
///     untouched: the double only adds the counting and the public handles.
/// </remarks>
public class CountingChatViewModelStub : ChatViewModelBase
{
    private readonly TestDispatcherAdapter _dispatcher;

    /// <summary>Creates the stub with its own dispatcher and a null logger.</summary>
    public CountingChatViewModelStub()
        : this(new TestDispatcherAdapter())
    {
    }

    /// <summary>Creates the stub with the given dispatcher and a null logger.</summary>
    public CountingChatViewModelStub(TestDispatcherAdapter dispatcher)
        : base(dispatcher, NullLogger.Instance)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Creates the stub with an explicit dispatcher and logger.</summary>
    public CountingChatViewModelStub(TestDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>The dispatcher this stub was built with.</summary>
    public TestDispatcherAdapter DispatcherDouble => _dispatcher;

    /// <summary>How many times <c>OnStoreChanged</c> ran.</summary>
    public int OnStoreChangedCallCount { get; private set; }

    /// <summary>Applies every registered selector against a state snapshot.</summary>
    public new void ApplySelectors(UiState state) => base.ApplySelectors(state);

    /// <inheritdoc />
    protected override void OnStoreChanged(UiState state)
    {
        OnStoreChangedCallCount++;
        base.OnStoreChanged(state);
    }

    /// <summary>Feeds a state into the view-model as a store transition would.</summary>
    public void TriggerStoreChanged(UiState state) => OnStoreChanged(state);

    /// <summary>Registers a state→property projection.</summary>
    public void RegisterSelector<T>(Func<UiState, T> read, Action<T> apply, IEqualityComparer<T>? cmp = null)
        => Select(read, apply, cmp);
}

/// <summary>
///     A <see cref="DiffViewModelBase" /> double that feeds a state into the
///     base's protected store hook — the diff-shaped sibling of
///     <see cref="CountingStoreSubscriberStub" />.
/// </summary>
/// <remarks>
///     The abstract base has no in-repo subclass, so the store feed is driven
///     through a double; before #1549 that double was a private class inside
///     <c>tests/Harbor.Ui.Framework.Tests/LineDiffAlignmentGuardTests.cs</c>.
/// </remarks>
public class DiffViewModelTestStub : DiffViewModelBase
{
    private readonly TestDispatcherAdapter _dispatcher;

    /// <summary>Creates the stub with its own dispatcher and a null logger.</summary>
    public DiffViewModelTestStub()
        : this(new TestDispatcherAdapter())
    {
    }

    /// <summary>Creates the stub with the given dispatcher and a null logger.</summary>
    public DiffViewModelTestStub(TestDispatcherAdapter dispatcher)
        : base(dispatcher, NullLogger.Instance)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Creates the stub with an explicit dispatcher and logger.</summary>
    public DiffViewModelTestStub(TestDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>The dispatcher this stub was built with.</summary>
    public TestDispatcherAdapter DispatcherDouble => _dispatcher;

    /// <summary>Feeds a state into the view-model as a store transition would.</summary>
    public void TriggerStoreChanged(UiState state) => OnStoreChanged(state);

    /// <summary>Publishes a state transition through the dispatcher.</summary>
    public void SimulateStateChange(UiState state) => _dispatcher.Raise(state);
}
