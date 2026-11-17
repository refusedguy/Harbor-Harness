// ScreenSearchParityTests.cs — the pooled searches are PROVED against the old
// ones, and MEASURED against them.
//
// WHAT THIS FILE IS
// -----------------
// `TuiDriver.WaitForTextAsync` polls a running TUI until a piece of text shows
// up. The log it polls is append-only and never trimmed, so it grows for the
// whole scenario, and the old code copied all of it — twice — on every poll.
//
// The new searches read the characters where they lie, in a pooled buffer. The
// only thing they are allowed to change is the cost: the log is what a PTY
// scenario's verdict is read off, so a search that answers differently makes
// a scenario pass that used to fail.
//
// So the old searches stay, in `ScreenSearchOracle`, and this file:
//
//   1. requires the new searches to answer EXACTLY as the old ones do, over
//      generated inputs; and
//   2. MEASURES what each side allocated, and prints the two numbers.
//
// THE MEASUREMENT
// ---------------
// `GC.GetAllocatedBytesForCurrentThread()` counts the bytes the CALLING THREAD
// allocated. The bodies measured here are synchronous and never await, so the
// per-thread counter is the right one.
//
// The haystack is prepared OUTSIDE the measured window in both cases: the old
// code was handed a string it already had, and the new code is handed the
// characters it already has. What the figure measures is the SEARCH.

using Harbor.E2E.Framework;

namespace Harbor.E2E.Framework.Tests;

/// <summary>
///     The parity proof and the allocation measurement for the pooled screen
///     searches.
/// </summary>
public class ScreenSearchParityTests
{
    /// <summary>How many times a measured body runs, so the figure is a per-call one.</summary>
    private const int Iterations = 500;

    /// <summary>
    ///     The haystacks the parity proof runs over.
    /// </summary>
    /// <remarks>
    ///     The set is chosen around the one rule the old helper had a quirk
    ///     about: it returns its input UNCHANGED when the input holds none of
    ///     ' ', '\n', '\r' and '\t', even when the input holds some other
    ///     whitespace character such as '\v'. The new matcher reproduces that
    ///     rule, and the "vertical tab" case below is the one that would catch
    ///     a matcher that "fixed" it.
    /// </remarks>
    private static readonly string[] Haystacks =
    [
        string.Empty,
        "plain text with no whitespace at all",
        "hello world",
        "hello\nworld",
        "hello\r\nworld",
        "tab\there",
        "vertical\vtab and form\ffeed",
        "non\u00a0breaking space",
        "mixed \t\r\n\v text",
        new string('x', 4096),
        new string(' ', 1024),
        "the quick brown fox jumps over the lazy dog"
    ];

    /// <summary>The needles the parity proof searches for.</summary>
    private static readonly string[] Needles =
    [
        "helloworld",
        "hello",
        "world",
        "zzz",
        "x",
        "verticalvtab",
        "nonbreaking",
        "quickbrownfox",
        "mixedtext",
        "e"
    ];

