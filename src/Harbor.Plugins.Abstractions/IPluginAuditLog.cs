using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     One audit record: a plugin exercised (or attempted to exercise) a capability.
///     Written as a single JSON line to the append-only audit log.
/// </summary>
/// <param name="Timestamp">UTC timestamp of the event.</param>
/// <param name="PluginName">Stable plugin id.</param>
/// <param name="Capability">Canonical capability name (e.g. <c>read_files</c>).</param>
/// <param name="Target">Concrete target: file path, URL, process, agent id, env var name.</param>
/// <param name="Result">Outcome: <c>allow</c> or <c>deny</c>.</param>
/// <param name="Detail">Optional extra context (denial reason, truncated error).</param>
public sealed record PluginAuditEntry(
    DateTime Timestamp,
    string PluginName,
    string Capability,
    string Target,
    string Result,
    string? Detail = null);

/// <summary>
///     Append-only audit sink for plugin capability usage. Every grant/deny decision
///     is recorded; plugins run with unprivileged host credentials and the log
///     directory is outside the plugin data root, so a plugin cannot rewrite or
///     delete its own audit trail.
/// </summary>
public interface IPluginAuditLog
{
    /// <summary>
    ///     Append one audit entry. Must never throw for I/O problems — audit failures
    ///     are logged, not propagated (best-effort security telemetry).
    /// </summary>
    /// <param name="pluginName">Stable plugin id.</param>
    /// <param name="capability">The capability being exercised.</param>
    /// <param name="target">Concrete target (path, URL, command, ...).</param>
    /// <param name="result"><c>allow</c> or <c>deny</c>.</param>
    /// <param name="detail">Optional extra context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(
        string pluginName,
        PluginCapability capability,
        string target,
        string result,
        string? detail = null,
        CancellationToken ct = default);
}