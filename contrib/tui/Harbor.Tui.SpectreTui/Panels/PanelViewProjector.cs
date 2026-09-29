using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Console;
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
///         snapshot. State transitions flow through <see cref="AppReducer" />.
///     </para>
/// </remarks>
internal sealed class PanelViewProjector
{

    public PanelViewProjector(ChatViewProjector chat, PanelRegistry registry, PanelServices? services = null)
    {
        Chat = chat;
        Registry = registry;
        Services = services ?? PanelServices.Empty;
    }

    /// <summary>Expose the underlying chat projector for the screen.</summary>
    public ChatViewProjector Chat
    {
        get;
    }

    /// <summary>The panel registry (for key routing / size queries).</summary>
    public PanelRegistry Registry
    {
        get;
    }

    /// <summary>
    ///     Typed panel dependencies (#470) handed to every
    ///     <see cref="PanelContext" /> this projector builds. Before #470 the
    ///     Build path passed no dependencies at all while the key path passed the
    ///     raw container, so a panel behaved differently on paint than on keypress.
    /// </summary>
    public PanelServices Services
    {
        get;
    }

/// <summary>
    ///     Build the full widget map: chat regions first, then panel regions by name.
    ///     Keys for panel regions follow the convention
    ///     <c>"{StackName}.{PanelId}"</c> with the tab strip at <c>"{StackName}.Tabs"</c>.
    /// </summary>
public IReadOnlyDictionary<string, IWidget> BuildWidgets(int historyHeight, UiState state)
{
    var chat = Chat.BuildWidgets(historyHeight);
    var map = new Dictionary<string, IWidget>(chat.Count + 8);
    foreach ((string k, var v) in chat)
        map[k] = v;

    var view = Registry.View(state);
    AddStack(map, "TopPanels", TuiPanelPlacement.Top, view, state);
    AddStack(map, "BottomPanels", TuiPanelPlacement.Bottom, view, state);
    AddStack(map, "LeftPanels", TuiPanelPlacement.Left, view, state);
    AddStack(map, "RightPanels", TuiPanelPlacement.Right, view, state);
    return map;
}

private void AddStack(
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
        var ctx = new PanelContext(state, 80, height, Services);
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
    var p = new Paragraph().Alignment(Justify.Left);
    var line = new TextLine();
    line.Spans.Add(new TextSpan(" ",
        new Style(Color.Grey)));
    for (int i = 0; i < panels.Count; i++)
    {
        var panel = panels[i];
        bool focused = state.Ui.FocusedPanelId == panel.Id;
        string label = $" {i + 1}:{panel.Title} ";
        var style = focused
            ? new Style(Color.Aqua, null, Decoration.Bold)
            : new Style(Color.Grey);
        line.Spans.Add(new TextSpan(label, style));
        line.Spans.Add(new TextSpan("|",
            new Style(Color.Grey)));
    }
    p.Lines.Add(line);
    return p;
}

    private static IWidget Placeholder(string title)
    {
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup($"[dim]  {title} (empty)[/]"));
        return p;
    }

    private static IWidget ErrorPlaceholder(string title, string message)
    {
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup(
            $"[red]  {title} crashed:[/] [grey]{ChatMarkup.Escape(message)}[/]"));
        return p;
    }
}
