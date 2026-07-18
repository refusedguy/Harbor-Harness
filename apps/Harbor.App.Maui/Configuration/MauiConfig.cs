// MauiConfig.cs — MAUI app-specific configuration record.
//
// Stored at ~/.harbor/maui.json (NON-overlapping with the other apps' configs).
// MAUI is multi-platform (Windows, macOS Catalyst, iOS, Android) so this config
// tracks the last platform the user launched on, plus a platform-agnostic dark
// mode preference (separate from the shared Theme field because MAUI's
// Application.Current.UserAppTheme API expects an explicit bool).

using Harbor.Desktop.Abstractions.Configuration;

namespace Harbor.App.Maui.Configuration;

/// <summary>
///     Per-app configuration for the Harbor MAUI desktop app. Stored at
///     <c>~/.harbor/maui.json</c>. Non-overlapping with CLI/Avalonia/WPF/Blazor.
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
    ///     shared <see cref="AppConfigBase.Theme"/> field stays at
    ///     <c>"system"</c> to delegate to the OS. Defaults to <c>true</c>.
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
