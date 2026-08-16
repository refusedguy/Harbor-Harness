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

    [Test]
    public async Task CommandPaletteViewModel_DoesNotReferenceMainViewModel()
    {
        var type = typeof(ViewModels.CommandPaletteViewModel);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task KeyboardShortcutService_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Services.KeyboardShortcutService);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task ProviderBrowserView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.ProviderBrowserView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task DiffView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.DiffView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task TokenUsageView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.TokenUsageView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task FocusSessionView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.FocusSessionView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task SettingsView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.SettingsView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task CommandPaletteView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.CommandPaletteView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task ModalHostView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.Overlays.ModalHostView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task ToolCallCardView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.Controls.ToolCallCardView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task ActivityRailView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.Shell.ActivityRailView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }

    [Test]
    public async Task RightDrawerView_DoesNotReferenceMainViewModel()
    {
        var type = typeof(Views.Shell.RightDrawerView);
        await Assert.That(HasMainViewModelRef(type)).IsFalse();
    }
}
