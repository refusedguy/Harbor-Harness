namespace Harbor.Tui.CellForge.Panels;

/// <summary>
/// Render density for a permission prompt ([steal/opencode] #1170, epic
/// #1155): <see cref="Full"/> is the fullscreen dialog
/// (<c>routes/session/permission.tsx</c>), <see cref="Compact"/> the
/// one-line footer (<c>mini/footer.permission.tsx</c>). Same machine, two
/// budgets — the info object is identical, only line count differs.
/// </summary>
public enum PermissionDensity : byte
{
    /// <summary>Fullscreen prompt: title + up to <see cref="PermissionPresentation.MaxFullBodyLines"/> body lines + full hint.</summary>
    Full = 0,

    /// <summary>Footer prompt: title + one body line + short hint.</summary>
    Compact,
}

/// <summary>
/// Per-tool permission content ([steal/opencode] #1170 (c), epic #1155):
/// title + body + hint for one gate, built from final tool args only.
/// Pure BCL, no rendering — both densities share it; the host paints it.
/// </summary>
/// <param name="Title">One-line header (per-tool: edit shows the file, shell the command, lsp file:line:col).</param>
/// <param name="Body">Detail lines (capped by density; never built from streaming deltas — see (d)).</param>
/// <param name="Hint">Key hint row (full includes the <c>ctrl+f</c> fullscreen prompt).</param>
public sealed record PermissionPresentationInfo(string Title, IReadOnlyList<string> Body, string Hint);

/// <summary>
/// Per-tool permission presentation next to
/// <see cref="CellForgeDiffPreviewPanel"/> ([steal/opencode] #1170 (c)+(e),
/// epic #1155; sources <c>permissionPresentation()</c> in opencode's
/// <c>packages/tui/src/util/permission.ts</c>).
/// Builds what the user decides on: edit shows title+diff, shell the
/// command, glob/grep the pattern, lsp <c>file:line:col</c>, subagent the
/// delegation target — instead of today's generic tool+detail dump.
/// Streaming input is ignored ((d)): an empty detail renders an explicit
/// waiting line, never a half-parsed lie.
/// </summary>
public static class PermissionPresentation
{
    /// <summary>Body-line budget for <see cref="PermissionDensity.Full"/> (fullscreen).</summary>
    public const int MaxFullBodyLines = 6;

    /// <summary>Body-line budget for <see cref="PermissionDensity.Compact"/> (footer).</summary>
    public const int MaxCompactBodyLines = 1;

    /// <summary>Fullscreen hint row — includes the <c>ctrl+f</c> fullscreen prompt ((e)).</summary>
    public const string FullHint = "[y] approve   [a] always   [n] deny + message   [ctrl+f] fullscreen";

    /// <summary>Footer hint row — keys only, fits one line.</summary>
    public const string CompactHint = "[y/a/n]";

    /// <summary>
    /// Builds the presentation for one permission gate.
    /// <paramref name="detail"/> must already be final args (the router's
    /// <c>SanitizeGateDetail</c> blanks streaming deltas); a blank detail
    /// still yields a well-formed object with a waiting line.
    /// </summary>
    public static PermissionPresentationInfo Build(
        string? toolName,
        string? detail,
        string? diffText = null,
        string? filePath = null,
        PermissionDensity density = PermissionDensity.Full)
    {
        string tool = (toolName ?? string.Empty).Trim();
        string toolKey = tool.ToLowerInvariant();
        if (tool.Length == 0)
        {
            tool = "?";
        }

        int budget = density == PermissionDensity.Compact ? MaxCompactBodyLines : MaxFullBodyLines;
        string hint = density == PermissionDensity.Compact ? CompactHint : FullHint;

        string title;
        List<string> body = new(budget + 1);

        switch (toolKey)
        {
            case "edit":
            case "write":
            case "patch":
                title = "edit " + DisplayPath(filePath, detail);
                if (!string.IsNullOrWhiteSpace(diffText))
                {
                    body.Add("diff " + DisplayPath(filePath, detail) + ": " + CountLines(diffText) + " lines");
                }

                AddDetailLines(body, detail, budget);
                break;

            case "bash":
            case "shell":
            case "sh":
                title = "run shell: " + Preview(detail, 48);
                AddDetailLines(body, detail, budget);
                break;

            case "glob":
                title = "glob " + Preview(detail, 48);
                AddDetailLines(body, detail, budget);
                break;

            case "grep":
            case "ripgrep":
            case "rg":
            case "search":
                title = "search " + Preview(detail, 48);
                AddDetailLines(body, detail, budget);
                break;

            case "lsp":
                title = "inspect " + ParseLocation(detail, Preview(detail, 48));
                AddDetailLines(body, detail, budget);
                break;

            case "task":
            case "subagent":
                title = "delegate subagent: " + Preview(detail, 48);
                AddDetailLines(body, detail, budget);
                break;

            case "external_directory":
                title = "access external directory";
                AddDetailLines(body, detail, budget);
                break;

            case "doom_loop":
                title = "review loop guard";
                AddDetailLines(body, detail, budget);
                break;

            default:
                string preview = Preview(detail, 48);
                title = preview.Length == 0 ? tool : tool + " " + preview;
                AddDetailLines(body, detail, budget);
                break;
        }

        if (body.Count == 0)
        {
            body.Add("(streaming input ignored — waiting for final args)");
        }

        while (body.Count > budget)
        {
            body.RemoveAt(body.Count - 1);
        }

        return new PermissionPresentationInfo(title, body.ToArray(), hint);
    }

