// DebouncedSettleShapeRule.cs — the settle that waits out the debounce must not
// BE the debounce, multiplied (#757).
//
// THE DEFECT
// ----------
// `DebouncedPluginWatcherTests.QuickSaveBurst_CollapsesToSingleModified` settles
// on `await Task.Delay(Debounce * 2)` and then asserts a callback COUNT. With
// `Debounce = 120ms` that is a 240ms window over a 120ms contract — the settle is
// the same order of magnitude as the thing it is waiting out, so a whole legal
// debounce width is left inside which a second burst is indistinguishable from
// "nothing else fired". The test reddened intermittently in `test (platform)`
// (run 36669693739, "Expected to be 2 but found 3") and passed on re-run of the
// same commit.
//
// The arithmetic, with t0 = the last of five writes and D = the debounce:
// `NextAsync` returns at ~t0+D, the assertion lands at ~t0+3D, and a raw event
// delayed past t0+D arms a timer that fires at t0+2D+e < t0+3D whenever e < D.
// So the test fails whenever ANY one event of the burst arrives more than a
// debounce after the first — which needs ~119ms of pre-emption on a cycle that
// otherwise runs in ~1ms.
//
// WHY A RULE AND NOT A PATCH
// --------------------------
// The one-line patch is obvious, which is exactly why it needs a gate: nothing
// about `await Task.Delay(Debounce * 2)` is a compile error, and the next
// debounced test to be written will reach for the same shape because it is the
// shape that is already there. A flake that was fixed once by hand is a flake
// with one defence and no memory.
//
// SCOPE, AND THE HONEST BOUNDARY OF IT
// ------------------------------------
// This grades ONE method, and that is a considered limit rather than a narrow
// one. `NonCsFiles_AreIgnored` keeps a fixed `Task.Delay` — it asserts a
// NEGATIVE (zero callbacks), where "wait longer" IS the assertion rather than a
// proxy for "nothing else fired", so the failure this rule describes cannot
// happen there. Widening the rule to every settle in the file would flag a
// correct test and train the next reader to add an exemption instead of
// reading the difference.
//
// The sibling `Delete_OutranksEarlierModifications` is NOT here either, and for
// the opposite reason: its exposure is a MISSING baseline drain, not a fixed
// settle, so the shape graded here is not the defect. It is fixed in the same
// change because both are the same issue — but a rule that graded "every settle
// in the file" would have missed it while catching a correct test.
//
// NON-VACUITY
// -----------
//   1. TheTargetFileIsReadable — the graded file was found and the graded method
//      is in it. Without it, "no fixed-multiple settles" and "the walk found
//      nothing" are the same report — the exact defect class #877 is about,
//      where a measurement that cannot see its subject reports a healthy zero.
//   2. TheMatcherAnswersTheDeclaredQuestion — a positive control over a fixed
//      table of synthetic method bodies, so the rule above is anchored to a
//      STATED contract rather than to whatever the matcher happens to do on the
//      day it is pinned. Without it, a matcher that stopped matching anything
//      would leave this rule permanently green.
//
// The rule shipped RED on purpose: the first CI run is the measurement. There is
// no local dotnet in the authoring environment, so that run's log is the only
// execution of this matcher there has ever been; the fix in the second commit is
// what turns it green.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Grades the settle shape of the burst-collapse test in
///     <c>DebouncedPluginWatcherTests</c>: a fixed multiple of the debounce is a
///     settle sized like the thing it waits out, and cannot certify a count.
/// </summary>
public sealed class DebouncedSettleShapeRule
{
    /// <summary>The graded file, relative to the repository root.</summary>
    internal const string GradedFile =
        "tests/Harbor.Plugins.Runtime.Tests/Hosting/DebouncedPluginWatcherTests.cs";

    /// <summary>The one method this rule grades. See the scope note in the file header.</summary>
    internal const string GradedMethod = "QuickSaveBurst_CollapsesToSingleModified";

