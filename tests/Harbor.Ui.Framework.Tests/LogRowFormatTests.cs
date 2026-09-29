using System.Globalization;
using System.Runtime.CompilerServices;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #563: the level mnemonic was a 6-producer / 1-consumer string
///     format. A consumer re-read it out of an already-rendered row by slicing
///     <c>row[13..17]</c>, an offset that only ever described ONE producer's
///     layout.
/// </summary>
/// <remarks>
///     <para>
///         <b>The concrete breakage.</b> The logs panel emits
///         <c>HH:mm:ss.fff TAG category message</c>, so the tag really does land
///         at [13..17] and the slice appears to work. The log file emits
///         <c>HH:mm:ss.fff [TAG] [thread] category: message</c> — the tag is
///         bracketed and a thread-id column sits after it — so
///         <c>row[13..17]</c> of a file row returns <c>"[INF"</c>: the opening
///         bracket plus the first three tag characters. That matches no level
///         arm, so the row lost its level colour and fell through to being
///         rendered as plain escaped text.
///     </para>
///     <para>
///         <c>FixedOffsetSlice</c> below reimplements the retired reader verbatim,
///         so the two <c>…_MisreadsTheFileLayout</c> / <c>…_HappensToWorkOnThePanelLayout</c>
///         assertions are not commentary — they are the defect, executable. The
///         parser cases then show the replacement reading both layouts correctly.
///     </para>
/// </remarks>
public sealed class LogRowFormatTests
{
    /// <summary>
    ///     The retired reader, reproduced exactly as it was:
    ///     <c>row.Length >= 17 ? row[13..17] : string.Empty</c>.
    /// </summary>
    private static string FixedOffsetSlice(string row) => row.Length >= 17 ? row[13..17] : string.Empty;

