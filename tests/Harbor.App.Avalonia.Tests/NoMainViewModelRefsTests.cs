using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Ensures palette, hotkeys, and views do not reference <see cref="MainViewModel" />
///     directly. Per §S1 of DECISIONS.md, these types must depend on
///     <see cref="IShellChrome" /> / <see cref="IWorkspaceCommands" /> ports, not on
///     the concrete MainViewModel.
/// </summary>
public sealed class NoMainViewModelRefsTests
{
    private static Type? MainViewModelType =>
        typeof(NoMainViewModelRefsTests).Assembly
            .GetType("Harbor.App.Avalonia.ViewModels.MainViewModel");

    private static bool HasMainViewModelRef(Type type)
    {
        var mainVm = MainViewModelType;
        if (mainVm is null) return false;

        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == mainVm))
            || type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(f => f.FieldType == mainVm)
            || type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(p => p.PropertyType == mainVm)
            || type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.GetParameters().Any(p => p.ParameterType == mainVm)
                           && m.Name.Contains("GetRequiredService", StringComparison.Ordinal));
    }

    // #1770: the twelve former copies differed only in the type they scanned —
    // the assert never changed. The type is the table now (`typeof(...)` is
    // legal in an attribute argument), and every row keeps its original test
    // name as the display name.
    [Test]
    [Arguments(typeof(ViewModels.CommandPaletteViewModel), DisplayName = "CommandPaletteViewModel_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Services.KeyboardShortcutService), DisplayName = "KeyboardShortcutService_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.ProviderBrowserView), DisplayName = "ProviderBrowserView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.DiffView), DisplayName = "DiffView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.TokenUsageView), DisplayName = "TokenUsageView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.FocusSessionView), DisplayName = "FocusSessionView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.SettingsView), DisplayName = "SettingsView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.CommandPaletteView), DisplayName = "CommandPaletteView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.Overlays.ModalHostView), DisplayName = "ModalHostView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.Controls.ToolCallCardView), DisplayName = "ToolCallCardView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.Shell.ActivityRailView), DisplayName = "ActivityRailView_DoesNotReferenceMainViewModel")]
    [Arguments(typeof(Views.Shell.RightDrawerView), DisplayName = "RightDrawerView_DoesNotReferenceMainViewModel")]
    public async Task Type_DoesNotReferenceMainViewModel(Type type)
    {
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }
}
