using System.Globalization;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The single seam for output-limit mechanics (#1587): every builtin tool
///     used to compose its own cap arithmetic, its own "truncated" flag and its
///     own suffix wording at the tail of its output. The wording, the marker
///     composition and the head-kept cut now live in one place; tools keep
///     declaring what their limits ARE (line windows, entry caps, result
///     clamps are tool semantics — resource bounds and arg contracts) while
///     HOW a limit is rendered/unified is policy here.
/// </summary>
/// <remarks>
///     <para>
///         The change point the issue names — "a single per-turn budget" — is the
///         <see cref="OutputLimitToolDecorator" /> applied in
///         <c>ToolsCatalog.CreateToolRegistry</c>; its default cap is
///         <see cref="DecoratorDefaultCap" />. Adding or retuning the global
///         policy edits this file, not N tools.
///     </para>
///     <para>
///         Not new extension axes: public types inside
///         <c>Harbor.Tools.Builtin</c>, applied at composition; nothing consumer
///         facing is added and <c>Harbor.Abstractions</c> is untouched — no seam
///         a plugin reaches, so <c>PluginAxisRegistry</c> does not name one.
///     </para>
/// </remarks>
public static class ToolOutputLimit
{
    /// <summary>
    ///     Default outermost char cap the <see cref="OutputLimitToolDecorator" />
    ///     applies to the five generation-point tools (read/bash/grep/webfetch/tree).
    ///     Sits above every per-tool char cap (bash hard-cut 50k, read 100k) so the
    ///     two layers compose — the tool's own note stays intact and the
    ///     decorator only fires on outputs past both, e.g. a webfetch run with
    ///     <c>maxChars</c> above this budget.
    /// </summary>
    public const int DecoratorDefaultCap = 200_000;

    /// <summary>
    ///     The unified truncation note. <paramref name="limit" /> is the limit that
    ///     was hit, <paramref name="unit" /> what the limit counts
    ///     (<c>"chars"</c>, <c>"lines"</c>, <c>"entries"</c> — the caller's
    ///     semantics), and <paramref name="hint" /> the optional continuation
    ///     guidance rendered in parentheses after it.
    /// </summary>
    public static string TruncationNote(int limit, string unit, string? hint = null)
    {
        string note = $"\n\n… truncated at {limit.ToString(CultureInfo.InvariantCulture)} {unit}";
        return hint is null ? note : note + $" ({hint})";
    }

    /// <summary>
    ///     Head-kept char cap: within budget the text passes through untouched
    ///     (same reference, zero allocation); over budget the first
    ///     <paramref name="cap" /> chars stay verbatim and the tail is replaced
    ///     by the unified note. Shape mirrors
    ///     <c>ToolResultContextTrim</c> (Harbor.Application), which caps the
    ///     provider-bound copy downstream — this seam covers the generation point.
    /// </summary>
    public static string Apply(string output, int cap, string? hint = null)
    {
        if (output.Length <= cap)
            return output;

        return string.Concat(
            output.AsSpan(0, cap),
            TruncationNote(cap, "chars", hint));
    }
}
