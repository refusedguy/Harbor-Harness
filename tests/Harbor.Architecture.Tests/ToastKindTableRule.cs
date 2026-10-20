// ToastKindTableRule.cs — issue #1543: the kind-to-presentation mapping is ONE
// table, and no product file may write a second one.
//
// THE DEFECT THIS GUARDS
// ----------------------
// A toast KIND and the shape it is drawn in was written in three places by two
// UI stacks:
//
//   * src/Harbor.Tui.CellForge/Chat/Widgets/ToastOverlay.cs   the glyph
//   * apps/Harbor.App.Avalonia/Views/ToastConverters.cs       the icon key
//   * apps/Harbor.App.Avalonia/Views/ToastConverters.cs       the brush key
//
// and a fourth copy in the desktop shell had already drifted in VALUE: it drew
// "▲" and "✕" where the terminal renderer drew "!" and "✗" for the same two
// kinds. Nothing caught it, because a mapping that lives in a switch is only
// wrong to the eye.
//
// THE TABLE THAT REPLACED THEM
// ----------------------------
// src/Harbor.Ui.Framework.Services/Services/ToastPresentation.cs — one row per
// ToastKind, holding a glyph and two theme resource KEYS. Every renderer reads
// its row; none of them decides what a kind looks like.
//
// THE RULE
// --------
// No product file may map a ToastKind member to a GLYPH, a THEME RESOURCE KEY
// or a presentation row in a switch or expression arm.
//
// WHAT THE RULE IS NOT
// --------------------
// It bans the ARM, not the mention. An arm whose value is neither a literal nor
// a construction is not a table, and is not reported. Two shapes in this tree
// are such arms. The CellForge overlay picks a palette colour for its accent
// strip, a colour that lives in the design-system project the shared table
// does not reference. The desktop shell converts one ToastKind enum into the
// other so it can reach the table at all — kind to kind, not kind to
// appearance.
//
// The scope is src/ + apps/, as the other single-source rules are. The test tree
// is not scanned: the CellForge cost gate deliberately keeps a frozen copy of
// the row build the toast used to do, and that copy is a cost baseline, not a
// presentation.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard. Three
// defences, in the shape the other rules here use:
//
//   * `Discovery_FindsTheProductTrees` — the walk really reads src/ + apps/,
//     and really sees the canonical table
//   * `Detector_FiresOnAKnownTable` — the matcher is handed the pre-#1543
//     shape and MUST report it
//   * `Detector_StaysQuietOnTheShapesThatAreNotTables` — the canonical table's
//     own shape, the palette arm, the enum bridge and prose about the old
//     switch must all stay silent

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #1543: one table says how a toast kind looks, and no product file writes
///     a second one.
/// </summary>
public class ToastKindTableRule
{
    /// <summary>The single banned shape's sub-id, shared by the rule data and the matcher.</summary>
    private const string SubId = "TOAST-KIND-TO-PRESENTATION-ARM";

    /// <summary>
    ///     The banned shape: a <c>ToastKind</c> member used as a switch or
    ///     expression arm whose value is a glyph, a theme resource key, or a
    ///     presentation row.
    /// </summary>
    /// <remarks>
    ///     The value half is what makes this a rule about TABLES rather than a
    ///     rule about mentions: an arm that names a palette colour or another
    ///     enum's member is not a second opinion about what a kind looks like,
    ///     and a rule that reported those would cry wolf and get switched off.
    /// </remarks>
    private static readonly Regex ForbiddenShape = new(
        @"ToastKind\s*\.\s*\w+\s*=>\s*(?:['""]|new\b)",
        RegexOptions.Compiled);