    /// <summary>
    ///     What the matcher is supposed to decide, on a fixed table of synthetic
    ///     method bodies.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The declared contract, stated as data. A FIXED MULTIPLE settle —
    ///         <c>Task.Delay(Debounce * 2)</c> — is the defect: its length is
    ///         derived from the debounce it is waiting out, so the two scale
    ///         together and the settle can never be comfortably longer than the
    ///         contract it is meant to certify.
    ///     </para>
    ///     <para>
    ///         The other rows are why the matcher is not simply "any
    ///         <c>Task.Delay</c>". A literal <c>Task.Delay(900)</c> is a fixed
    ///         number of milliseconds that says nothing about the debounce — it is
    ///         stale the moment <c>Debounce</c> changes, which is a real defect, but
    ///         a DIFFERENT one, and a rule claiming it would be claiming two
    ///         things. And a settle that derives its length from a measured
    ///         <c>SettledCountAsync</c> is the fix, so it must be accepted.
    ///     </para>
    /// </remarks>
    private static readonly (string Name, string Body, bool IsFixedMultiple)[] DeclaredContract =
    [
        ("multiple of the debounce",
            "await Task.Delay(Debounce * 2);", true),
        ("multiple written without spaces",
            "await Task.Delay(Debounce*3);", true),
        ("literal milliseconds",
            "await Task.Delay(900);", false),
        ("literal wrapped in TimeSpan",
            "await Task.Delay(TimeSpan.FromMilliseconds(900));", false),
        ("quiescence drain — the fix",
            "await SettledCountAsync(received, Debounce, DrainAttempts);", false),
        ("polling interval, not a settle",
            "await Task.Delay(40);", false),
    ];

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     The graded method must not settle on a fixed multiple of the debounce.
    /// </summary>
    /// <remarks>
    ///     When this fails, the message IS the finding: it names the line, so the
    ///     arithmetic above can be checked against the code that produced it
    ///     rather than believed.
    /// </remarks>
    [Test]
    public async Task ABurstCollapseTest_MustSettleOnQuiescenceNotAFixedMultiple()
    {
        string[] lines = SourceCommentStripper.StripAll(File.ReadAllLines(GradedPath()));

        List<int> offenders = FixedMultipleSettles(lines, GradedMethod);
        string described = offenders.Count == 0
            ? "(none)"
            : string.Join(" | ", offenders.Select(o => "line " + o + ": " + lines[o - 1].Trim()));

        await Assert.That(offenders.Count)
            .IsEqualTo(0)
            .Because(
                "DebouncedPluginWatcherTests." + GradedMethod + " settles on a fixed multiple of its own "
                + "debounce and then asserts a callback count. A settle sized like the window it waits out "
                + "leaves a whole legal debounce width in which a second burst is indistinguishable from "
                + "'nothing else fired', so the count assertion cannot tell a debounce that failed to "
                + "collapse from a burst that was split by host pre-emption. That is what reddened "
                + "test (platform) at run 36669693739 ('Expected to be 2 but found 3'), and it passed on "
                + "re-run of the same commit. Wait for quiescence instead, and bound the wait — an "
                + "unbounded drain loop is the other half of the same defect. Offending settles: "
                + described);
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The graded file was found, and the graded method is in it. Without
    ///     this, a matcher pointed at the wrong path reports the same clean zero.
    /// </summary>
    [Test]
    public async Task TheTargetFileIsReadable()
    {
        string path = GradedPath();

        await Assert.That(File.Exists(path))
            .IsTrue()
            .Because(
                "this rule grades " + GradedFile + ", and with no checkout there is nothing to read. "
                + "A missing file and a file with no fixed-multiple settles produce the same report, "
                + "which is the defect class #877 exists to stop.");

        string[] lines = SourceCommentStripper.StripAll(File.ReadAllLines(path));
        int headers = MethodHeaderLines(lines, GradedMethod).Count;

        await Assert.That(headers)
            .IsGreaterThan(0)
            .Because(
                "the rule above grades exactly one method by name, so a rename makes it grade nothing at "
                + "all — which is green. If the method was renamed or moved, point this rule at the new "
                + "name in the same change. Method headers found for '" + GradedMethod + "': " + headers);
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     The matcher answers the question the rule above states, on a fixed
    ///     table of synthetic method bodies.
    /// </summary>
    /// <remarks>
    ///     Load-bearing for the rule, not decorative. If the matcher silently
    ///     stopped matching, the rule would pass on a tree that has the defect
    ///     back — and nothing else in this file would notice. This control fails
    ///     first.
    /// </remarks>
    [Test]
    public async Task TheMatcherAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredContract
            .Where(row => FixedMultipleSettleLine(row.Body) != row.IsFixedMultiple)
            .Select(row =>
                row.Name + " (expected " + (row.IsFixedMultiple ? "a fixed multiple" : "not one")
                + ", matcher says " + (FixedMultipleSettleLine(row.Body) ? "a fixed multiple" : "not one") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the rule above is only meaningful while the matcher still recognises a settle whose length "
                + "is derived from the debounce. If a row disagrees, the matcher changed and the rule's "
                + "clean report no longer means what it says. Note the scope: a LITERAL millisecond delay "
                + "is not a fixed multiple and is deliberately not graded — it is stale rather than "
                + "self-scaling, which is a different defect with a different fix. Mismatches: "
                + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    // =====================================================================
    // helpers
    // =====================================================================

    /// <summary>Absolute path of the graded file, from the repository root.</summary>
    private static string GradedPath()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            // Reported as a failed assertion by the caller rather than thrown: a
            // rule that throws cannot say what it was unable to measure.
            return GradedFile;
        }

        return Path.Combine(root, GradedFile.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    ///     1-based line numbers of every test-method header in the file, i.e. every
    ///     line declaring a <c>public async Task …(</c>.
    /// </summary>
    /// <remarks>
    ///     Deliberately not brace-matched. The graded file contains
    ///     <c>$"// v{i + 2}"</c>, and an interpolated <c>}</c> inside a string
    ///     literal closes a brace-counted block one token early. Method headers
    ///     sit on their own lines here, so a header scan is both simpler and exact.
    /// </remarks>
    private static List<int> TestMethodHeaders(string[] lines)
    {
        var found = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("public async Task ", StringComparison.Ordinal)
                && lines[i].Contains('('))
            {
                found.Add(i + 1);
            }
        }

        return found;
    }

    /// <summary>
    ///     1-based line numbers of the declarations of <paramref name="methodName" />.
    /// </summary>
    private static List<int> MethodHeaderLines(string[] lines, string methodName)
        =>
        [
            .. TestMethodHeaders(lines)
                .Where(line => lines[line - 1].Contains(methodName + "(", StringComparison.Ordinal))
        ];

    /// <summary>
    ///     The 1-based line numbers inside <paramref name="methodName" /> that settle
    ///     on a fixed multiple of a debounce constant.
    /// </summary>
    /// <remarks>
    ///     Attributed by method-header position rather than by braces: a method's
    ///     span runs from its header to the next test-method header, or to EOF.
    /// </remarks>
    private static List<int> FixedMultipleSettles(string[] lines, string methodName)
    {
        List<int> declared = MethodHeaderLines(lines, methodName);
        if (declared.Count == 0)
        {
            return [];
        }

        int first = declared[0];
        List<int> allHeaders = TestMethodHeaders(lines);
        int following = allHeaders.FirstOrDefault(line => line > first);
        int stop = following > first ? following : lines.Length + 1;

        var offenders = new List<int>();
        for (int line = first; line < stop && line <= lines.Length; line++)
        {
            if (FixedMultipleSettleLine(lines[line - 1]))
            {
                offenders.Add(line);
            }
        }

        return offenders;
    }

    /// <summary>
    ///     Whether a single line is <c>await Task.Delay(&lt;expr&gt; * &lt;integer&gt;)</c>.
    /// </summary>
    /// <remarks>
    ///     The integer literal on the right of the <c>*</c> is what makes it a
    ///     multiple. <c>Debounce * 2</c> scales with the debounce; <c>40</c>,
    ///     <c>900</c> and <c>TimeSpan.FromMilliseconds(900)</c> are fixed numbers
    ///     that do not.
    /// </remarks>
    private static bool FixedMultipleSettleLine(string line)
    {
        const string call = "await Task.Delay(";

        string trimmed = line.Trim();
        int start = trimmed.IndexOf(call, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        // Keep only the argument list; anything past its closing paren is not part
        // of the expression being graded.
        string rest = trimmed[(start + call.Length)..];
        int close = rest.IndexOf(')');
        if (close >= 0)
        {
            rest = rest[..close];
        }

        int asterisk = rest.IndexOf('*');
        if (asterisk < 0)
        {
            return false;
        }

        string multiplier = rest[(asterisk + 1)..].Trim();
        return multiplier.Length > 0 && multiplier.All(char.IsAsciiDigit);
    }
}
