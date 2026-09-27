using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="PluginPanelModel" /> (+ <see cref="PanelFuzzy" />):
///     seed → status mapping, substring + fuzzy filtering, ↑↓ clamping and
///     the Enter-confirm contract. Deterministic — pure state only.
/// </summary>
public class PluginPanelModelTests
{
    private static readonly PluginSeed[] Seeds =
    [
        new("GitTools.cs", "1.2.0", "global", "/home/u/.harbor/plugins/GitTools.cs", Enabled: true, Loaded: true),
        new("TodoWrite.cs", null, "project", "/repo/.harbor/plugins/TodoWrite.cs", Enabled: true, Loaded: false),
        new("WebSearch.cs", "0.9.1", "global", "/home/u/.harbor/plugins/WebSearch.cs.disabled", Enabled: false, Loaded: true),
    ];

    private static PluginPanelModel Open()
    {
        var model = new PluginPanelModel();
        model.Show(Seeds);
        return model;
    }

    [Test]
    public async Task Show_MapsStatus_LoadedInstalledDisabled()
    {
        var model = Open();

        await Assert.That(model.Visible).IsTrue();
        await Assert.That(model.Results).Count().IsEqualTo(3);
        await Assert.That(model.Results[0].Status).IsEqualTo(PluginPanelStatus.Loaded);
        await Assert.That(model.Results[1].Status).IsEqualTo(PluginPanelStatus.Installed);
        // Disabled wins over a stale loaded flag (a renamed file never loads).
        await Assert.That(model.Results[2].Status).IsEqualTo(PluginPanelStatus.Disabled);
    }

    [Test]
    public async Task RowText_TitleFirst_DetailDimmedSecond()
    {
        var model = Open();

        await Assert.That(model.Results[0].Title).IsEqualTo("GitTools.cs");
        await Assert.That(model.Results[0].Detail).IsEqualTo("v1.2.0 · global · loaded");
        await Assert.That(model.Results[0].Group).IsEqualTo("global");
        await Assert.That(model.Results[1].Detail).IsEqualTo("unversioned · project · installed");
        await Assert.That(model.Results[0].RowText).IsEqualTo("GitTools.cs  v1.2.0 · global · loaded");
    }

    [Test]
    public async Task SetQuery_FiltersByName_AndResetsSelection()
    {
        var model = Open();
        model.MoveDown();
        model.MoveDown();

        model.SetQuery("todo");

        await Assert.That(model.Results).Count().IsEqualTo(1);
        await Assert.That(model.Results[0].Name).IsEqualTo("TodoWrite.cs");
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task SetQuery_FuzzySubsequence_Matches()
    {
        var model = Open();

        model.SetQuery("gtools");

        await Assert.That(model.Results).Count().IsEqualTo(1);
        await Assert.That(model.Results[0].Name).IsEqualTo("GitTools.cs");
    }

    [Test]
    public async Task SetQuery_NoMatch_ClearsSelection()
    {
        var model = Open();

        model.SetQuery("zzz-no-such-plugin");

        await Assert.That(model.Results).Count().IsEqualTo(0);
        await Assert.That(model.Confirm()).IsNull();
    }

    [Test]
    public async Task MoveDown_MoveUp_ClampAtBounds()
    {
        var model = Open();

        model.MoveUp();
        await Assert.That(model.SelectedIndex).IsEqualTo(0);

        model.MoveDown();
        model.MoveDown();
        model.MoveDown();
        await Assert.That(model.SelectedIndex).IsEqualTo(2);

        model.MoveUp();
        await Assert.That(model.SelectedIndex).IsEqualTo(1);
    }

    [Test]
    public async Task Confirm_ReturnsSelectedRow_ForToggle()
    {
        var model = Open();
        model.MoveDown();
        model.MoveDown();

        var confirmed = model.Confirm();

        await Assert.That(confirmed).IsNotNull();
        await Assert.That(confirmed!.FullPath).IsEqualTo("/home/u/.harbor/plugins/WebSearch.cs.disabled");
        await Assert.That(confirmed.Enabled).IsFalse();
    }

    [Test]
    public async Task Confirm_Hidden_ReturnsNull()
    {
        var model = new PluginPanelModel();

        await Assert.That(model.Confirm()).IsNull();
    }

    [Test]
    public async Task Hide_ResetsEverything()
    {
        var model = Open();
        model.SetQuery("git");
        model.Hide();

        await Assert.That(model.Visible).IsFalse();
        await Assert.That(model.Results).Count().IsEqualTo(0);
        await Assert.That(model.Confirm()).IsNull();
    }

    [Test]
    public async Task Fuzzy_EmptyQuery_MatchesAll()
    {
        await Assert.That(PanelFuzzy.Score("anything", string.Empty)).IsEqualTo(0);
        await Assert.That(PanelFuzzy.Score("anything", null)).IsEqualTo(0);
    }

    [Test]
    public async Task Fuzzy_Substring_OutranksSubsequence()
    {
        int substring = PanelFuzzy.Score("GitTools.cs", "git");
        int subsequence = PanelFuzzy.Score("Gxxixtxxxt", "git");

        await Assert.That(substring).IsGreaterThan(subsequence);
        await Assert.That(subsequence).IsGreaterThanOrEqualTo(0);
        await Assert.That(PanelFuzzy.Score("GitTools.cs", "zzz")).IsEqualTo(-1);
    }
}
