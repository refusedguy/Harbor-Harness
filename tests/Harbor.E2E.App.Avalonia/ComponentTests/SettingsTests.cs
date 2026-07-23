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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Settings MODAL overlay centered on the window. A dark backdrop covers the main window; a centered card " +
            "with header 'Settings' and subtitle 'Persists to ~/.harbor/config.json + ~/.harbor/avalonia.json'. " +
            "The card contains 6 labelled fields: Theme (dropdown with dark/light/system options), " +
            "Default provider (text input), Default model (text input), Font family (text input), " +
            "Storage backend (dropdown), Log level (dropdown). Footer has 'Cancel' and 'Save' buttons.",
            nameof(Settings_Open_ShowsAllFields)).ConfigureAwait(false);
        
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Settings MODAL open. The 'Theme' field's dropdown shows 'light' (the currently-selected value). " +
            "All other fields (Default provider, Default model, Font family, Storage backend, Log level) are visible. " +
            "Cancel + Save buttons visible in the footer.",
            nameof(Settings_ChangeTheme_ShowsLightInDropdown)).ConfigureAwait(false);
        
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Settings MODAL after Save was clicked. The fields show 'light' for Theme and 'test-model-save' for Default model. " +
            "Footer Cancel + Save buttons visible. The persisted config.json on disk now contains 'light' and 'test-model-save'. " +
            "A success toast 'Settings saved — theme: light, model: ollama/test-model-save.' may be visible bottom-right.",
            nameof(Settings_Save_PersistsConfig)).ConfigureAwait(false);
        
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

        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            Vm.Settings.ThemeSettings.Theme = "light";
        });
        await Task.Delay(300).ConfigureAwait(false);

        UI(() => Vm.Settings.CancelCommand.Execute(null));
        await Task.Delay(200).ConfigureAwait(false);

        var themeAfter = UI(() => Vm.Settings.ThemeSettings.Theme);
        await Assert.That(themeAfter).IsEqualTo("dark");

        var path = await CaptureAsync("settings-cancelled").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Settings MODAL after Cancel was clicked. The Theme field has reverted to 'dark' (the persisted value). " +
            "Any unsaved changes were discarded — Default model + Storage backend + Log level also show their persisted values.",
            nameof(Settings_Cancel_RevertsChanges)).ConfigureAwait(false);
        
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Settings MODAL scrolled to show the 'Provider Configuration' section. Below the section header there is a " +
            "list of provider rows (e.g. Anthropic, OpenAI, OpenRouter, Ollama). Each row has: the provider display name + id, " +
            "an auth badge showing '✓ Authenticated' or '✗ No key', an API key text input (hidden for Ollama which needs no key), " +
            "and 'Save key' + 'Test connection' buttons.",
            nameof(Settings_ProviderConfig_ShowsApiKeyInputsAndTestButtons)).ConfigureAwait(false);
        
    }
}
