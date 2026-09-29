using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Spectre.Console;
using Spectre.Tui;

namespace Harbor.Tui.SpectreTui.View;

/// <summary>
///     ChatLine / role / body text → display <see cref="TextLine" /> rows.
///     No scroll, no layout, no Spectre Layout tree.
/// </summary>
/// <remarks>
///     Label, markdown rule and colour slot come from the shared
///     <see cref="ChatRolePresentation" /> table (#556) — this formatter no
///     longer contains any ChatRole→label or ChatRole→markdown rule of its own.
///     It adds only what SpectreTui can paint and the other three backends
///     cannot: the bold/italic speaker band. The band's hue is the slot's hue,
///     so the assistant is <c>White</c> here exactly as in the other backends
///     (it used to be <c>Aqua</c>, which is why the "same hue" comments the
///     three mappers carried were false).
/// </remarks>
internal static class ChatMessageFormatter
{
    public static void AppendRole(List<TextLine> target, ChatRole role, string content, bool markdown, int maxWidth = 0)
    {
        if (role is not ChatRole.ToolResult)
            target.Add(RoleHeader(role));

        foreach (var line in BodyLines(role, content, markdown, maxWidth))
            target.Add(line);

        target.Add(Gap());
    }

    public static TextLine RoleHeader(ChatRole role)
    {
        var line = new TextLine();
        line.Spans.Add(new TextSpan("─ ", new Style(Color.Grey)));
        line.Spans.Add(new TextSpan(ChatRolePresentation.Label(role), HeaderStyle(role)));
        line.Spans.Add(new TextSpan(" ─", new Style(Color.Grey)));
        return line;
    }

    /// <summary>
    ///     Speaker-band emphasis. The hue is the shared slot's hue — only the
    ///     decoration is decided here, because a bold/italic band is the one
    ///     axis the other three backends have no surface for. The default arm
    ///     throws rather than inventing a style: C# will not check enum
    ///     exhaustiveness here (a discard-free switch is rejected as CS8524, not
    ///     accepted as a total match), so a role nobody wired up has to be loud.
    /// </summary>
    private static Style HeaderStyle(ChatRole role)
    {
        var color = ToColor(role);
        return role switch
        {
            ChatRole.User => new Style(color, null, Decoration.Bold),
            ChatRole.Assistant => new Style(color, null, Decoration.Bold),
            ChatRole.Thinking => new Style(color, null, Decoration.Italic),
            ChatRole.Tool => new Style(color, null, Decoration.Bold),
            ChatRole.ToolResult => new Style(color),
            ChatRole.System => new Style(color),
            ChatRole.Error => new Style(color, null, Decoration.Bold),
            _ => throw ChatRolePresentation.Unhandled(role)
        };
    }

    public static TextLine Gap()
    {
        var line = new TextLine();
        line.Spans.Add(new TextSpan(" ", new Style(Color.Grey)));
        return line;
    }

    public static IEnumerable<TextLine> BodyLines(ChatRole role, string content, bool markdown, int maxWidth = 0)
    {
        var color = ToColor(role);
        string body = (content ?? string.Empty).Replace("\\n", "\n", StringComparison.Ordinal);
        string indent = "  ";

        string[] lines = body.Split('\n');
        bool blockMd = markdown && ChatRolePresentation.UsesMarkdown(role);

        int i = 0;
        while (i < lines.Length)
        {
            if (blockMd && ChatTableRenderer.IsTableStart(lines, i))
            {
                (var tableRows, int next) = ChatTableRenderer.Render(lines, i, color, maxWidth);
                foreach (var r in tableRows)
                    yield return r;
                // Advance past the consumed table lines; the while loop increments.
                i = next;
                continue;
            }

            var line = new TextLine();
            line.Spans.Add(new TextSpan(indent, new Style(color)));

            if (blockMd)
            {
                foreach (var span in ChatMarkdown.ToSpans(lines[i], color))
                    line.Spans.Add(span);
            }
            else
            {
                line.Spans.Add(new TextSpan(lines[i], new Style(color)));
            }

            yield return line;
            i++;
        }
    }

    /// <summary>
    ///     Body hue for a role. Internal (not private) so
    ///     <c>ChatRolePresentationTests</c> can assert this backend paints roles
    ///     that share a slot with the same hue, exactly as it asserts the other
    ///     three mappers.
    /// </summary>
    internal static Color ToColor(ChatRole role) => ToColor(ChatRolePresentation.Slot(role));

    /// <summary>SpectreTui's palette table, keyed on the shared slot.</summary>
    internal static Color ToColor(ChatColorSlot slot) => slot switch
    {
        ChatColorSlot.User => Color.Green,
        ChatColorSlot.Assistant => Color.White,
        ChatColorSlot.Muted => Color.Grey,
        ChatColorSlot.Tool => Color.Blue,
        ChatColorSlot.Danger => Color.Red,
        _ => throw ChatRolePresentation.UnhandledSlot(slot)
    };
}
