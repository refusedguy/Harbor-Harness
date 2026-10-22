// ScreenSearchOracle.cs — the OLD searches, kept as the ORACLE the pooled
// searches are proved against.
//
// WHY THE OLD IMPLEMENTATION IS STILL HERE
// -----------------------------------------
// The new `ScreenSearch` is only allowed to be cheaper. It is not allowed to
// be DIFFERENT: `TuiDriver.WaitForTextAsync` decides whether a PTY scenario
// has seen its text, and a search that answers differently makes a scenario
// pass that used to fail, or the other way round. So the old searches stay,
// as the thing the new ones are compared against, and they are used in
// exactly one place: the parity test.
//
// The proof is in `ScreenSearchParityTests` (Harbor.E2E.Framework.Tests): the
// new search equals the oracle over generated inputs, and the test MEASURES
// what the old one allocated, so "cheaper" is a number and not a claim.
//
// WHAT THE OLD SEARCHES WERE
// ---------------------------
//   * the exact match — `haystack.Contains(needle, StringComparison.Ordinal)`;
//   * the tear-tolerant match — `StripWhitespace(haystack).Contains(needle,
//     StringComparison.Ordinal)`, where `StripWhitespace` is the helper in
//     `ScreenText.cs`, quirk included.
//
// The old searches took STRINGS, because the old code had already built the
// string it was searching. The new ones take spans, because the new code
// searches where the characters lie.

namespace Harbor.E2E.Framework;

/// <summary>
///     The searches the PTY driver used before <see cref="ScreenSearch" />,
///     kept verbatim as the ground truth.
/// </summary>
internal static class ScreenSearchOracle
{
    /// <summary>
    ///     The OLD exact match: an ordinal substring search over a string.
    /// </summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The text to look for.</param>
    /// <returns><c>true</c> when the needle occurs in the haystack.</returns>
    internal static bool ContainsOrdinal(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    /// <summary>
    ///     The OLD tear-tolerant match: the haystack with its whitespace
    ///     removed, then an ordinal search.
    /// </summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The text to look for.</param>
    /// <returns><c>true</c> when the needle occurs in the whitespace-stripped haystack.</returns>
    internal static bool ContainsIgnoringWhitespace(string haystack, string needle) =>
        ScreenText.StripWhitespace(haystack).Contains(needle, StringComparison.Ordinal);
}
