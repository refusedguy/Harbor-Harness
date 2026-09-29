using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel;
using TUnit.Assertions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     #490 — <c>Freeze()</c> contract for <see cref="ViewRegistry" /> and
///     <see cref="ViewModelRegistry" />: a post-freeze <c>Register</c> /
///     <c>Unregister</c> must be <b>accepted</b> and must drop the frozen
///     snapshot — never silently drop the late registration. Freezing a
///     registry too early is the one way this could regress, so the
///     accept-and-refreeze behaviour is pinned here.
///     <para>
///         The freeze state is observed through the public API only: the frozen
///         path returns one shared array (same reference on every call), the
///         unfrozen path builds a fresh <c>List</c> per call. A reference
///         comparison therefore distinguishes "frozen" from "unfrozen" without
///         exposing new state on the registry.
///     </para>
/// </summary>
public class ViewRegistryFreezeContractTests
{
    private const int ViewCount = 4;

    private static ViewRegistry NewFrozenViewRegistry()
    {
        var registry = new ViewRegistry();
        for (int i = 0; i < ViewCount; i++)
        {
            registry.Register(new ProbeView($"v{i}", TuiViewPlacement.ChatHistory));
        }

        registry.Freeze();
        return registry;
    }

    private static ViewModelRegistry NewFrozenViewModelRegistry()
    {
        var registry = new ViewModelRegistry();
        for (int i = 0; i < ViewCount; i++)
        {
            registry.Register(new ProbeViewModel($"vm{i}"));
        }

        registry.Freeze();
        return registry;
    }

    /// <summary>The frozen path hands back one shared array — same reference each call.</summary>
    private static async Task AssertSharedSnapshot<T>(IReadOnlyList<T> first, IReadOnlyList<T> second)
        => await Assert.That(ReferenceEquals(first, second)).IsTrue();

    /// <summary>The unfrozen path builds a fresh list — never the same reference twice.</summary>
    private static async Task AssertFreshSnapshot<T>(IReadOnlyList<T> first, IReadOnlyList<T> second)
        => await Assert.That(ReferenceEquals(first, second)).IsFalse();

    [Test]
    public async Task ViewRegistry_Frozen_GetAll_ReturnsSharedSnapshot()
    {
        var registry = NewFrozenViewRegistry();
        await AssertSharedSnapshot(registry.GetAll(), registry.GetAll());
    }

    [Test]
    public async Task ViewRegistry_Unfrozen_GetAll_ReturnsFreshList()
    {
        // Undo the freeze the way a late mutation would, then confirm the
        // unfrozen path is genuinely a different (allocating) list per call.
        var registry = NewFrozenViewRegistry();
        registry.Register(new ProbeView("v-extra", TuiViewPlacement.ChatHistory));
        var first = registry.GetAll();
        var second = registry.GetAll();
        await AssertFreshSnapshot(first, second);
        await Assert.That(first.Count).IsEqualTo(second.Count);
    }

    [Test]
    public async Task ViewRegistry_Frozen_GetAll_MatchesUnfrozenContent()
    {
        var frozen = NewFrozenViewRegistry();
        var live = new ViewRegistry();
        for (int i = 0; i < ViewCount; i++)
        {
            live.Register(new ProbeView($"v{i}", TuiViewPlacement.ChatHistory));
        }

        var frozenAll = frozen.GetAll();
        var liveAll = live.GetAll();
        await Assert.That(frozenAll.Count).IsEqualTo(liveAll.Count);
        for (int i = 0; i < liveAll.Count; i++)
        {
            await Assert.That(frozenAll[i].Id).IsEqualTo(liveAll[i].Id);
        }
    }

