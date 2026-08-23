// MauiConfig.cs — MAUI app-specific configuration record.
//
// Stored at ~/.harbor/maui.json (NON-overlapping with the other apps' configs
// AND with the shared ~/.harbor/config.json which holds CommonConfig).
//
// MAUI is multi-platform (Windows, macOS Catalyst, iOS, Android) so this
// config tracks the last platform the user launched on, plus a platform-
// agnostic dark-mode preference (separate from the shared theme tokens
// because MAUI's Application.Current.UserAppTheme API expects an explicit
// bool).
//
// As of task C1, common fields (Theme, LogLevel, LastUsed*, RecentSessions)
// live in CommonConfig (~/.harbor/config.json). MauiConfig owns ONLY
// MAUI-specific UX preferences.

using Harbor.Desktop.Abstractions.Configuration;

namespace Harbor.App.Maui.Configuration;

/// <summary>
///     Per-app configuration for the Harbor MAUI desktop app. Stored at
///     <c>~/.harbor/maui.json</c>. Non-overlapping with CLI/Avalonia/WPF/Blazor
///     and with the shared <c>~/.harbor/config.json</c>.
/// </summary>
public sealed record MauiConfig : AppConfigBase
{
    /// <inheritdoc />
    public override string AppId => "maui";

    /// <inheritdoc />
    public override string ConfigFileName => "maui.json";

    /// <summary>
    ///     Whether the MAUI app should render in dark mode. When <c>false</c>,
    ///     light mode is forced. When <c>true</c>, dark mode is forced. The
    ///     shared theme tokens (in <c>~/.harbor/theme.json</c>) still apply
    ///     to the colour palette; this flag only selects which palette variant
    ///     the MAUI renderer picks. Defaults to <c>true</c>.
    /// </summary>
    public bool UseDarkMode { get; init; } = true;

    /// <summary>
    ///     The last platform the app was launched on: <c>"windows"</c>,
    ///     <c>"maccatalyst"</c>, <c>"ios"</c>, <c>"android"</c>. Used by the
    ///     settings UI to pre-select the platform-specific font picker.
    ///     Defaults to <c>"windows"</c>.
    /// </summary>
    public string LastPlatform { get; init; } = "windows";
}
