using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Mechanical a11y subset gate for #433 (WCAG 2.1 AA, 2.4.3 Focus Order +
///     2.1.1 Keyboard): the Tab order of the covered shell/chrome views must
///     match their visual reading order, every directly-focusable control must
///     stay keyboard-reachable, and no control may opt out of Tab navigation.
///     Avalonia tabs in visual-tree order while no TabIndex override exists,
///     and no TabIndex override exists anywhere under
///     <c>apps/Harbor.App.Avalonia/</c> (see <c>docs/ACCESSIBILITY.md</c> §2.5)
///     — so pinning document order pins Tab order. Same headless-safe
///     construction as <c>AccessibilityNamesTests</c>: only views that inflate
///     without a headless session are covered (ChatView/MainWindow need one —
///     see ViewInflationTests).
/// </summary>
[NotInParallel("avalonia-headless")]
public class AccessibilityFocusOrderTests
{
    [Test]
    public async Task TitleBarView_FocusOrder_MatchesVisualOrder() =>
        await AssertFocusOrder(
            new Views.Chrome.TitleBarView(),
            "TitleBar_CommandPaletteTrigger", "TitleBar_ThemeButton", "TitleBar_SettingsButton");

    [Test]
    public async Task ActivityRailView_FocusOrder_MatchesVisualOrder() =>
        await AssertFocusOrder(
            new Views.Shell.ActivityRailView(),
            "Rail_ToggleButton", "Rail_BoardButton", "Rail_SearchButton", "Rail_DiffButton",
            "Refresh file tree", "FileTreeView", "Rail_ThemeButton", "Rail_SettingsButton");

    [Test]
    public async Task StatusBarView_FocusOrder_MatchesVisualOrder() =>
        await AssertFocusOrder(
            new Views.Shell.StatusBarView(),
            "StatusBar_ModelPickerButton");

    [Test]
    public async Task SessionsFlyoutView_FocusOrder_MatchesVisualOrder() =>
        await AssertFocusOrder(
            new Views.Shell.SessionsFlyoutView(),
            "SessionsFlyout_NewSessionButton", "SessionsFlyout_SearchBox", "SessionsFlyout_List");

    private static async Task AssertFocusOrder(Control view, params string[] expectedKeys)
    {
        var interactives = view.GetLogicalDescendants()
            .OfType<Control>()
            .Where(c => c is Button or TextBox or ComboBox or ListBox or Expander or TreeView or MenuItem)
            .ToList();

        // 2.1.1 Keyboard: every directly-focusable control stays
        // keyboard-reachable. ListBox/TreeView containers are excluded on
        // purpose: they delegate keyboard interaction to their realized items
        // and report Focusable=False when bare-constructed (framework behavior,
        // observed in CI — not a Tab trap; a trap would be IsTabStop=False,
        // which the scan below forbids everywhere).
        var unreachable = interactives
            .Where(c => c is Button or TextBox or ComboBox or Expander or MenuItem)
            .Where(c => !c.Focusable || !c.IsTabStop)
            .Select(c => $"{Describe(c)} Focusable={c.Focusable} IsTabStop={c.IsTabStop}")
            .ToList();

        await Assert.That(unreachable).IsEmpty()
            .Because($"{view.GetType().Name} has {unreachable.Count} focusable control(s) unreachable via keyboard: {string.Join("; ", unreachable)}");

        // No Tab opt-outs anywhere in these views (pins the §2.5 no-focus-trap finding).
        var tabOptOuts = view.GetLogicalDescendants()
            .OfType<Control>()
            .Where(c => !c.IsTabStop)
            .Select(Describe)
            .ToList();

        await Assert.That(tabOptOuts).IsEmpty()
            .Because($"{view.GetType().Name} has {tabOptOuts.Count} control(s) with IsTabStop=False: {string.Join("; ", tabOptOuts)}");

        // 2.4.3 Focus Order: document order (== Tab order under the default
        // FocusManager) matches the visual reading order.
        var actual = interactives.Select(Describe).ToList();

        await Assert.That(string.Join(" > ", actual)).IsEqualTo(string.Join(" > ", expectedKeys))
            .Because($"{view.GetType().Name} Tab order changed. Actual: {string.Join(" > ", actual)}");
    }

    private static string Describe(Control c) =>
        AutomationProperties.GetAutomationId(c) is { Length: > 0 } id ? id
        : !string.IsNullOrEmpty(c.Name) ? c.Name
        : AutomationProperties.GetName(c) is { Length: > 0 } name ? name
        : c.GetType().Name;
}
