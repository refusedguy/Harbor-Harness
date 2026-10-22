// ScreenSearch.cs — the searches the PTY driver runs on every poll.
//
// WHAT THIS FILE IS
// -----------------
// `TuiDriver.WaitForTextAsync` polls a running TUI until a piece of text shows
// up: it reads the append-only log of everything the child has written, and it
// also reads the 2D grid. Both readings used to be MATERIALISED — copied into
// a fresh string — and both happened on every poll, every 100 ms, for up to
// ten seconds.
//
// The log is append-only and never trimmed, so it grows for the whole
// scenario: a TUI that repaints a 120x50 screen ten times a second writes
// 6 KiB per frame, and a thirty-second scenario leaves a log of a megabyte or
// two. Copying that log on every poll, and then copying it a second time
// with the whitespace removed, is the single largest source of garbage in the
// E2E and ui shards.
//
// WHAT THIS FILE CHANGES
// ----------------------
// Nothing about WHAT is searched. The searches here are the same searches the
// old code made, over the same characters, with the same answers:
//
//   * `ContainsOrdinal` is `string.Contains(pattern, StringComparison.Ordinal)`.
//
//   * `ContainsIgnoringWhitespace` is the tear-tolerant matcher: the haystack
//     with its whitespace removed, then an ordinal search. The old code
//     spelled that as `StripWhitespace(text).Contains(needle, Ordinal)`, and
//     `StripWhitespace` has a quirk that is preserved HERE on purpose: it
//     returns its input UNCHANGED when the input contains none of ' ', '\n',
//     '\r' and '\t' — even if the input holds some other whitespace
//     character. A matcher that "fixed" that would answer differently from the
//     code it replaced, and the whole point of this file is that it does not.
//
// The difference is WHERE the characters are searched: in a pooled buffer
// instead of a fresh string. The pooled buffer is rented, filled and returned
// inside the call, so a poll allocates nothing.
//
// WHY THE PARITY TEST LIVES NEXT TO IT
// ------------------------------------
// `ScreenSearchParityTests` in `Harbor.E2E.Framework.Tests` holds the OLD
// implementations verbatim and requires the new ones to answer identically on
// generated inputs, and it MEASURES what the old ones allocated. A rewrite
// that is not measured and not pinned is a rewrite that can only be judged by
// reading it.

using System.Buffers;

namespace Harbor.E2E.Framework;

/// <summary>
///     The searches the PTY driver runs on every poll, over pooled buffers
///     instead of fresh strings.
/// </summary>
internal static class ScreenSearch
{
    /// <summary>
    ///     An ordinal substring search — the same answer as
    ///     <c>haystack.Contains(needle, StringComparison.Ordinal)</c>.
    /// </summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The text to look for.</param>
    /// <returns><c>true</c> when the needle occurs in the haystack.</returns>
    internal static bool ContainsOrdinal(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle) =>
        MemoryExtensions.Contains(haystack, needle);

    /// <summary>
    ///     The tear-tolerant matcher: the haystack with its whitespace
    ///     removed, then an ordinal search for the needle.
    /// </summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The text to look for, already free of whitespace.</param>
    /// <returns>
    ///     <c>true</c> when the needle occurs in the whitespace-stripped
    ///     haystack.
    /// </returns>
    /// <remarks>
    ///     The whitespace rule is the one the old <c>StripWhitespace</c> helper
    ///     applied, quirk included: a haystack that holds none of ' ', '\n',
    ///     '\r' and '\t' is searched AS IT IS, so a rare whitespace character
    ///     such as '\v' survives in that case and does not in the other. The
    ///     two spellings of the rule are not the same function, and the caller
    ///     this replaces used the quirky one.
    /// </remarks>
    internal static bool ContainsIgnoringWhitespace(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        if (needle.IsEmpty)
        {
            // `string.Contains("")` is true, and the old spelling was true too.
            return true;
        }

        if (haystack.IsEmpty)
        {
            return false;
        }

        if (!HasCommonWhitespace(haystack))
        {
            // The old helper returned its input unchanged here, so the search
            // runs over the haystack itself.
            return MemoryExtensions.Contains(haystack, needle);
        }

        char[] buffer = ArrayPool<char>.Shared.Rent(haystack.Length);
        try
        {
            int length = StripWhitespace(haystack, buffer);
            return MemoryExtensions.Contains(buffer.AsSpan(0, length), needle);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Copies the haystack into <paramref name="destination" /> with every
    ///     whitespace character removed, and says how many characters were
    ///     written.
    /// </summary>
    /// <param name="haystack">The text to strip.</param>
    /// <param name="destination">The buffer to write into.</param>
    /// <returns>The number of characters written.</returns>
    internal static int StripWhitespace(ReadOnlySpan<char> haystack, char[] destination)
    {
        int written = 0;

        foreach (char ch in haystack)
        {
            if (!char.IsWhiteSpace(ch))
            {
                destination[written] = ch;
                written++;
            }
        }

        return written;
    }

    /// <summary>
    ///     Whether the haystack holds any of the four whitespace characters the
    ///     old helper's fast path looked for.
    /// </summary>
    /// <param name="haystack">The text to inspect.</param>
    /// <returns><c>true</c> when at least one of them is present.</returns>
    private static bool HasCommonWhitespace(ReadOnlySpan<char> haystack) =>
        haystack.IndexOf(' ') >= 0
        || haystack.IndexOf('\n') >= 0
        || haystack.IndexOf('\r') >= 0
        || haystack.IndexOf('\t') >= 0;
}
