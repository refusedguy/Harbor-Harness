using System.Globalization;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Parses unified-diff text into hunks. Pure function — no I/O, no logging.
///     Extracted verbatim from <c>PatchTool</c> (#95, one seam: hunk parsing).
/// </summary>
public static class HunkParser
{
    /// <summary>
    ///     Parse unified-diff <paramref name="patch" /> into hunks, skipping any
    ///     leading diff-header lines until the first <c>@@ ... @@</c> header.
    ///     Result railway (#201 C3): LLM-generated hunk headers travel as
    ///     <c>Failure("Malformed hunk header/range ...")</c> instead of throwing.
    /// </summary>
    public static Result<List<Hunk>> TryParse(string patch)
    {
        var hunks = new List<Hunk>();
        string[] lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        int i = 0;
        // Skip past the diff header lines (origin and destination file markers)
        // until we reach the first hunk header line beginning with double-at sign.
        while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            i++;
        }

        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Parse "@@ -oldStart,oldCount +newStart,newCount @@"
            Result<HunkHeader> headerResult = TryParseHunkHeader(lines[i]);
            if (headerResult.IsFailure)
                return Result.Failure<List<Hunk>>(headerResult.Error);
            var header = headerResult.Value;
            i++;

            var hunkLines = new List<HunkLine>(header.OldCount + header.NewCount);
            int seenOld = 0, seenNew = 0;

            while (i < lines.Length
                   && (seenOld < header.OldCount || seenNew < header.NewCount)
                   && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    // Empty line in the patch is treated as a blank context line.
                    hunkLines.Add(new HunkLine(HunkLineType.Context, string.Empty));
                    seenOld++;
                    seenNew++;
                    i++;
                    continue;
                }

                char type = line[0];
                string text = line[1..];
                switch (type)
                {
                    case ' ':
                        hunkLines.Add(new HunkLine(HunkLineType.Context, text));
                        seenOld++;
                        seenNew++;
                        break;
                    case '-':
                        hunkLines.Add(new HunkLine(HunkLineType.Deletion, text));
                        seenOld++;
                        break;
                    case '+':
                        hunkLines.Add(new HunkLine(HunkLineType.Addition, text));
                        seenNew++;
                        break;
                    case '\\':
                        // "\ No newline at end of file" — ignore, we handle newlines ourselves.
                        break;
                    default:
                        // Unknown line — stop hunk.
                        i = lines.Length;
                        break;
                }
                i++;
            }

            hunks.Add(new Hunk(header.OldStart, header.OldCount, header.NewStart, header.NewCount, hunkLines));
        }

        return Result.Success(hunks);
    }

    /// <summary>
    ///     Legacy throwing entry point: delegates to <see cref="TryParse" /> so the
    ///     railway stays single-sourced; kept for existing callers/tests.
    /// </summary>
    /// <exception cref="FormatException">A hunk header is malformed.</exception>
    public static List<Hunk> Parse(string patch) =>
        TryParse(patch).Match(static h => h, err => throw new FormatException(err));

    private static Result<HunkHeader> TryParseHunkHeader(string line)
    {
        // @@ -10,7 +10,8 @@ context
        int atAt = line.IndexOf("@@", 2, StringComparison.Ordinal);
        string body = atAt > 0 ? line[3..atAt].Trim() : line[3..].Trim();

        // "-10,7 +10,8"
        int plusIdx = body.IndexOf('+');
        if (plusIdx <= 0)
            return Result.Failure<HunkHeader>($"Malformed hunk header: {line}");

        string oldPart = body[..plusIdx].Trim();
        string newPart = body[plusIdx..].Trim();

        Result<(int start, int count)> oldResult = TryParseRange(oldPart, line);
        if (oldResult.IsFailure)
            return Result.Failure<HunkHeader>(oldResult.Error);
        Result<(int start, int count)> newResult = TryParseRange(newPart, line);
        if (newResult.IsFailure)
            return Result.Failure<HunkHeader>(newResult.Error);

        (int oldStart, int oldCount) = oldResult.Value;
        (int newStart, int newCount) = newResult.Value;

        return Result.Success(new HunkHeader(oldStart, oldCount, newStart, newCount));
    }

    private static Result<(int start, int count)> TryParseRange(string s, string headerLine)
    {
        // s like "-10,7" or "-10"
        if (s.StartsWith('-')) s = s[1..];
        else if (s.StartsWith('+')) s = s[1..];

        int comma = s.IndexOf(',');
        if (comma < 0)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int only))
                return Result.Success((only, 1));
            return Result.Failure<(int start, int count)>($"Malformed hunk range '{s}' in header: {headerLine}");
        }

        if (int.TryParse(s.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start)
            && int.TryParse(s.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            return Result.Success((start, count));
        return Result.Failure<(int start, int count)>($"Malformed hunk range '{s}' in header: {headerLine}");
    }

    private readonly record struct HunkHeader(int OldStart, int OldCount, int NewStart, int NewCount);
}

/// <summary>Kind of a single unified-diff hunk line.</summary>
public enum HunkLineType { Context, Deletion, Addition }

/// <summary>Single parsed unified-diff hunk line.</summary>
/// <param name="Type">Whether the line is context, a deletion, or an addition.</param>
/// <param name="Text">Line content without the leading <c>' '/'-'/'+'</c> marker.</param>
public readonly record struct HunkLine(HunkLineType Type, string Text);

/// <summary>Parsed unified-diff hunk.</summary>
/// <param name="OldStart">1-based start line in the original file.</param>
/// <param name="OldCount">Lines consumed from the original file.</param>
/// <param name="NewStart">1-based start line in the patched file.</param>
/// <param name="NewCount">Lines produced into the patched file.</param>
/// <param name="Lines">Hunk body lines in order.</param>
public sealed record Hunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<HunkLine> Lines);
