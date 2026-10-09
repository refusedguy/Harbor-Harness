using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;
// #436: the pipeline, effects and diff engine under test are engine-typed;
// expectations computed through the host PanelFx stay Rendering-typed and
// cross through packed-uint round-trips (exact — both structs share the
// layout), compared as uint Values.
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Shader-like post-render effects (renderer-moat T3): a composable pipeline
/// runs after the diff selects cells and before SGR encoding; warning/error
/// accents (pending approval gates) bloom toward a hot tone. The empty
/// pipeline is byte-identical to the classic scan — zero regression on
/// non-effect frames.
/// </summary>
public class PostFxTests
{
    private static (byte R, byte G, byte B) Channels(EngineCells.PackedColor c) =>
        c.IsRgb ? c.RgbChannels : ((byte)0, (byte)0, (byte)0);

    private static EngineCells.PackedColor HotTone(EngineCells.PackedColor accent)
    {
        var (r, g, b) = Channels(accent);
        const double burn = 0.65; // GlowEffect.HotBurn
        return EngineCells.PackedColor.Rgb(
            (byte)(r + ((255 - r) * burn)),
            (byte)(g + ((255 - g) * burn)),
            (byte)(b + ((255 - b) * burn)));
    }

    private static UIR.PackedColor ToUi(EngineCells.PackedColor c) => UIR.PackedColor.FromRaw(c.Value);

    private static string HotSgr(EngineCells.PackedColor accent)
    {
        var (r, g, b) = Channels(ToUi(PanelFx.Lerp(ToUi(accent), ToUi(HotTone(accent)), GlowEffect.PeakStrength)));
        return $"\x1B[38;2;{r};{g};{b}m";
    }

    // ── Pipeline mechanics ─────────────────────────────────────────────────

    [Test]
    public async Task EmptyPipeline_TransformIsIdentity()
    {
        var pipeline = new PostFxPipeline();
        var cell = EngineCells.Cell.From(new Rune('x'), new EngineCells.CellStyle(EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value), attrs: EngineCells.StyleAttr.Bold));

