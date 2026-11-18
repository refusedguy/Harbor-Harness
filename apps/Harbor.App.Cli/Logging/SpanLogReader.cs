using System.Globalization;

namespace Harbor.App.Cli.Logging;

/// <summary>
///     Reads the finished-span lines written by Harbor's visible telemetry
///     sink (<c>HARBOR_TELEMETRY_EXPORT=file</c>, default path
///     <c>~/.harbor/logs/telemetry.txt</c>) and turns them into sortable
///     entries for <c>harbor logs --spans</c>.
/// </summary>
/// <remarks>
///     <para>
///         The sink renders one line per record as
///         <c>activity &lt;name&gt; duration_ms="..." status="..." key="..."</c>.
///         Only complete span lines are accepted: a line truncated by the
///         sink's byte budget has no closing quote on its duration attribute,
///         so it is skipped rather than reported with a fabricated value.
///     </para>
///     <para>
///         This is a read-only view of an existing export. It never starts a
///         listener, never changes the exporter, and writes nothing.
///     </para>
/// </remarks>
internal static class SpanLogReader
{
    /// <summary>Name of the exported attribute holding the span duration.</summary>
    internal const string DurationAttribute = "duration_ms";

    /// <summary>Name of the exported attribute holding the span status.</summary>
    internal const string StatusAttribute = "status";

    private const string ActivityPrefix = "activity ";

    /// <summary>One parsed, finished span.</summary>
    /// <param name="Name">Span operation name.</param>
    /// <param name="DurationMs">Duration in milliseconds.</param>
    /// <param name="Status">Exported status, or an empty string when absent.</param>
    /// <param name="Line">The original line, for callers that want the full record.</param>
    internal readonly record struct SpanEntry(string Name, double DurationMs, string Status, string Line);

    /// <summary>
    ///     Read every parseable span from <paramref name="path" />. A missing
    ///     file is an empty result: no telemetry export yet is a normal state.
    /// </summary>
    internal static IReadOnlyList<SpanEntry> Read(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<SpanEntry>();
        }

        List<SpanEntry> entries = new();
        foreach (string line in File.ReadLines(path))
        {
            if (TryParse(line, out SpanEntry entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    ///     Parse one exported line. Returns <see langword="false" /> for
    ///     metrics, comments, blank lines and truncated span records.
    /// </summary>
    internal static bool TryParse(string? line, out SpanEntry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(ActivityPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string? rawDuration = ReadAttribute(line, DurationAttribute);
        if (rawDuration is null
            || !double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            || double.IsNaN(duration)
            || double.IsInfinity(duration)
            || duration < 0)
        {
            return false;
        }

        int nameStart = ActivityPrefix.Length;
        int nameEnd = line.IndexOf(' ', nameStart);
        string name = nameEnd < 0 ? line[nameStart..] : line[nameStart..nameEnd];

        entry = new SpanEntry(name, duration, ReadAttribute(line, StatusAttribute) ?? string.Empty, line);
        return true;
    }

    /// <summary>
    ///     Extract one <c> key="value"</c> attribute. The export escapes
    ///     quotes inside values, so the next unescaped quote ends the value.
    /// </summary>
    private static string? ReadAttribute(string line, string key)
    {
        string marker = " " + key + "=\"";
        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        for (int i = start; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == start || line[i - 1] != '\\'))
            {
                return line[start..i];
            }
        }

        return null;
    }
}
