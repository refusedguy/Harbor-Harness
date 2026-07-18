using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Settings dialog view-model. Holds the editable provider/model configuration
///     (read from env vars on first open). Settings are applied on Save by mutating
///     the in-memory registries (or, for permanent changes, writing to
///     <c>~/.harbor/config.json</c> — not implemented in v0.4 for the standalone app).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ToastService _toasts;

    /// <summary>Construct the settings view-model.</summary>
    public SettingsViewModel(ThemeService theme, ILogger<SettingsViewModel> logger, ToastService toasts)
    {
        _theme = theme;
        _logger = logger;
        _toasts = toasts;
        Model = Environment.GetEnvironmentVariable("HARBOR_MODEL") ?? "ollama/qwen2.5-coder:7b";
        Storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? "memory";
        LogLevel = Environment.GetEnvironmentVariable("HARBOR_LOGLEVEL") ?? "Information";
        OllamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";
    }

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _storage = "memory";

    [ObservableProperty]
    private string _logLevel = "Information";

    [ObservableProperty]
    private string _ollamaHost = string.Empty;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>Save settings (writes to env vars in the current process — does not persist across restarts).</summary>
    [RelayCommand]
    private void Save()
    {
        Environment.SetEnvironmentVariable("HARBOR_MODEL", Model);
        Environment.SetEnvironmentVariable("HARBOR_STORAGE", Storage);
        Environment.SetEnvironmentVariable("HARBOR_LOGLEVEL", LogLevel);
        Environment.SetEnvironmentVariable("OLLAMA_HOST", OllamaHost);
        _logger.LogInformation("Settings saved (in-process): model={Model}, storage={Storage}, log={LogLevel}", Model, Storage, LogLevel);
        _toasts.Show($"Settings saved — restart the app to apply '{Model}'.", ToastKind.Success);
    }

    /// <summary>Apply the current theme selection.</summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        if (IsDarkTheme) _theme.ApplyDark(); else _theme.ApplyLight();
    }
}
