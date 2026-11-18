using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>One record in the dev-only markup tool channel.</summary>
/// <param name="At">UTC time of the record.</param>
/// <param name="Source">Producer: <c>watcher</c> or <c>interpreter</c>.</param>
/// <param name="Message">Short decision or failure, never markup text or a secret.</param>
public sealed record DevLogEntry(DateTimeOffset At, string Source, string Message);

/// <summary>
///     DEV-ONLY diagnostic channel for the markup reload tools (issue #2437).
///     Records go to a bounded in-memory tail and, when a path is supplied, to
///     a JSONL file. Nothing here touches <c>IEventBus</c>, an <c>ILogger</c>
///     provider or a renderer, so a watcher or interpreter write cannot reach
///     the CellForge cell grid or mark a frame dirty.
/// </summary>
/// <remarks>
///     The assembly that owns this type is referenced by the CLI only with
///     <c>HarborWithDslDev=true</c>. Both AOT recipes switch that flag and the
///     <c>HARBOR_WITH_DSL_DEV</c> symbol off, so the channel is absent from the
///     AOT binary together with the dev interpreter. A file write failure is
///     contained: the in-memory tail keeps the record and the caller never
///     sees an exception from logging.
/// </remarks>
public sealed class DevLogChannel
{
    private const int DefaultCapacity = 256;

    private readonly object _gate = new();
    private readonly Queue<DevLogEntry> _recent = new();
    private readonly string? _filePath;
    private readonly int _capacity;

    /// <summary>The default file, under the user's Harbor directory.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor", "dev", "dsl-devlog.jsonl");

    /// <summary>Creates a channel. A null path keeps records in memory only.</summary>
    /// <param name="filePath">Optional JSONL sink, append-only.</param>
    /// <param name="capacity">Size of the in-memory tail.</param>
    public DevLogChannel(string? filePath = null, int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _capacity = capacity;
    }

    /// <summary>The append-only file, or null for a memory-only channel.</summary>
    public string? FilePath => _filePath;

    /// <summary>Appends one record to the tail and to the optional file.</summary>
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

            if (_filePath is null)
                return;

            try
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

                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using var stream = new FileStream(
                    _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(buffer.WrittenSpan);
                stream.WriteByte((byte)'\n');
            }
            catch (IOException)
            {
                // A full disk or a locked file must not break a dev reload.
            }
            catch (UnauthorizedAccessException)
            {
                // The in-memory tail above still carries the record.
            }
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
    ///     Reads the tail of a channel file. This is the reader half used by a
    ///     separate command; it never needs the producer's in-memory state.
    ///     Truncated or malformed lines are skipped, so a live file can be
    ///     tailed while it is being written.
    /// </summary>
    /// <param name="filePath">Path written by <see cref="Write"/>.</param>
    /// <param name="count">Maximum number of records to return.</param>
    public static IReadOnlyList<DevLogEntry> ReadFile(string? filePath, int count = 50)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return [];

        var entries = new List<DevLogEntry>();
        foreach (var line in File.ReadLines(filePath))
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
}
