using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="ProviderPanelModel" />: display-name-first rows
///     with auth status, ready/needs-key grouping, fuzzy filtering and the
///     Enter-confirm contract. Deterministic — pure state only.
/// </summary>
public class ProviderPanelModelTests
{
    private static readonly ProviderSeed[] Seeds =
    [
        new("kilocode", "Kilo Code Gateway", "Multi-provider gateway with FREE models", RequiresKey: true, HasKey: false),
        new("anthropic", "Anthropic (Claude)", "Direct Anthropic API", RequiresKey: true, HasKey: true),
        new("ollama", "Ollama (local)", "Local LLM inference", RequiresKey: false, HasKey: false),
    ];

    private static ProviderPanelModel Open()
    {
        var model = new ProviderPanelModel();
        model.Show(Seeds);
        return model;
    }

    [Test]
    public async Task Show_ListsAll_WithAuthStatus()
    {
        var model = Open();

        await Assert.That(model.Visible).IsTrue();
        await Assert.That(model.Results).Count().IsEqualTo(3);
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
        await Assert.That(model.Results[0].AuthText).IsEqualTo("○ no key");
        await Assert.That(model.Results[1].AuthText).IsEqualTo("✓ key set");
        await Assert.That(model.Results[2].AuthText).IsEqualTo("local · no key needed");
    }

    [Test]
    public async Task RowText_DisplayNameFirst_IdDimmedSecond()
    {
        var model = Open();

        await Assert.That(model.Results[0].Title).IsEqualTo("Kilo Code Gateway");
        await Assert.That(model.Results[0].Detail).IsEqualTo("kilocode · ○ no key");
        await Assert.That(model.Results[0].Group).IsEqualTo("2 · Needs key");
        await Assert.That(model.Results[1].Group).IsEqualTo("1 · Ready");
        await Assert.That(model.Results[2].Group).IsEqualTo("1 · Ready");
    }

    [Test]
    public async Task SetQuery_FiltersById_DisplayName_Description()
    {
        var model = Open();

        model.SetQuery("kilo");
        await Assert.That(model.Results).Count().IsEqualTo(1);
        await Assert.That(model.Results[0].Id).IsEqualTo("kilocode");

        model.SetQuery("local llm");
        await Assert.That(model.Results).Count().IsEqualTo(1);
        await Assert.That(model.Results[0].Id).IsEqualTo("ollama");
    }

    [Test]
    public async Task SetQuery_NoMatch_ClearsSelection()
    {
        var model = Open();

        model.SetQuery("zzz-no-such-provider");

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
    public async Task Confirm_ReturnsSelected_AfterMove()
    {
        var model = Open();
        model.MoveDown();

        var confirmed = model.Confirm();

        await Assert.That(confirmed).IsNotNull();
        await Assert.That(confirmed!.Id).IsEqualTo("anthropic");
        await Assert.That(confirmed.HasKey).IsTrue();
    }

    [Test]
    public async Task Confirm_Hidden_ReturnsNull()
    {
        var model = new ProviderPanelModel();

        await Assert.That(model.Confirm()).IsNull();
    }

    [Test]
    public async Task Hide_ResetsEverything()
    {
        var model = Open();
        model.SetQuery("kilo");
        model.Hide();

        await Assert.That(model.Visible).IsFalse();
        await Assert.That(model.Query).IsEqualTo(string.Empty);
        await Assert.That(model.Results).Count().IsEqualTo(0);
        await Assert.That(model.Confirm()).IsNull();
    }
}
