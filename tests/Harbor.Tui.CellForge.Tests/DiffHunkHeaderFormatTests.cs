namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #740: a hunk header is a <em>format</em>, and this parser reads it off
/// untrusted text.
///
/// <para>Where the text comes from. <c>UnifiedDiffParser.Parse</c> is not fed
/// by Harbor's own diff engine. Its one production caller,
/// <c>ToolCardTracker.TryExtractDiff</c>, hands it <em>any</em> tool result
/// whose first line starts with <c>"--- "</c>, <c>"@@ -"</c> or
/// <c>"diff --git"</c> — that three-prefix shape check is the entire gate.
/// Tool output in Harbor includes text this process did not author: an MCP
/// server's resource or prompt body is returned verbatim by
/// <c>McpResourceTool</c>/<c>McpPromptTool</c> (registered in
/// <c>ToolsCatalog</c>), and MCP servers are out-of-process third-party
/// programs. It also includes arbitrary file and command output, which is
/// where a <c>.patch</c> file or a <c>git show</c> lives. So the header is
/// attacker- and model-reachable text, and "it parsed as an int" is not
/// evidence that it is a hunk.</para>
///
/// <para>What the guard asserts. A line whose <c>@@ … @@</c> does not spell
/// out <c>-a[,b] +c[,d]</c> with unsigned decimal numbers is not a hunk
/// header, and <c>Parse</c> must skip it the way it skips any other
/// non-diff line — no hunk row, and no line number derived from it. The
/// numbers must be unsigned (a sign is not diff syntax, and
/// <c>NumberStyles.Integer</c> used to accept one) and must fit an
/// <see cref="int"/> (a longer run is malformed input, not a number to clamp).</para>
///
/// <para>What the guard deliberately does <em>not</em> assert: that long
/// numbers are rejected. They are not. <c>@@ -49876,12 +49880,3 @@</c> is an
/// ordinary header for an ordinary 50 000-line file and must keep parsing —
/// see <see cref="Parse_HunkHeaderWithRealFiveDigitLineNumbers_StillResolves"/>
/// . A four-digit cap would be #737's width bug re-introduced as a parse bug.</para>
/// </summary>
public class DiffHunkHeaderFormatTests
{
    /// <summary>
    /// Malformed headers, each of which the pre-#740 parser believed. The
    /// rows after each one are there so a "skip the header but keep its
    /// numbers" half-fix cannot pass by accident.
    /// </summary>
    [Test]
    [Arguments("@@ - -5,1 +1,1 @@")]                    // sign smuggled past the '@@ -'
    [Arguments("@@ --5,1 +1,1 @@")]                    // negative old start
    [Arguments("@@ -1,+2 +3,4 @@")]                    // explicit plus on a count
    [Arguments("@@ -1,-2 +3,4 @@")]                    // negative count
    [Arguments("@@ -1,2 +3,-4 @@")]                    // negative new-side count
    [Arguments("@@ -1, +2 @@")]                        // comma, then no number
    [Arguments("@@ -1,zzz +2 @@")]                     // old count is not a number
    [Arguments("@@ -1,2 +abc @@")]                     // new start is not a number
    [Arguments("@@ -1,2 +3,zzz @@")]                   // new count is not a number
    [Arguments("@@ -x,1 +1,1 @@")]                     // old start is not a number
    [Arguments("@@ -1 2 +3,4 @@")]                     // count without its comma
    [Arguments("@@ -99999999999999999999,1 +1,1 @@")]  // longer than any int
    [Arguments("@@ -1,2 +99999999999999999999,4 @@")]  // new side overflows only
    public async Task Parse_HunkHeaderNotSpellingOutTheFormat_IsSkippedEntirely(string header)
    {
        var lines = UnifiedDiffParser.Parse($"{header}\n-removed\n+added\n");

        await Assert.That(lines.Any(l => l.Kind == DiffLineKind.HunkHeader))
            .IsFalse()
            .Because($"'{header}' is not a hunk header, so it must not become a hunk row");

        // Every row keeps the sign convention of its own line. A number can
        // only be negative if a malformed header seeded the counter with it.
        await Assert.That(lines.Any(l => l.OldNo < 0 || l.NewNo < 0))
            .IsFalse()
            .Because($"'{header}' must not seed the line counters with a negative value");
    }

    /// <summary>
    /// The counterpart: a header that <em>is</em> the format still parses, at
    /// any width. This is the anti-over-tightening half — the fix must bound
    /// the number, not the file size.
    /// </summary>
    [Test]
    [Arguments("@@ -49876,12 +49880,3 @@", 49876, 49880)]  // ordinary large file
    [Arguments("@@ -1 +1 @@", 1, 1)]                        // count omitted, both sides
    [Arguments("@@ -7,4 +7,4 @@ ctx { }", 7, 7)]            // section heading after '@@'
    public async Task Parse_HunkHeaderWithRealLineNumbers_StillResolves(
        string header,
        int expectedOld,
        int expectedNew)
    {
        var lines = UnifiedDiffParser.Parse($"{header}\n ctx\n-removed\n+added\n");

        await Assert.That(lines.Any(l => l.Kind == DiffLineKind.HunkHeader))
            .IsTrue()
            .Because($"'{header}' is a well-formed hunk header and must not be tightened away");

        // The hunk starts AT the header number; the first row after it
        // consumes it, exactly as the pre-existing suite asserts for
        // '@@ -10,7 +10,8 @@'.
        var ctx = lines.Single(l => l.Kind == DiffLineKind.Context);
        await Assert.That(ctx.OldNo).IsEqualTo(expectedOld + 1);
        await Assert.That(ctx.NewNo).IsEqualTo(expectedNew + 1);
    }

    /// <summary>
    /// The bound is <see cref="int.MaxValue"/>, not a digit count — so the
    /// largest header the parser can hold is still a hunk, and the first
    /// value past it is not.
    /// </summary>
    [Test]
    public async Task Parse_HunkHeaderBoundIsIntMaxValue_NotADigitCount()
    {
        var atBound = UnifiedDiffParser.Parse("@@ -2147483647,1 +2147483647,1 @@\n ctx\n");
        await Assert.That(atBound.Any(l => l.Kind == DiffLineKind.HunkHeader))
            .IsTrue()
            .Because("int.MaxValue is a number the format can hold, so the header is well formed");

        var pastBound = UnifiedDiffParser.Parse("@@ -2147483648,1 +1,1 @@\n ctx\n");
        await Assert.That(pastBound.Any(l => l.Kind == DiffLineKind.HunkHeader))
            .IsFalse()
            .Because("one past int.MaxValue is not a number, so the header is malformed");

        // The row counter's own increment used to go unasserted here, on the
        // reasoning that reaching the wrap needs a two-billion-line file.
        // That was wrong, and this test is the proof: it has just asserted
        // that the header above IS a hunk, so a single row underneath it is
        // a two-billion-line file in 36 bytes. The increment is asserted, in
        // DiffLineNumberWrapTests (#850).
    }
}
