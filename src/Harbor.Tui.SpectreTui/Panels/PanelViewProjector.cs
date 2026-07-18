using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Abstractions.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels;

/// <summary>
///     Adapter that wraps the existing <c>ChatViewProjector</c> and overlays panel
///     widgets produced by registered <see cref="IPanelProvider" /> instances. The
///     host <c>ChatScreen</c> calls <see cref="BuildWidgets" /> every frame and
///     <c>context.Render</c>s each entry by name into the matching
///     <see cref="PanelLayoutShell" /> region.
/// </summary>
/// <remarks>
///     <para>
///         <b>TEA compliance:</b> all panel visibility / size reads come from the
///         supplied <see cref="UiState" /> via <see cref="PanelRegistryView" />. This
///         projector never mutates panel state — it only builds widgets from a
///         snapshot. State transitions flow through <see cref="UiReducer" />.
///     </para>
/// </remarks>
internal sealed class PanelViewProjector
{
    private readonly ChatViewProjector _chat;
    private readonly PanelRegistry _registry;

    public PanelViewProjector(ChatViewProjector chat, PanelRegistry registry)
    {
        _chat = chat;
        _registry = registry;
    }

    /// <summary>Expose the underlying chat projector for the screen.</summary>
    public ChatViewProjector Chat => _chat;

    /// <summary>The panel registry (for key routing / size queries).</summary>
    public PanelRegistry Registry => _registry;

    /// <summary>
    ///     Build the full widget map: chat regions first, then panel regions by name.
    ///     Keys for panel regions follow the convention
    ///     <c>"{StackName}.{PanelId}"</c> with the tab strip at <c>"{StackName}.Tabs"</c>.
    /// </summary>
    public IReadOnlyDictionary<string, IWidget> BuildWidgets(int historyHeight, UiState state)
    {
        var chat = _chat.BuildWidgets(historyHeight);
        var map = new Dictionary<string, IWidget>(chat.Count + 8);
        foreach (var (k, v) in chat)
            map[k] = v;

        var view = _registry.View(state);
        AddStack(map, "TopPanels", TuiPanelPlacement.Top, view, state);
        AddStack(map, "BottomPanels", TuiPanelPlacement.Bottom, view, state);
        AddStack(map, "LeftPanels", TuiPanelPlacement.Left, view, state);
        AddStack(map, "RightPanels", TuiPanelPlacement.Right, view, state);
        return map;
    }

    private static void AddStack(
        Dictionary<string, IWidget> map,
        string stackName,
        TuiPanelPlacement placement,
        PanelRegistryView view,
        UiState state)
    {
        var panels = view.GetVisibleByPlacement(placement);
        if (panels.Count == 0)
            return;

        map[stackName + ".Tabs"] = BuildTabStrip(stackName, panels, state);

        foreach (var p in panels)
        {
            int width = view.GetSize(p.Id);
            int height = Math.Max(2, width);
            // Real geometry is measured by Spectre.Tui later — pass best-effort hints.
            var ctx = new PanelContext(state, Width: 80, Height: height);
            try
            {
                var widget = p.Build(ctx) as IWidget;
                map[stackName + "." + p.Id] = widget ?? Placeholder(p.Title);
            }
            catch (Exception ex)
            {
                map[stackName + "." + p.Id] = ErrorPlaceholder(p.Title, ex.Message);
            }
        }
    }

    private static IWidget BuildTabStrip(
        string stackName,
        IReadOnlyList<IPanelProvider> panels,
        UiState state)
    {
        var p = new Spectre.Tui.Paragraph().Alignment(Spectre.Tui.Justify.Left);
        var line = new Spectre.Tui.TextLine();
        line.Spans.Add(new Spectre.Tui.TextSpan(" ",
            new Spectre.Console.Style(Spectre.Console.Color.Grey)));
        for (int i = 0; i < panels.Count; i++)
        {
            var panel = panels[i];
            bool focused = state.FocusedPanelId == panel.Id;
            string label = $" {i + 1}:{panel.Title} ";
            var style = focused
                ? new Spectre.Console.Style(Spectre.Console.Color.Aqua, null, Spectre.Console.Decoration.Bold)
                : new Spectre.Console.Style(Spectre.Console.Color.Grey);
            line.Spans.Add(new Spectre.Tui.TextSpan(label, style));
            line.Spans.Add(new Spectre.Tui.TextSpan("|",
                new Spectre.Console.Style(Spectre.Console.Color.Grey)));
        }
        p.Lines.Add(line);
        return p;
    }

    private static IWidget Placeholder(string title)
    {
        var p = new Spectre.Tui.Paragraph().Alignment(Spectre.Tui.Justify.Left);
        p.Lines.Add(Spectre.Tui.TextLine.FromMarkup($"[dim]  {title} (empty)[/]"));
        return p;
    }

    private static IWidget ErrorPlaceholder(string title, string message)
    {
        var p = new Spectre.Tui.Paragraph().Alignment(Spectre.Tui.Justify.Left);
        p.Lines.Add(Spectre.Tui.TextLine.FromMarkup(
            $"[red]  {title} crashed:[/] [grey]{ChatMarkup.Escape(message)}[/]"));
        return p;
    }
}
