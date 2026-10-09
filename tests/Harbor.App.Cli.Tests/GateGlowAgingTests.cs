// GateGlowAgingTests.cs — issue #889, the CONSUMER half of #648.
//
// WHAT #648 LEFT OUT
// ------------------
// `PostFxTests` proves the PRODUCER: `VirtualizedChatTimeline.ConsumeGlowRegions`
// reports one region while a pending gate pulses and zero once it is decided.
// That test flaked, was diagnosed as a palette race, and was fixed in #703. It is
// a good test on the wrong half of the pipeline.
//
// The half a person actually sees is the CONSUMER: `ReplLifecycle.ArmGateGlow`,
// which turns the ledger into `ScreenSession.Effects.Set(i, …)` and then DRAINS
// the slots the ledger stopped feeding. Before this file, `ArmGateGlow`,
// `_glowScratch` and `_glowEffects` appeared nowhere under `tests/`. Delete the
// drain and `PostFxTests` stayed green while an answered gate kept pulsing.
//
// THE EXPERIMENT, AND WHY THE BACK BUFFER IS PINNED ACROSS BOTH FRAMES
// ---------------------------------------------------------------------
// `PostFxPipeline` holds a fixed slot table and nothing resets it between
// frames; `Flush` runs every armed effect whose region contains the cell. So a
// slot that is armed and not drained keeps transforming forever. WHETHER THAT IS
// VISIBLE depends on the pulse phase, and that is the trap:
//
//   * `GlowEffect.Transform` returns the cell untouched when `_intensity <= 0`,
//     and the ledger KEEPS publishing the region at the pulse trough precisely so
//     the glow can be cleared on screen. So if the last armed frame before the
//     decision happened to be a trough, the stale slot holds intensity 0 and a
//     missing drain is INVISIBLE.
//
// A hand-run "answer the gate, look, see no glow" therefore passes on a broken
// build about half the time, and the flakiness is a property of the defect rather
// than of the test. The only way to see it is to hold the raw back-buffer content
// identical across both frames and vary ONE thing: whether the consumer drained.
// That is what these two frames do — same cell, same colour, same region; armed
// slot versus drained slot.
//
// WHY THE FRAME IS BUILT BY HAND
// ------------------------------
// `ArmGateGlow` is `internal` (one word, in a class that already carries
// `InternalsVisibleTo` seams — `BuildStatusSnapshot`, `ShouldKeepHeartbeat`,
// `Utf8`) precisely so it can be called here. The alternative was driving
// `LoopAsync`, which reaches the same method through `RenderFrameGatedAsync` —
// gated by a 60 fps `FrameTicker`, a version-equality short-circuit and an
// `AnimationClock`. That is a wall-clock test in the one domain that has already
// produced a flake (#648), and it would buy nothing: `ArmGateGlow` touches no
// clock, no timer and no async.
//
// What is reproduced from `RenderFrameAsync` is only the part that matters and is
// in order: solve, prepare, BeginFrameScope (which pins the palette for the whole
// frame — the #458/#648 fix, and the reason nothing here needs a manual pin),
// PaintAll, ArmGateGlow, flush.
//
// ONE THING THE FIRST CI RUN OF THIS FILE GOT WRONG, since it will bite the next
// person who extends it: the pulse intensity is NOT 1.0 at the peak. `PanelFx`'s
// pulse is a sine, so the ledger publishes something comfortably below full
// strength, and `GlowEffect.Transform` blends by `intensity * PeakStrength`.
// `PostFxTests.HotSgr` can hard-code `PeakStrength` because every test that uses
// it sets `intensity: 1.0` on a synthetic effect. Driving the real ledger and
// assuming 1.0 produces an expected byte string brighter than the renderer can
// ever emit, and the assertion then fails on a build whose glow is perfect. The
// intensity is read out of `_glowScratch` and carried into the expectation.

using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Application.Permissions;
using Harbor.Registries.Events;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Drives the real <c>ReplLifecycle.ArmGateGlow</c> against a real
/// <see cref="VirtualizedChatTimeline" />, a real <see cref="ApprovalGateView" />, a real
/// <see cref="PostFxPipeline" /> and a real <see cref="ScreenSession" />, and asserts that a
/// glow slot which the ledger stops feeding actually goes dark.
/// </summary>
public sealed class GateGlowAgingTests
{
    private const int Cols = 60;
    private const int Rows = 20;

