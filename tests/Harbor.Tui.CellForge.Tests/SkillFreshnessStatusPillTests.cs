using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #384: the aggregate skill-freshness pill in the CellForge status
///     line. The pill is <b>default-on</b> — no env var — because it lives in
///     the footer row, which has no <c>Alt+1..9</c> slot order to preserve; the
///     per-skill detail panel stays host opt-in. A clean snapshot renders
///     nothing at all (no-data ⇒ no segment), so the row stays byte-identical
///     to the pre-#384 layout for every workspace whose skills are current.
/// </summary>
public class SkillFreshnessStatusPillTests
{
    private static UiState MockState(string status = "idle") => new()
    {
        Status = status,
        Provider = "prov",
        Model = "m",
        AgentName = "code",
        Cost = new CostSnapshot(0, 0, 0m),
    };

    private static SkillFreshnessModel Model(params SkillFreshnessEntry[] entries)
    {
        var model = new SkillFreshnessModel();
        model.SetSkills(entries);
        return model;
    }

    private static string Joined(StatusSeg[] workspace, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(" | ");
            }

            sb.Append(workspace[i].Text);
        }

        return sb.ToString();
    }

    private static int Find(StatusSeg[] workspace, int count, string text)
    {
        for (int i = 0; i < count; i++)
        {
            if (workspace[i].Text == text)
            {
                return i;
            }
        }

        return -1;
    }

    private static SkillFreshnessSummary Stale(params SkillFreshnessEntry[] entries) =>
        SkillFreshnessAggregate.Of(entries)!;

    [Test]
    public async Task Build_NoSkillsSummary_AddsNoSegment()
    {
        var ws = new StatusSeg[StatusProjectorPanel.MaxSegments];
        int n = StatusProjectorPanel.BuildSegments(MockState(), ws);

        // chrome, status, agent, scroll — byte-identical to the pre-#384 row.
        await Assert.That(n).IsEqualTo(4);
        await Assert.That(Joined(ws, n)).DoesNotContain("skills");
    }

    [Test]
    public async Task Build_StaleSkills_AppendsAggregateSegment()
    {
        var ws = new StatusSeg[StatusProjectorPanel.MaxSegments];
        var summary = Stale(new SkillFreshnessEntry("a", "aa", "bb"), new SkillFreshnessEntry("b", "cc", "dd"));
        int n = StatusProjectorPanel.BuildSegments(MockState(), ws, skills: summary);

        await Assert.That(n).IsEqualTo(5);
        int index = Find(ws, n, "skills ●2 changed");
        await Assert.That(index).IsEqualTo(3);
        await Assert.That(ws[index].Accent).IsEqualTo(StatusAccent.Accent);
    }

    [Test]
    public async Task Build_StaleSkills_SitsAfterRetryBeforeScroll()
    {
        var ws = new StatusSeg[StatusProjectorPanel.MaxSegments];
        var state = new UiState
        {
            Status = "idle",
            Provider = "prov",
            Model = "m",
            AgentName = "code",
            Cost = new CostSnapshot(0, 0, 0m),
            ScrollOffset = 5,
            ViewportLines = 10,
            TotalLines = 20,
        };
        int n = StatusProjectorPanel.BuildSegments(
            state, ws, "retry 1/3 in 4s", skills: Stale(new SkillFreshnessEntry("a", "aa", "bb")));

        int retry = Find(ws, n, "retry 1/3 in 4s");
        int skills = Find(ws, n, "skills ●1 changed");
        int scroll = Find(ws, n, "scroll 50%");
        await Assert.That(retry).IsGreaterThanOrEqualTo(0);
        await Assert.That(skills).IsEqualTo(retry + 1);
        await Assert.That(scroll).IsGreaterThan(skills);
    }

    [Test]
    public async Task Build_StaleSkills_IsFixedPrioritySoTruncationKeepsIt()
    {
        var ws = new StatusSeg[StatusProjectorPanel.MaxSegments];
        int n = StatusProjectorPanel.BuildSegments(
            MockState("running"), ws, skills: Stale(new SkillFreshnessEntry("a", null, "bb")));
        int index = Find(ws, n, "skills ✗1 missing");

        await Assert.That(index).IsGreaterThanOrEqualTo(0);
        await Assert.That(ws[index].FixedPriority).IsTrue();
        await Assert.That(ws[index].Accent).IsEqualTo(StatusAccent.Error);
    }

    [Test]
    public async Task Panel_ProjectedSkills_PaintsPillAndUpdatesInPlace()
    {
        var composer = new ComposerController();
        var screen = ChatScreen.Build(composer, new StatusViewModel { Model = "m" }, includeSidebar: false);
        screen.Status.ProjectedState = MockState("idle");
        var model = Model(new SkillFreshnessEntry("drifted", "aa", "bb"));
        screen.Status.ProjectedSkills = model;

        await Assert.That(Paint(screen)).Contains("skills ●1 changed");

        // `/skills refresh` or `/skills update` reseeds the shared model — the
        // pill must follow on the next paint without any host push.
        model.SetSkills([new SkillFreshnessEntry("drifted", "aa", "aa")]);
        await Assert.That(Paint(screen)).DoesNotContain("skills");
    }

    [Test]
    public async Task Panel_WithoutModel_PaintsNoPill()
    {
        var composer = new ComposerController();
        var screen = ChatScreen.Build(composer, new StatusViewModel { Model = "m" }, includeSidebar: false);
        screen.Status.ProjectedState = MockState("idle");

        await Assert.That(Paint(screen)).DoesNotContain("skills");
    }

    /// <summary>Fresh buffer per paint — the footer does not clear its own row.</summary>
    private static string Paint(ChatScreen screen)
    {
        var buffer = new ScreenBuffer(80, 8);
        screen.Tree.Solve(80, 8);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return GridDump.Art(buffer);
    }
}
