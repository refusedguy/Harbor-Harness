using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Harbor.Application.Sessions;

/// <summary>
///     Pulls a JSON object out of a free-form model response without regex and
///     without repairing truncated output (goose-features #60, port of
///     <c>goose-context-management/src/structured.rs</c>'s <c>json_candidates</c>).
/// </summary>
/// <remarks>
/// <para>
///     Structured summaries and other schema-bearing answers arrive wrapped in
///     prose, a fenced <c>```json</c> block, or an <c>&lt;analysis&gt;</c> scratchpad
///     terminated by <c>&lt;/analysis&gt;</c>. Every plausible slice is tried, in
///     the same order as goose: after each terminator (last first), then fenced
///     blocks (last first) and a leading object of that tail, and finally a
///     leading object of the whole text. The first slice that is a syntactically
///     valid JSON object wins.
/// </para>
/// <para>
///     A tail is accepted only when it still contains every later terminator
///     occurrence. That proves those occurrences were quoted inside its JSON,
///     so a fenced example in the scratchpad can never shadow the real answer.
///     Braces are counted by hand with string/escape awareness (a string value
///     may legally contain braces or a fence), and an unterminated object is
///     never padded with closing braces: repairing it would silently drop the
///     late sections that a raw-text fallback would have preserved.
/// </para>
/// <para>
///     One-shot parsing on a non-hot path. Pure BCL, no reflection, AOT-safe.
/// </para>
/// </remarks>
public static class ModelJsonExtractor
{
    private const string Terminator = "</analysis>";
    private const string Fence = "```json";

    /// <summary>
    ///     Try to extract the first well-formed JSON object from <paramref name="text" />.
    /// </summary>
    /// <param name="text">Raw model response. May be null or empty.</param>
    /// <param name="json">
    ///     The exact object slice on success (no surrounding prose or fence);
    ///     null when no candidate is a valid JSON object.
    /// </param>
    /// <returns>True when <paramref name="json" /> holds a complete JSON object.</returns>
    public static bool TryExtract(string? text, [NotNullWhen(true)] out string? json)
    {
        json = null;
        if (string.IsNullOrEmpty(text)) return false;

        // Cut points: the start of the text and just past each terminator.
        // Walked in reverse so the last terminator (nearest the answer) is
        // considered first, exactly as in goose's json_candidates.
        List<int> cuts = [0];
        int searchFrom = 0;
        while (true)
        {
            int at = text.IndexOf(Terminator, searchFrom, StringComparison.Ordinal);
            if (at < 0) break;
            cuts.Add(at + Terminator.Length);
            searchFrom = at + Terminator.Length;
        }

        for (int c = cuts.Count - 1; c >= 0; c--)
        {
            int cut = cuts[c];
            string tail = text[cut..];
            int laterTerminators = CountOccurrences(tail, Terminator);

            // Fences are tried last-first: a JSON string value can quote a
            // fenced example, and that embedded fence must not shadow the real one.
            List<int> fences = FindAll(tail, Fence);
            for (int f = fences.Count - 1; f >= 0; f--)
            {
                string? candidate = LeadingObject(tail[(fences[f] + Fence.Length)..]);
                if (candidate is not null
                    && CountOccurrences(candidate, Terminator) == laterTerminators
                    && IsValidObject(candidate))
                {
                    json = candidate;
                    return true;
                }
            }

            string? leading = LeadingObject(tail);
            if (leading is not null
                && CountOccurrences(leading, Terminator) == laterTerminators
                && IsValidObject(leading))
            {
                json = leading;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The brace-balanced object starting at the first non-whitespace
    ///     character, or null when that character is not '{' or the object is
    ///     unterminated. Braces and quotes inside string values are ignored.
    /// </summary>
    private static string? LeadingObject(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        if (span.IsEmpty || span[0] != '{') return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < span.Length; i++)
        {
            char ch = span[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}' && --depth == 0)
            {
                return span[..(i + 1)].ToString();
            }
        }

        return null; // unterminated: never repair by inventing closing braces
    }

    private static int CountOccurrences(string text, string marker)
    {
        int count = 0;
        int from = 0;
        while (true)
        {
            int at = text.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) return count;
            count++;
            from = at + marker.Length;
        }
    }

    private static List<int> FindAll(string text, string marker)
    {
        var indices = new List<int>();
        int from = 0;
        while (true)
        {
            int at = text.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) return indices;
            indices.Add(at);
            from = at + marker.Length;
        }
    }

    private static bool IsValidObject(string candidate)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