    /// <summary>
    ///     The tear-tolerant matcher answers exactly as the old one did.
    /// </summary>
    [Test]
    public async Task ContainsIgnoringWhitespace_AnswersWhatTheOldSearchAnswered()
    {
        var failures = new List<string>();

        foreach (string haystack in Haystacks)
        {
            foreach (string needle in Needles)
            {
                bool expected = ScreenSearchOracle.ContainsIgnoringWhitespace(haystack, needle);
                bool actual = ScreenSearch.ContainsIgnoringWhitespace(haystack.AsSpan(), needle.AsSpan());

                if (expected != actual)
                {
                    failures.Add(
                        $"haystack [{Escape(haystack)}], needle '{needle}': "
                        + $"old said {expected}, new said {actual}");
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("; ", failures));
    }

    /// <summary>
    ///     The exact matcher answers exactly as the old one did.
    /// </summary>
    [Test]
    public async Task ContainsOrdinal_AnswersWhatTheOldSearchAnswered()
    {
        var failures = new List<string>();

        foreach (string haystack in Haystacks)
        {
            foreach (string needle in Needles)
            {
                bool expected = ScreenSearchOracle.ContainsOrdinal(haystack, needle);
                bool actual = ScreenSearch.ContainsOrdinal(haystack.AsSpan(), needle.AsSpan());

                if (expected != actual)
                {
                    failures.Add(
                        $"haystack [{Escape(haystack)}], needle '{needle}': "
                        + $"old said {expected}, new said {actual}");
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("; ", failures));
    }

    /// <summary>
    ///     The quirk of the old helper is preserved: a haystack with no ' ',
    ///     '\n', '\r' or '\t' is searched as it is, so a rarer whitespace
    ///     character survives in that case.
    /// </summary>
    /// <remarks>
    ///     This is the case a "cleaned up" matcher would break, so it is
    ///     pinned rather than left to the parity test above: the parity test
    ///     says the two agree, and this one says WHICH behaviour they agree
    ///     on.
    /// </remarks>
    [Test]
    public async Task TheRareWhitespace_QuirkOfTheOldHelperIsPreserved()
    {
        const string Haystack = "vertical\vtab";

        // The old helper returns its input unchanged here — no ' ', '\n', '\r'
        // or '\t' in it — so the vertical tab is part of what is searched.
        bool expected = ScreenSearchOracle.ContainsIgnoringWhitespace(Haystack, "verticalvtab");
        bool actual = ScreenSearch.ContainsIgnoringWhitespace(Haystack.AsSpan(), "verticalvtab".AsSpan());

        await Assert.That(expected).IsFalse()
            .Because("the old helper kept the vertical tab, so the stripped form does not hold the needle");
        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    ///     What the searches cost, before and after.
    /// </summary>
    [Test]
    public async Task TheScreenSearches_AllocateLessThanTheOldOnes()
    {
        string log = new('x', 64 * 1024);
        log += "\n" + new string('y', 64 * 1024);
        const string Needle = "y" + "y" + "y";

        // Warm both sides first, so the first call's JIT is not in the figure.
        ScreenSearchOracle.ContainsIgnoringWhitespace(log, Needle);
        ScreenSearch.ContainsIgnoringWhitespace(log.AsSpan(), Needle.AsSpan());
        ScreenSearchOracle.ContainsOrdinal(log, Needle);
        ScreenSearch.ContainsOrdinal(log.AsSpan(), Needle.AsSpan());

        long oldTolerant = Measure(() => ScreenSearchOracle.ContainsIgnoringWhitespace(log, Needle));
        long newTolerant = Measure(() => ScreenSearch.ContainsIgnoringWhitespace(log.AsSpan(), Needle.AsSpan()));

        long oldExact = Measure(() => ScreenSearchOracle.ContainsOrdinal(log, Needle));
        long newExact = Measure(() => ScreenSearch.ContainsOrdinal(log.AsSpan(), Needle.AsSpan()));

        Console.WriteLine(
            $"[alloc-cost] mechanism=screen-tear-tolerant-search before={oldTolerant} after={newTolerant} "
            + $"per-call={Iterations}");
        Console.WriteLine(
            $"[alloc-cost] mechanism=screen-exact-search before={oldExact} after={newExact} "
            + $"per-call={Iterations}");

        await Assert.That(newTolerant).IsLessThanOrEqualTo(oldTolerant)
            .Because("the pooled matcher must not allocate more than the string-building one did");
        await Assert.That(newExact).IsLessThanOrEqualTo(oldExact)
            .Because("the pooled matcher must not allocate more than the string-building one did");
    }

    /// <summary>
    ///     The bytes one call of <paramref name="body" /> allocates, averaged
    ///     over <see cref="Iterations" /> calls.
    /// </summary>
    /// <param name="body">The body to measure. Must not suspend.</param>
    /// <returns>The average number of bytes per call.</returns>
    private static long Measure(Action body)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            body();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return allocated / Iterations;
    }

    /// <summary>A haystack, escaped, so a failure message is readable.</summary>
    /// <param name="text">The text to escape.</param>
    /// <returns>The escaped text, cut to a readable length.</returns>
    private static string Escape(string text)
    {
        string escaped = text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("\v", "\\v");
        return escaped.Length <= 40 ? escaped : escaped[..40] + "...";
    }
}
