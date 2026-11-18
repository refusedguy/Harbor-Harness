using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>One record in the dev-only markup tool channel.</summary>
/// <param name="At">UTC time of the record.</param>
/// <param name="Source">Producer: <c>watcher</c> or <c>interpreter</c>.</param>
/// <param name="Message">Short decision or failure, never markup text or a secret.</param>
public sealed record DevLogEntry(DateTimeOffset At, string Source, string Message);

/// <summary>
///     DEV-ONLY diagnostic channel for the markup reload tools (issue #2437).
///     Records go to a bounded in-memory tail and to an append callback the
///     dev CLI host supplies. Nothing here touches <c>IEventBus</c>, an
///     <c>ILogger</c> provider or a renderer, so a watcher or interpreter
///     write cannot reach the CellForge cell grid or mark a frame dirty.
/// </summary>
/// <remarks>
///     This assembly is a Roslyn component, so it owns no file or environment
///     access. The dev CLI supplies the append callback and reads the file for
///     its reader command. That split keeps analyzer rules intact and keeps
///     the channel, watcher and interpreter out of AOT builds: the CLI
///     references this assembly only when <c>HarborWithDslDev=true</c>.
/// </remarks>
public sealed class DevLogChannel
{
    private const int DefaultCapacity = 256;

    private readonly object _gate = new();
    private readonly Queue<DevLogEntry> _recent = new();
    private readonly Action<string>? _append;
    private readonly int _capacity;

    /// <summary>Creates a channel. A null callback keeps records in memory only.</summary>
    /// <param name="append">Receives one UTF-8 JSON record per write.</param>
    /// <param name="capacity">Size of the in-memory tail.</param>
    public DevLogChannel(Action<string>? append = null, int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _append = append;
        _capacity = capacity;
    }

    /// <summary>Appends one record to the tail and to the optional sink.</summary>
    /// <param name="source">Short producer name.</param>
    /// <param name="message">Short decision or failure.</param>
    public void Write(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("A dev-log source is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A dev-log message is required.", nameof(message));

        var entry = new DevLogEntry(DateTimeOffset.UtcNow, source, message);
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > _capacity)
                _recent.Dequeue();

            _append?.Invoke(Encode(entry));
        }
    }

    /// <summary>Returns the last <paramref name="count"/> records, oldest first.</summary>
    /// <param name="count">Maximum number of records to return.</param>
    public IReadOnlyList<DevLogEntry> Read(int count = 50)
    {
        if (count <= 0)
            return [];

        lock (_gate)
        {
            int take = Math.Min(count, _recent.Count);
            return take == 0 ? [] : _recent.Skip(_recent.Count - take).ToArray();
        }
    }

    /// <summary>
    ///     Parses the tail of a channel file for the separate reader command.
    ///     Truncated or malformed lines are skipped, so a live file can be
    ///     tailed while it is being written.
    /// </summary>
    /// <param name="lines">Lines read by the host from the channel file.</param>
    /// <param name="count">Maximum number of records to return.</param>
    public static IReadOnlyList<DevLogEntry> ParseLines(IEnumerable<string> lines, int count = 50)
    {
        if (count <= 0)
            return [];

        var entries = new List<DevLogEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                long milliseconds = root.GetProperty("ms").GetInt64();
                string source = root.GetProperty("source").GetString() ?? string.Empty;
                string message = root.GetProperty("message").GetString() ?? string.Empty;
                entries.Add(new DevLogEntry(
                    DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), source, message));
            }
            catch (JsonException)
            {
                // A torn last line is normal while the producer is appending.
            }
            catch (KeyNotFoundException)
            {
                // A line from another producer is not part of this channel.
            }
        }

        int take = Math.Min(count, entries.Count);
        return take == 0 ? [] : entries.GetRange(entries.Count - take, take);
    }

    private static string Encode(DevLogEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("ms", entry.At.ToUnixTimeMilliseconds());
            writer.WriteString("source", entry.Source);
            writer.WriteString("message", entry.Message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