    // ── Colour helpers, the PostFxTests idiom ─────────────────────────────

    private static (byte R, byte G, byte B) Channels(PackedColor c) =>
        c.IsRgb ? c.RgbChannels : ((byte)0, (byte)0, (byte)0);

    /// <summary>The fixed burn <c>GlowEffect</c> applies toward white to derive its hot tone.</summary>
    private static PackedColor HotTone(PackedColor accent)
    {
        var (r, g, b) = Channels(accent);
        const double burn = 0.65; // GlowEffect.HotBurn
        return PackedColor.Rgb(
            (byte)(r + ((255 - r) * burn)),
            (byte)(g + ((255 - g) * burn)),
            (byte)(b + ((255 - b) * burn)));
    }

    private static string Sgr(PackedColor c)
    {
        var (r, g, b) = Channels(c);
        return $"\x1B[38;2;{r};{g};{b}m";
    }

    /// <summary>
    /// The tone a glow blends a cell's foreground toward, for a GIVEN ledger intensity.
    /// </summary>
    /// <remarks>
    /// The intensity is a parameter and not a constant, and that is a measured correction rather
    /// than a stylistic one. <c>GlowEffect.Transform</c> blends by <c>_intensity * PeakStrength</c>,
    /// and the pulse is a SINE: the ledger's intensity at the peak frame is comfortably below 1.0.
    /// <c>PostFxTests.HotSgr</c> can hard-code <c>PeakStrength</c> because every test that uses it
    /// sets <c>intensity: 1.0</c> on a synthetic effect. This test drives the REAL ledger, so the
    /// only correct expectation is the one built from the value the ledger actually published —
    /// assuming the peak is 1.0 produces an expected string that is brighter than anything the
    /// terminal can ever be sent, and the byte assertion then fails on a build whose glow is perfect.
    /// </remarks>
    private static string HotSgr(PackedColor accent, double intensity) =>
        Sgr(PanelFx.Lerp(accent, HotTone(accent), intensity * GlowEffect.PeakStrength));

    // ── The producer half, observed through the real consumer ────────────

