using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Panels;

// ── skill-freshness opt-in (issue #23 slice 2) ─────────────────────────────

// KILLER_FEATURES §2.7 Feature 10 (Orca `SkillFreshnessStatusPill.tsx`):
// host opt-in for `CellForgeSkillFreshnessPanel`. The panel is deliberately NOT
// a renderer builtin — `RegisterBuiltinPanels` keeps exactly the 9 panels in
// Spectre Alt+1..9 slot order (pinned by `PanelWiringTests`), and this 10th
// panel takes no Alt slot. Hosts opt in explicitly:
//
//   panels.RegisterSkillFreshness(sharedModel);
//
// The CLI wires it when `HARBOR_SKILL_FRESHNESS=1` (see
// `SkillFreshnessStartup.TryRegisterPanel`); tests and other hosts call the
// extension directly. Re-seeding after registration is safe:
// `CellForgePanelRegistry.EnsureSeeded` preserves already-known panel states.

/// <summary>
///     Opt-in registration for the skill-freshness panel (KILLER_FEATURES
///     §2.7 Feature 10, issue #23 slice 2). Keeps the 9-panel Alt+1..9 slot
///     order untouched — the freshness panel appends after the builtins.
/// </summary>
public static class SkillFreshnessPanelRegistration
{
    /// <summary>Env opt-in read by the CLI host (<c>1</c> or <c>true</c>).</summary>
    public const string OptInVariable = "HARBOR_SKILL_FRESHNESS";

    /// <summary>
    ///     Register <see cref="CellForgeSkillFreshnessPanel" /> over the
    ///     host-shared <paramref name="model" /> (in-place replace on
    ///     duplicate id, same rule as every other provider).
    /// </summary>
    public static void RegisterSkillFreshness(this CellForgePanelRegistry panels, SkillFreshnessModel model)
    {
        ArgumentNullException.ThrowIfNull(panels);
        ArgumentNullException.ThrowIfNull(model);
        panels.Register(new CellForgeSkillFreshnessPanel(model));
    }

    /// <summary>True when the host asked for the freshness panel via the environment.</summary>
    public static bool SkillFreshnessOptInEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable(OptInVariable);
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }
}
