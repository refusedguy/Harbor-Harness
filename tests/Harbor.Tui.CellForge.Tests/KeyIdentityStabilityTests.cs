using Harbor.TestKit;
using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2356: the single stable-key identity rule (gpui-kit
///     <c>ElementId</c>, commit 249a963). One rule, three carriers:
///     <list type="bullet">
///         <item><description><see cref="IProjector{TState}.ScreenId"/>: the screen key.</description></item>
///         <item><description><c>CellForgePanelBase.Id</c>: the panel key (dock, focus, Alt+1..9).</description></item>
///         <item><description><see cref="TimelineLayoutCache.PinAnchor"/>: the timeline key (a block reference).</description></item>
///     </list>
///     The key is a frame invariant: for one logical entity it never changes as a
///     result of producing a frame. Changing it is an explicit act (a different
///     entity, an intentional remap), never a side effect of a repaint. Every test
///     below drives several repaint frames and asserts the key holds; the
///     <c>ExplicitChange</c> tests pin the boundary where a key change is deliberate.
/// </summary>
public class KeyIdentityStabilityTests
{
    // ── Screen key ──────────────────────────────────────────────────────────

    private sealed class FrameProjector(string screenId) : IProjector<UiState>
    {
        private string _screenId = screenId;

        public string ScreenId => _screenId;

        public int Calls { get; private set; }

        public void ChangeId(string id) => _screenId = id;

        public void Project(UiState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            Calls++;
        }
    }

    [Test]
    public async Task ScreenId_IsStableAcrossFrames_AndRoutesToTheSameProjector()
    {
        var registry = new ScreenProjectorRegistry();
        var projector = new FrameProjector("chat");
        registry.Register(projector);

        string key = projector.ScreenId;
        for (int frame = 0; frame < 5; frame++)
        {
            var state = new UiState
            {
                Chat = ChatDomainState.Empty with { IsAgentRunning = frame % 2 == 0 }
            };

            await Assert.That(registry.Project(key, state)).IsTrue();
            await Assert.That(projector.ScreenId).IsEqualTo(key); // invariant across the frame
        }

        await Assert.That(projector.Calls).IsEqualTo(5);
        await Assert.That(registry.TryGet(key, out var resolved)).IsTrue();
        await Assert.That(ReferenceEquals(resolved, projector)).IsTrue();
    }

    [Test]
    public async Task ChangingScreenId_IsAnExplicitRemap_NotAFrameSideEffect()
    {
        var registry = new ScreenProjectorRegistry();
        var projector = new FrameProjector("chat");
        registry.Register(projector);
        await Assert.That(registry.Project("chat", new UiState())).IsTrue();

        // Deliberate remap: the projector's own key changes out of band.
        projector.ChangeId("settings");
        await Assert.That(projector.ScreenId).IsEqualTo("settings");

        // The change silently detaches routing under the new key until someone
        // registers again, which is exactly why it must be intentional.
        await Assert.That(registry.Project(projector.ScreenId, new UiState())).IsFalse();
        registry.Register(projector);
        await Assert.That(registry.Count).IsEqualTo(2);
        await Assert.That(registry.Project("settings", new UiState())).IsTrue();
    }

    // ── Panel key ───────────────────────────────────────────────────────────

    private static readonly CellForgePanelBase[] BuiltinPanels =
    [
        new CellForgeHelpPanel(),
        new CellForgeTodoListPanel(),
        new CellForgeDiffPreviewPanel(),
        new CellForgeFileTreePanel(),
        new CellForgeTokenBreakdownPanel(),
        new CellForgeDiagnosticsPanel(),
        new CellForgeLogsPanel(),
    ];

    [Test]
    public async Task PanelId_IsStableAcrossFrames_AndIsTheRoutingKey()
    {
        var registry = new PanelRegistry();
        foreach (var panel in BuiltinPanels)
        {
            await Assert.That(registry.Register(panel).IsSuccess).IsTrue();
        }

        string[] keys = BuiltinPanels.Select(p => p.Id).ToArray();
        await Assert.That(keys.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(keys.Length);

        for (int frame = 0; frame < 4; frame++)
        {
            var state = new UiState
            {
                Chat = ChatDomainState.Empty with { IsAgentRunning = frame % 2 == 0 }
            };

            foreach (var panel in BuiltinPanels)
            {
                _ = panel.Build(new PanelContext(state, 80, 24));
                await Assert.That(panel.Id).IsEqualTo(keys[Array.IndexOf(BuiltinPanels, panel)]);

                // Focus / overlay routing resolves by this same stable key.
                await Assert.That(ReferenceEquals(registry.Get(panel.Id), panel)).IsTrue();
            }
        }
    }

    [Test]
    public async Task ChangingPanelKey_IsAnExplicitReregistration()
    {
        // Routing follows the key captured at registration, not the provider's
        // identity. Moving a provider to another key is an explicit unregister
        // plus register, never a side effect of a frame.
        var help = new CellForgeHelpPanel();
        var logs = new CellForgeLogsPanel();
        var registry = new PanelRegistry();

        await Assert.That(registry.Register(help).IsSuccess).IsTrue();
        await Assert.That(ReferenceEquals(registry.Get("help"), help)).IsTrue();

        await Assert.That(registry.Unregister("help").IsSuccess).IsTrue();
        await Assert.That(registry.Get("help")).IsNull();

        await Assert.That(registry.Register(logs).IsSuccess).IsTrue();
        await Assert.That(ReferenceEquals(registry.Get("logs"), logs)).IsTrue();
    }

    // ── Timeline key ────────────────────────────────────────────────────────

    // Reuse the existing width-sensitive chat-block double rather than adding a
    // new implementer (the seam-graph gate tracks chat-block implementers).
    // 10 lines x 20 chars = 200 chars: 3 rows at width 80, 2 at width 120.
    private static WrappingBlock Block(string id) =>
        new(id, logicalLines: 10, lineChars: 19, honestEstimate: true);

    [Test]
    public async Task TimelineIdentity_IsStableAcrossRepaintFrames()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 20; i++)
        {
            cache.Append(Block($"b{i}"));
        }

        _ = cache.PrepareLayout(80, 10, scrollY: 0);
        cache.PinAnchor(scrollTopY: 12); // b4 at 3-row geometry

        var pinned = cache.BlockAt(cache.EntryAtY(cache.RestoreAnchor()));
        await Assert.That(pinned.RawText()).IsEqualTo("b4");

        // Repaint frames at varying width and tick: the restored Y moves with the
        // geometry, but the key (the block reference) must not change.
        for (int frame = 0; frame < 4; frame++)
        {
            int width = frame % 2 == 0 ? 120 : 80;
            _ = cache.PrepareLayout(width, 10, scrollY: 0);
            var current = cache.BlockAt(cache.EntryAtY(cache.RestoreAnchor()));
            await Assert.That(ReferenceEquals(current, pinned)).IsTrue();
        }
    }

    [Test]
    public async Task ReplacingTheAnchoredBlock_IsAnExplicitIdentityChange()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 20; i++)
        {
            cache.Append(Block($"b{i}"));
        }

        _ = cache.PrepareLayout(80, 10, scrollY: 0);
        cache.PinAnchor(scrollTopY: 12); // b4

        // Explicit remap: same message, new instance. The anchor follows it.
        var replacement = Block("b4:committed");
        cache.Replace(4, replacement);

        _ = cache.PrepareLayout(120, 10, scrollY: 0);
        var pinned = cache.BlockAt(cache.EntryAtY(cache.RestoreAnchor()));
        await Assert.That(ReferenceEquals(pinned, replacement)).IsTrue();
    }
}