    /// <summary>
    /// A pulsing pending gate arms exactly one post-fx slot, and the pipeline really
    /// transforms a cell inside the armed region.
    /// </summary>
    /// <remarks>
    ///     The accent and the region are read back from <c>runner._glowScratch</c> and
    ///     <c>runner._glowEffects</c> AFTER <c>ArmGateGlow</c> has run, so they are the values the
    ///     consumer actually armed — not a re-derivation, and not a second probe pass whose
    ///     palette projection could differ from this frame's. Injecting the test cell with
    ///     <em>that</em> accent at <em>that</em> origin is what makes the byte assertion
    ///     independent of how the gate widget chooses to style its own text.
    /// </remarks>
    [Test]
    public async Task APulsingGate_ArmsExactlyOneSlot_AndThePipelineTransformsInsideIt()
    {
        var backend = new CapturingBackend();
        var screen = new ScreenSession(new AnsiWriter(backend), Cols, Rows);
        var (runner, _) = BuildRunner(backend, screen);
        var timeline = runner._timeline;
        timeline.EnablePostFx = true;
        timeline.DisableEntranceFx();

        var gate = new ApprovalGateView("bash", "ls -la /tmp");
        timeline.Append(gate);
        gate.BeginWarnPulse(100);
        timeline.CurrentTick = 100 + (PanelFx.PulseFrames / 4); // the pulse peak

        var lifecycle = new ReplLifecycle(runner);

        PackedColor accent;
        Rect region;
        double intensity;
        using (var frame = screen.BeginFrameScope())
        {
            Solve(runner);
            PaintFrame(runner, screen);
            lifecycle.ArmGateGlow();

            // ONE slot, not eight: the consumer arms the slots the ledger feeds and leaves the
            // rest alone for the drain to clear.
            await Assert.That(screen.Effects.Count).IsEqualTo(1)
                .Because("the ledger published exactly one region (one pending gate at the pulse "
                       + "peak), so the consumer arms exactly one slot");

            var effect = runner._glowEffects[0];
            await Assert.That(effect).IsNotNull()
                .Because("the consumer's persistent effect array is filled in place — it reuses the "
                       + "instance rather than allocating, which is what makes arming allocation-free");
            region = effect!.Region;
            accent = runner._glowScratch[0].Accent;
            intensity = runner._glowScratch[0].Intensity;

            await Assert.That(region.Width).IsGreaterThan(0)
                .Because("an empty region would make the glow invisible for a reason that has "
                       + "nothing to do with the drain, and this test would then pass on a build "
                       + "where the gate never glows at all");
            await Assert.That(region.Height).IsGreaterThan(0)
                .Because("same: a zero-height region is a vacuous glow");
            await Assert.That(region.Contains(region.X, region.Y)).IsTrue()
                .Because("the injected cell sits at the region's own origin");
            await Assert.That(accent.IsRgb).IsTrue()
                .Because("the publisher contract is truecolor accents — a palette-index accent is "
                       + "deliberately not glowed (GlowEffect.Update keeps the hot tone equal to the "
                       + "accent), so an indexed one would make every byte assertion below vacuous");

            // The REAL pulse intensity, not 1.0. The pulse is a sine, so the peak frame publishes
            // something below full strength, and GlowEffect.Transform blends by
            // intensity * PeakStrength. Carrying the value forward is what makes the byte assertion
            // below exact instead of approximately-right-in-the-wrong-direction.
            await Assert.That(intensity).IsGreaterThan(0.0)
                .Because("a zero intensity would make the whole aging experiment blind: the slot would "
                       + "be armed and the transform would return every cell untouched, so a missing "
                       + "drain would be invisible. This is the guarantee the second test needs");
            await Assert.That(intensity).IsLessThanOrEqualTo(1.0)
                .Because("GlowRegion clamps intensity to [0..1], and anything above that would mean the "
                       + "clamp stopped working. Measured: " + intensity);

            // The slot is armed AND non-zero: this is the assertion that makes the aging test
            // below sensitive. A last-armed frame at the pulse trough would satisfy Count == 1
            // and still hide a missing drain completely.
            var probe = Cell.From(new Rune('G'), new CellStyle(accent, attrs: StyleAttr.Bold));
            var glowed = screen.Effects.Transform(region.X, region.Y, in probe);
            await Assert.That(glowed.Style.Fg).IsNotEqualTo(probe.Style.Fg)
                .Because("an armed slot with non-zero intensity blends an accent cell toward the hot "
                       + "tone. If this passes with the intensity at zero, the aging test below is "
                       + "blind to a missing drain and must not be trusted");

            // Hold the back-buffer content constant for the aging test: same cell, same colour.
            screen.Back.SetText(region.X, region.Y, "GLOW", new CellStyle(accent, attrs: StyleAttr.Bold));

            await frame.FlushAsync();
        }

        string hot = HotSgr(accent, intensity);
        await Assert.That(backend.Text.Contains(hot)).IsTrue()
            .Because("the armed slot must reach the wire: the diff selects the cell, the pipeline "
                   + "transforms it, and the SGR automaton encodes the HOT tone rather than the raw "
                   + "accent. Captured: " + backend.Escaped);
    }

