// AvaloniaConfig.cs — Avalonia app-specific configuration record.
//
// Stored at ~/.harbor/avalonia.json (NON-overlapping with CLI/WPF/MAUI/Blazor
// config files AND with the shared ~/.harbor/config.json which holds
// CommonConfig).
//
// As of task C1, common fields (Theme, LogLevel, LastUsed*, RecentSessions)
// live in CommonConfig (~/.harbor/config.json). AvaloniaConfig owns ONLY
// Avalonia-specific UX preferences (window size, fonts, panel visibility,
// open tabs). The optional per-app Theme field here is an OVERRIDE: when set
// to "system" (the default) the Avalonia app uses the shared
// CommonConfig.PermissionMode / theme tokens; when set to "dark"/"light" the
// app forces its own theme regardless of the shared preference.

using System.Collections.Immutable;
using Harbor.Desktop.Abstractions.Configuration;
namespace Harbor.App.Avalonia.Configuration;
/// <summary>
///     Per-app configuration for the Harbor Avalonia desktop app. Stored at
///     <c>~/.harbor/avalonia.json</c>. Non-overlapping with CLI/WPF/MAUI/Blazor
///     and with the shared <c>~/.harbor/config.json</c>.
/// </summary>
public sealed record AvaloniaConfig : AppConfigBase
{
    /// <inheritdoc />
    public override string AppId => "avalonia";

    /// <inheritdoc />
    public override string ConfigFileName => "avalonia.json";

    /// <summary>
    ///     App-specific theme override: <c>"system"</c> (default — use the
    ///     shared theme tokens), <c>"dark"</c> (force dark), <c>"light"</c>
    ///     (force light). Lets the user override the cross-app theme for the
    ///     Avalonia app only.
    /// </summary>
    public string Theme { get; init; } = "system";

    /// <summary>
    ///     Initial window width in device-independent pixels. Defaults to
    ///     <c>1200</c>. Restored on next launch.
    /// </summary>
    public int WindowWidth { get; init; } = 1200;

    /// <summary>
    ///     Initial window height in device-independent pixels. Defaults to
    ///     <c>800</c>.
    /// </summary>
    public int WindowHeight { get; init; } = 800;

    /// <summary>
    ///     Whether the window should open maximized on next launch. When
    ///     <c>true</c>, <see cref="WindowWidth" /> / <see cref="WindowHeight" />
    ///     are ignored until the user un-maximizes. Defaults to <c>false</c>.
    /// </summary>
    public bool WindowMaximized { get; init; } = false;

    /// <summary>
    ///     Sans-serif font family for UI text. Defaults to <c>"Inter"</c>
    ///     (bundled via the Avalonia.Fonts.Inter package).
    /// </summary>
    public string FontFamily { get; init; } = "Inter";

    /// <summary>
    ///     Monospace font family for code blocks + the integrated editor.
    ///     Defaults to <c>"JetBrains Mono"</c>.
    /// </summary>
    public string MonospaceFontFamily { get; init; } = "JetBrains Mono";

    /// <summary>
    ///     Whether the bottom status bar (model, provider, session ID) is
    ///     visible. Defaults to <c>true</c>.
    /// </summary>
    public bool ShowStatusBar { get; init; } = true;

    /// <summary>
    ///     Whether the left session-sidebar is visible. Defaults to <c>true</c>.
    /// </summary>
    public bool ShowSessionSidebar { get; init; } = true;

    /// <summary>
    ///     Session IDs currently open in tabs (left-to-right order). Empty when
    ///     only the default session is open.
    /// </summary>
    public ImmutableList<string> OpenTabs { get; init; } = ImmutableList<string>.Empty;
}