        await Assert.That(pipeline.Count).IsEqualTo(0);
        await Assert.That(pipeline.Transform(3, 2, in cell)).IsEqualTo(cell);
    }

    [Test]
    public async Task Set_Clear_MaintainSlotBookkeeping()
    {
        var pipeline = new PostFxPipeline();
        var effect = new GlowEffect();

        pipeline.Set(0, effect);
        pipeline.Set(3, effect);
        await Assert.That(pipeline.Count).IsEqualTo(2);

        pipeline.Set(3, null);
        await Assert.That(pipeline.Count).IsEqualTo(1);

        pipeline.Clear();
        await Assert.That(pipeline.Count).IsEqualTo(0);
        await Assert.That(() => pipeline.Set(PostFxPipeline.MaxEffects, effect)).Throws<ArgumentOutOfRangeException>();
    }

    // ── GlowEffect semantics: warning/error accents only ───────────────────

    [Test]
    public async Task Glow_BrightensAccentCells_OthersPassThrough()
    {
        var accent = EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value);
        var effect = new GlowEffect();
        effect.Update(new GlowRegion(new EngineCells.Rect(0, 0, 20, 3), accent, intensity: 1.0));

        var accentCell = EngineCells.Cell.From(new Rune('!'), new EngineCells.CellStyle(accent, attrs: EngineCells.StyleAttr.Bold));
        var textCell = EngineCells.Cell.From(new Rune('a'), new EngineCells.CellStyle(EngineCells.PackedColor.FromRaw(ChatPalette.Text.Value)));
        var dimCell = EngineCells.Cell.From(new Rune('-'), new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Dim));

        var glowed = effect.Transform(5, 1, in accentCell);
        await Assert.That(glowed.Style.Fg.Value).IsEqualTo(PanelFx.Lerp(ToUi(accent), ToUi(HotTone(accent)), GlowEffect.PeakStrength).Value);
        await Assert.That(glowed.Rune).IsEqualTo(accentCell.Rune); // rune untouched
        await Assert.That((int)glowed.Style.Attrs).IsEqualTo((int)EngineCells.StyleAttr.Bold); // attrs untouched

        await Assert.That(effect.Transform(6, 1, in textCell)).IsEqualTo(textCell); // plain text — no wash
        await Assert.That(effect.Transform(7, 1, in dimCell)).IsEqualTo(dimCell);   // hints — no wash
        await Assert.That(effect.Transform(5, 9, in accentCell)).IsEqualTo(accentCell); // outside region
    }

    [Test]
    public async Task Glow_ZeroIntensity_IsIdentity()
    {
        var effect = new GlowEffect();
        effect.Update(new GlowRegion(new EngineCells.Rect(0, 0, 10, 2), EngineCells.PackedColor.FromRaw(ChatPalette.Error.Value), intensity: 0.0));

        var cell = EngineCells.Cell.From(new Rune('!'), new EngineCells.CellStyle(EngineCells.PackedColor.FromRaw(ChatPalette.Error.Value), attrs: EngineCells.StyleAttr.Bold));
        await Assert.That(effect.Transform(1, 0, in cell)).IsEqualTo(cell);
    }

    [Test]
    public async Task Glow_PaletteIndexAccent_DoesNotGlow()
    {
        var effect = new GlowEffect();
        effect.Update(new GlowRegion(new EngineCells.Rect(0, 0, 10, 2), EngineCells.PackedColor.Indexed(3), intensity: 1.0));

        var cell = EngineCells.Cell.From(new Rune('!'), new EngineCells.CellStyle(EngineCells.PackedColor.Indexed(3)));
        await Assert.That(effect.Transform(1, 0, in cell)).IsEqualTo(cell);
    }

    // ── Diff engine hook: bytes + mirror semantics ──────────────────────────

    private static DiffEngine SeededEngine(int cols, int rows, out EngineCells.ScreenBuffer back, out AnsiWriter writer, out RecordingBackend backend)
    {
        var engine = new DiffEngine(cols, rows);
        back = new EngineCells.ScreenBuffer(cols, rows);
        backend = new RecordingBackend();
        writer = new AnsiWriter(backend);
        writer.BeginFrame();
        engine.Flush(back, writer);
        writer.EndFrame();
        backend.ResetForTests();
        return engine;
    }

    [Test]
    public async Task ArmedEmptyPipeline_ByteIdentical_ToUnarmedScan()
    {
        const int cols = 40, rows = 8;

        var engineA = SeededEngine(cols, rows, out var backA, out var writerA, out var backendA);
        var engineB = SeededEngine(cols, rows, out var backB, out var writerB, out var backendB);
        engineB.Effects = new PostFxPipeline(); // armed but empty

        backA.SetText(2, 1, "delta", new EngineCells.CellStyle(EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value), attrs: EngineCells.StyleAttr.Bold));
        backB.SetText(2, 1, "delta", new EngineCells.CellStyle(EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value), attrs: EngineCells.StyleAttr.Bold));

        writerA.BeginFrame();
        engineA.Flush(backA, writerA);
        await writerA.EndFrameAsync();
        writerB.BeginFrame();
        engineB.Flush(backB, writerB);
        await writerB.EndFrameAsync();

        await Assert.That(backendB.Text).IsEqualTo(backendA.Text);
        await Assert.That(engineB.FrontMatches(backB)).IsTrue();
    }

    [Test]
    public async Task ArmedGlow_TransformsEmittedStyle_AndMirrorsTerminalView()
    {
        var engine = SeededEngine(30, 4, out var back, out var writer, out var backend);
        var accent = EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value);
        var glow = new GlowEffect();
        glow.Update(new GlowRegion(new EngineCells.Rect(0, 1, 30, 1), accent, intensity: 1.0));
        var pipeline = new PostFxPipeline();
        pipeline.Set(0, glow);
        engine.Effects = pipeline;

        back.SetText(4, 1, "WARN", new EngineCells.CellStyle(accent, attrs: EngineCells.StyleAttr.Bold));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // Emitted bytes carry the hot tone, not the raw accent.
        await Assert.That(backend.Text.Contains(HotSgr(accent))).IsTrue();

        // FRONT mirrors the TERMINAL: the emitted cell is stored transformed —
        // that is what makes disarm convergence a one-frame plain repaint.
        await Assert.That(engine.Front.Get(4, 1).Style.Fg.Value)
            .IsEqualTo(PanelFx.Lerp(ToUi(accent), ToUi(HotTone(accent)), GlowEffect.PeakStrength).Value);

        // Disarm: the plain cell now differs from the mirrored glow and is
        // repainted once — no glow sticks to the terminal.
        backend.ResetForTests();
        glow.Update(new GlowRegion(new EngineCells.Rect(0, 1, 30, 1), accent, intensity: 0.0));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(backend.Text.Contains("WARN")).IsTrue(); // repainted plain
        await Assert.That(backend.Text.Contains(HotSgr(accent))).IsFalse(); // no glow bytes
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task ArmedGlow_UnchangedCells_StillRepaintWhenIntensityChanges()
    {
        // The pulse's whole point: identical raw BACK content across frames,
        // different glow intensity → the transformed look differs → re-emit.
        var engine = SeededEngine(20, 2, out var back, out var writer, out var backend);
        var accent = EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value);
        var glow = new GlowEffect();
        var pipeline = new PostFxPipeline();
        pipeline.Set(0, glow);
        engine.Effects = pipeline;

        back.SetText(0, 0, "PULSE", new EngineCells.CellStyle(accent, attrs: EngineCells.StyleAttr.Bold));

        glow.Update(new GlowRegion(new EngineCells.Rect(0, 0, 20, 1), accent, intensity: 1.0));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        string peak = backend.Text;
        await Assert.That(peak).IsNotEqualTo("");

        backend.ResetForTests();
        writer.BeginFrame();
        glow.Update(new GlowRegion(new EngineCells.Rect(0, 0, 20, 1), accent, intensity: 0.4));
        engine.Flush(back, writer); // raw BACK unchanged — glow drives the repaint
        await writer.EndFrameAsync();

        string mid = backend.Text;
        await Assert.That(mid).IsNotEqualTo(peak);
        await Assert.That(mid.Contains(HotSgr(accent))).IsFalse(); // dimmer than the peak tone
        await Assert.That(mid.Length).IsGreaterThan(0);
    }

    // ── Timeline glow ledger ───────────────────────────────────────────────

    [Test]
    public async Task Timeline_PublishesGateGlowRegions_AndStopsOnDecision()
    {
        // #648 — this test was the one flake in the shard. It is the only
        // method here that re-derived a colour from the GLOBAL palette after
        // the value under test had been captured: Paint publishes the ledger
        // accent from ChatPalette.Warning, and the expectation below was
        // recomputed by a second PanelFx.WarnTone call — two awaits later. A
        // theme switch landing in that window made two different catalog
        // projections meet and the equality failed (observed on four unrelated
        // PRs). Every sibling test snapshots the palette into a local first,
        // which is why this read as method-scoped rather than class-scoped.
        //
        // Pin the catalog for the paint and materialize the expectation under
        // the same pin, so both sides resolve against ONE projection — the
        // PanelFxTests.BlendRegion_WideChar idiom. Belt to the braces of the
        // bare [NotInParallel] now on the palette-mutating classes: their old
        // ("pty") key excluded only other "pty" tests, never a reader.
        ChatPalette.PinFrame();
        try
        {
            var timeline = new VirtualizedChatTimeline { EnablePostFx = true };
            var gate = new ApprovalGateView("bash", "ls -la /tmp");
            timeline.Append(gate);
            _ = timeline.PrepareFrame(60, 10);
            timeline.CurrentTick = 100;
            gate.BeginWarnPulse(100);

            var regions = new GlowRegion[VirtualizedChatTimeline.MaxFxDamage];

            // Pulse peak (¼ cycle): full-intensity region with the painted accent.
            long peakTick = 100 + (PanelFx.PulseFrames / 4);
            timeline.CurrentTick = peakTick;

            // Resolved under the pin, before any await: the ledger captures
            // WarnTone(PulseBirthTick, CurrentTick) during the paint below, so
            // this must be derived from the same catalog, not a later one.
            EngineCells.PackedColor expectedAccent = EngineCells.PackedColor.FromRaw(PanelFx.WarnTone(100, peakTick).Fg.Value);

            timeline.Paint(new ScreenBuffer(60, 10), new Rect(0, 0, 60, 10));
            int count = timeline.ConsumeGlowRegions(regions);
            await Assert.That(count).IsEqualTo(1);
            await Assert.That(regions[0].Intensity).IsGreaterThan(0.0);
            await Assert.That(regions[0].Bounds.Height).IsGreaterThan(0);
            await Assert.That(regions[0].Accent.Value).IsEqualTo(expectedAccent.Value);

            // Pulse trough (¾ cycle — sine negative → clamped 0): the region is
            // STILL published at zero so the glow can be cleared on the terminal.
            timeline.CurrentTick = 100 + PanelFx.PulseFrames + ((PanelFx.PulseFrames * 3) / 4);
            timeline.Paint(new ScreenBuffer(60, 10), new Rect(0, 0, 60, 10));
            count = timeline.ConsumeGlowRegions(regions);
            await Assert.That(count).IsEqualTo(1);
            await Assert.That(regions[0].Intensity).IsEqualTo(0.0);

            // Decision kills the glow feed.
            _ = gate.TryDecide(ApprovalChoice.Deny);
            timeline.CurrentTick++;
            timeline.Paint(new ScreenBuffer(60, 10), new Rect(0, 0, 60, 10));
            count = timeline.ConsumeGlowRegions(regions);
            await Assert.That(count).IsEqualTo(0);
        }
        finally
        {
            ChatPalette.UnpinFrame();
        }
    }

    [Test]
    public async Task Timeline_PostFxOff_PublishesNothing()
    {
        var timeline = new VirtualizedChatTimeline(); // EnablePostFx defaults false
        var gate = new ApprovalGateView("bash", "ls -la /tmp");
        timeline.Append(gate);
        _ = timeline.PrepareFrame(60, 10);
        gate.BeginWarnPulse(50);

        timeline.CurrentTick = 50 + (PanelFx.PulseFrames / 4);
        timeline.Paint(new ScreenBuffer(60, 10), new Rect(0, 0, 60, 10));

        var regions = new GlowRegion[1];
        await Assert.That(timeline.ConsumeGlowRegions(regions)).IsEqualTo(0);
    }

    [Test]
    public async Task Timeline_PostFxKeepsGateRowsDamagedThroughTroughs()
    {
        var timeline = new VirtualizedChatTimeline { EnablePostFx = true };
        var gate = new ApprovalGateView("bash", "ls -la /tmp");
        timeline.Append(gate);
        _ = timeline.PrepareFrame(60, 10);
        gate.BeginWarnPulse(birthTick: 0);

        // Flush the append/first-frame broad damage.
        var fx = new Rect[VirtualizedChatTimeline.MaxFxDamage];
        timeline.CurrentTick = 1;
        timeline.Paint(new ScreenBuffer(60, 10), new Rect(0, 0, 60, 10));
        _ = timeline.ConsumeFrameDamage(fx, out _);
        _ = timeline.ConsumeGlowRegions([]);

        // Trough frame — pulse == 0 — must STILL damage the gate rows so the
        // effect pipeline can repaint it at zero strength (glow convergence).
        timeline.CurrentTick = PanelFx.PulseFrames * 3 / 4;
        timeline.Paint(new ScreenBuffer(60, 10), new Rect(0, 0, 60, 10));
        bool broad = timeline.ConsumeFrameDamage(fx, out int fxCount);

        await Assert.That(broad).IsFalse();
        await Assert.That(fxCount).IsEqualTo(1);
    }

    // ── Session wiring ─────────────────────────────────────────────────────

    [Test]
    public async Task ScreenSession_ArmsEffectsThroughFlush_EmptyStaysByteIdentical()
    {
        var backend = new RecordingBackend();
        var session = new ScreenSession(new AnsiWriter(backend, syncUpdates: true), 20, 3);

        session.BeginFrame();
        session.FlushFrame();
        backend.ResetForTests();

        session.Back.SetText(1, 0, "plain", EngineCells.CellStyle.Plain);
        session.BeginFrame();
        session.FlushFrame(); // Effects empty → classic path

        await Assert.That(backend.Text.Contains("plain")).IsTrue();
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
    }

    [Test]
    public async Task ScreenSession_ArmedGlow_FlowsThroughFlushFrame()
    {
        var backend = new RecordingBackend();
        var session = new ScreenSession(new AnsiWriter(backend, syncUpdates: true), 20, 3);
        session.BeginFrame();
        session.FlushFrame();

        var accent = EngineCells.PackedColor.FromRaw(ChatPalette.Warning.Value);
        var glow = new GlowEffect();
        glow.Update(new GlowRegion(new EngineCells.Rect(0, 1, 20, 1), accent, intensity: 1.0));
        session.Effects.Set(0, glow);

        session.Back.SetText(2, 1, "HOT", new EngineCells.CellStyle(accent, attrs: EngineCells.StyleAttr.Bold));
        session.BeginFrame();
        session.FlushFrame();

        await Assert.That(backend.Text.Contains(HotSgr(accent))).IsTrue();
        await Assert.That(session.Front.Get(2, 1).Style.Fg.Value)
            .IsEqualTo(PanelFx.Lerp(ToUi(accent), ToUi(HotTone(accent)), GlowEffect.PeakStrength).Value);
    }
}
