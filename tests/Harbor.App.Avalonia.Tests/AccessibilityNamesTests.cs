using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Mechanical a11y subset gate for #433 (WCAG 2.1 AA, 4.1.2 Name/Role/Value):
///     every interactive control in the covered shell/chrome views must expose
///     an accessible name (<c>AutomationProperties.Name</c>) or at minimum a
///     stable <c>AutomationProperties.AutomationId</c>, so a new unnamed
///     icon-button cannot ship unnoticed.
///     This is a deliberately narrow, runtime-safe subset of the manual audit
///     in <c>docs/ACCESSIBILITY.md</c>: only views that inflate without a
///     headless session are covered (ChatView/MainWindow need one and are
///     flaky under headless — see ViewInflationTests). Controls-level views
///     with the known Avalonia 12 "Stack empty" inflation bug are excluded.
/// </summary>
[NotInParallel("avalonia-headless")]
public class AccessibilityNamesTests
{
    [Test]
    public async Task ActivityRailView_InteractiveControls_HaveAccessibleNameOrId() =>
        await AssertUnnamedInteractiveControlsEmpty(new Views.Shell.ActivityRailView());

    [Test]
    public async Task StatusBarView_InteractiveControls_HaveAccessibleNameOrId() =>
        await AssertUnnamedInteractiveControlsEmpty(new Views.Shell.StatusBarView());

    [Test]
    public async Task SessionsFlyoutView_InteractiveControls_HaveAccessibleNameOrId() =>
        await AssertUnnamedInteractiveControlsEmpty(new Views.Shell.SessionsFlyoutView());

    [Test]
    public async Task TitleBarView_InteractiveControls_HaveAccessibleNameOrId() =>
        await AssertUnnamedInteractiveControlsEmpty(new Views.Chrome.TitleBarView());

    private static async Task AssertUnnamedInteractiveControlsEmpty(Control view)
    {
        var unnamed = view.GetLogicalDescendants()
            .OfType<Control>()
            .Where(c => c is Button or TextBox or ComboBox or ListBox or Expander or TreeView or MenuItem)
            .Where(c => string.IsNullOrEmpty(AutomationProperties.GetName(c))
                     && string.IsNullOrEmpty(AutomationProperties.GetAutomationId(c)))
            .Select(c => $"{c.GetType().Name} Name='{c.Name}'")
            .ToList();

        await Assert.That(unnamed).IsEmpty()
            .Because($"{view.GetType().Name} has {unnamed.Count} interactive control(s) without Name/AutomationId: {string.Join("; ", unnamed)}");
    }
}
