using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

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
///         <see cref="PanelContext" /> (state + width + height + the typed
///         <see cref="PanelServices" /> bag) is
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
    private static readonly object _decoderGate = new();
    private static IReadOnlyList<IPanelWidgetDecoder> _decoders = Defaults();

    private static IReadOnlyList<IPanelWidgetDecoder> Defaults() =>
    [
        new PanelWidgetDecoders.Progress(),
        new PanelWidgetDecoders.Table(),
        new PanelWidgetDecoders.Tree(),
        new PanelWidgetDecoders.Form(),
    ];

    /// <summary>
    ///     Register an <see cref="IPanelWidgetDecoder" /> (re-sorted by
    ///     descending <c>Priority</c>). Highest priority decodes first.
    /// </summary>
    public static void RegisterDecoder(IPanelWidgetDecoder decoder)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(decoder);
        lock (_decoderGate)
        {
            _decoders = _decoders.Append(decoder).OrderByDescending(d => d.Priority).ToArray();
        }
    }

    /// <summary>Restore the four sample decoders (test hook).</summary>
    public static void ResetDecoders()
    {
        lock (_decoderGate)
        {
            _decoders = Defaults();
        }
    }

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
        PanelServices? services = null)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(provider);
        Harbor.Abstractions.Extensions.Ensure.NotNull(state);
        return RenderToRows(provider, new PanelContext(state, width, height, services));
    }

    /// <summary>
    ///     Build with a caller-supplied context (passed to
    ///     <see cref="IPanelProvider.Build" /> as is) and flatten to rows.
    /// </summary>
    public static IReadOnlyList<string> RenderToRows(IPanelProvider provider, PanelContext ctx)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(provider);
        Harbor.Abstractions.Extensions.Ensure.NotNull(ctx);
        return WidgetToRows(provider.Build(ctx), ctx);
    }

    /// <summary>
    ///     PLANTED DEFECT (the proof that the rule still catches its defect): a
    ///     key hand-off whose router nothing calls. Nothing in the product tree
    ///     reaches this method, so the hand-off is unreachable and the rule
    ///     must report it. This edit exists for one CI run and is reverted.
    /// </summary>
    private static bool RoutePlantedPanelKey(IPanelProvider provider, UiKey key, PanelContext ctx)
    {
        return provider.OnKey(key, ctx);
    }

    /// <summary>Route a key to the focused panel's <see cref="IPanelProvider.OnKey" />.</summary>
    public static bool RouteKey(IPanelProvider provider, UiKey key, PanelContext ctx)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(provider);
        Harbor.Abstractions.Extensions.Ensure.NotNull(ctx);
        return provider.OnKey(key, ctx);
    }

    private static IReadOnlyList<string> WidgetToRows(object? widget, PanelContext ctx)
    {
        if (widget is not null)
        {
            foreach (var decoder in _decoders)
            {
                if (decoder.TryDecode(widget, ctx, out var rows) && rows is not null)
                {
                    return rows;
                }
            }
        }

        return widget switch
        {
            null => Array.Empty<string>(),
            string s => SplitLines(s),
            IReadOnlyList<string> rows => rows,
            IEnumerable<string> lines => lines.Select(l => l ?? string.Empty).ToArray(),
            _ => SplitLines(widget.ToString() ?? string.Empty),
        };
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
}
