// BlazorConfig.cs — Blazor Server app-specific configuration record.
//
// Stored at ~/.harbor/blazor.json (NON-overlapping with the other apps'
// configs AND with the shared ~/.harbor/config.json which holds
// CommonConfig).
//
// The Blazor app hosts Kestrel on localhost and serves the chat UI over a
// SignalR circuit; this config owns the listen port + browser-launch
// behavior + hot-reload toggle.
//
// As of task C1, common fields (Theme, LogLevel, LastUsed*, RecentSessions)
// live in CommonConfig (~/.harbor/config.json). BlazorConfig owns ONLY
// Blazor-specific runtime preferences.

using Harbor.Desktop.Abstractions.Configuration;
namespace Harbor.App.Blazor.Configuration;
/// <summary>
///     Per-app configuration for the Harbor Blazor Server desktop app. Stored
///     at <c>~/.harbor/blazor.json</c>. Non-overlapping with
///     CLI/Avalonia/WPF/MAUI and with the shared <c>~/.harbor/config.json</c>.
/// </summary>
public sealed record BlazorConfig : AppConfigBase
{
    /// <inheritdoc />
    public override string AppId => "blazor";

    /// <inheritdoc />
    public override string ConfigFileName => "blazor.json";

    /// <summary>
    ///     TCP port Kestrel listens on. Defaults to <c>5000</c>. Set to
    ///     <c>0</c> to bind to an OS-assigned ephemeral port (useful for
    ///     parallel test runs).
    /// </summary>
    public int ListenPort { get; init; } = 5000;

    /// <summary>
    ///     Whether to auto-open the host browser to
    ///     <c>http://localhost:&lt;ListenPort&gt;</c> on startup. The
    ///     <c>--no-open-browser</c> CLI flag overrides this at runtime.
    ///     Defaults to <c>true</c>.
    /// </summary>
    public bool AutoOpenBrowser { get; init; } = true;

    /// <summary>
    ///     Whether Blazor's hot-reload subsystem is enabled. Defaults to
    ///     <c>true</c> in DEBUG builds, <c>false</c> in RELEASE. Stored as a
    ///     config field so users can opt out without recompiling.
    /// </summary>
    public bool EnableHotReload { get; init; } = true;
}