    [Test]
    public async Task ViewRegistry_Frozen_GetByPlacement_PreservesRegistrationOrder()
    {
        var registry = new ViewRegistry();
        registry.Register(new ProbeView("a", TuiViewPlacement.ChatHistory));
        registry.Register(new ProbeView("b", TuiViewPlacement.StatusBar));
        registry.Register(new ProbeView("c", TuiViewPlacement.ChatHistory));
        registry.Freeze();

        var chat = registry.GetByPlacement(TuiViewPlacement.ChatHistory);
        await Assert.That(chat.Count).IsEqualTo(2);
        await Assert.That(chat[0].Id).IsEqualTo("a");
        await Assert.That(chat[1].Id).IsEqualTo("c");

        var status = registry.GetByPlacement(TuiViewPlacement.StatusBar);
        await Assert.That(status.Count).IsEqualTo(1);
        await Assert.That(status[0].Id).IsEqualTo("b");

        // A placement with nothing registered: empty, never null.
        await Assert.That(registry.GetByPlacement(TuiViewPlacement.Footer).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ViewRegistry_Frozen_GetByPlacement_ReturnsSharedSnapshot()
    {
        var registry = NewFrozenViewRegistry();
        var first = registry.GetByPlacement(TuiViewPlacement.ChatHistory);
        var second = registry.GetByPlacement(TuiViewPlacement.ChatHistory);
        await AssertSharedSnapshot(first, second);
    }

    [Test]
    public async Task ViewRegistry_Frozen_EmptyRegistry_StillServesEmptySnapshots()
    {
        var registry = new ViewRegistry();
        registry.Freeze();
        await Assert.That(registry.GetAll().Count).IsEqualTo(0);
        await Assert.That(registry.GetByPlacement(TuiViewPlacement.Overlay).Count).IsEqualTo(0);
        await AssertSharedSnapshot(registry.GetAll(), registry.GetAll());
    }

    // ---- Late registration is ACCEPTED, and invalidates the snapshot ----

    [Test]
    public async Task ViewRegistry_RegisterAfterFreeze_IsAccepted_AndVisibleEverywhere()
    {
        var registry = NewFrozenViewRegistry();
        var late = new ProbeView("late", TuiViewPlacement.Overlay);

        registry.Register(late);

        await Assert.That(registry.Get("late")).IsSameReferenceAs(late);
        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount + 1);
        var overlay = registry.GetByPlacement(TuiViewPlacement.Overlay);
        await Assert.That(overlay.Count).IsEqualTo(1);
        await Assert.That(overlay[0]).IsSameReferenceAs(late);
    }

    [Test]
    public async Task ViewRegistry_RegisterAfterFreeze_DropsFrozenSnapshot()
    {
        var registry = NewFrozenViewRegistry();
        await AssertSharedSnapshot(registry.GetAll(), registry.GetAll());

        registry.Register(new ProbeView("late", TuiViewPlacement.ChatHistory));

        // No longer the shared frozen array — reads fell back to the live path.
        await AssertFreshSnapshot(registry.GetAll(), registry.GetAll());
    }

    [Test]
    public async Task ViewRegistry_RefreezeAfterLateRegister_RestoresSharedSnapshot()
    {
        var registry = NewFrozenViewRegistry();
        registry.Register(new ProbeView("late", TuiViewPlacement.ChatHistory));
        await AssertFreshSnapshot(registry.GetAll(), registry.GetAll());

        registry.Freeze();

        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount + 1);
        await AssertSharedSnapshot(registry.GetAll(), registry.GetAll());
    }

    [Test]
    public async Task ViewRegistry_ReplaceAfterFreeze_KeepsSingleViewAndPlacementCoherent()
    {
        var registry = NewFrozenViewRegistry();
        var replacement = new ProbeView("v0", TuiViewPlacement.Overlay);

        registry.Register(replacement);

        await Assert.That(registry.Get("v0")).IsSameReferenceAs(replacement);
        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount);

