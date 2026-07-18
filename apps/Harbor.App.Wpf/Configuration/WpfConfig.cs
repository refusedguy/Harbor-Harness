// WpfConfig.cs — WPF app-specific configuration record.
//
// Stored at ~/.harbor/wpf.json (NON-overlapping with the other apps' configs
// AND with the shared ~/.harbor/config.json which holds CommonConfig).
// Mirrors AvaloniaConfig's windowing fields but with WPF-specific defaults
// (e.g. AvalonDock is the panel system instead of Avalonia's dock panel).
//
// As of task C1, common fields (Theme, LogLevel, LastUsed*, RecentSessions)
// live in CommonConfig (~/.harbor/config.json). WpfConfig owns ONLY
// WPF-specific UX preferences.

using Harbor.Desktop.Abstractions.Configuration;

namespace Harbor.App.Wpf.Configuration;

/// <summary>
///     Per-app configuration for the Harbor WPF desktop app. Stored at
///     <c>~/.harbor/wpf.json</c>. Non-overlapping with CLI/Avalonia/MAUI/Blazor
///     and with the shared <c>~/.harbor/config.json</c>.
/// </summary>
public sealed record WpfConfig : AppConfigBase
{
    /// <inheritdoc />
    public override string AppId => "wpf";

    /// <inheritdoc />
    public override string ConfigFileName => "wpf.json";

    /// <summary>
    ///     App-specific theme override: <c>"system"</c> (default — use the
    ///     shared theme tokens), <c>"dark"</c> (force dark), <c>"light"</c>
    ///     (force light).
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
    ///     Whether the window should open maximized on next launch. Defaults
    ///     to <c>false</c>.
    /// </summary>
    public bool WindowMaximized { get; init; } = false;

    /// <summary>
    ///     Whether to use AvalonDock (Dirkster.AvalonDock) as the panel system.
    ///     When <c>false</c>, a simpler Grid-based layout is used. Defaults to
    ///     <c>true</c>.
    /// </summary>
    public bool UseAvalonDock { get; init; } = true;
}
