// #2325: the '?' legend is a projection of the bindings registry. These tests
// drive a real BindingsMap and the real WhichKeyHelpOverlayLayer so the slice
// proves the hint is derived, not copied from HelpKeymap or hardcoded text.

using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public sealed class KeyLegendTests
{
    private static EngineInput.Binding Bind(
        string id,
        string spec,
        string action,
        string description = "",
        bool show = true,
        int priority = 0,
        string? group = null,
        string? keyDisplay = null)
    {
        if (!EngineInput.KeySpec.TryParse(spec, out EngineInput.KeyGesture gesture))
        {
            throw new InvalidOperationException($"Bad test spec: {spec}");
        }

        return new EngineInput.Binding(
            id,
            gesture,
            action,
            Description: description,
            Show: show,
            KeyDisplay: keyDisplay,
            Priority: priority,
            Group: group);
    }

    private static EngineInput.BindingsMap Registry() => new();

    [Test]
    public async Task Rows_ProjectVisibleBindingsInRegistryOrder()
    {
        var map = Registry();
        map.Bind(Bind("help", "?", "help", "toggle this help panel", group: "global"));
        map.Bind(Bind("jump", "ctrl+j", "jump", "open jump palette", priority: 5, group: "panel"));
        map.Bind(Bind("secret", "f9", "debug", "hidden in help", show: false));

        var legend = new EngineInput.KeyLegend(map);

        await Assert.That(legend.Rows.Count).IsEqualTo(2)
            .Because("ShownKeys excludes Show=false bindings and the legend is that view");
        await Assert.That(legend.Rows[0].Key).IsEqualTo("ctrl+j")
            .Because("registry order is priority desc, so the higher-priority binding comes first");
        await Assert.That(legend.Rows[0].Description).IsEqualTo("open jump palette");
        await Assert.That(legend.Rows[0].Group).IsEqualTo("panel");
        await Assert.That(legend.Rows[1].Key).IsEqualTo("?");
        await Assert.That(legend.Rows[1].Group).IsEqualTo("global");
        await Assert.That(legend.Rows.Any(r => r.Description == "hidden in help")).IsFalse();
    }

    [Test]
    public async Task Hint_ComesFromTheRegisteredHelpBinding()
    {
        var map = Registry();
        map.Bind(Bind("jump", "ctrl+j", "jump", "open jump palette"));
        map.Bind(Bind("help", "?", "help", "toggle this help panel", keyDisplay: "?"));

        var legend = new EngineInput.KeyLegend(map);

        await Assert.That(legend.HelpKey).IsEqualTo("?");
        await Assert.That(legend.HelpDescription).IsEqualTo("toggle this help panel");
        await Assert.That(legend.Hint).IsEqualTo("?  toggle this help panel")
            .Because("both the key and the label are the binding's own metadata, not a literal here");
    }

    [Test]
    public async Task Hint_FollowsRebind_AndIsEmptyWhenHelpIsUnbound()
    {
        var map = Registry();
        map.Bind(Bind("help", "?", "help", "toggle this help panel"));
        await Assert.That(new EngineInput.KeyLegend(map).Hint).IsEqualTo("?  toggle this help panel");

        // A keymap override moves the gesture; the legend must move with it.
        _ = map.ApplyOverride("help", "h", out _);
        await Assert.That(new EngineInput.KeyLegend(map).Hint).IsEqualTo("h  toggle this help panel")
            .Because("the hint is rebuilt from the registry after a rebind");

        _ = map.Unbind("help");
        await Assert.That(new EngineInput.KeyLegend(map).Hint).IsEmpty()
            .Because("an unbound help action prints no invented fallback key");
    }

    [Test]
    public async Task Hint_UsesOnlyTheKeyWhenDescriptionIsEmpty()
    {
        var map = Registry();
        map.Bind(Bind("help", "f1", "help"));

        await Assert.That(new EngineInput.KeyLegend(map).Hint).IsEqualTo("f1");
    }

    [Test]
    public async Task Legend_RejectsNullRegistry()
    {
        await Assert.That(() => new EngineInput.KeyLegend(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Overlay_WithLegend_RendersRegistryRowsAndDerivedHint()
    {
        var map = Registry();
        map.Bind(Bind("help", "?", "help", "toggle this help panel"));
        map.Bind(Bind("jump", "ctrl+j", "jump", "open jump palette"));
        map.Bind(Bind("hidden", "f9", "debug", "hidden in help", show: false));

        var overlay = new WhichKeyHelpOverlay();
        overlay.SetLegend(new EngineInput.KeyLegend(map));
        overlay.Show();
        var buffer = TestRender.Buffer(80, 32);

        overlay.Paint(buffer, new Rect(0, 0, 80, 32));

        string art = TestRender.Art(buffer);
        await Assert.That(overlay.Legend.HasValue).IsTrue();
        await Assert.That(art).Contains("Hotkeys");
        await Assert.That(art).Contains("open jump palette");
        await Assert.That(art).Contains("toggle this help panel");
        await Assert.That(art).Contains("?  toggle this help panel");
        await Assert.That(art).DoesNotContain("hidden in help");
        await Assert.That(art).DoesNotContain("Alt+1..9")
            .Because("the curated HelpKeymap table is replaced, not merged");
        await Assert.That(art).DoesNotContain("Press ? to close.")
            .Because("the footer is the registry-derived hint on this path");
    }

    [Test]
    public async Task SetLegend_Null_RestoresLegacyRendering()
    {
        var map = Registry();
        map.Bind(Bind("help", "?", "help", "registry-only legend label"));
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetLegend(new EngineInput.KeyLegend(map));
        overlay.SetLegend(null);
        overlay.Show();
        var buffer = TestRender.Buffer(80, 32);

        overlay.Paint(buffer, new Rect(0, 0, 80, 32));

        string art = TestRender.Art(buffer);
        await Assert.That(overlay.Legend.HasNoValue).IsTrue();
        await Assert.That(art).Contains("Alt+1..9");
        await Assert.That(art).Contains("Press ? to close.")
            .Because("the legacy footer is back after the legend is cleared");
        await Assert.That(art).DoesNotContain("registry-only legend label")
            .Because("that label exists only in the registry, not in HelpKeymap");
    }

    [Test]
    public async Task Layer_Reused_PaintsLegendAndKeepsItsHitTest()
    {
        var map = Registry();
        map.Bind(Bind("help", "?", "help", "toggle this help panel"));
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetLegend(new EngineInput.KeyLegend(map));
        overlay.Show();

        // The existing WhichKeyHelpOverlayLayer is reused: #2325 adds no
        // second overlay layer to the z-stack census.
        var layer = new WhichKeyHelpOverlayLayer(overlay);
        layer.Sync(new Rect(0, 0, 80, 24));
        var stack = new OverlayStack();
        stack.Push(layer);
        var buffer = TestRender.Buffer(80, 24);

        stack.PaintOver(buffer);

        await Assert.That(TestRender.Art(buffer)).Contains("?  toggle this help panel");
        await Assert.That(stack.HitTest(layer.Bounds.X + 1, layer.Bounds.Y + 1)!.Id)
            .IsEqualTo(WhichKeyHelpOverlayLayer.LayerId);
    }

    [Test]
    public async Task Legend_DoesNotAddAKeyGateSite()
    {
        var map = Registry();
        map.Bind(Bind("help", "?", "help", "toggle this help panel"));
        var legend = new EngineInput.KeyLegend(map);
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetLegend(legend);
        overlay.Show();

        // #833 / KeyGateFamilyRules: the '?' gate stays on the declared
        // WhichKeyHelpOverlay site (Buffer family: Shift admitted, chords
        // refused). A legend is data only — it introduces no new gate.
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, false)))
            .IsTrue()
            .Because("'?' is a shifted rune, so the buffer gate admits it");
        overlay.Show();
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, true)))
            .IsFalse()
            .Because("Ctrl+? is a chord, not a dismissal");
        await Assert.That(legend.HelpKey).IsEqualTo("?")
            .Because("the hint key is the same gesture the declared site gates");
    }
}