        // The replaced view left its old placement and joined the new one.
        var chat = registry.GetByPlacement(TuiViewPlacement.ChatHistory);
        await Assert.That(chat.Count).IsEqualTo(ViewCount - 1);
        var overlay = registry.GetByPlacement(TuiViewPlacement.Overlay);
        await Assert.That(overlay.Count).IsEqualTo(1);
        await Assert.That(overlay[0]).IsSameReferenceAs(replacement);
    }

    [Test]
    public async Task ViewRegistry_UnregisterAfterFreeze_IsAccepted_AndVisibleEverywhere()
    {
        var registry = NewFrozenViewRegistry();

        await Assert.That(registry.Unregister("v1")).IsTrue();

        await Assert.That(registry.Get("v1")).IsNull();
        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount - 1);
        await Assert.That(registry.GetByPlacement(TuiViewPlacement.ChatHistory).Count).IsEqualTo(ViewCount - 1);

        // A mutation also drops the snapshot — no stale read.
        await AssertFreshSnapshot(registry.GetAll(), registry.GetAll());
    }

    // ---- ViewModelRegistry: same contract, plus the new Freeze() ----

    [Test]
    public async Task ViewModelRegistry_Frozen_GetAll_ReturnsSharedSnapshot()
    {
        var registry = NewFrozenViewModelRegistry();
        await AssertSharedSnapshot(registry.GetAll(), registry.GetAll());
    }

    [Test]
    public async Task ViewModelRegistry_Frozen_Lookups_ServeSnapshot()
    {
        var registry = NewFrozenViewModelRegistry();
        var vm = new ProbeViewModel("vm0");
        await Assert.That(registry.Get("vm0")).IsSameReferenceAs(vm);
        await Assert.That(registry.Get<ProbeViewModel>("vm0")).IsSameReferenceAs(vm);
        await Assert.That(registry.Get("nope")).IsNull();
    }

    [Test]
    public async Task ViewModelRegistry_RegisterAfterFreeze_IsAccepted_AndVisible()
    {
        var registry = NewFrozenViewModelRegistry();
        var late = new ProbeViewModel("late");

        registry.Register(late);

        await Assert.That(registry.Get("late")).IsSameReferenceAs(late);
        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount + 1);
        await AssertFreshSnapshot(registry.GetAll(), registry.GetAll());
    }

    [Test]
    public async Task ViewModelRegistry_RefreezeAfterLateRegister_RestoresSharedSnapshot()
    {
        var registry = NewFrozenViewModelRegistry();
        registry.Register(new ProbeViewModel("late"));
        registry.Freeze();
        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount + 1);
        await AssertSharedSnapshot(registry.GetAll(), registry.GetAll());
    }

    [Test]
    public async Task ViewModelRegistry_UnregisterAfterFreeze_IsAccepted_AndVisible()
    {
        var registry = NewFrozenViewModelRegistry();

        await Assert.That(registry.Unregister("vm0")).IsTrue();

        await Assert.That(registry.Get("vm0")).IsNull();
        await Assert.That(registry.GetAll().Count).IsEqualTo(ViewCount - 1);
        await Assert.That(registry.Unregister("vm0")).IsFalse();
    }

    // ---- Renderer integration: InitializeAsync must freeze BOTH registries ----

    [Test]
    public async Task BaseTuiRenderer_InitializeAsync_FreezesViewsAndViewModels()
    {
        var renderer = new ProbeTuiRenderer();
        var result = await renderer.InitializeAsync();
        await Assert.That(result.IsSuccess).IsTrue();

        // A shared-reference pair means the frozen snapshot is live for both.
        await AssertSharedSnapshot(renderer.Views.GetAll(), renderer.Views.GetAll());
        await AssertSharedSnapshot(renderer.ViewModels.GetAll(), renderer.ViewModels.GetAll());
    }

    [Test]
    public async Task BaseTuiRenderer_LateViewRegistration_IsAccepted_NotSwallowed()
    {
        var renderer = new ProbeTuiRenderer();
        await renderer.InitializeAsync();

        // Registering after InitializeAsync must be accepted, not throw, and not
        // be swallowed by the frozen snapshot.
        var late = new ProbeView("late-view", TuiViewPlacement.ChatHistory);
        renderer.Views.Register(late);

        await Assert.That(renderer.Views.Get("late-view")).IsSameReferenceAs(late);
        await Assert.That(renderer.Views.GetAll().Count).IsGreaterThan(0);
    }

    private sealed class ProbeView(string id, TuiViewPlacement placement) : ITuiView
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = id;

        public TuiViewPlacement Placement { get; } = placement;

        public ITuiViewModel? ViewModel { get; set; }

        public Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) => Task.CompletedTask;

        public Task OnEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class ProbeViewModel(string id) : ITuiViewModel
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = id;

        // Stub: nothing raises PropertyChanged. Explicit accessors (rather than a
        // field-like event) keep the never-used-event diagnostic quiet without a
        // warning suppression.
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ProbeTuiRenderer : BaseTuiRenderer
    {
        public ProbeTuiRenderer() : base(NullLogger.Instance) { }

        public override ITuiRenderContext Context => throw new NotSupportedException();

        public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) => throw new NotSupportedException();

        public override Task<Result> WriteAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();

        public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) => throw new NotSupportedException();

        public override Task<Result> ClearAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>
///     #490 — allocation tripwires for the frozen <c>GetAll()</c> /
///     <c>GetByPlacement()</c> / <c>Get</c> paths. The gate is
///     <b>== 0 bytes per call</b>; the unfrozen (pre-freeze) path is measured
///     in the same run purely to print the honest delta, and is asserted only
///     to be non-zero (so a future "optimisation" that leaves the unfrozen path
///     free is still visible rather than silently changing what we measure).
///     <para>
///         Linux-gated: per-thread GC accounting varies several-fold across OS
///         runtimes and the exact-zero gate is only trustworthy on the runtime
///         CI runs. <see cref="NotInParallelAttribute" /> plus min-of-3: TUnit
///         runs classes in parallel, and a neighbour resuming on this thread
///         (timer, await continuation) would land in the per-thread counter.
///         Serializing the tripwire and taking the best of three rounds discards
///         that noise.
///     </para>
/// </remarks>
[NotInParallel("alloc-tripwire")]
public class ViewRegistryFreezeAllocationTests
{
    private const int Iterations = 10_000;
    private const int ViewCount = 4;

