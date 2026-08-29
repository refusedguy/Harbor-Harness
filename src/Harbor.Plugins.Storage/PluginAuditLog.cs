using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Storage;

/// <summary>
///     One audit record: a plugin exercised (or attempted to exercise) a capability.
///     Written as a single JSON line to the append-only audit log.
/// </summary>
/// <param name="Timestamp">UTC timestamp of the event.</param>
/// <param name="PluginName">Stable plugin id.</param>
/// <param name="Capability">Canonical capability name (e.g. <c>read_files</c>).</param>
/// <param name="Target">Concrete target: file path, URL, process, agent id, env var name.</param>
/// <param name="Result">Outcome: <c>allow</c> or <c>deny</c> (plus short detail in <c>detail</c>).</param>
/// <param name="Detail">Optional extra context (denial reason, truncated error).</param>
public sealed record PluginAuditEntry(
    DateTime Timestamp,
    string PluginName,
    string Capability,
    string Target,
    string Result,
    string? Detail = null);

/// <summary>
///     Append-only JSONL audit sink for plugin capability usage. Every grant/deny
///     decision is recorded; plugins run with unprivileged host credentials and the
///     log directory is outside the plugin data root, so a plugin cannot rewrite or
///     delete its own audit trail.
/// </summary>
public interface IPluginAuditLog
{
    /// <summary>
    ///     Append one audit entry. Must never throw for I/O problems — audit failures
    ///     are logged, not propagated (best-effort security telemetry).
    /// </summary>
    Task WriteAsync(string pluginName, PluginCapability capability, string target, string result, string? detail = null, CancellationToken ct = default);
}

/// <summary>
///     Default <see cref="IPluginAuditLog" /> — append-only JSONL at
///     <c>{harborDir}/logs/plugin-audit.jsonl</c>. Append-only by construction:
///     <see cref="FileStream" /> with <see cref="FileMode.Append" /> and no read-back;
///     no truncation, no rewrite, no delete on any code path.
/// </summary>
public sealed class PluginAuditLog : IPluginAuditLog
{
    private readonly string _logPath;
    private readonly ILogger<PluginAuditLog> _logger;
    private readonly object _sync = new();

    /// <summary>
    ///     Construct an audit log rooted at the given harbor directory.
    /// </summary>
    /// <param name="harborDir">Harbor home (e.g. <c>~/.harbor</c>). The file lands at <c>{dir}/logs/plugin-audit.jsonl</c>.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public PluginAuditLog(string harborDir, ILogger<PluginAuditLog> logger)
    {
        if (string.IsNullOrWhiteSpace(harborDir))
            throw new ArgumentException("Harbor directory cannot be empty.", nameof(harborDir));
        _logPath = Path.Combine(harborDir, "logs", "plugin-audit.jsonl");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Absolute path of the audit log file.</summary>
    public string LogPath => _logPath;

    /// <inheritdoc />
    public Task WriteAsync(
        string pluginName,
        PluginCapability capability,
        string target,
        string result,
        string? detail = null,
        CancellationToken ct = default)
    {
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            plugin = pluginName ?? "unknown",
            capability = PluginCapabilities.ToName(capability),
            target = target ?? string.Empty,
            result = result switch
            {
                "allow" or "deny" => result,
                _ => "allow",
            },
            detail,
        };

        // Utf8JsonWriter on a rented buffer: per-line JSON without reflection (AOT-safe).
        byte[] buffer = ArrayPoolRent();
        try
        {
            int written;
            using (var ms = new MemoryStream(buffer))
            {
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteString("timestamp", entry.timestamp);
                    writer.WriteString("plugin", entry.plugin);
                    writer.WriteString("capability", entry.capability);
                    writer.WriteString("target", entry.target);
                    writer.WriteString("result", entry.result);
                    if (entry.detail is not null)
                        writer.WriteString("detail", entry.detail);
                    writer.WriteEndObject();
                }

                written = (int)ms.Position;
            }

            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    using var stream = new FileStream(
                        _logPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read);
                    stream.Write(buffer, 0, written);
                    stream.WriteByte((byte)'\n');
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to append plugin audit entry to {Log}", _logPath);
                }
            }

            return Task.CompletedTask;
        }
        finally
        {
            ArrayPoolReturn(buffer);
        }
    }

    private static byte[] ArrayPoolRent() => ArrayPool<byte>.Shared.Rent(1024);

    private static void ArrayPoolReturn(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
}