    /// <summary>Fixed instant, so the rendered clock is 12:00:00 in UTC.</summary>
    private static LogRow Row(LogLevel level, string category, string message) =>
        new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), level, category, message);

    /// <summary>
    ///     The panel layout renders LOCAL time, so the expected clock is computed
    ///     rather than hard-coded — a test that hard-codes <c>12:00:00</c> passes on
    ///     a UTC runner and fails on every other one.
    /// </summary>
    private static string PanelClock()
    {
        DateTimeOffset instant = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        return instant.ToLocalTime().ToString(LogRowFormat.TimestampFormat, CultureInfo.InvariantCulture);
    }

    // =====================================================================
    // 1. The defect, pinned.
    // =====================================================================

    /// <summary>
    ///     The offset hard-coded into the consumer is correct for the panel row
    ///     and wrong for the file row. This is the whole of #563 in one test.
    /// </summary>
    [Test]
    public async Task FixedOffsetSlice_MisreadsTheFileLayout()
    {
        string panelRow = Row(LogLevel.Information, "Harbor", "hi").Format();
        string fileRow = Row(LogLevel.Information, "Harbor", "hi").FormatForFile(7);

        await Assert.That(panelRow).IsEqualTo($"{PanelClock()} INFO Harbor hi")
            .Because("the panel layout is what the old offset was measured against");

        await Assert.That(FixedOffsetSlice(panelRow)).IsEqualTo("INFO")
            .Because("the tag sits at [13..17] of a panel row, which is why the offset looked "
                   + "correct for the producer it was written against");

        await Assert.That(FixedOffsetSlice(fileRow)).IsEqualTo("[INF")
            .Because("the file layout brackets the tag, so [13..17] is the opening bracket plus "
                   + "three tag characters. It matches no level arm, so the consumer lost the "
                   + "row's level colour and rendered it as plain text. A slice this shape can "
                   + "never reject a row — it always returns four characters and leaves the "
                   + "caller to guess whether they mean anything.");
    }

    // =====================================================================
    // 2. The replacement: rule-based, and total over every LogLevel.
    // =====================================================================

    /// <summary>
    ///     Every <see cref="LogLevel" /> — read by REFLECTION, so a member added
    ///     later is covered without editing this file — round-trips through both
    ///     layouts. This is the issue's "a test asserts LogRow.Format round-trips
    ///     through the panel's parser for every LogLevel, including any member
    ///     added later".
    /// </summary>
    [Test]
    public async Task FormatThenParse_RoundTrips_ForEveryLogLevel_InBothLayouts()
    {
        var problems = new List<string>();
        int covered = 0;

        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            covered++;
            LogRow original = Row(level, "Harbor.Ui", "a message with spaces");

            foreach (string rendered in new[] { original.Format(), original.FormatForFile(7) })
            {
                if (!LogRowFormat.TryParse(rendered, out LogRow? parsed))
                {
                    problems.Add($"{level} / '{rendered}' — the parser rejected a row LogRowFormat produced");
                    continue;
                }

                if (parsed.Level != level)
                {
                    problems.Add($"{level} / '{rendered}' — parsed back as {parsed.Level}");
                }

                if (parsed.Category != original.Category)
                {
                    problems.Add($"{level} / '{rendered}' — category '{parsed.Category}' != '{original.Category}'");
                }

                if (parsed.Message != original.Message)
                {
                    problems.Add($"{level} / '{rendered}' — message '{parsed.Message}' != '{original.Message}'");
                }

                // A rendered clock carries no date, so only the time of day survives.
                if (parsed.Format() != original.Format())
                {
                    problems.Add($"{level} / '{rendered}' — re-rendered as '{parsed.Format()}'");
                }
            }
        }

        await Assert.That(covered).IsGreaterThan(0)
            .Because("Enum.GetValues<LogLevel>() returned nothing, so the round-trip proved nothing");

        await Assert.That(problems).IsEmpty()
            .Because("the mnemonic and the row layout are a shared format, so a row must read back "
                   + "as the row that was written, at every level, in both layouts. "
                   + string.Join("\n", problems));
    }

    /// <summary>
    ///     The mnemonic is total, fixed-width, and one-to-one with the enum — and
    ///     it never falls back to a sentinel.
    /// </summary>
    [Test]
    public async Task LevelTag_IsTotal_FixedWidth_AndReversible()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            string tag = LogLevelTag.For(level);

            if (tag.Length != LogLevelTag.Width)
            {
                problems.Add($"{level} → '{tag}' is {tag.Length} characters, not {LogLevelTag.Width}");
            }

            if (!seen.Add(tag))
            {
                problems.Add($"{level} → '{tag}' — two levels share one mnemonic, so a parsed row "
                          + "cannot tell them apart");
            }

            if (!LogLevelTag.TryParse(tag, out LogLevel back) || back != level)
            {
                problems.Add($"{level} → '{tag}' does not parse back to {level}");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join("\n", problems));
    }

    /// <summary>
    ///     <see cref="LogLevelTag.For" /> has no wildcard arm, so a value outside
    ///     the enum fails loudly instead of rendering a token that means nothing.
    ///     That silent fallback is what let the four #563 producers disagree about
    ///     the same level.
    /// </summary>
    [Test]
    public async Task LevelTag_ForAValueOutsideTheEnum_Throws()
    {
        // Statement-bodied so the lambda binds to the Action overload of Throws
        // unambiguously — `For` returns a string, and a discarded result is the
        // shape the repo's other Throws call sites use.
        Assert.Throws<SwitchExpressionException>(() => { _ = LogLevelTag.For((LogLevel)999); });
    }

    // =====================================================================
    // 3. The parser rejects what it does not understand.
    // =====================================================================

    /// <summary>
    ///     Each of these is a row a fixed-offset slice would have read a
    ///     four-character "level" out of. Every one is rejected instead, which is
    ///     the property a slice structurally cannot have.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("Logs (F12 to hide · live ILogger output)")]
    [Arguments("────────────────────────")]
    [Arguments("No log entries yet.")]
    [Arguments("12:00:00.000 INFOF Harbor hi")]            // a mnemonic nobody produces
    [Arguments("not-a-clock INFO Harbor hi")]
    [Arguments("12:00:00.000 [INFO] Harbor hi")]           // file layout, thread column missing
    [Arguments("12:00:00.000 [INFO] 7 Harbor hi")]         // file layout, thread column unbracketed
    [Arguments("12:00:00.000 [INFO] [  7] Harbor hi")]      // file layout, no category/message colon
    [Arguments("12:00:00.000 [] [  7] Harbor: hi")]         // empty tag
    [Arguments("[INFO] [  7] Harbor: hi")]                   // no clock at all
    public async Task TryParse_RowsThisFormatNeverProduces_AreRejected(string line)
    {
        await Assert.That(LogRowFormat.TryParse(line, out LogRow? row)).IsFalse()
            .Because($"'{line}' is not a row LogRowFormat produces, and a parser that accepted it "
                   + "would be inventing a level the way a `_ =>` arm did");
    }

    /// <summary>A missing line is not a row either.</summary>
    [Test]
    public async Task TryParse_Null_IsRejected()
    {
        await Assert.That(LogRowFormat.TryParse(null, out LogRow? row)).IsFalse();
    }

    /// <summary>
    ///     A row with a level but no body is legal — the panel's column budget
    ///     starves the body on a narrow viewport — so the parser must not insist
    ///     on one.
    /// </summary>
    [Test]
    public async Task TryParse_PanelRowWithNoBody_IsAccepted()
    {
        bool parsed = LogRowFormat.TryParse("12:00:00.000 WARN Harbor", out LogRow? row);

        await Assert.That(parsed).IsTrue();
        await Assert.That(row!.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(row.Category).IsEqualTo("Harbor");
        await Assert.That(row.Message).IsEqualTo(string.Empty);
    }

    /// <summary>
    ///     Serilog's console sink renders <c>HH:mm:ss</c> rather than
    ///     <c>HH:mm:ss.fff</c>, eight lines above the file template that uses the
    ///     longer form. Both are accepted, so a line captured from either console
    ///     is still readable — the clock is matched as a FIELD, which is what a
    ///     fixed-width offset could never do.
    /// </summary>
    [Test]
    public async Task TryParse_AcceptsEitherClockWidth()
    {
        bool withMillis = LogRowFormat.TryParse("12:00:00.000 ERRO Harbor boom", out LogRow? fine);
        bool without = LogRowFormat.TryParse("12:00:00 ERRO Harbor boom", out LogRow? coarse);

        await Assert.That(withMillis).IsTrue();
        await Assert.That(without).IsTrue();
        await Assert.That(fine!.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(fine.Category).IsEqualTo("Harbor");
        await Assert.That(fine.Message).IsEqualTo("boom");
        await Assert.That(coarse!.Level).IsEqualTo(LogLevel.Error);
    }

    /// <summary>
    ///     The file layout's thread column is right-aligned in a 3-wide field, so
    ///     <c>[  7]</c> genuinely contains spaces. A parser that read every field
    ///     as "up to the next space" would cut it after the first space and reject
    ///     the whole row — the same failure mode as a fixed offset, one level up:
    ///     a layout detail treated as a terminator.
    /// </summary>
    [Test]
    public async Task TryParse_FileRow_ReadsAPaddedThreadColumn()
    {
        foreach (int threadId in new[] { 0, 7, 42, 999 })
        {
            string rendered = Row(LogLevel.Error, "Harbor.Ui", "boom").FormatForFile(threadId);

            bool parsed = LogRowFormat.TryParse(rendered, out LogRow? back);

            await Assert.That(parsed).IsTrue()
                .Because($"'{rendered}' is a row LogRowFormat.File produced and must read back");
            await Assert.That(back!.Level).IsEqualTo(LogLevel.Error);
            await Assert.That(back.Category).IsEqualTo("Harbor.Ui");
            await Assert.That(back.Message).IsEqualTo("boom");
        }
    }

    // =====================================================================
    // 4. The panel rows themselves, end to end.
    // =====================================================================

    /// <summary>
    ///     <see cref="PanelRows.LogRows" /> is the canonical producer, and every row
    ///     it emits is readable back by the rule-based parser at every level. The
    ///     old table had a fourth rendering — a 4-question-mark sentinel — for a
    ///     level it did not name; there is no such arm left to reach.
    /// </summary>
    [Test]
    public async Task PanelRows_ProduceRowsTheParserReadsBack_AtEveryLevel()
    {
        const string Category = "Harbor.Ui.Framework.Projection";
        const string Message = "hello from the diagnostics panel";
        var problems = new List<string>();

        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            var entry = new DiagnosticEntry(
                new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
                level,
                Category,
                Message);

            List<string> rows = PanelRows.LogRows([entry], width: 120, height: 8);

            // rows[0] is the header, rows[1] the separator, rows[2] the entry.
            string row = rows[2];

            if (!LogRowFormat.TryParse(row, out LogRow? parsed))
            {
                problems.Add($"{level} — PanelRows produced an unreadable row: '{row}'");
                continue;
            }

            if (parsed.Level != level)
            {
                problems.Add($"{level} — PanelRows row read back as {parsed.Level}");
            }

            // ShortenCategory reduces the category to its leaf name.
            if (parsed.Category != "Projection")
            {
                problems.Add($"{level} — category '{parsed.Category}' != 'Projection'");
            }

            if (parsed.Message != Message)
            {
                problems.Add($"{level} — message '{parsed.Message}' != '{Message}'");
            }

            if (!row.Contains(LogLevelTag.For(level), StringComparison.Ordinal))
            {
                problems.Add($"{level} — the row does not carry its own mnemonic: '{row}'");
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because("PanelRows.LogRows is the canonical producer of the panel row, and a producer "
                   + "whose rows nobody can parse is exactly the drift #563 was filed about. "
                   + string.Join("\n", problems));
    }

    /// <summary>
    ///     The row a producer builds is budgeted against the REAL widths — the
    ///     clock's 12 characters and the mnemonic's 4 — so widening either cannot
    ///     silently push the body off the end. The renderer clips what the producer
    ///     emits, and clipping is what keeps the panel inside its viewport.
    /// </summary>
    [Test]
    public async Task PanelRows_ClippedRows_StayInsideTheViewport_AtEveryWidth()
    {
        var entry = new DiagnosticEntry(
            new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
            LogLevel.Information,
            "Harbor.Ui.Framework.Projection",
            new string('x', 400));

        foreach (int width in new[] { 20, 32, 48, 64, 80, 120, 200 })
        {
            IReadOnlyList<string> rows = PanelText.Clip(
                PanelRows.LogRows([entry], width, height: 8), width, height: 8);

            foreach (string row in rows)
            {
                await Assert.That(row.Length).IsLessThanOrEqualTo(width)
                    .Because($"at width {width} a painted row must not overflow, or the panel wraps "
                           + "a line the reader would then have to re-parse mid-row");
            }
        }
    }

    /// <summary>
    ///     <c>HH:mm:ss.fff</c> is twelve characters — the assumption the old
    ///     <c>[13..17]</c> offset rested on. Pinned so a calendar change cannot move
    ///     the tag column without a rule-based reader noticing.
    /// </summary>
    [Test]
    public async Task TimestampFormat_IsTwelveCharactersWide()
    {
        string clock = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)
            .ToLocalTime()
            .ToString(LogRowFormat.TimestampFormat, CultureInfo.InvariantCulture);

        await Assert.That(clock.Length).IsEqualTo(LogRowFormat.TimestampWidth);
        await Assert.That(LogLevelTag.Width).IsEqualTo(4);
    }
}
