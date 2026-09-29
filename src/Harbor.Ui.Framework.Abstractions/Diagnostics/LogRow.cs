using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Diagnostics;

/// <summary>
///     One rendered log row: the structured form of what a logs panel paints and
///     what the log file writes. Producers format it; <see cref="LogRowFormat" />
///     reads it back by rules rather than by character offset.
/// </summary>
/// <param name="Timestamp">
///     The moment the entry was recorded. Renderers choose which time base they
///     show — see the two layouts on <see cref="LogRowFormat" />, which
///     <b>disagree</b> and say so instead of drifting.
/// </param>
/// <param name="Level">The level. Rendered through <see cref="LogLevelTag" />.</param>
/// <param name="Category">
///     The logger category, already shortened for display by the producer that
///     builds the row. Empty is a legal category and is not replaced here.
/// </param>
/// <param name="Message">
///     The message, already collapsed to a single line and truncated to the
///     producer's column budget. Empty is legal.
/// </param>
public sealed record LogRow(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message)
{
    /// <summary>The level mnemonic, from the one table that spells them.</summary>
    public string LevelTag => LogLevelTag.For(Level);

    /// <summary>
    ///     Render in the panel layout: <c>HH:mm:ss.fff TAG category message</c> —
    ///     <b>local</b> time, bare tag, no thread column. This is what the logs
    ///     panel paints and what the cell renderer clips to the viewport.
    /// </summary>
    public string Format() => LogRowFormat.Panel(this);

    /// <summary>
    ///     Render in the log-file layout:
    ///     <c>HH:mm:ss.fff [TAG] [thread] category: message</c> — <b>UTC</b> time,
    ///     bracketed tag, right-aligned thread id, colon after the category.
    /// </summary>
    /// <param name="threadId">The managed thread id, rendered in a 3-wide column.</param>
    public string FormatForFile(int threadId) => LogRowFormat.File(this, threadId);
}

/// <summary>
///     The two rendered log-row layouts, and the rule-based parser that reads
///     either of them back.
/// </summary>
/// <remarks>
///     <para>
///         <b>What #563 found.</b> The mnemonic was a de-facto wire format: a
///         consumer re-read it out of an already-rendered row by slicing
///         <c>row[13..17]</c>. That offset is only correct for the PANEL layout,
///         and the offsets were never derived from the layout — they were a
///         photograph of one producer's string. The FILE layout is
///         <c>{ts} [TAG] [{thread,3}] {cat}: {msg}</c>, so the same slice lands
///         on the opening bracket plus the first three tag characters:
///         <c>row[13..17]</c> of <c>"12:00:00.000 [INFO] [  7] Harbor: hi"</c> is
///         <c>"[INF"</c>. That matches no level arm, so the row silently lost its
///         colour and fell through to being rendered as plain text.
///     </para>
///     <para>
///         <b>Why the layouts differ, and why that is now stated.</b> The panel
///         wants a bare tag and a column budget; the file wants the thread id and
///         a grep-friendly <c>cat: msg</c>. Both also disagree on the time base —
///         the panel shows local time, the file writes UTC. That inconsistency is
///         pre-existing and predates this type; it is called out here, in one
///         place, rather than being re-derived at each producer. Reconciling it is
///         #558's call (it owns the log-file sink), not this issue's.
///     </para>
///     <para>
///         <b>Why there is a parser.</b> Once a row is a named type, a consumer
///         that receives a pre-rendered string has something honest to call:
///         <see cref="TryParse" /> returns <see langword="false" /> for anything it
///         does not recognise, so an unknown level or a third layout is REJECTED
///         rather than mis-coloured. A fixed-offset slice cannot reject anything —
///         it always returns a four-character string, and whether that string means
///         something is a detail the slice has no way to check.
///     </para>
/// </remarks>
public static class LogRowFormat
{
    /// <summary>
    ///     The clock format both layouts use. Always <see cref="TimestampWidth" />
    ///     characters, which is what made a fixed offset look like it worked.
    /// </summary>
    public const string TimestampFormat = "HH:mm:ss.fff";

