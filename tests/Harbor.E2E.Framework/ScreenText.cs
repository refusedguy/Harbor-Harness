// ScreenText.cs — the whitespace-stripping helper the PTY driver matches with.
//
// WHY THIS FILE EXISTS
// --------------------
// `TuiDriver.WaitForTextAsync` has a tear-tolerant fallback: a renderer whose
// panels word-wrap, or which repaints mid-line, splits the text it was
// looking for across grid rows, and the exact match never finds it. The
// fallback drops EVERY whitespace character from both sides and matches what
// is left, so a pattern still matches after a renderer tore the row.
//
// The helper used to live inside `TuiDriver` as a private method. It is here
// because the PARITY TEST needs it: `ScreenSearch.ContainsIgnoringWhitespace`
// is the pooled, allocation-free spelling of the same match, and the only
// honest way to say "the same match" is to compare the two against the
// helper they both use.
//
// THE QUIRK IS DELIBERATE, AND IT IS NOT A QUIRK
// ------------------------------------------------
// The fast path returns the input UNCHANGED when the input holds none of
// ' ', '\n', '\r' and '\t' — even when the input holds some OTHER whitespace
// character, such as '\v'. The slow path would have removed that character.
// So the two paths do not agree, and the fast path is the one the driver has
// always used.
//
// Changing that would change what a PTY scenario matches, so the helper is
// kept exactly as it was. A reader who thinks the early-out is a bug is not
// wrong — it is a difference, and it is preserved on purpose.
//
// The name is `StripWhitespace`, not `CollapseWhitespace`, and the difference
// is load-bearing rather than cosmetic. The other whitespace helper in this
// repo is `PanelText.SingleLine` (src/Harbor.Ui.Framework.Projection), which
// SUBSTITUTES CR/LF with a space so a multi-line log message fits one display
// row — it preserves length and every other character. This one DELETES every
// whitespace character so a pattern still matches after a renderer tore the
// row across grid lines — its result is shorter than its input.
//
// Issue #574's duplicate census saw only the three dead `contrib/` copies,
// all of which really are equivalent to `PanelText.SingleLine`, and so
// reported a byte-identical triad without ever comparing this live one. Same
// name, opposite intent; the call site could not have told them apart,
// because both take a string and return a string. Pinned by
// `WhitespaceCollapseDivergenceTests` so neither side is "simplified" into
// the other.

namespace Harbor.E2E.Framework;

/// <summary>
///     The whitespace-stripping helper the tear-tolerant matcher is built on.
/// </summary>
internal static class ScreenText
{
    /// <summary>
    ///     Removes every whitespace character, used by the tear-tolerant matcher.
    /// </summary>
    /// <param name="input">The text to strip.</param>
    /// <returns>The text without its whitespace.</returns>
    internal static string StripWhitespace(string input)
    {
        if (input.IndexOf(' ') < 0 && input.IndexOf('\n') < 0 && input.IndexOf('\r') < 0 && input.IndexOf('\t') < 0)
            return input;
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (char ch in input)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }
}
