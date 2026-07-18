// AvaloniaConfig.cs — Avalonia app-specific configuration record.
//
// Stored at ~/.harbor/avalonia.json (NON-overlapping with CLI/WPF/MAUI/Blazor
// config files). Common fields (Theme, LogLevel, LastUsed*, RecentSessions)
// are inherited from AppConfigBase; Avalonia-specific fields are declared here.

using System.Collections.Immutable;
using Harbor.Desktop.Abstractions.Configuration;

namespace Harbor.App.Avalonia.Configuration;

/// <summary>
///     Per-app configuration for the Harbor Avalonia desktop app. Stored at
///     <c>~/.harbor/avalonia.json</c>. Non-overlapping with CLI/WPF/MAUI/Blazor.
/// </summary>
public sealed record AvaloniaConfig : AppConfigBase
{
    /// <inheritdoc />
    public override string AppId => "avalonia";

    /// <inheritdoc />
    public override string ConfigFileName => "avalonia.json";

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
    ///     <c>true</c>, <see cref="WindowWidth"/> / <see cref="WindowHeight"/>
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
