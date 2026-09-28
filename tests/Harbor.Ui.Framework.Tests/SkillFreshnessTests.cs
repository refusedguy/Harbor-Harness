using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Tests for the skill-freshness slice (KILLER_FEATURES §2.7 Feature 10,
///     issue #23): <see cref="SkillFreshnessEntry"/> status/pill derivation,
///     <see cref="SkillFreshnessModel"/> snapshot semantics, and
///     <see cref="PanelRows.SkillFreshnessRows"/> rendering.
/// </summary>
public class SkillFreshnessTests
{
    [Test]
    public async Task Status_MatchingHashes_IsCurrent()
    {
        var entry = new SkillFreshnessEntry("code-review", "9ef94db7", "9ef94db7");
        await Assert.That(entry.Status).IsEqualTo(SkillFreshnessStatus.Current);
        await Assert.That(entry.PillText).IsEqualTo("✓ current");
        await Assert.That(entry.IsStale).IsFalse();
    }

    [Test]
    public async Task Status_HashMatch_IgnoresCaseAndWhitespace()
    {
        var entry = new SkillFreshnessEntry("code-review", "  9EF94DB7 ", "9ef94db7");
        await Assert.That(entry.Status).IsEqualTo(SkillFreshnessStatus.Current);
    }

    [Test]
    public async Task Status_DifferingHashes_IsChanged()
    {
        var entry = new SkillFreshnessEntry("code-review", "aaaa", "bbbb");
        await Assert.That(entry.Status).IsEqualTo(SkillFreshnessStatus.Changed);
        await Assert.That(entry.PillText).IsEqualTo("● changed");
        await Assert.That(entry.IsStale).IsTrue();
    }

    [Test]
    public async Task Status_NullLock_IsUntracked()
    {
        var entry = new SkillFreshnessEntry("code-review", "aaaa", null);
        await Assert.That(entry.Status).IsEqualTo(SkillFreshnessStatus.Untracked);
        await Assert.That(entry.PillText).IsEqualTo("? untracked");
        await Assert.That(entry.IsStale).IsTrue();
    }

    [Test]
    public async Task Status_NullOrBlankInstall_IsMissing()
    {
        await Assert.That(new SkillFreshnessEntry("a", null, "bbbb").Status).IsEqualTo(SkillFreshnessStatus.Missing);
        await Assert.That(new SkillFreshnessEntry("a", "   ", "bbbb").Status).IsEqualTo(SkillFreshnessStatus.Missing);
        await Assert.That(new SkillFreshnessEntry("a", null, "bbbb").PillText).IsEqualTo("✗ missing");
        await Assert.That(new SkillFreshnessEntry("a", null, "bbbb").IsStale).IsTrue();
    }

    [Test]
    public async Task Model_SetSkills_CountsStale()
    {
        var model = new SkillFreshnessModel();
        model.SetSkills([
            new SkillFreshnessEntry("fresh", "aa", "aa"),
            new SkillFreshnessEntry("drifted", "aa", "bb"),
            new SkillFreshnessEntry("gone", null, "bb"),
        ]);

        await Assert.That(model.GetEntries().Count).IsEqualTo(3);
        await Assert.That(model.StaleCount).IsEqualTo(2);
    }

    [Test]
    public async Task Model_Clear_EmptiesSnapshot()
    {
        var model = new SkillFreshnessModel();
        model.SetSkills([new SkillFreshnessEntry("drifted", "aa", "bb")]);
        model.Clear();

        await Assert.That(model.GetEntries().Count).IsEqualTo(0);
        await Assert.That(model.StaleCount).IsEqualTo(0);
    }

    [Test]
    public async Task Rows_Empty_RendersPlaceholder()
    {
        var rows = PanelRows.SkillFreshnessRows(Array.Empty<SkillFreshnessEntry>());
        string text = string.Join("\n", rows);
        await Assert.That(text).Contains("Skills (0)");
        await Assert.That(text).Contains("No skills installed.");
    }

    [Test]
    public async Task Rows_WithEntries_RendersPillAndName()
    {
        var rows = PanelRows.SkillFreshnessRows([
            new SkillFreshnessEntry("code-review", "aa", "aa"),
            new SkillFreshnessEntry("stale-one", "aa", "bb"),
        ]);
        string text = string.Join("\n", rows);
        await Assert.That(text).Contains("Skills (2)");
        await Assert.That(text).Contains("✓ current  code-review");
        await Assert.That(text).Contains("● changed  stale-one");
        await Assert.That(text).Contains("1 need attention");
    }

    [Test]
    public async Task Rows_AllCurrent_RendersUpToDateFooter()
    {
        var rows = PanelRows.SkillFreshnessRows([new SkillFreshnessEntry("code-review", "aa", "aa")]);
        string text = string.Join("\n", rows);
        await Assert.That(text).Contains("All skills up to date.");
    }

    // ── #384: aggregate status-line pill ──────────────────────────────────

    [Test]
    public async Task Aggregate_AllCurrent_IsNoPill()
    {
        await Assert.That(SkillFreshnessAggregate.Of([
            new SkillFreshnessEntry("a", "aa", "aa"),
            new SkillFreshnessEntry("b", "bb", "bb"),
        ])).IsNull();

        await Assert.That(SkillFreshnessAggregate.Of([])).IsNull();
    }

    [Test]
    public async Task Aggregate_OnlyChanged_RendersCountPill()
    {
        var summary = SkillFreshnessAggregate.Of([
            new SkillFreshnessEntry("a", "aa", "aa"),
            new SkillFreshnessEntry("b", "aa", "bb"),
            new SkillFreshnessEntry("c", "cc", "dd"),
        ]);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.Text).IsEqualTo("skills ●2 changed");
        await Assert.That(summary.Stale).IsEqualTo(2);
        await Assert.That(summary.Style).IsEqualTo(UiSpanStyle.Accent);
    }

    [Test]
    public async Task Aggregate_MixedBuckets_RendersInFixedOrder()
    {
        var summary = SkillFreshnessAggregate.Of([
            new SkillFreshnessEntry("a", "aa", "bb"),
            new SkillFreshnessEntry("b", null, "bb"),
            new SkillFreshnessEntry("c", "cc", null),
            new SkillFreshnessEntry("d", "dd", "dd"),
        ]);

        await Assert.That(summary!.Text).IsEqualTo("skills ●1 changed ✗1 missing ?1 untracked");
        await Assert.That(summary.Changed).IsEqualTo(1);
        await Assert.That(summary.Missing).IsEqualTo(1);
        await Assert.That(summary.Untracked).IsEqualTo(1);
    }

    [Test]
    public async Task Aggregate_Missing_EscalatesToDanger()
    {
        var summary = SkillFreshnessAggregate.Of([new SkillFreshnessEntry("gone", null, "bb")]);
        await Assert.That(summary!.Text).IsEqualTo("skills ✗1 missing");
        await Assert.That(summary.Style).IsEqualTo(UiSpanStyle.Danger);
    }

    [Test]
    public async Task Aggregate_OnlyUntracked_StaysDim()
    {
        var summary = SkillFreshnessAggregate.Of([new SkillFreshnessEntry("loose", "aa", null)]);
        await Assert.That(summary!.Text).IsEqualTo("skills ?1 untracked");
        await Assert.That(summary.Style).IsEqualTo(UiSpanStyle.Dim);
    }

    [Test]
    public async Task Model_Rrevision_AdvancesOnEveryMutation()
    {
        var model = new SkillFreshnessModel();
        int start = model.Revision;

        model.SetSkills([new SkillFreshnessEntry("a", "aa", "bb")]);
        int afterSet = model.Revision;
        model.Clear();

        await Assert.That(afterSet).IsGreaterThan(start);
        await Assert.That(model.Revision).IsGreaterThan(afterSet);
    }
}
