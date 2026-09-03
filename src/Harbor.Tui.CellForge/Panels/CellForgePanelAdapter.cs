using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Skeleton adapter from the renderer-agnostic
///     <see cref="IPanelProvider.Build(PanelContext)" /> contract to CellForge
///     cell rows (CF-E-001 preparation). No Spectre dependencies: the provider's
///     <c>object?</c> widget is flattened to plain text lines via
///     <c>ToString</c> / line splitting; geometry clipping stays with the
///     caller (<c>LayoutTree</c> / chat-screen layout).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="PanelContext" /> (state + width + height + services) is
///         passed through untouched — the adapter never mutates
///         <see cref="UiState" /> and never interprets the widget type.
///     </para>
///     <para>
///         CellForge-native providers (CF-E-002…E-006) may later return
///         <c>string</c> / <c>IReadOnlyList&lt;string&gt;</c> directly to skip
///         the <c>ToString</c> fallback; this adapter already handles both.
///     </para>
/// </remarks>
public static class CellForgePanelAdapter
{
    /// <summary>
    ///     Build the provider's widget for the given state/geometry and flatten
    ///     it to text rows. Returns an empty list when the provider returns
    ///     <see langword="null" /> (empty placeholder).
    /// </summary>
    public static IReadOnlyList<string> RenderToRows(
        IPanelProvider provider,
        UiState state,
        int width,
        int height,
        IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(state);
        return RenderToRows(provider, new PanelContext(state, width, height, services));
    }

    /// <summary>
    ///     Build with a caller-supplied context (passed to
    ///     <see cref="IPanelProvider.Build" /> as is) and flatten to rows.
    /// </summary>
    public static IReadOnlyList<string> RenderToRows(IPanelProvider provider, PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(ctx);
        return WidgetToRows(provider.Build(ctx));
    }

    /// <summary>Route a key to the focused panel's <see cref="IPanelProvider.OnKey" />.</summary>
    public static bool RouteKey(IPanelProvider provider, UiKey key, PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(ctx);
        return provider.OnKey(key, ctx);
    }

    private static IReadOnlyList<string> WidgetToRows(object? widget) => widget switch
    {
        null => Array.Empty<string>(),
        string s => SplitLines(s),
        IReadOnlyList<string> rows => rows,
        IEnumerable<string> lines => lines.Select(l => l ?? string.Empty).ToArray(),
        _ => SplitLines(widget.ToString() ?? string.Empty),
    };

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
}
