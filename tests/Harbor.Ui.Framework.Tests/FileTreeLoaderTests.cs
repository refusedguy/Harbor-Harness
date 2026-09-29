using CSharpFunctionalExtensions;
using Harbor.Abstractions.Filesystem;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #667 — the file-tree seam. These tests exist because the thing they
///     cover used to be untestable: the listing was a private field on the panel
///     and the walk was inline in <c>Build</c>, so the only way to exercise
///     "what happens when the directory walk is slow" was to make a real
///     directory walk slow.
/// </summary>
/// <remarks>
///     The four properties pinned here are the ones the design actually rests
///     on. Everything else is plumbing.
/// </remarks>
public sealed class FileTreeLoaderTests
{
    private const string PanelId = "file-tree";
    private const string DirA = "/tmp/harbor-667-a";
    private const string DirB = "/tmp/harbor-667-b";

    // ── the demand signal is cheap and idempotent ─────────────────────────

    [Test]
    [Arguments("", "")]
    [Arguments("file-tree", "")]
    [Arguments("", "/tmp/x")]
    public async Task Request_WithABlankArgument_IsIgnored(string panelId, string directory)
    {
        // The "means CWD" empty string must never reach the port: the resolver is
        // the panel's job (TerminalUiState.ResolvePanelDirectory), and a request
        // carrying it would be answered with a listing whose Directory field
        // disagrees with what the reducer compares against.
        var lister = new CountingLister();
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);

        loader.Request(panelId, directory, PointedAt(DirA));
        await Settled(loader);

        await Assert.That(lister.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Request_ForTheSameDirectoryTwice_StartsOneWalk()
    {
        var lister = new CountingLister();
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);

        // PointedAt matters, and it is not setup ceremony: the reducer only
        // accepts a listing for the directory the panel is actually on, so a
        // store that is not pointed at DirA rejects every result and the loader
        // would — correctly — walk again on the next request. A bare UiStore
        // would have made this test pass for the wrong reason had the guard gone
        // the other way.
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        loader.Request(PanelId, DirA, store);
        loader.Request(PanelId, DirA, store);

        await Settled(loader);
        await Assert.That(lister.Calls).IsEqualTo(1)
            .Because("Build asks on every frame; a loader that re-walked per frame would "
                   + "make the panel's own demand signal into the busy-work it replaced");
    }

    [Test]
    public async Task Request_ReturnsBeforeTheWalkCompletes()
    {
        // The gate is not a style preference: Request runs on the render thread.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lister = new GatedLister(gate.Task);
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store); // must not block on `gate`
        await Assert.That(lister.Started.Task.IsCompleted).IsTrue()
            .Because("Request has to have reached the port synchronously to have started it");