    /// <summary>Rendered width of <see cref="TimestampFormat" />: <c>HH:mm:ss.fff</c>.</summary>
    public const int TimestampWidth = 12;

    /// <summary>
    ///     Clock layouts the parser accepts. The panel and the file both use
    ///     <see cref="TimestampFormat" />; Serilog's console sink uses the
    ///     second, so a line captured from a console is still readable.
    /// </summary>
    private static readonly string[] AcceptedClockFormats = [TimestampFormat, "HH:mm:ss"];

    /// <summary>Panel layout: <c>HH:mm:ss.fff TAG category message</c>, local time, no brackets.</summary>
    public static string Panel(LogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string time = row.Timestamp.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{time} {row.LevelTag} {row.Category} {row.Message}".TrimEnd();
    }

    /// <summary>
    ///     Log-file layout:
    ///     <c>HH:mm:ss.fff [TAG] [thread] category: message</c>, UTC time, bracketed
    ///     tag, 3-wide thread column, colon after the category.
    /// </summary>
    public static string File(LogRow row, int threadId)
    {
        ArgumentNullException.ThrowIfNull(row);

        string time = row.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{time} [{row.LevelTag}] [{threadId,3}] {row.Category}: {row.Message}";
    }

    /// <summary>
    ///     Read a rendered row back, by rules.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The parser walks FIELDS, never offsets. Field 1 is a clock, field 2
    ///         is the level, and whether that field is bracketed is what selects
    ///         the layout — so a row that adds a column before the tag, or removes
    ///         one, is read correctly or rejected, and never mis-read.
    ///     </para>
    ///     <para>
    ///         A row is rejected (<see langword="false" />) when: the clock does not
    ///         parse; field 2 is not a mnemonic <see cref="LogLevelTag.For" />
    ///         produces; a bracketed tag is not followed by a bracketed thread
    ///         column; or a file-layout row has no <c>": "</c> between category and
    ///         message. Rejecting is the point — the old slice returned four
    ///         characters for every row and left the caller to guess.
    ///     </para>
    /// </remarks>
    /// <param name="line">A rendered row. Leading and trailing spaces are tolerated.</param>
    /// <param name="row">The parsed row when the method returns <see langword="true" />.</param>
    /// <returns>Whether <paramref name="line" /> is a row this format produced.</returns>
    public static bool TryParse(string? line, [NotNullWhen(true)] out LogRow? row)
    {
        row = null;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        int cursor = SkipSpaces(line, 0);

        if (!TryReadField(line, ref cursor, out string clock) || !TryReadClock(clock, out TimeSpan timeOfDay))
        {
            return false;
        }

        // Brackets, not offsets, pick the layout. A bare token is the panel's
        // level column; a bracketed one is the file's. Deciding here — from what
        // the token IS — is what lets a third layout be read or rejected rather
        // than mis-sliced.
        bool bracketed = cursor < line.Length && line[cursor] == '[';

        // Nullable because TryReadBracketedField's out is `string?` — it reports
        // failure with null. Nothing dereferences it before LogLevelTag.TryParse
        // has rejected null, and that method takes `string?` on purpose.
        string? tag;
        if (bracketed)
        {
            if (!TryReadBracketedField(line, ref cursor, out tag))
            {
                return false;
            }
        }
        else if (!TryReadField(line, ref cursor, out tag))
        {
            return false;
        }

        if (!LogLevelTag.TryParse(tag, out LogLevel level))
        {
            return false;
        }

        if (bracketed)
        {
            // The file layout's thread-id column sits between the tag and the
            // category, and it is written right-aligned in a 3-wide field — so
            // it CONTAINS SPACES. It is read as a bracketed unit, not as a
            // space-delimited token, or "[  7]" would be cut after the first
            // space and the whole row rejected.
            if (!TryReadBracketedField(line, ref cursor, out _))
            {
                return false;
            }

            if (!TryReadCategoryAndMessage(line, cursor, expectColon: true, out string fileCategory, out string fileMessage))
            {
                return false;
            }

            row = new LogRow(AnchorToday(timeOfDay), level, fileCategory, fileMessage);
            return true;
        }

        if (!TryReadCategoryAndMessage(line, cursor, expectColon: false, out string panelCategory, out string panelMessage))
        {
            return false;
        }

        row = new LogRow(AnchorToday(timeOfDay), level, panelCategory, panelMessage);
        return true;
    }

