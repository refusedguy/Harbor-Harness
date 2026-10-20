namespace Harbor.Telemetry;

/// <summary>
///     Where the visible telemetry sink writes. <see cref="None" /> is the
///     default everywhere: telemetry is instrumented by default and VISIBLE
///     only when a host asks for it.
/// </summary>
public enum TelemetryExportTarget
{
    /// <summary>No sink. The default — a run that did not ask for telemetry writes nothing.</summary>
    None = 0,

    /// <summary>One line per record on standard output. For non-interactive runs (ask / run / headless).</summary>
    Console = 1,

    /// <summary>One line per record appended to a file. The target for an interactive TUI session.</summary>
    File = 2,
}

/// <summary>
///     Configuration of the visible, bounded telemetry sink
///     (<see cref="TelemetryTextExporter" />).
/// </summary>
/// <remarks>
///     Every bound here is a LIMIT, not a hint: the sink truncates a record
///     that exceeds <see cref="MaxRecordBytes" />, and drops a record outright
///     when the buffer would exceed <see cref="MaxBufferBytes" /> or
///     <see cref="MaxBufferedRecords" />. Both outcomes are counted and
///     reported — never silent.
/// </remarks>
public sealed record TelemetryExportOptions
{
    /// <summary>Default cap on one exported record, in UTF-8 bytes.</summary>
    public const int DefaultMaxRecordBytes = 4096;

    /// <summary>Default cap on the bytes held in the export buffer.</summary>
    public const int DefaultMaxBufferBytes = 262144;

    /// <summary>Default cap on the number of records held in the export buffer.</summary>
    public const int DefaultMaxBufferedRecords = 2048;

    /// <summary>Default cadence at which the buffer is written out.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Where the sink writes. <see cref="TelemetryExportTarget.None" /> means "no sink".</summary>
    public TelemetryExportTarget Target { get; init; } = TelemetryExportTarget.None;

    /// <summary>
    ///     File to append to when <see cref="Target" /> is
    ///     <see cref="TelemetryExportTarget.File" />. The directory is created
    ///     on first write.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>Cap on one exported record, in UTF-8 bytes. A longer record is truncated, and counted.</summary>
    public int MaxRecordBytes { get; init; } = DefaultMaxRecordBytes;

    /// <summary>Cap on the bytes held in the export buffer. A record that does not fit is dropped, and counted.</summary>
    public int MaxBufferBytes { get; init; } = DefaultMaxBufferBytes;

    /// <summary>Cap on the number of records held in the export buffer.</summary>
    public int MaxBufferedRecords { get; init; } = DefaultMaxBufferedRecords;

    /// <summary>
    ///     Cadence at which the buffer is written out. <see langword="null" />
    ///     starts no background flusher: the host then calls
    ///     <see cref="TelemetryTextExporter.Flush" /> itself.
    /// </summary>
    public TimeSpan? FlushInterval { get; init; } = DefaultFlushInterval;

    /// <summary>Whether finished activities (spans) are exported.</summary>
    public bool IncludeActivities { get; init; } = true;

    /// <summary>Whether measurements are exported.</summary>
    public bool IncludeMetrics { get; init; } = true;
}

/// <summary>
///     A snapshot of what the visible sink has done. Every number here is a
///     count of something that HAPPENED — a drop is never a silent loss.
/// </summary>
/// <param name="ExportedRecords">Records written to the sink.</param>
/// <param name="ExportedBytes">Bytes written to the sink.</param>
/// <param name="DroppedRecords">Records dropped because the buffer was full.</param>
/// <param name="DroppedBytes">Bytes dropped with them.</param>
/// <param name="TruncatedRecords">Records shortened to fit the per-record cap.</param>
/// <param name="TruncatedBytes">Bytes lost to that shortening.</param>
/// <param name="PendingRecords">Records currently held in the buffer.</param>
/// <param name="PendingBytes">Bytes currently held in the buffer.</param>
public readonly record struct TelemetryExportStats(
    long ExportedRecords,
    long ExportedBytes,
    long DroppedRecords,
    long DroppedBytes,
    long TruncatedRecords,
    long TruncatedBytes,
    int PendingRecords,
    long PendingBytes);