        gate.SetResult(true);
        await Settled(loader);
    }

    [Test]
    public async Task TwoPanels_AreTrackedIndependently()
    {
        // A per-panel source, not a global one: navigating one panel's directory
        // must not cancel another panel's load.
        var slow = new PerDirectoryLister { Hangs = true };
        using var loader = new FileTreeLoader(slow, NullLogger<FileTreeLoader>.Instance);
        var store = new UiStore();
        store.Dispatch(new AppMsg.SetPanelDirectory("tree-a", DirA));
        store.Dispatch(new AppMsg.SetPanelDirectory("tree-b", DirB));

        // Panel A's walk hangs; panel B's request must not cancel it, which is
        // only true because the CTS is per panel id. A single global CTS would
        // make two panels fight over one token, and whichever started second
        // would silently kill the first.
        loader.Request("tree-a", DirA, store);
        await slow.StartedFor(DirA);
        loader.Request("tree-b", DirB, store);
        await slow.StartedFor(DirB);

        await Assert.That(slow.TokenFor(DirA).IsCancellationRequested).IsFalse()
            .Because("panel-b's walk must not have cancelled panel-a's in-flight one");

        slow.Release();
        await Settled(loader);
    }

    [Test]
    public async Task AnUncancelledWalk_StillPublishesItsResult()
    {
        // Paired with TwoPanels_AreTrackedIndependently: that test proves panel B
        // did not cancel panel A, this one proves panel A then finished normally.
        // Neither holds alone — a source cancelled by everything and a walk that
        // never completes both satisfy "was not cancelled".
        var lister = new CountingLister { Hangs = true };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await lister.Started.Task;

        lister.Release.TrySetResult(true);
        await Settled(loader);

        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirA).Status).IsEqualTo(AsyncStatus.Success)
            .Because("a walk nothing cancelled must still publish its result");
    }

    [Test]
    public async Task Request_WithNoStore_IsANoOp()
    {
        // The degraded host (no store, no loader) must not fall back to reading
        // the disk — that fallback is the defect.
        var lister = new CountingLister();
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);

        loader.Request(PanelId, DirA, null);
        await Settled(loader);

        await Assert.That(lister.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Request_AfterASettledLoad_DoesNotWalkAgain()
    {
        var lister = new CountingLister();
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await Settled(loader);
        loader.Request(PanelId, DirA, store);
        await Settled(loader);

        await Assert.That(lister.Calls).IsEqualTo(1);
    }

    // ── supersession: a late result must never repaint a left directory ────

    [Test]
    public async Task NavigatingAway_CancelsTheWalkInFlight()
    {
        // PerDirectoryLister, not a single-token one: the second request starts
        // its OWN walk, which overwrites any shared "current token" field. A
        // single field would then answer with the NEW walk's — uncancelled —
        // token and the assertion would be about the wrong thing entirely. That
        // is not hypothetical; it is how this test read on the first CI run.
        var lister = new PerDirectoryLister { Hangs = true };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await lister.StartedFor(DirA);

        store.Dispatch(new AppMsg.SetPanelDirectory(PanelId, DirB));
        loader.Request(PanelId, DirB, store);
        await lister.StartedFor(DirB);

        await Assert.That(lister.TokenFor(DirA).IsCancellationRequested).IsTrue()
            .Because("the whole reason the loader owns a CTS: a walk the user has "
                   + "navigated away from must be stoppable, not merely ignored later");

        lister.Release();
        await Settled(loader);
    }

    [Test]
    public async Task ASlowWalkForAnOldDirectory_DoesNotPublishOverTheNewOne()
    {
        // A StubbornLister on purpose. A well-behaved port observes the token, so
        // walking away cancels walk A and A simply never answers — which would
        // make this test pass without the guards doing anything. The lister here
        // ignores cancellation and answers anyway, late, addressed to the
        // directory the user already left. That is the only shape in which a
        // stale result is actually possible, and the store must still end up
        // describing B.
        var lister = new StubbornLister();
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await lister.StartedFor(DirA);

        store.Dispatch(new AppMsg.SetPanelDirectory(PanelId, DirB));
        loader.Request(PanelId, DirB, store);
        await lister.StartedFor(DirB);

        lister.Release();
        await Settled(loader);

        FileTreeSnapshot shown = store.State.Ui.FileTreeFor(PanelId, DirB);
        await Assert.That(shown.Directory).IsEqualTo(DirB)
            .Because("a result addressed to a directory the panel left is not a result "
                   + "for anything the user is looking at");
    }

    [Test]
    public async Task AStubbornWalkThatIgnoresCancellation_StillCannotOverwriteTheNewerOne()
    {
        // The same race, pinned at the store rather than at the loader: A answers
        // late for a directory the panel has left, and B's own result must be the
        // one that survives. Written against the messages so it does not depend
        // on which half of the two defences happens to fire first.
        var lister = new StubbornLister();
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await lister.StartedFor(DirA);

        store.Dispatch(new AppMsg.SetPanelDirectory(PanelId, DirB));
        loader.Request(PanelId, DirB, store);
        await lister.StartedFor(DirB);

        lister.Release();
        await Settled(loader);

        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirB).Entries.Count).IsEqualTo(1)
            .Because("the surviving snapshot must be B's, not A's — a stale frame drawn over "
                   + "a fresh one is the failure this design exists to prevent");
    }

    [Test]
    public async Task TheReducerDropsAResultForADirectoryThePanelLeft()
    {
        // Pinned separately from the loader, because the two halves are
        // independent defences: the loader can be bypassed, replaced or simply
        // re-implemented, and the guard has to hold in the state machine itself.
        var store = PointedAt(DirB);
        store.Dispatch(new AppMsg.SetFileTreeLoaded(
            PanelId,
            DirA,
            [new FileTreeEntry("from-a", DirA + "/from-a", false, false)]));

        await Assert.That(store.State.Ui.FileTrees.ContainsKey(PanelId)).IsFalse();
    }

    [Test]
    public async Task TheReducerAcceptsAResultForTheDirectoryThePanelIsOn()
    {
        var store = PointedAt(DirB);
        store.Dispatch(new AppMsg.SetFileTreeLoaded(
            PanelId,
            DirB,
            [new FileTreeEntry("from-b", DirB + "/from-b", false, false)]));

        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirB).Entries.Length).IsEqualTo(1);
    }

    // ── failure and cancellation are different things ─────────────────────

    [Test]
    public async Task AFailedWalk_PublishesTheReason_NotAnException()
    {
        var lister = new CountingLister { Failure = "permission denied" };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await Settled(loader);

        FileTreeSnapshot snapshot = store.State.Ui.FileTreeFor(PanelId, DirA);
        await Assert.That(snapshot.Status).IsEqualTo(AsyncStatus.Error);
        await Assert.That(snapshot.Error).IsEqualTo("permission denied");
    }

    [Test]
    public async Task AThrowingWalk_BecomesAFailedSnapshot_RatherThanEscaping()
    {
        var lister = new CountingLister { Throws = new InvalidOperationException("boom") };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await Settled(loader);

        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirA).Status).IsEqualTo(AsyncStatus.Error)
            .Because("the contract says expected failures are Results, so a throw is a bug — "
                   + "but a bug in a background task must not be able to take the renderer down");
    }

    [Test]
    public async Task ACancelledWalk_LeavesNoErrorSnapshot()
    {
        // Cancellation is not a failure. Painting "cannot read" for a walk the
        // user themselves superseded would be a lie they have to read.
        var lister = new CountingLister { Throws = new OperationCanceledException() };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await Settled(loader);

        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirA).Status).IsNotEqualTo(AsyncStatus.Error);
    }

    // ── lifecycle ─────────────────────────────────────────────────────────

    [Test]
    public async Task Dispose_CancelsEverythingInFlight()
    {
        var lister = new CountingLister { Hangs = true };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);

        loader.Request(PanelId, DirA, store);
        await lister.Started.Task;

        loader.Dispose();

        await Assert.That(lister.Token.IsCancellationRequested).IsTrue();
        lister.Release.TrySetResult(true);
        await Settled(loader);
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        // A renderer that is torn down twice must not throw on the second pass:
        // no assertion wrapper, the return IS the assertion.
        var loader = new FileTreeLoader(new CountingLister(), NullLogger<FileTreeLoader>.Instance);
        loader.Dispose();
        loader.Dispose();

        await Assert.That(loader.InFlightCount).IsEqualTo(0);
    }

    [Test]
    public async Task AfterDispose_RequestIsIgnored()
    {
        var lister = new CountingLister();
        var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        loader.Dispose();

        loader.Request(PanelId, DirA, PointedAt(DirA));
        await Settled(loader);

        await Assert.That(lister.Calls).IsEqualTo(0)
            .Because("a renderer shutting down must not be able to start new walks");
    }

    [Test]
    public async Task CancelPanelLoad_StopsTheWalk_AndKeepsTheSettledListing()
    {
        // The store entry is a true statement about that directory; cancelling a
        // refresh in progress must not throw it away.
        var lister = new CountingLister { Hangs = true };
        using var loader = new FileTreeLoader(lister, NullLogger<FileTreeLoader>.Instance);
        var store = PointedAt(DirA);
        store.Dispatch(new AppMsg.SetFileTreeLoaded(
            PanelId,
            DirA,
            [new FileTreeEntry("kept", DirA + "/kept", false, false)]));

        loader.Request(PanelId, DirA, store); // no-op: already settled
        await Settled(loader);
        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirA).Entries.Length).IsEqualTo(1);

        loader.CancelPanelLoad(PanelId);
        await Assert.That(store.State.Ui.FileTreeFor(PanelId, DirA).Entries.Length).IsEqualTo(1);
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    /// <summary>A store whose panel is pointed at <paramref name="dir" />.</summary>
    private static UiStore PointedAt(string dir)
    {
        var store = new UiStore();
        store.Dispatch(new AppMsg.SetPanelDirectory(PanelId, dir));
        return store;
    }

    private static DirectoryEntry Entry(string name) => new(name, "/x/" + name, false, false);

    /// <summary>
    ///     Waits until nothing is in flight. Polls rather than sleeping a fixed
    ///     amount, so the suite is neither flaky on a loaded machine nor slow on
    ///     an idle one.
    /// </summary>
    private static async Task Settled(FileTreeLoader loader)
    {
        for (int i = 0; i < 500 && loader.InFlightCount > 0; i++)
        {
            await Task.Delay(10);
        }
    }

    private class CountingLister : IDirectoryLister
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? Failure { get; init; }

        public Exception? Throws { get; init; }

        public bool Hangs { get; init; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets a <see cref="Hangs" /> walk finish, so the test does not leave one running.</summary>
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public async Task<Result<DirectoryListing>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Token = cancellationToken;
            Started.TrySetResult(true);

            if (Hangs)
            {
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Throws is not null)
            {
                throw Throws;
            }

            return Failure is null
                ? Result.Success(new DirectoryListing(directory, [Entry("one")]))
                : Result.Failure<DirectoryListing>(Failure);
        }
    }

    /// <summary>
    ///     A lister that keeps the token of each directory separately, so a test
    ///     can assert on panel A's walk after panel B has started its own. A
    ///     single shared <c>Token</c> field would answer for whichever ran last,
    ///     which is exactly the question at issue.
    /// </summary>
    private sealed class PerDirectoryLister : IDirectoryLister
    {
        private readonly Dictionary<string, CancellationToken> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<bool>> _started = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Hangs { get; init; }

        public async Task StartedFor(string directory)
        {
            TaskCompletionSource<bool> signal;
            lock (_started)
            {
                if (!_started.TryGetValue(directory, out signal!))
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[directory] = signal;
                }
            }

            await signal.Task;
        }

        public CancellationToken TokenFor(string directory)
        {
            lock (_started)
            {
                return _tokens[directory];
            }
        }

        public void Release() => _release.TrySetResult(true);

        public async Task<Result<DirectoryListing>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> signal;
            lock (_started)
            {
                _tokens[directory] = cancellationToken;
                if (!_started.TryGetValue(directory, out signal!))
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[directory] = signal;
                }
            }

            signal.TrySetResult(true);

            if (Hangs)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return Result.Success(new DirectoryListing(directory, [Entry("one")]));
        }
    }

    /// <summary>A lister that stalls every walk until told to answer.</summary>
    private sealed class GatedLister(Task gate) : IDirectoryLister
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<DirectoryListing>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new DirectoryListing(directory, [Entry("one")]));
        }
    }

    /// <summary>
    ///     A lister that ignores cancellation entirely and answers only when told
    ///     to, always under the directory it was asked for.
    /// </summary>
    /// <remarks>
    ///     Models the misbehaving implementation the guards exist for. A
    ///     cooperative lister cannot produce a stale result — it observes the token
    ///     and unwinds — so every test about "what happens when a walk the user
    ///     abandoned answers anyway" needs this one instead. It is a test double
    ///     for a broken port, not a port.
    /// </remarks>
    private sealed class StubbornLister : IDirectoryLister
    {
        private readonly Dictionary<string, TaskCompletionSource<bool>> _started = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartedFor(string directory)
        {
            TaskCompletionSource<bool> signal;
            lock (_started)
            {
                if (!_started.TryGetValue(directory, out signal!))
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[directory] = signal;
                }
            }

            await signal.Task;
        }

        /// <summary>Lets every stalled walk answer at once, late.</summary>
        public void Release() => _release.TrySetResult(true);

        public async Task<Result<DirectoryListing>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> signal;
            lock (_started)
            {
                if (!_started.TryGetValue(directory, out signal!))
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[directory] = signal;
                }
            }

            signal.TrySetResult(true);

            // No WaitAsync(cancellationToken): that is the entire point.
            await _release.Task.ConfigureAwait(false);

            return Result.Success(new DirectoryListing(directory, [Entry(directory[^1..])]));
        }
    }

}