    /// <summary>
    /// The frame after the gate is answered, the slot the ledger stopped feeding is drained and
    /// the same cell is repainted plain.
    /// </summary>
    /// <remarks>
    ///     This is the assertion #889 is for. Delete the drain loop at the tail of
    ///     <c>ArmGateGlow</c> and it fails twice over: <c>Effects.Count</c> stays at 1, and the
    ///     frame emits nothing at all for the cell (the transform reproduces the tone already in
    ///     FRONT, so the diff is empty) rather than the plain accent. The stale <c>GlowEffect</c>
    ///     instance is asserted to still be sitting in the consumer's persistent array with its
    ///     region intact, which is the difference between "the pipeline dropped the slot" and
    ///     "something reallocated the scratch" — and the reason the fix is a drain rather than a
    ///     reset.
    /// </remarks>
    [Test]
    public async Task AfterTheDecision_TheSlotTheLedgerStoppedFeeding_IsDrainedAndTheCellGoesPlain()
    {
        var backend = new CapturingBackend();
        var screen = new ScreenSession(new AnsiWriter(backend), Cols, Rows);
        var (runner, gate) = BuildRunner(backend, screen);
        var timeline = runner._timeline;
        timeline.EnablePostFx = true;
        timeline.DisableEntranceFx();

        timeline.Append(gate);
        gate.BeginWarnPulse(100);
        timeline.CurrentTick = 100 + (PanelFx.PulseFrames / 4);

        var lifecycle = new ReplLifecycle(runner);

        PackedColor accent;
        Rect region;
        double intensity;
        using (var frame = screen.BeginFrameScope())
        {
            Solve(runner);
            PaintFrame(runner, screen);
            lifecycle.ArmGateGlow();
            region = runner._glowEffects[0]!.Region;
            accent = runner._glowScratch[0].Accent;
            intensity = runner._glowScratch[0].Intensity;
            screen.Back.SetText(region.X, region.Y, "GLOW", new CellStyle(accent, attrs: StyleAttr.Bold));
            await frame.FlushAsync();
        }

        await Assert.That(intensity).IsGreaterThan(0.0)
            .Because("the frame that arms the slot must arm it at non-zero strength, or a missing "
                   + "drain is invisible and this whole test is decorative. Measured: " + intensity);

        string hot = HotSgr(accent, intensity);
        await Assert.That(backend.Text.Contains(hot)).IsTrue()
            .Because("the first frame must actually glow, or the second frame proves nothing: a test "
                   + "that asserts 'no hot bytes' against a build that never emitted any would be "
                   + "green forever. Expected the tone for the ledger's own intensity (" + intensity
                   + "), not for 1.0 — the pulse is a sine. Captured: " + backend.Escaped);

        // The decision. This is what makes the ledger stop publishing — the producer half
        // PostFxTests already covers — and it is the only thing that changes between the frames.
        await Assert.That(gate.TryDecide(ApprovalChoice.Approve)).IsTrue()
            .Because("the gate must accept the decision for the ledger to go quiet");
        timeline.CurrentTick++;

        backend.Reset();

        using (var frame = screen.BeginFrameScope())
        {
            Solve(runner);
            PaintFrame(runner, screen);

            // THE CONTROLLED VARIABLE. Identical cell, identical colour, identical position as
            // the glowing frame — so if the pipeline still transforms it, the only explanation
            // left is a slot the consumer failed to drain.
            screen.Back.SetText(region.X, region.Y, "GLOW", new CellStyle(accent, attrs: StyleAttr.Bold));

            lifecycle.ArmGateGlow();

            await Assert.That(screen.Effects.Count).IsEqualTo(0)
                .Because(
                    "the ledger published nothing this frame, so the consumer must have drained every "
                    + "slot. PostFxPipeline's table is fixed and persistent — Flush runs every armed "
                    + "effect whose region contains the cell and nothing resets the table between "
                    + "frames — so a slot left armed at non-zero intensity is a gate that glows "
                    + "forever after it has been answered. That is the second half of #648, and it "
                    + "was uncovered");

            // The instance is still in the consumer's array with its region intact: the pipeline
            // dropped the SLOT, nothing reallocated the scratch. This is what separates "the drain
            // is missing" from "the drain points at the wrong place".
            var stale = runner._glowEffects[0];
            await Assert.That(stale).IsNotNull()
                .Because("_glowEffects is persistent by design; the drain clears the pipeline, not the "
                       + "cache, so a null here would mean the array was reallocated instead");
            await Assert.That(stale!.Region).IsEqualTo(region)
                .Because("the stale effect still holds the region it was armed with, which is why an "
                       + "undrained slot keeps painting: Flush tests Region.Contains on every cell");

            await frame.FlushAsync();
        }

        await Assert.That(backend.Text.Contains(hot)).IsFalse()
            .Because("the hot tone must be gone from the wire. With the drain deleted the transform "
                   + "reproduces the tone already in FRONT, so the frame emits NOTHING for this cell "
                   + "rather than a plain repaint — which is the visible symptom: the gate keeps its "
                   + "glow. Captured: " + backend.Escaped);

        await Assert.That(backend.Text.Contains(Sgr(accent))).IsTrue()
            .Because("and the cell must come back PLAIN: FRONT holds the glowed cell from the previous "
                   + "frame, so a drained pipeline repaints it with the raw accent. Asserting the "
                   + "absence of the hot tone alone would also be satisfied by a frame that emitted "
                   + "nothing at all, which is the broken-build behaviour. Captured: "
                   + backend.Escaped);
    }

    // ── Harness ──────────────────────────────────────────────────────────

