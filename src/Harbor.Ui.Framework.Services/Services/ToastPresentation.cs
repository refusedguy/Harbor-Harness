using System.Collections.Frozen;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     How one <see cref="ToastKind" /> is presented: the glyph a text renderer
///     draws, the icon resource key a vector renderer paints, and the brush
///     resource key the accent wears.
/// </summary>
/// <param name="Glyph">
///     The character a text renderer draws as the toast's leading glyph. A char
///     and not a string: the CellForge row is written into a caller-owned
///     buffer, and a one-character string per toast per frame would be an
///     allocation for a constant.
/// </param>
/// <param name="IconResourceKey">
///     The theme resource key of the icon geometry that paints the toast's kind.
///     A KEY and not a geometry: the XAML dictionary stays the source of truth
///     for the shape, and the renderer resolves the key against it.
/// </param>
/// <param name="BrushResourceKey">
///     The theme resource key of the brush that paints the toast's accent. A KEY
///     and not a brush: the theme dictionaries own the colours, so a renderer
///     resolves this against the merged resources it already has.
/// </param>
public readonly record struct ToastPresentation(
    char Glyph,
    string IconResourceKey,
    string BrushResourceKey);

/// <summary>
///     The one table that says how a toast kind looks (#1543).
/// </summary>
/// <remarks>
///     <para>
///         Before this table, the same mapping was written in three places by
///         two UI stacks: the CellForge toast overlay had its own glyph switch,
///         and the Avalonia app had one converter for the icon key and another
///         for the brush key. The two Avalonia converters and the TUI overlay
///         had already drifted apart in shape, and a fourth copy in the
///         desktop shell had drifted in VALUE: it drew "▲" and "✕" where the
///         TUI drew "!" and "✗" for the same two kinds. Nothing caught it,
///         because a mapping that lives in a switch is only wrong to the eye.
///     </para>
///     <para>
///         The table stores KEYS, never brushes or geometries, so the theme
///         dictionaries stay the source of truth for what a toast looks like
///         and no C# token duplicates a colour or a path. It lives here, in the
///         presentation layer, next to the <see cref="ToastKind" /> it
///         describes: the domain must not know how a toast is drawn, and
///         <see cref="ToastKind" /> itself is a presentation concept, so a
///         domain-level table would have been the wrong layer to put it in.
///     </para>
/// </remarks>
public static class ToastPresentations
{
    /// <summary>
    ///     The table itself. Frozen because it is read on the toast overlay's
    ///     paint path — once per painted toast per frame — and because a
    ///     frozen map cannot be changed behind a reader that holds it.
    /// </summary>
    private static readonly FrozenDictionary<ToastKind, ToastPresentation> _byKind =
        new Dictionary<ToastKind, ToastPresentation>
        {
            [ToastKind.Info] = new('ℹ', "IcInfo", "AccentPrimaryBrush"),
            [ToastKind.Success] = new('✓', "IcCheck", "StateSuccessBrush"),
            [ToastKind.Warning] = new('!', "IcWarning", "StateWarningBrush"),
            [ToastKind.Error] = new('✗', "IcError", "StateErrorBrush"),
        }.ToFrozenDictionary();

    /// <summary>
    ///     The table, one row per <see cref="ToastKind" /> member.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Exposed so the guard can prove it is COMPLETE: a kind added to
    ///         the enum without a row here fails
    ///         <c>ToastPresentationTests.Every_ToastKind_Has_A_Row</c>, which
    ///         is the property a hand-written switch cannot hold on its own —
    ///         a switch whose discard arm answers "info" for everything
    ///         renders a new kind as an informational toast and says so
    ///         nowhere.
    ///     </para>
    /// </remarks>
    public static IReadOnlyDictionary<ToastKind, ToastPresentation> All => _byKind;

    /// <summary>
    ///     The presentation of <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The kind to present.</param>
    /// <returns>
    ///     The row for <paramref name="kind" />, or the informational row for a
    ///     value that is not a declared kind. The fallback is deliberate and is
    ///     what every one of the three call sites did before the table existed:
    ///     a toast whose kind did not arrive still has to render.
    /// </returns>
    public static ToastPresentation For(ToastKind kind) =>
        _byKind.TryGetValue(kind, out ToastPresentation presentation)
            ? presentation
            : _byKind[ToastKind.Info];

    /// <summary>
    ///     The leading glyph of <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The kind to present.</param>
    /// <returns>The character the toast's row starts with.</returns>
    public static char Glyph(ToastKind kind) => For(kind).Glyph;

    /// <summary>
    ///     The icon resource key of <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The kind to present.</param>
    /// <returns>The theme resource key of the icon geometry for the kind.</returns>
    public static string IconResourceKey(ToastKind kind) => For(kind).IconResourceKey;

    /// <summary>
    ///     The brush resource key of <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The kind to present.</param>
    /// <returns>The theme resource key of the brush for the kind.</returns>
    public static string BrushResourceKey(ToastKind kind) => For(kind).BrushResourceKey;
}
