using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Diagnostics;

/// <summary>
///     Host-agnostic view state for a live log stream: bounded history, level
///     and text/regex filtering, follow-tail, wrap and timestamp toggles.
/// </summary>
/// <remarks>
///     <para>
///         The logs panel used to inline its own "take the last N entries" rule.
///         That left follow, level filters and text filters to every future log
///         viewport to reinvent. This controller keeps those decisions in one
///         place so a panel, an overlay or a dedicated log screen all agree on
///         what "follow", "filter" and "newest first" mean.
///     </para>
///     <para>
///         History is a fixed-capacity ring: writes are O(1) and old entries are
///         evicted FIFO. An external source such as <see cref="IDiagnosticsPanel"/>
///         can feed it with <see cref="Append(DiagnosticEntry)"/>. Appends are
///         thread-safe. The preference properties are owned by the UI thread,
///         like the rest of the UI state family.
///     </para>
/// </remarks>
public sealed class LogStreamController
{
    /// <summary>Default ring capacity, matching <see cref="InMemoryDiagnosticsPanel.DefaultCapacity"/>.</summary>
    public const int DefaultCapacity = 1000;

    /// <summary>Bounded regex evaluation, so a user-supplied pattern cannot hang a paint.</summary>
    public const int RegexTimeoutMs = 20;

    private readonly DiagnosticEntry?[] _buffer;
    private readonly object _gate = new();
    private int _head;
    private int _count;

    /// <summary>Create a controller with a bounded history.</summary>
    /// <param name="capacity">Maximum entries kept. Values &lt;= 0 fall back to <see cref="DefaultCapacity"/>.</param>
    public LogStreamController(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            capacity = DefaultCapacity;
        }