    /// <summary>
    /// Caps an always-stage pattern list for display (widest-first, at most
    /// <paramref name="max"/> rows). Takes the router's
    /// <c>PermissionAlwaysLines</c> output — no pattern logic lives here.
    /// </summary>
    public static IReadOnlyList<string> FormatAlwaysLines(IReadOnlyList<string>? lines, int max = 5)
    {
        if (lines is null || lines.Count == 0 || max <= 0)
        {
            return Array.Empty<string>();
        }

        int take = Math.Min(max, lines.Count);
        var clipped = new List<string>(take);
        for (int i = 0; i < take; i++)
        {
            clipped.Add(lines[i] ?? string.Empty);
        }

        return clipped;
    }

    private static void AddDetailLines(List<string> body, string? detail, int budget)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        string[] lines = detail.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length && body.Count < budget; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            body.Add(line.Length <= 96 ? line : line[..96]);
        }
    }

    private static string DisplayPath(string? filePath, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return filePath.Trim();
        }

        string preview = Preview(detail, 48);
        if (preview.Length == 0)
        {
            return "?";
        }

        int cut = preview.IndexOfAny([' ', '\t', '\n']);
        return cut < 0 ? preview : preview[..cut];
    }

    private static string Preview(string? detail, int max)
    {
        if (string.IsNullOrWhiteSpace(detail) || max <= 0)
        {
            return string.Empty;
        }

        string first = detail.Trim();
        int nl = first.IndexOf('\n');
        if (nl >= 0)
        {
            first = first[..nl].Trim();
        }

        return first.Length <= max ? first : first[..max];
    }

    private static int CountLines(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count + 1;
    }

    /// <summary>
    /// Extracts the first <c>file:line:col</c> (or <c>file:line</c>) token
    /// from <paramref name="detail"/>; falls back to
    /// <paramref name="fallback"/> when nothing parses.
    /// </summary>
    private static string ParseLocation(string? detail, string fallback)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return fallback.Length == 0 ? "?" : fallback;
        }

        string[] tokens = detail.Split([' ', '\t', '\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string raw in tokens)
        {
            string token = raw.Trim().Trim('"', '\'', '`');
            int firstColon = token.IndexOf(':');
            if (firstColon <= 0 || firstColon == token.Length - 1)
            {
                continue;
            }

            string rest = token[(firstColon + 1)..];
            int secondColon = rest.IndexOf(':');
            string linePart = secondColon < 0 ? rest : rest[..secondColon];
            string colPart = secondColon < 0 ? string.Empty : rest[(secondColon + 1)..];

            if (!IsDigits(linePart))
            {
                continue;
            }

            if (colPart.Length > 0 && !IsDigits(colPart))
            {
                continue;
            }

            return token;
        }

        return fallback.Length == 0 ? "?" : fallback;
    }

    private static bool IsDigits(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9')
            {
                return false;
            }
        }

        return true;
    }
}
