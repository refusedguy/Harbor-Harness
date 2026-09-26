using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Contract tests for <see cref="CellForgeSkillFreshnessPanel" /> (issue #23,
///     skill freshness pill slice 1): identity (<c>skill-freshness</c> id),
///     <c>Build</c> rendering pill rows from an injected
///     <see cref="SkillFreshnessModel" />, and non-interactive <c>OnKey</c>.
///     The panel is host-registered (opt-in), not a renderer builtin.
/// </summary>
public class CellForgeSkillFreshnessPanelTests
{
    private static PanelContext Ctx(UiState state, int width = 80, int height = 24, IServiceProvider? services = null) =>
        new(state, width, height, services);

    private static IReadOnlyList<string> Rows(object? widget) => widget switch
    {
        null => Array.Empty<string>(),
        string s => s.Split('\n'),
        IReadOnlyList<string> rows => rows,
        IEnumerable<string> lines => lines.ToArray(),
        _ => new[] { widget.ToString() ?? string.Empty },
    };

    private static string Joined(object? widget) => string.Join("\n", Rows(widget));

    private static CellForgeSkillFreshnessPanel WithModel(out SkillFreshnessModel model)
    {
        model = new SkillFreshnessModel();
        model.SetSkills([
            new SkillFreshnessEntry("code-review", "aa", "aa"),
            new SkillFreshnessEntry("drifted", "aa", "bb"),
        ]);
        return new CellForgeSkillFreshnessPanel(model);
    }

    [Test]
    public async Task Contract_Id_Title_Placement_Size()
    {
        var panel = new CellForgeSkillFreshnessPanel();
        await Assert.That(panel.Id).IsEqualTo("skill-freshness");
        await Assert.That(panel.Title).IsEqualTo("Skills");
        await Assert.That(panel.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);
        await Assert.That(panel.DefaultSize).IsEqualTo(40);
    }

    [Test]
    public async Task Build_Returns_CellRows_NotSpectreWidgets()
    {
        object? widget = new CellForgeSkillFreshnessPanel().Build(Ctx(new UiState()));
        await Assert.That(widget is IReadOnlyList<string>).IsTrue();
    }

    [Test]
    public async Task Build_Empty_RendersPlaceholder()
    {
        string text = Joined(new CellForgeSkillFreshnessPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("Skills (0)");
        await Assert.That(text).Contains("No skills installed.");
    }

    [Test]
    public async Task Build_WithEntries_RendersPillRows()
    {
        var panel = WithModel(out _);
        string text = Joined(panel.Build(Ctx(new UiState())));
        await Assert.That(text).Contains("✓ current  code-review");
        await Assert.That(text).Contains("● changed  drifted");
        await Assert.That(text).Contains("1 need attention");
    }

    [Test]
    public async Task Build_Reflects_ModelRefresh()
    {
        var panel = WithModel(out var model);
        model.SetSkills([new SkillFreshnessEntry("only", "aa", "aa")]);
        string text = Joined(panel.Build(Ctx(new UiState())));
        await Assert.That(text).Contains("Skills (1)");
        await Assert.That(text).Contains("All skills up to date.");
        await Assert.That(text).DoesNotContain("drifted");
    }

    [Test]
    public async Task Clipping_TinyViewport_NeverThrows()
    {
        var panel = WithModel(out _);
        var rows = Rows(panel.Build(Ctx(new UiState(), width: 10, height: 3, services: null)));
        await Assert.That(rows.Count <= 3).IsTrue();
        foreach (string line in rows)
        {
            await Assert.That(line.Length <= 10).IsTrue();
        }
    }

    [Test]
    public async Task OnKey_NeverConsumes()
    {
        var panel = WithModel(out _);
        var ctx = Ctx(new UiState());
        await Assert.That(panel.OnKey(UiKey.ForChar('j'), ctx)).IsFalse();
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), ctx)).IsFalse();
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Escape), ctx)).IsFalse();
    }
}
