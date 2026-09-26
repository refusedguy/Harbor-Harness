using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

// ── skill-freshness (Right/40, pure) ─────────────────────────────────────────

// NOTE: intentionally NOT registered in CellForgeTuiRenderer.RegisterBuiltinPanels
// (issue #23 slice 1): the 9-panel Alt+1..9 slot order is pinned by
// PanelWiringTests, and host seeding (installed hashes vs skills-lock.json)
// lands in a follow-up. Hosts opt in by registering this provider directly.

/// <summary>
///     Cell-native skill-freshness panel (KILLER_FEATURES §2.7 Feature 10,
///     Orca <c>SkillFreshnessStatusPill.tsx</c>): one pill row per skill from
///     <see cref="SkillFreshnessModel"/> (<c>✓ current</c> / <c>● changed</c> /
///     <c>? untracked</c> / <c>✗ missing</c>). Non-interactive.
/// </summary>
public sealed class CellForgeSkillFreshnessPanel : IPanelProvider
{
    private readonly SkillFreshnessModel _model;

    /// <summary>Create a panel with a fresh model (host registration path).</summary>
    public CellForgeSkillFreshnessPanel()
        : this(new SkillFreshnessModel())
    {
    }

    /// <summary>Create a panel over an explicit model (tests drive pills through it).</summary>
    internal CellForgeSkillFreshnessPanel(SkillFreshnessModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <inheritdoc />
    public string Id => "skill-freshness";

    /// <inheritdoc />
    public string Title => "Skills";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 40;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return PanelText.Clip(
            PanelRows.SkillFreshnessRows(_model.Entries),
            ctx.Width,
            ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;
}