    /// <summary>
    /// The production frame steps this test reproduces, in the order
    /// <c>ReplLifecycle.RenderFrameAsync</c> runs them: solve the layout, measure the frame, then
    /// paint. Only these three — everything else in that method is chrome, status projection or
    /// damage hints, none of which the glow pipeline reads.
    /// </summary>
    private static void Solve(CellForgeReplRunner runner)
    {
        runner.Screen.Tree.Solve(Cols, Rows);
        Rect tl = runner.Screen.Timeline.Rect;
        _ = runner._timeline.PrepareFrame(tl.Width > 0 ? tl.Width : Cols, Math.Max(1, tl.Height));
    }

    /// <summary>
    /// Paints the real widget tree into the real back buffer. This is what publishes the glow
    /// ledger — <c>ChatTimelinePanel.Paint</c> advances the tick and delegates to
    /// <c>VirtualizedChatTimeline.Paint</c> — so the region the consumer arms is the one the
    /// timeline computed, not one this test made up.
    /// </summary>
    private static void PaintFrame(CellForgeReplRunner runner, ScreenSession screen) =>
        runner.Screen.Tree.PaintAll(screen.Back);

    /// <summary>
    /// A runner with nothing switched on. The runner takes its dependencies as
    /// three bundles plus two run values, and this test exercises four of them,
    /// but the constructor is not a seam, so the rest are the same cheap
    /// doubles <c>CellForgeReplSmokeTests</c> uses — copied rather than
    /// re-invented, so a change to the runner's shape breaks in one place.
    /// </summary>
    private static (CellForgeReplRunner Runner, ApprovalGateView Gate) BuildRunner(
        CapturingBackend backend,
        ScreenSession screen)
    {
        var configStore = new StubConfigStore();
        var authStore = new AuthStore(configStore);
        var agentDef = TestAgents.AllowAll();
        var agentRegistry = new FakeAgentRegistry(agentDef);
        var providerRegistry = new FakeProviderRegistry(new ScriptedLlmClient());
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "mock/mock-model" };
        var chatScreen = ChatScreen.Build(composer, status);
        var bus = new InMemoryEventBus();
        var agent = new FakeAgent();
        var sessionModel = new Session(
            "gate-glow", "proj", Directory.GetCurrentDirectory(), "smoke",
            "code", "mock-model", "mock",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SessionMetadata.Empty);

        var bridge = new ChatScreenBridge(bus, chatScreen.Timeline, status, autoSubscribe: false);
        // Not disposed: the runner owns it, and neither test drives input.
        var input = new TerminalInputSource(
            new MemoryStream(Encoding.UTF8.GetBytes(string.Empty)),
            new TerminalInputSourceOptions { SizeProvider = () => (Cols, Rows) });

        var legacySlash = new LegacySlashRunner(
            new SlashCommandDispatcher(
                NullLogger<SlashCommandDispatcher>.Instance,
                new FakeToolRegistry(),
                new FakeSessionStore(),
                new OnboardingWizard(configStore, authStore),
                new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance)),
            agentRegistry,
            configStore,
            authStore,
            providerRegistry);

        var runner = new CellForgeReplRunner(
            new CellForgeCoreServices(
                configStore, providerRegistry, agentRegistry, authStore, bus, agent,
                legacySlash, NullLogger<CellForgeReplRunner>.Instance),
            new CellForgeOptionalServices(null, null, null, null, null, null, null, null),
            new CellForgeScreens(
                screen, chatScreen, bridge, input, backend,
                new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance)),
            sessionModel,
            new NullModeController());

        return (runner, new ApprovalGateView("bash", "ls -la /tmp"));
    }

    /// <summary>In-memory <see cref="ITerminalBackend" /> capturing every frame write.</summary>
    private sealed class CapturingBackend : ITerminalBackend
    {
        private readonly List<byte[]> _writes = [];

        public string Text
        {
            get
            {
                var combined = new List<byte>();
                foreach (byte[] w in _writes)
                {
                    combined.AddRange(w);
                }

                return Encoding.UTF8.GetString([.. combined]);
            }
        }

        /// <summary>Control characters rendered visible so a failure message is readable.</summary>
        public string Escaped => Text
            .Replace("\u001B", "\\e", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        public void Reset() => _writes.Clear();

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            _writes.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Nothing here reads configuration; the runner only needs a live store reference.</summary>
    private sealed class StubConfigStore : IConfigStore
    {
        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(HarborConfig.Default));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<string>("no api key in the glow harness"));
    }
}
