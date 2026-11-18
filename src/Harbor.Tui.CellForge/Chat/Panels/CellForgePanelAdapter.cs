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
    private static volatile IReadOnlyList<IPanelWidgetDecoder> _decoders = Defaults();

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
            // #2648: _decoders is an interface-typed volatile field. Read it once
            // and index it, so the per-frame path neither boxes an enumerator nor
            // sees the field change between Count and the last access.
            var decoders = _decoders;
            for (int i = 0; i < decoders.Count; i++)
            {
                if (decoders[i].TryDecode(widget, ctx, out var rows) && rows is not null)
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
            IEnumerable<string> lines => MaterializeLines(lines),
            _ => SplitLines(widget.ToString() ?? string.Empty),
        };
    }

    /// <summary>
    ///     #2648: <c>string.Split</c> allocates a separator array, a result
    ///     array and one substring per line on every frame. Walk the span with
    ///     <c>IndexOf</c>, count first, then fill an exact-sized array.
    ///     <c>\r\n</c> stays one separator (it was first in the old separator
    ///     list) and empty entries, including a trailing one, are kept exactly
    ///     as <c>StringSplitOptions.None</c> kept them.
    /// </summary>
    private static IReadOnlyList<string> SplitLines(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan();

        int count = 1; // Splitting "" yields one empty entry.
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '\r')
            {
                if (i + 1 < span.Length && span[i + 1] == '\n')
                {
                    i++;
                }
                count++;
            }
            else if (span[i] == '\n')
            {
                count++;
            }
        }

        var rows = new string[count];
        int next = 0;
        while (true)
        {
            int cr = span.IndexOf('\r');
            int lf = span.IndexOf('\n');
            int at = cr < 0 ? lf : lf < 0 ? cr : Math.Min(cr, lf);
            if (at < 0)
            {
                rows[next] = span.ToString();
                break;
            }

            bool carriageReturn = span[at] == '\r';
            rows[next++] = span[..at].ToString();
            span = span[(at + 1)..];
            if (carriageReturn && !span.IsEmpty && span[0] == '\n')
            {
                span = span[1..];
            }
        }

        return rows;
    }

    /// <summary>
    ///     #2648: <c>Select(...).ToArray()</c> allocates a delegate, an iterator
    ///     and a growing buffer on every frame. A source that knows its size
    ///     copies straight into an exact array; the only remaining work is the
    ///     null-to-empty coalescing the old Select performed.
    /// </summary>
    private static IReadOnlyList<string> MaterializeLines(IEnumerable<string> lines)
    {
        if (lines is ICollection<string> sized)
        {
            var rows = new string[sized.Count];
            sized.CopyTo(rows, 0);
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] ??= string.Empty;
            }
            return rows;
        }

        var list = new List<string>();
        foreach (string line in lines)
        {
            list.Add(line ?? string.Empty);
        }
        return list;
    }
}
