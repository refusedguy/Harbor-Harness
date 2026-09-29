using System.Collections.Immutable;
using System.Reflection;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Scheduling + allocation budget for the store→widget projection (issue #466).
/// <para>
///     The projection used to run on the <see cref="UiStore" /> notification
///     thread, i.e. once per <c>TextDeltaEvent</c>. Each run re-did the whole
///     job — 7 status setters, 4 chat setters, a full
///     <c>PromptBuffer.SnapshotText()</c> of the draft, a linear pass over the
///     session list for the quick-switch slots, and a sidebar projection that
///     re-scanned <c>Chat.Sessions</c> and re-copied it into a fresh array —
///     for state a text delta does not change. With 200 sessions that array
///     copy alone is ~1.6 KB per token; a 4000-token answer paid it 4000
///     times.
/// </para>
/// <para>
///     Now the notification only parks the newest state and the frame tick
///     (<see cref="CellForgeTuiRenderer.PumpProjection" />) applies it once;
///     inside the projection each slice is skipped when its own inputs are
///     reference-identical, and the sidebar goes through
///     <see cref="SideBarProjectionCache" />. Both halves are measured here
///     against the SAME production code, so the collapsed amount is a real
///     before/after rather than a comment.
/// </para>
/// <para>
///     <b>Not vacuous:</b>
///     <see cref="LegacyProjection_StillCopiesSessionsPerDelta" /> keeps the
///     pre-#466 projection body verbatim and asserts it really does allocate
///     the session copy per delta, so the collapse cannot come from measuring
///     nothing.
/// </para>
/// <para>
///     <see cref="NotInParallelAttribute" />: same reason as
///     <c>SseWireDecodeAllocationTests</c> / <c>AgentLoopAllocationTests</c> —
///     the counters are thread-scoped and neighbour tests move them.
///     Min-of-3 rounds, linux-only.
/// </para>
/// </summary>
[NotInParallel("alloc-tripwire")]
public class ProjectionCoalescingAllocationTests
{
    /// <summary>Sessions in the store — the copy the #466 cache removes.</summary>
    private const int Sessions = 200;

    /// <summary>Draft length — the buffer the pre-#466 path materialised per delta.</summary>
    private const int DraftChars = 128;

    private const int Deltas = 400;

    /// <summary>
    ///     Bytes of the 200-element <c>ImmutableArray</c>→<c>SessionInfo[]</c>
    ///     copy (1600 B of references + array header), rounded down. The
    ///     per-delta collapse gate is anchored on it so the number is derived
    ///     from the shape the audit describes, not from a tuning pass.
    /// </summary>
    private const long SessionCopyFloor = Sessions * 8L;

    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    private static AssistantMessage Partial() => AssistantMessage.Empty("s1", "test-model");

    private static ImmutableArray<SessionInfo> SessionList()
    {
        var builder = ImmutableArray.CreateBuilder<SessionInfo>(Sessions);
        for (int i = 0; i < Sessions; i++)
        {
            builder.Add(new SessionInfo(
                SessionId.Create($"session-{i}"), $"session title {i}", Epoch, Epoch, "active"));
        }

        return builder.ToImmutable();
    }

    private static UiStore PrivateStore(CellForgeTuiRenderer renderer) =>
        (UiStore)typeof(CellForgeTuiRenderer)
            .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;

    /// <summary>
    ///     A renderer mid-stream: 200 sessions, a non-empty draft, an open
    ///     message, and a live sidebar — the exact shape the audit measures.
    ///     The VMs are the ctor defaults (fresh) so the base
    ///     <c>RenderAsync</c> fan-out cannot double-apply the deltas; these
    ///     tests drive the store directly and let the projection be the only
    ///     writer of the widgets.
    /// </summary>
    private static async Task<CellForgeTuiRenderer> CreateAsync()
    {
        var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance, new RecordingBackend());
        var init = await renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();

        renderer.Screen = ChatScreen.Build(
            new ComposerController(),
            new StatusViewModel(),
            includeSidebar: true);
        await Assert.That(renderer.Screen!.Sidebar).IsNotNull();

        var store = PrivateStore(renderer);
        store.Dispatch(new ChatAppMsg.ConfigureRuntime("m-1", "prov-1", "code"));
        store.Dispatch(new ChatAppMsg.SyncSessions(SessionList(), SessionId.Create("session-7")));
        store.Dispatch(new AppMsg.InputText(new string('d', DraftChars)));
        store.Dispatch(new ChatAppMsg.Agent(new AgentStartEvent("s1", [])));
        store.Dispatch(new ChatAppMsg.Agent(new MessageStartEvent(Partial())));
        _ = renderer.PumpProjection();
        await Assert.That(renderer.PromptBuffer.Length).IsEqualTo(DraftChars);
        return renderer;
    }

    private static readonly ChatAppMsg.Agent[] DeltaBurst = BuildBurst();

    private static ChatAppMsg.Agent[] BuildBurst()
    {
        var partial = Partial();
        var messages = new ChatAppMsg.Agent[Deltas];
        for (int i = 0; i < Deltas; i++)
        {
            // Hoisted out of the measured region on purpose: the gate is about
            // what the notification costs, not what constructing an event costs.
            messages[i] = new ChatAppMsg.Agent(
                new MessageUpdateEvent(new TextDeltaEvent("t1", "token"), partial));
        }

        return messages;
    }

    /// <summary>
    ///     Bytes per delta for the live renderer with the projection drained on
    ///     the pre-#466 cadence (once per store notification, which is what the
    ///     inline projection did) — so the arm isolates the projection body
    ///     from the scheduling change, which
    ///     <see cref="FrameCoalescing_NNotifications_OneProjection" /> pins
    ///     deterministically.
    /// </summary>
    private static async Task<long> BestPerDeltaAsync()
    {
        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            // Fresh renderer per round: ProjectionCount is cumulative per
            // instance and the previous round's grown buffers would make this
            // one cheaper for free.
            var renderer = await CreateAsync();
            var store = PrivateStore(renderer);
            foreach (var warm in DeltaBurst) store.Dispatch(warm);
            _ = renderer.PumpProjection();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var msg in DeltaBurst)
            {
                store.Dispatch(msg);
                _ = renderer.PumpProjection();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            renderer.Dispose();

            long perDelta = allocated / Deltas;
            if (perDelta < best)
            {
                best = perDelta;
            }
        }

        return best;
    }

    [Test]
    public async Task PerDeltaProjection_CollapsedAgainstLegacyChain()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies across OS runtimes.

        long legacy = BestLegacyPerDelta();
        long current = await BestPerDeltaAsync();

        Console.WriteLine(
            $"proj-alloc: per-delta projection = {legacy} B before #466, {current} B after (min of 3)");

        // Both arms drive the same store with the same messages, so the
        // reducer + event + notification cost cancels out and what is left is
        // the projection. The pre-#466 body copied the 200-session list into a
        // fresh array on every notification; the #466 body answers from
        // SideBarProjectionCache and skips every slice whose inputs are
        // reference-identical. The collapse must therefore be at least one
        // full session-list copy, which is derived from the shape the audit
        // describes rather than from a tuning pass.
        await Assert.That(legacy - current).IsGreaterThanOrEqualTo(SessionCopyFloor);
    }

    [Test]
    public async Task LegacyProjection_StillCopiesSessionsPerDelta()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only (see above).

        long best = BestLegacyPerDelta();

        Console.WriteLine($"proj-alloc: legacy per-delta projection = {best} B (min of 3)");
        // One 200-element array copy (1600 B) plus the draft copy, on top of
        // the store's own cost. The gate above demands a collapse of at least
        // SessionCopyFloor, so this proves the thing being removed is real and
        // that large — the collapse cannot come from measuring nothing.
        await Assert.That(best).IsGreaterThan(SessionCopyFloor);
    }

    /// <summary>
    ///     The pre-#466 projection body, verbatim: the sidebar scan + array
    ///     copy, the draft <c>SnapshotText()</c>, the eleven setters, and the
    ///     LINQ walk over the notification list — run on the same store, with
    ///     the same messages, as the live arm above. That is what the collapse
    ///     is measured against.
    /// </summary>
    private static long BestLegacyPerDelta()
    {
        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            var store = new UiStore();
            var status = new StatusBarViewModel();
            var chat = new ChatHistoryViewModel();
            var buffer = new PromptBuffer();
            var partial = Partial();
            _ = buffer.InsertText(new string('d', DraftChars));

            // Seed before the handler is attached: the legacy code projected on
            // every notification, so seeding through it would only add warm-up.
            store.Dispatch(new ChatAppMsg.ConfigureRuntime("m-1", "prov-1", "code"));
            store.Dispatch(new ChatAppMsg.SyncSessions(SessionList(), SessionId.Create("session-7")));
            store.Dispatch(new AppMsg.InputText(new string('d', DraftChars)));
            store.Dispatch(new ChatAppMsg.Agent(new AgentStartEvent("s1", [])));
            store.Dispatch(new ChatAppMsg.Agent(new MessageStartEvent(partial)));

            void LegacyProject(UiState state)
            {
                if (!string.IsNullOrEmpty(state.Chat.Status)) status.Status = state.Chat.Status;
                if (!string.IsNullOrEmpty(state.Chat.Model)) status.Model = state.Chat.Model;
                if (!string.IsNullOrEmpty(state.Chat.Provider)) status.Provider = state.Chat.Provider;
                if (!string.IsNullOrEmpty(state.Chat.AgentName)) status.Agent = state.Chat.AgentName;
                status.TokensIn = (int)Math.Min(state.Chat.Cost.TokensIn, int.MaxValue);
                status.TokensOut = (int)Math.Min(state.Chat.Cost.TokensOut, int.MaxValue);
                status.Cost = state.Chat.Cost.CostUsd;
                chat.IsStreaming = state.Chat.IsStreaming;
                chat.StreamingText = state.Chat.Active.TextBuffer;
                chat.ThinkingText = state.Chat.Active.ThinkBuffer;
                chat.IsThinking = state.Chat.Active.ThinkBuffer.Length != 0;

                // The composer sync, verbatim: SnapshotText() per projection.
                string text = state.Ui.Input.Text ?? string.Empty;
                if (buffer.SnapshotText() != text)
                {
                    buffer.Clear();
                    if (text.Length != 0)
                        _ = buffer.InsertText(text);
                }

                _ = SideBarView.ProjectFromStore(state); // 200-scan + fresh array
            }

            // The pre-#466 UiStore.Notify fan-out, verbatim — a LINQ Cast over
            // the invocation list — wrapped around the projection. It walks
            // the PROJECTION's list, not the store's, so the single store
            // subscriber cannot re-enter itself.
            EventHandler<UiStateChangedEventArgs> project = (_, e) => LegacyProject(e.State);
            store.Changed += (_, e) =>
            {
                foreach (EventHandler<UiStateChangedEventArgs> single
                         in project.GetInvocationList().Cast<EventHandler<UiStateChangedEventArgs>>())
                {
                    single(store, e);
                }
            };

            foreach (var warm in DeltaBurst) store.Dispatch(warm);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var msg in DeltaBurst) store.Dispatch(msg);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            long perDelta = allocated / Deltas;
            if (perDelta < best)
            {
                best = perDelta;
            }
        }

        return best;
    }

    [Test]
    public async Task FrameCoalescing_NNotifications_OneProjection()
    {
        var renderer = await CreateAsync();
        using (renderer)
        {
            var store = PrivateStore(renderer);
            long before = renderer.ProjectionCount;

            foreach (var msg in DeltaBurst)
            {
                store.Dispatch(msg);
            }

            await Assert.That(renderer.HasPendingProjection).IsTrue();
            await Assert.That(renderer.PumpProjection()).IsTrue();
            await Assert.That(renderer.ProjectionCount - before).IsEqualTo(1);
            await Assert.That(renderer.HasPendingProjection).IsFalse();
            await Assert.That(renderer.PumpProjection()).IsFalse();
        }
    }

    [Test]
    public async Task RenderAsync_Drains_PendingProjection()
    {
        var renderer = await CreateAsync();
        using (renderer)
        {
            var partial = Partial();
            long before = renderer.ProjectionCount;
            await renderer.RenderAsync(new MessageUpdateEvent(
                new TextDeltaEvent("t1", "frame"), partial));
            await Assert.That(renderer.HasPendingProjection).IsFalse();
            await Assert.That(renderer.ProjectionCount - before).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Dispose_Drains_LastParkedProjection()
    {
        var renderer = await CreateAsync();
        var store = PrivateStore(renderer);
        long before = renderer.ProjectionCount;
        store.Dispatch(new ChatAppMsg.Agent(new MessageUpdateEvent(
            new TextDeltaEvent("t1", "last"), Partial())));
        await Assert.That(renderer.HasPendingProjection).IsTrue();

        renderer.Dispose();
        await Assert.That(renderer.HasPendingProjection).IsFalse();
        await Assert.That(renderer.ProjectionCount - before).IsEqualTo(1);
    }

    [Test]
    public async Task FrameCoalescing_ProjectionIsLossless()
    {
        // The invariant the coalescing must not break: for the same final
        // state, coalescing N notifications into one pump produces exactly the
        // widgets a per-notification projection does. Two renderers, identical
        // event streams, opposite scheduling.
        var eager = await CreateAsync();
        var coalesced = await CreateAsync();
        using (eager)
        using (coalesced)
        {
            await Assert.That(SameWidgets(eager, coalesced)).IsTrue();

            var partial = Partial();
            AgentEvent[] stream =
            [
                new MessageUpdateEvent(new TextDeltaEvent("t1", "a"), partial),
                new MessageUpdateEvent(new TextDeltaEvent("t1", "b"), partial),
                new MessageUpdateEvent(new TextDeltaEvent("t1", "c"), partial),
                new MessageUpdateEvent(new ThinkingDeltaEvent("h1", "hmm"), partial),
                new MessageUpdateEvent(new StepFinishEvent(0, "stop", new Usage(1000, 500)), partial),
                new MessageUpdateEvent(new TextDeltaEvent("t1", "d"), partial),
                new MessageUpdateEvent(new TextDeltaEvent("t1", "e"), partial),
            ];

            foreach (var evt in stream)
            {
                // Eager: drain after every notification (the pre-#466 cadence).
                _ = PrivateStore(eager).Dispatch(new ChatAppMsg.Agent(evt));
                _ = eager.PumpProjection();
                // Coalesced: dispatch only — the frame tick comes at the end.
                _ = PrivateStore(coalesced).Dispatch(new ChatAppMsg.Agent(evt));
            }

            _ = coalesced.PumpProjection();
            await Assert.That(SameWidgets(eager, coalesced)).IsTrue();

            // ...and the sidebar, which is where the cache could have hidden a
            // stale field. Compared field-wise because SideBarState.Sessions is
            // an array (record equality would compare it by reference, and the
            // two renderers each projected their own copy).
            var eagerSide = eager.Screen!.Sidebar!.State;
            var coalescedSide = coalesced.Screen!.Sidebar!.State;
            await Assert.That(SameSideBar(eagerSide, coalescedSide)).IsTrue();
            await Assert.That(coalescedSide.TokensIn).IsEqualTo(1000);
            await Assert.That(coalescedSide.TokensOut).IsEqualTo(500);
            await Assert.That(coalescedSide.CostUsd).IsGreaterThan(0d);
            await Assert.That(coalescedSide.Sessions!.Count).IsEqualTo(Sessions);
            await Assert.That(coalescedSide.SessionTitle).IsEqualTo("session title 7");
        }
    }

    private static bool SameSideBar(SideBarState a, SideBarState b) =>
        a.SessionTitle == b.SessionTitle
        && a.SessionId == b.SessionId
        && a.Model == b.Model
        && a.TokensIn == b.TokensIn
        && a.TokensOut == b.TokensOut
        && a.CostUsd == b.CostUsd
        && a.ActiveSessionId == b.ActiveSessionId
        && SameSessions(a.Sessions, b.Sessions);

    internal static bool SameSessions(IReadOnlyList<SessionInfo>? a, IReadOnlyList<SessionInfo>? b)
    {
        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameWidgets(CellForgeTuiRenderer a, CellForgeTuiRenderer b)
    {
        var aStatus = a.ViewModels.Get<StatusBarViewModel>("status-bar")!;
        var bStatus = b.ViewModels.Get<StatusBarViewModel>("status-bar")!;
        var aChat = a.ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        var bChat = b.ViewModels.Get<ChatHistoryViewModel>("chat-history")!;

        return aStatus.Status == bStatus.Status
               && aStatus.Model == bStatus.Model
               && aStatus.Provider == bStatus.Provider
               && aStatus.Agent == bStatus.Agent
               && aStatus.TokensIn == bStatus.TokensIn
               && aStatus.TokensOut == bStatus.TokensOut
               && aStatus.Cost == bStatus.Cost
               && aChat.IsStreaming == bChat.IsStreaming
               && aChat.StreamingText == bChat.StreamingText
               && aChat.ThinkingText == bChat.ThinkingText
               && aChat.IsThinking == bChat.IsThinking
               && a.PromptBuffer.SnapshotText() == b.PromptBuffer.SnapshotText()
               && a.PromptBuffer.Cursor == b.PromptBuffer.Cursor
               && a.SessionsSnapshot.AsSpan().SequenceEqual(b.SessionsSnapshot.AsSpan())
               && a.ActiveSessionIdSnapshot == b.ActiveSessionIdSnapshot
               && a.SessionsLoading == b.SessionsLoading;
    }
}

/// <summary>
/// Sidebar projection memo (issue #466): the scan + array copy must be gone
/// when nothing the sidebar reads has moved, and must still happen when it has.
/// </summary>
public class SideBarProjectionCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ImmutableArray<SessionInfo> Many(int n)
    {
        var builder = ImmutableArray.CreateBuilder<SessionInfo>(n);
        for (int i = 0; i < n; i++)
        {
            builder.Add(new SessionInfo(SessionId.Create($"s{i}"), $"t{i}", Now, Now, "active"));
        }

        return builder.ToImmutable();
    }

    private static UiState WithSessions(ImmutableArray<SessionInfo> sessions, SessionId? active) =>
        new()
        {
            Chat = ChatDomainState.Empty with
            {
                Sessions = sessions,
                ActiveSessionId = active
            }
        };

    [Test]
    public async Task Cached_Hit_ReturnsSameInstance()
    {
        var cache = new SideBarProjectionCache();
        var sessions = Many(200);
        var first = SideBarView.Project(WithSessions(sessions, SessionId.Create("s7")), cache);
        // A new UiState with the SAME session inputs — what a text delta
        // produces — must not re-scan or re-copy.
        var second = SideBarView.Project(WithSessions(sessions, SessionId.Create("s7")), cache);

        await Assert.That(ReferenceEquals(second, first)).IsTrue();
        await Assert.That(cache.MissCount).IsEqualTo(1);
        await Assert.That(first.SessionTitle).IsEqualTo("t7");
        await Assert.That(first.Sessions!.Count).IsEqualTo(200);
    }

    [Test]
    public async Task CacheHit_KeepsSessionsIdentity()
    {
        var cache = new SideBarProjectionCache();
        var sessions = Many(200);
        var first = SideBarView.Project(WithSessions(sessions, SessionId.Create("s7")), cache);
        var second = SideBarView.Project(WithSessions(sessions, SessionId.Create("s7")), cache);

        // A session sync hands out a FRESH SessionId for the same session, so
        // the fingerprint has to be content-exact, not reference-exact — the
        // two calls above built independent instances on purpose.
        // The memo must also not hand the sidebar a differently-shaped
        // snapshot: the session array is the very thing the cache keeps.
        await Assert.That(ReferenceEquals(second, first)).IsTrue();
        await Assert.That(ReferenceEquals(first.Sessions, second.Sessions)).IsTrue();
        await Assert.That(second.SessionTitle).IsEqualTo(first.SessionTitle);
        await Assert.That(second.ActiveSessionId).IsEqualTo(first.ActiveSessionId);
    }

    [Test]
    public async Task ChangedActiveSession_Reprojects()
    {
        var cache = new SideBarProjectionCache();
        var sessions = Many(200);
        var first = SideBarView.Project(WithSessions(sessions, SessionId.Create("s7")), cache);
        var second = SideBarView.Project(WithSessions(sessions, SessionId.Create("s8")), cache);

        await Assert.That(ReferenceEquals(second, first)).IsFalse();
        await Assert.That(second.SessionTitle).IsEqualTo("t8");
        await Assert.That(cache.MissCount).IsEqualTo(2);
    }

    [Test]
    public async Task ChangedSessionList_Reprojects()
    {
        var cache = new SideBarProjectionCache();
        var a = Many(200);
        var b = Many(201);
        var first = SideBarView.Project(WithSessions(a, SessionId.Create("s7")), cache);
        var second = SideBarView.Project(WithSessions(b, SessionId.Create("s7")), cache);

        await Assert.That(ReferenceEquals(second, first)).IsFalse();
        await Assert.That(second.Sessions!.Count).IsEqualTo(201);
    }

    [Test]
    public async Task ChangedCost_Reprojects()
    {
        // The audit suggested keying the cache on (Sessions, ActiveSessionId).
        // That key is WRONG: the sidebar also carries the token/cost line,
        // which moves on the core's SessionStatsEvent while both session inputs
        // stand still. Pinned here so the narrower key can never come back.
        var cache = new SideBarProjectionCache();
        var sessions = Many(200);
        var active = SessionId.Create("s7");
        var first = SideBarView.Project(WithSessions(sessions, active), cache);

        var withCost = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Sessions = sessions,
                ActiveSessionId = active,
                Cost = new CostSnapshot(1000, 500, 0.0105m)
            }
        };
        var second = SideBarView.Project(withCost, cache);

        await Assert.That(ReferenceEquals(second, first)).IsFalse();
        await Assert.That(second.TokensIn).IsEqualTo(1000);
        await Assert.That(second.TokensOut).IsEqualTo(500);
        await Assert.That(second.CostUsd).IsEqualTo(0.0105d);
    }

    [Test]
    public async Task Uncached_Overload_MatchesCached_Exactly()
    {
        var sessions = Many(200);
        var state = WithSessions(sessions, SessionId.Create("s7"));
        var uncached = SideBarView.ProjectFromStore(state);
        var cached = SideBarView.Project(state, new SideBarProjectionCache());

        // Field-wise, not record equality: Sessions is an array, so record
        // equality would compare it by reference and the two arms necessarily
        // hold different (equal-content) copies.
        await Assert.That(cached.SessionTitle).IsEqualTo(uncached.SessionTitle);
        await Assert.That(cached.SessionId).IsEqualTo(uncached.SessionId);
        await Assert.That(cached.Model).IsEqualTo(uncached.Model);
        await Assert.That(cached.TokensIn).IsEqualTo(uncached.TokensIn);
        await Assert.That(cached.TokensOut).IsEqualTo(uncached.TokensOut);
        await Assert.That(cached.CostUsd).IsEqualTo(uncached.CostUsd);
        await Assert.That(cached.ActiveSessionId).IsEqualTo(uncached.ActiveSessionId);
        await Assert.That(ProjectionCoalescingAllocationTests.SameSessions(
            cached.Sessions, uncached.Sessions)).IsTrue();
    }
}