    /// <summary>The rule as data: one banned shape, no baseline, six controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "ToastKindTable",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                ForbiddenShape,
                "read the row from ToastPresentations (Harbor.Ui.Framework.Services) — the one table "
                + "that says how a toast kind looks (#1543)."),
        ],
        Controls =
        [
            // The pre-#1543 shape, verbatim from the old Avalonia converter.
            new ScanControl("Known.cs", """
                public static string IconResourceKeyFor(ToastKind? kind) =>
                    kind switch
                    {
                        ToastKind.Success => "IcCheck",
                        _ => "IcInfo",
                    };
                """, SubId),
            // The same table written as a presentation ROW — a second table in a
            // shape the literal half of the matcher alone would have missed.
            new ScanControl("Row.cs", """
                private static ToastPresentation Row(ToastKind kind) => kind switch
                {
                    ToastKind.Success => new('✓', "IcCheck", "StateSuccessBrush"),
                    _ => throw new InvalidOperationException(),
                };
                """, SubId),
            // The canonical table's own shape: a dictionary initialiser, not an
            // arm. The rule must not report the one place the mapping lives.
            new ScanControl("Canonical.cs", """
                private static readonly Dictionary<ToastKind, ToastPresentation> ByKind = new()
                {
                    [ToastKind.Info] = new('ℹ', "IcInfo", "AccentPrimaryBrush"),
                };
                """, null),
            // The CellForge accent arm: a PALETTE COLOUR, not a presentation.
            new ScanControl("Palette.cs", """
                private static ChatPalette Accent(ToastKind kind) => kind switch
                {
                    ToastKind.Success => ChatPalette.Success,
                    _ => ChatPalette.Accent,
                };
                """, null),
            // The desktop shell's enum bridge: kind to kind, so it can reach the
            // table at all.
            new ScanControl("Bridge.cs", """
                private static ToastKind Canonical(ToastKind kind) => kind switch
                {
                    ToastKind.Success => Harbor.Ui.Framework.Services.ToastKind.Success,
                    _ => ToastKind.Info,
                };
                """, null),
            // Prose about the old table. A guard that fails on its own
            // documentation is a guard nobody keeps.
            new ScanControl("Prose.cs", """
                // The old switch read: ToastKind.Success => "IcCheck".
                """, null),
        ],
        MinHits = 500,
        MustContain = ["src/Harbor.Ui.Framework.Services/Services/ToastPresentation.cs"],
    };

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     No product file maps a toast kind to a presentation of its own.
    /// </summary>
    [Test]
    public async Task NoProductFile_MapsAToastKindToAPresentation()
    {
        List<string> violations = ScanRunner.Evaluate(Rule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "a toast kind and the shape it is drawn in is one fact, and it lives in one table "
                + "(ToastPresentations). Two tables are not a style difference: they are two "
                + "opinions about what a kind looks like, and the desktop shell's copy had already "
                + "drifted from the terminal renderer's — the same warning drew \"▲\" and \"✕\" "
                + "there and \"!\" and \"✗\" here (#1543). Offenders: "
                + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     Discovery really reads the product trees, and really sees the
    ///     canonical table. A scan rooted at a path that does not exist returns
    ///     an empty set and satisfies every rule above — the NetArchTest trap,
    ///     in its filesystem form.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheProductTrees()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/ + apps/ hold far more than 500 C# files, and the scope must still contain the "
                + "canonical table. A smaller count — or a scope that stopped seeing that file — means "
                + "the tree walk broke and the rule above would be reporting 'clean' about nothing. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The matcher is handed the pre-#1543 shape and
    ///     MUST report it, and the four shapes that are not tables MUST stay
    ///     silent. A matcher that stopped working would report nothing and the
    ///     rule above would go green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task Detector_FiresOnAKnownTable()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the pre-#1543 shape must be detected, or the rule guards nothing; the canonical "
                + "table's own shape, the palette arm, the enum bridge and prose about the old switch "
                + "must stay quiet, or the rule forbids the fix and cries wolf. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     The detector stays quiet on the shapes that are not tables — proved
    ///     by the same control verdict, read for the other half.
    /// </summary>
    [Test]
    public async Task Detector_StaysQuietOnTheShapesThatAreNotTables()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "a rule that reports the canonical table, the palette arm or the enum bridge would be "
                + "switched off rather than obeyed. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself.
    ///     Vacuous while the table is empty, and deliberately so: it is wired
    ///     from the first row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting
    ///     the row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }
}
