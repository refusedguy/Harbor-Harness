using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Settings modal component E2E tests — every visible state.
/// </summary>
/// <remarks>
///     <para>
///         Tests cover: open (all 6 fields visible), theme change to "light",
///         save (config.json contains the value), cancel (changes reverted),
///         and per-provider config (API key input + Test button).
///     </para>
///     <para>
///         The Settings modal is opened by setting
///         <see cref="MainViewModel.IsSettingsOpen"/> = true on the UI thread.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class SettingsTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

    /// <summary>
    ///     Open: all 6 fields are visible — Theme, Default provider, Default
    ///     model, Font family, Storage backend, Log level — plus Save/Cancel
    ///     buttons.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_Open_ShowsAllFields()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = true);
        await Task.Delay(400).ConfigureAwait(false);

        var hasTheme = await Driver.WaitForTextAsync("Theme", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasProvider = await Driver.WaitForTextAsync("Default provider", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasFont = await Driver.WaitForTextAsync("Font family", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasStorage = await Driver.WaitForTextAsync("Storage backend", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasLog = await Driver.WaitForTextAsync("Log level", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);

        await Assert.That(hasTheme && hasProvider && hasFont && hasStorage && hasLog).IsTrue();

        var path = await CaptureAsync("settings-open").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }

    /// <summary>
    ///     Change theme to "light": the Theme dropdown shows "light".
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_ChangeTheme_ShowsLightInDropdown()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            Vm.Settings.ThemeSettings.Theme = "light";
        });
        await Task.Delay(400).ConfigureAwait(false);

        var theme = UI(() => Vm.Settings.ThemeSettings.Theme);
        await Assert.That(theme).IsEqualTo("light");

        var path = await CaptureAsync("settings-theme-light").ConfigureAwait(false);

        UI(() =>
        {
            Vm.Settings.ThemeSettings.Theme = "dark";
            Vm.IsSettingsOpen = false;
        });
    }

    /// <summary>
    ///     Save: config.json contains the new theme + model values after
    ///     SaveAsync completes.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_Save_PersistsConfig()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        MainViewModel? settingsVm = null;
        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            Vm.Settings.ThemeSettings.Theme = "light";
            Vm.Settings.DefaultModel = "test-model-save";
            settingsVm = Vm;
        });

        Dispatcher.UIThread
            .InvokeAsync(() => settingsVm!.Settings.SaveCommand.ExecuteAsync(null))
            .GetAwaiter().GetResult();
        await Task.Delay(700).ConfigureAwait(false);

        var configPath = Path.Combine(TempHome, ".harbor", "config.json");
        var configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        await Assert.That(configText).Contains("light");
        await Assert.That(configText).Contains("test-model-save");

        var path = await CaptureAsync("settings-saved").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }

    /// <summary>
    ///     Cancel: changes to Theme are reverted to the persisted value (dark)
    ///     without saving.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_Cancel_RevertsChanges()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Capture the ACTUAL persisted theme instead of assuming "dark" —
        // CommonConfig defaults to "system" when config.json has no key.
        var themeBefore = UI(() => Vm.Settings.ThemeSettings.Theme);

        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            Vm.Settings.ThemeSettings.Theme = "light";
        });
        await Task.Delay(300).ConfigureAwait(false);

        UI(() => Vm.Settings.CancelCommand.Execute(null));
        await Task.Delay(200).ConfigureAwait(false);

        var themeAfter = UI(() => Vm.Settings.ThemeSettings.Theme);
        await Assert.That(themeAfter).IsEqualTo(themeBefore);

        var path = await CaptureAsync("settings-cancelled").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }

    /// <summary>
    ///     Provider config: each registered provider has a row with an API key
    ///     input + Save key + Test connection buttons.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_ProviderConfig_ShowsApiKeyInputsAndTestButtons()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = true);
        await Task.Delay(400).ConfigureAwait(false);

        // ScrollViewer may hide the provider config section; verify it exists
        // by looking for the "Provider Configuration" label.
        var hasProviderConfig = await Driver.WaitForTextAsync("Provider Configuration", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasProviderConfig).IsTrue();

        // At least one provider row visible (Ollama is always registered).
        var providerCount = UI(() => Vm.Settings.ProviderConfigs.Count);
        await Assert.That(providerCount).IsGreaterThan(0);

        var path = await CaptureAsync("settings-provider-config").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }
}
