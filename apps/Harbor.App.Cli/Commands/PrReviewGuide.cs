using System.Globalization;

namespace Harbor.App.Cli.Commands;

/// <summary>One actionable stop in a pull-request review guide (#2686 J04).</summary>
internal sealed record PrGuideEntry(string File, string Lines, string Why, bool LookClosely);

/// <summary>
///     Pure projection of a unified diff into a review guide: one stop per changed
///     hunk, with a deterministic reason and a risk flag derived from the path.
///     No I/O and no process spawn. The CLI verb owns fetching the diff.
/// </summary>
internal static class PrReviewGuide
{
    /// <summary>Cap so one large pull request cannot flood stdout.</summary>
    internal const int MaxEntries = 50;

    internal static IReadOnlyList<PrGuideEntry> Build(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff)) return Array.Empty<PrGuideEntry>();

        var entries = new List<PrGuideEntry>();
        string file = string.Empty;
        string oldFile = string.Empty;
        using var reader = new StringReader(unifiedDiff);
        string? line;
        while ((line = reader.ReadLine()) is not null && entries.Count < MaxEntries)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                string candidate = line.Substring(4).Trim();
                oldFile = candidate.StartsWith("a/", StringComparison.Ordinal) ? candidate.Substring(2) : string.Empty;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string candidate = line.Substring(4).Trim();
                file = candidate.StartsWith("b/", StringComparison.Ordinal)
                    ? candidate.Substring(2)
                    : candidate == "/dev/null" ? oldFile : string.Empty;
                continue;
            }

            if (!line.StartsWith("@@ ", StringComparison.Ordinal) || file.Length == 0) continue;
            if (ParseHunk(line) is not { } hunk) continue;
            entries.Add(new PrGuideEntry(file, hunk.Lines, Why(file), hunk.LookClosely || IsRisky(file)));
        }

        return entries;
    }

    private readonly record struct Hunk(string Lines, bool LookClosely);

    private static Hunk? ParseHunk(string header)
    {
        (int oldStart, int oldCount) = ReadRange(header, '-');
        (int newStart, int newCount) = ReadRange(header, '+');

        if (newCount > 0) return new Hunk(Format(newStart, newCount), false);
        if (oldCount > 0) return new Hunk(Format(oldStart, oldCount), true);
        return null;
    }

    private static (int Start, int Count) ReadRange(string header, char marker)
    {
        int at = header.IndexOf(marker);
        if (at < 0) return (0, 0);
        int index = at + 1;
        int start = ReadNumber(header, ref index);
        int count = 1;
        if (index < header.Length && header[index] == ',')
        {
            index++;
            count = ReadNumber(header, ref index);
        }

        return (start, count);
    }

    private static int ReadNumber(string text, ref int index)
    {
        int value = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9')
        {
            value = value * 10 + (text[index] - '0');
            index++;
        }

        return value;
    }

    private static string Format(int start, int count) =>
        count <= 1
            ? start.ToString(CultureInfo.InvariantCulture)
            : string.Concat(
                start.ToString(CultureInfo.InvariantCulture),
                "-",
                (start + count - 1).ToString(CultureInfo.InvariantCulture));

    private static bool IsRisky(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Contains("Permission", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/security/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Auth", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PublicAPI", StringComparison.OrdinalIgnoreCase);
    }

    private static string Why(string path)
    {
        string p = path.Replace('\\', '/');
        if (p.Contains("Permission", StringComparison.OrdinalIgnoreCase))
            return "permission boundary: verify allow/deny precedence";
        if (p.Contains("/security/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Auth", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            return "security-sensitive: check the trust boundary";
        if (p.Contains("PublicAPI", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return "public or build contract: check the API freeze and AOT";
        if (p.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".Tests/", StringComparison.OrdinalIgnoreCase))
            return "test change: check the assertion still describes behavior";
        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return "documentation: check links and claims";
        if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return "data change: check the schema and defaults";
        return "behavior change: check callers and invariants";
    }
}