        Capacity = capacity;
        _buffer = new DiagnosticEntry?[capacity];
    }

    /// <summary>Maximum number of entries kept.</summary>
    public int Capacity { get; }

    /// <summary>Current number of entries in the ring.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Lowest level that passes the filter. Defaults to Trace (show everything).</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Trace;

    /// <summary>Case-insensitive substring, or pattern when <see cref="UseRegex"/> is set.</summary>
    public string? TextFilter { get; set; }

    /// <summary>Interpret <see cref="TextFilter"/> as a regular expression.</summary>
    public bool UseRegex { get; set; }

    /// <summary>Wrap long rendered lines instead of clipping them.</summary>
    public bool Wrap { get; set; }

    /// <summary>Include the clock column in rendered lines.</summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>
    ///     Whether the view is pinned to the newest entry. An append keeps the
    ///     newest row visible while this is true; a manual scroll up clears it.
    /// </summary>
    public bool FollowTail { get; private set; } = true;

    /// <summary>
    ///     Distance from the tail in filtered entries. Zero means the newest
    ///     matching entry is visible. Only meaningful while <see cref="FollowTail"/>
    ///     is false.
    /// </summary>
    public int ScrollOffset { get; private set; }

    /// <summary>Append one entry, evicting the oldest when the ring is full.</summary>
    public void Append(DiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_count == Capacity)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % Capacity;
            }
            else
            {
                _buffer[(_head + _count) % Capacity] = entry;
                _count++;
            }
        }
    }

    /// <summary>Append an entry built from the current UTC instant.</summary>
    public void Append(LogLevel level, string category, string message) =>
        Append(new DiagnosticEntry(
            DateTimeOffset.UtcNow,
            level,
            category ?? string.Empty,
            message ?? string.Empty));

    /// <summary>Drop all entries and restore the default follow/timestamp state.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }

        ScrollOffset = 0;
        FollowTail = true;
    }

    /// <summary>Re-pin the view to the newest matching entry.</summary>
    public void ScrollToTail()
    {
        ScrollOffset = 0;
        FollowTail = true;
    }

    /// <summary>Scroll toward older entries. Stops following the tail.</summary>
    public void ScrollUp(int lines = 1)
    {
        if (lines <= 0)
        {
            return;
        }

        FollowTail = false;
        ScrollOffset += lines;
    }

    /// <summary>Scroll toward newer entries. Re-enables follow on reaching the tail.</summary>
    public void ScrollDown(int lines = 1)
    {
        if (lines <= 0)
        {
            return;
        }

        ScrollOffset = Math.Max(0, ScrollOffset - lines);
        if (ScrollOffset == 0)
        {
            FollowTail = true;
        }
    }

    /// <summary>
    ///     Apply the current filters to the ring and return matching entries,
    ///     oldest first. The result is a fresh list safe to hold while new
    ///     entries arrive.
    /// </summary>
    public IReadOnlyList<DiagnosticEntry> Matching()
    {
        DiagnosticEntry[] snapshot;
        lock (_gate)
        {
            if (_count == 0)
            {
                return Array.Empty<DiagnosticEntry>();
            }

            snapshot = new DiagnosticEntry[_count];
            for (int i = 0; i < _count; i++)
            {
                snapshot[i] = _buffer[(_head + i) % Capacity]!;
            }
        }

        Regex? regex = null;
        string? text = TextFilter;
        if (UseRegex && !string.IsNullOrEmpty(text))
        {
            try
            {
                regex = new Regex(
                    text,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(RegexTimeoutMs));
            }
            catch (ArgumentException)
            {
                // A malformed user pattern matches nothing rather than throwing
                // out of a paint. Clearing the text restores all rows.
                return Array.Empty<DiagnosticEntry>();
            }
        }

        var result = new List<DiagnosticEntry>(snapshot.Length);
        for (int i = 0; i < snapshot.Length; i++)
        {
            DiagnosticEntry entry = snapshot[i];
            if (entry.Level < MinLevel || !MatchesText(entry, text, regex))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    ///     Render at most <paramref name="maxRows"/> lines for a viewport of
    ///     <paramref name="width"/> columns. The controller owns only log lines;
    ///     the host draws its own header and footer around them.
    /// </summary>
    public IReadOnlyList<string> Render(int width, int maxRows)
    {
        if (width <= 0 || maxRows <= 0)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<DiagnosticEntry> matching = Matching();
        if (matching.Count == 0)
        {
            return Array.Empty<string>();
        }

        int lastStart = Math.Max(0, matching.Count - maxRows);
        int start = FollowTail ? lastStart : Math.Clamp(ScrollOffset, 0, lastStart);

        var lines = new List<string>(Math.Min(maxRows, matching.Count));
        for (int i = start; i < matching.Count && lines.Count < maxRows; i++)
        {
            DiagnosticEntry entry = matching[i];
            string line = Format(entry);
            if (Wrap)
            {
                AppendWrapped(lines, line, width, maxRows);
            }
            else
            {
                lines.Add(line.Length > width ? line[..width] : line);
            }
        }

        return lines;
    }

    private static bool MatchesText(DiagnosticEntry entry, string? text, Regex? regex)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (regex is not null)
        {
            try
            {
                return regex.IsMatch(entry.Category) || regex.IsMatch(entry.Message);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return entry.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private string Format(DiagnosticEntry entry)
    {
        string line = new LogRow(
            entry.Timestamp,
            entry.Level,
            ShortenCategory(entry.Category),
            entry.Message).Format();

        if (ShowTimestamp)
        {
            return line;
        }

        // Panel layout is "HH:mm:ss.fff TAG category message" with one space
        // after the clock, so the tag starts at TimestampWidth + 1.
        int body = LogRowFormat.TimestampWidth + 1;
        return line.Length > body ? line[body..] : line;
    }

    private static string ShortenCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return string.Empty;
        }

        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1
            ? category[(lastDot + 1)..]
            : category;
    }

    private static void AppendWrapped(List<string> lines, string line, int width, int maxRows)
    {
        if (line.Length <= width)
        {
            lines.Add(line);
            return;
        }

        int position = 0;
        while (position < line.Length && lines.Count < maxRows)
        {
            int take = Math.Min(width, line.Length - position);
            int cut = take;

            // Prefer a word boundary, but never a cut so small that the next
            // iteration cannot make progress.
            if (take < line.Length - position)
            {
                int lastSpace = line.LastIndexOf(' ', position + take - 1, take);
                if (lastSpace > position + 2)
                {
                    cut = lastSpace - position;
                }
            }

            lines.Add(line.Substring(position, cut).TrimEnd());
            position += cut;
            while (position < line.Length && line[position] == ' ')
            {
                position++;
            }
        }
    }
}
