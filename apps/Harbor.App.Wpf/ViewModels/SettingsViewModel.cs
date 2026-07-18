using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Wpf.Services;

namespace Harbor.App.Wpf.ViewModels;

/// <summary>
///     Settings view model. Toggles theme, edits default agent/model,
///     shows diagnostic info.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;

    /// <summary>Construct a <see cref="SettingsViewModel" />.</summary>
    /// <param name="theme">Theme service.</param>
    public SettingsViewModel(ThemeService theme)
    {
        _theme = theme;
        Themes = new ObservableCollection<string> { "Dark", "Light" };
        SelectedTheme = theme.Current == ThemeService.Theme.Dark ? "Dark" : "Light";
        DefaultAgent = "code";
        DefaultModel = "claude-3-5-sonnet-20241022";
        DefaultProvider = "anthropic";
        SendTelemetry = false;
        AutoScrollChat = true;
        FontSize = 13;
    }

    /// <summary>Available themes for the dropdown.</summary>
    public ObservableCollection<string> Themes { get; }

    /// <summary>Currently selected theme name ("Dark" or "Light").</summary>
    [ObservableProperty] private string _selectedTheme = "Dark";

    /// <summary>Default agent name.</summary>
    [ObservableProperty] private string _defaultAgent = "code";

    /// <summary>Default model id.</summary>
    [ObservableProperty] private string _defaultModel = "claude-3-5-sonnet-20241022";

    /// <summary>Default provider id.</summary>
    [ObservableProperty] private string _defaultProvider = "anthropic";

    /// <summary>Whether anonymous telemetry is sent.</summary>
    [ObservableProperty] private bool _sendTelemetry;

    /// <summary>Whether chat auto-scrolls on new content.</summary>
    [ObservableProperty] private bool _autoScrollChat = true;

    /// <summary>UI font size.</summary>
    [ObservableProperty] private int _fontSize = 13;

    /// <summary>Apply pending settings.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (Enum.TryParse<ThemeService.Theme>(SelectedTheme, true, out var theme))
        {
            _theme.ApplyTheme(theme);
        }
    }

    /// <summary>Reset to defaults.</summary>
    [RelayCommand]
    private void Reset()
    {
        SelectedTheme = "Dark";
        DefaultAgent = "code";
        DefaultModel = "claude-3-5-sonnet-20241022";
        DefaultProvider = "anthropic";
        SendTelemetry = false;
        AutoScrollChat = true;
        FontSize = 13;
        _theme.ApplyTheme(ThemeService.Theme.Dark);
    }
}
