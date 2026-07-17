using Harbor.Tui.Abstractions.State;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     ChatLine / role / body text → display <see cref="TextLine"/> rows.
///     No scroll, no layout, no Spectre Layout tree.
/// </summary>
internal static class ChatMessageFormatter
{
    public static void AppendRole(List<TextLine> target, ChatRole role, string content, bool markdown)
    {
        if (role is not ChatRole.ToolResult)
            target.Add(RoleHeader(role));

        foreach (var line in BodyLines(role, content, markdown))
            target.Add(line);

        target.Add(Gap());
    }

    public static TextLine RoleHeader(ChatRole role)
    {
        var (label, style) = role switch
        {
            ChatRole.User => ("you", new Style(Color.Green, null, Decoration.Bold)),
            ChatRole.Assistant => ("assistant", new Style(Color.Aqua, null, Decoration.Bold)),
            ChatRole.Thinking => ("thinking", new Style(Color.Grey, null, Decoration.Italic)),
            ChatRole.Tool => ("tool", new Style(Color.Blue, null, Decoration.Bold)),
            ChatRole.System => ("system", new Style(Color.Grey)),
            ChatRole.Error => ("error", new Style(Color.Red, null, Decoration.Bold)),
            ChatRole.ToolResult => ("result", new Style(Color.Grey)),
            _ => ("msg", new Style(Color.White))
        };

        var line = new TextLine();
        line.Spans.Add(new TextSpan("─ ", new Style(Color.Grey)));
        line.Spans.Add(new TextSpan(label, style));
        line.Spans.Add(new TextSpan(" ─", new Style(Color.Grey)));
        return line;
    }

    public static TextLine Gap()
    {
        var line = new TextLine();
        line.Spans.Add(new TextSpan(" ", new Style(Color.Grey)));
        return line;
    }

    public static IEnumerable<TextLine> BodyLines(ChatRole role, string content, bool markdown)
    {
        var color = ToColor(role);
        string body = (content ?? string.Empty).Replace("\\n", "\n", StringComparison.Ordinal);
        const string indent = "  ";

        foreach (var segment in body.Split('\n'))
        {
            var line = new TextLine();
            line.Spans.Add(new TextSpan(indent, new Style(color)));

            if (markdown && role is ChatRole.Assistant or ChatRole.User or ChatRole.System)
            {
                foreach (var span in ChatMarkdown.ToSpans(segment, color))
                    line.Spans.Add(span);
            }
            else
            {
                line.Spans.Add(new TextSpan(segment, new Style(color)));
            }

            yield return line;
        }
    }

    private static Color ToColor(ChatRole role) => role switch
    {
        ChatRole.User => Color.Green,
        ChatRole.Assistant => Color.White,
        ChatRole.Thinking => Color.Grey,
        ChatRole.Tool => Color.Blue,
        ChatRole.ToolResult => Color.Grey,
        ChatRole.System => Color.Grey,
        ChatRole.Error => Color.Red,
        _ => Color.White
    };
}