    /// <summary>
    ///     Warm up (JIT tiering) outside the measured region, then report the
    ///     best of three measured rounds.
    /// </summary>
    private static long MeasureBytesPerCall(Action action)
    {
        for (int i = 0; i < 1_000; i++)
        {
            action();
        }

        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                action();
            }

            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
            if (perCall < best)
            {
                best = perCall;
            }
        }

        return best;
    }

    private static ViewRegistry NewViewRegistry()
    {
        var registry = new ViewRegistry();
        for (int i = 0; i < ViewCount; i++)
        {
            registry.Register(new CountingView($"v{i}", TuiViewPlacement.ChatHistory));
        }

        return registry;
    }

    private static ViewModelRegistry NewViewModelRegistry()
    {
        var registry = new ViewModelRegistry();
        for (int i = 0; i < ViewCount; i++)
        {
            registry.Register(new CountingViewModel($"vm{i}"));
        }

        return registry;
    }

    [Test]
    public async Task ViewRegistry_GetAll_Frozen_AllocatesZeroBytes()
    {
        if (!OperatingSystem.IsLinux()) return;

        var live = NewViewRegistry();
        var unfrozen = MeasureBytesPerCall(() => { _ = live.GetAll(); });

        var frozen = NewViewRegistry();
        frozen.Freeze();
        var frozenBytes = MeasureBytesPerCall(() => { _ = frozen.GetAll(); });

        Console.WriteLine($"#490 ViewRegistry.GetAll (n={ViewCount}): unfrozen {unfrozen} B/call -> frozen {frozenBytes} B/call (min of 3 x {Iterations})");
        await Assert.That(unfrozen).IsGreaterThan(0L);
        await Assert.That(frozenBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task ViewRegistry_GetByPlacement_Frozen_AllocatesZeroBytes()
    {
        if (!OperatingSystem.IsLinux()) return;

        var live = NewViewRegistry();
        var unfrozen = MeasureBytesPerCall(() => { _ = live.GetByPlacement(TuiViewPlacement.ChatHistory); });

        var frozen = NewViewRegistry();
        frozen.Freeze();
        var frozenBytes = MeasureBytesPerCall(() => { _ = frozen.GetByPlacement(TuiViewPlacement.ChatHistory); });
        var emptyBytes = MeasureBytesPerCall(() => { _ = frozen.GetByPlacement(TuiViewPlacement.Footer); });

        Console.WriteLine($"#490 ViewRegistry.GetByPlacement (n={ViewCount}): unfrozen {unfrozen} B/call -> frozen {frozenBytes} B/call; frozen empty placement {emptyBytes} B/call (min of 3 x {Iterations})");
        await Assert.That(unfrozen).IsGreaterThan(0L);
        await Assert.That(frozenBytes).IsEqualTo(0L);
        await Assert.That(emptyBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task ViewModelRegistry_GetAll_Frozen_AllocatesZeroBytes()
    {
        if (!OperatingSystem.IsLinux()) return;

        var live = NewViewModelRegistry();
        var unfrozen = MeasureBytesPerCall(() => { _ = live.GetAll(); });

        var frozen = NewViewModelRegistry();
        frozen.Freeze();
        var frozenBytes = MeasureBytesPerCall(() => { _ = frozen.GetAll(); });

        Console.WriteLine($"#490 ViewModelRegistry.GetAll (n={ViewCount}): unfrozen {unfrozen} B/call -> frozen {frozenBytes} B/call (min of 3 x {Iterations})");
        await Assert.That(unfrozen).IsGreaterThan(0L);
        await Assert.That(frozenBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task ViewRegistry_Get_Frozen_AllocatesZeroBytes()
    {
        if (!OperatingSystem.IsLinux()) return;

        var live = NewViewRegistry();
        var unfrozen = MeasureBytesPerCall(() => { _ = live.Get("v0"); });

        var frozen = NewViewRegistry();
        frozen.Freeze();
        var frozenBytes = MeasureBytesPerCall(() => { _ = frozen.Get("v0"); });

        Console.WriteLine($"#490 ViewRegistry.Get: unfrozen {unfrozen} B/call -> frozen {frozenBytes} B/call (min of 3 x {Iterations})");
        await Assert.That(frozenBytes).IsEqualTo(0L);
    }

    private sealed class CountingView(string id, TuiViewPlacement placement) : ITuiView
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = id;

        public TuiViewPlacement Placement { get; } = placement;

        public ITuiViewModel? ViewModel { get; set; }

        public Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class CountingViewModel(string id) : ITuiViewModel
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = id;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;
    }
}