    // ---------------------------------------------------------------------
    // Field readers. All of them advance `cursor` past the space that ended
    // the field, and none of them looks at an absolute character position.
    // ---------------------------------------------------------------------

    /// <summary>Index of the first non-space at or after <paramref name="from" />.</summary>
    private static int SkipSpaces(string line, int from)
    {
        int i = from;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i;
    }

    /// <summary>Read a space-delimited field starting at <paramref name="cursor" />.</summary>
    private static bool TryReadField(string line, ref int cursor, out string field)
    {
        field = string.Empty;

        int start = SkipSpaces(line, cursor);
        if (start >= line.Length)
        {
            cursor = line.Length;
            return false;
        }

        int end = line.IndexOf(' ', start);
        if (end < 0)
        {
            field = line[start..];
            cursor = line.Length;
            return true;
        }

        field = line[start..end];
        cursor = SkipSpaces(line, end);
        return true;
    }

    /// <summary>
    ///     Read a bracketed field — the file layout's <c>[TAG]</c> and
    ///     <c>[  7]</c> — as a single unit. A bracketed field ends at its
    ///     <c>]</c>, not at the next space, because the thread column is
    ///     right-aligned and genuinely contains spaces.
    /// </summary>
    private static bool TryReadBracketedField(
        string line,
        ref int cursor,
        [NotNullWhen(true)] out string? inner)
    {
        inner = null;

        int start = SkipSpaces(line, cursor);
        if (start >= line.Length || line[start] != '[')
        {
            cursor = line.Length;
            return false;
        }

        int close = line.IndexOf(']', start + 1);
        if (close < 0)
        {
            cursor = line.Length;
            return false;
        }

        string text = line[(start + 1)..close];
        cursor = SkipSpaces(line, close + 1);
        if (text.Length == 0)
        {
            return false;
        }

        inner = text;
        return true;
    }

    private static bool TryReadClock(string field, out TimeSpan timeOfDay)
    {
        if (DateTime.TryParseExact(
                field,
                AcceptedClockFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            timeOfDay = parsed.TimeOfDay;
            return true;
        }

        timeOfDay = default;
        return false;
    }

    /// <summary>
    ///     The tail of a row, split into category and message. The file layout
    ///     separates them with <c>": "</c>; the panel layout with the first space
    ///     (and a row with no message is legal — the panel's column budget can
    ///     starve it).
    /// </summary>
    private static bool TryReadCategoryAndMessage(
        string line,
        int from,
        bool expectColon,
        out string category,
        out string message)
    {
        category = string.Empty;
        message = string.Empty;

        int start = SkipSpaces(line, from);
        if (start >= line.Length)
        {
            // "HH:mm:ss.fff TRAC Harbor" — a category and no body is a real row.
            return !expectColon;
        }

        string tail = line[start..].TrimEnd();

        if (expectColon)
        {
            int colon = tail.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            category = tail[..colon].TrimEnd();
            message = tail[(colon + 1)..].TrimStart();
            return true;
        }

        int space = tail.IndexOf(' ');
        if (space < 0)
        {
            category = tail;
            return true;
        }

        category = tail[..space];
        message = tail[(space + 1)..];
        return true;
    }

    /// <summary>
    ///     A rendered clock carries no date, so a parsed row is anchored to
    ///     today's date at local time. That is enough for <see cref="LogRow.Format" />
    ///     to reproduce the same <c>HH:mm:ss.fff</c> it was read from, which is the
    ///     round-trip the format promises. The date is NOT recovered and must not
    ///     be read back as if it were.
    /// </summary>
    private static DateTimeOffset AnchorToday(TimeSpan timeOfDay) =>
        new(DateTime.Today + timeOfDay);
}
