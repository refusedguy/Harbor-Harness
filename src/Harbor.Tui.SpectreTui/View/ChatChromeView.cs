using Harbor.Tui.Abstractions.State;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Header / stream bar / input / footer widgets. No history, no scroll math.
/// </summary>
internal sealed class ChatChromeView
{
    public string Status { get; set; } = "idle";
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public bool IsReadingInput { get; set; }
    public bool IsStreaming { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public decimal Cost { get; set; }
    public string InputText { get; set; } = string.Empty;
    public FocusMode Focus { get; set; } = FocusMode.Input;

    /// <summary>Optional override from ChatScreen (key help). Empty → default short footer.</summary>
    public string FooterText { get; set; } = string.Empty;

    public IWidget BuildHeader()
    {
        string route = string.IsNullOrEmpty(Provider) ? "Harbor" : $"{Provider}/{Model}";
        string agent = string.IsNullOrEmpty(Agent) ? "" : $" · {Agent}";
        string usage = $"{TokensIn}↑ {TokensOut}↓ · ${Cost:F4}";
        string left = ChatMarkup.Truncate($"⚓ {route}{agent}", 48);
        string pill = ChatMarkup.StatusPill(Status);

        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup(
            $"[bold cyan]{ChatMarkup.Escape(left)}[/]  [grey]{ChatMarkup.Escape(usage)}[/]  {pill}"));
        return p;
    }

    public IWidget BuildStreamBar(string streamBuffer, string thinkBuffer)
    {
        string hint = !string.IsNullOrEmpty(thinkBuffer) ? "thinking…" : "generating…";
        int n = streamBuffer?.Length ?? 0;
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup(
            $"[cyan]▌[/] [cyan]{ChatMarkup.Escape(hint)}[/]  [dim]{n} chars[/]"));
        return p;
    }

    public IWidget BuildInput()
    {
        bool focused = Focus == FocusMode.Input;
        var border = focused ? Color.Green : Color.Grey;
        string mark = focused ? "[bold green]›[/]" : "[grey]›[/]";

        string body;
        if (string.IsNullOrEmpty(InputText))
        {
            body = IsReadingInput
                ? $"{mark} [dim]message or /command · enter send · esc quit[/]"
                : $"{mark} [dim]agent running · esc abort · ↑↓ scroll[/]";
        }
        else
        {
            string caret = focused && IsReadingInput ? "[invert] [/]" : "";
            body = $"{mark} {ChatMarkup.Escape(InputText)}{caret}";
        }

        return new BoxWidget()
            .Border(Border.Rounded)
            .Style(new Style(border))
            .Inner(new Paragraph(TextLine.FromMarkup(body)).Alignment(Justify.Left));
    }

    public IWidget BuildFooter(int maxScroll, int effectiveScroll)
    {
        string text = string.IsNullOrEmpty(FooterText)
            ? DefaultFooter(maxScroll, effectiveScroll)
            : FooterText;
        return Paragraph.FromMarkup(string.IsNullOrEmpty(text) ? " " : text).Centered();
    }

    private string DefaultFooter(int maxScroll, int effectiveScroll)
    {
        string scroll = maxScroll > 0 ? $"{effectiveScroll * 100 / maxScroll}%" : "live";
        string mode = Focus == FocusMode.Input ? "[green]IN[/]" : "[aqua]CHAT[/]";
        return $"[dim]esc[/] quit   [dim]F2[/] {mode}   [dim]↑↓[/] scroll   [dim]PgUp/Dn[/] page   [dim][/]{scroll}";
    }
}
