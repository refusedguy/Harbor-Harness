using System.Text;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
///     Accumulates a streaming Markdown answer and exposes only the prefix that
///     is safe to paint. A code fence, bold run or inline link whose closing
///     marker has not arrived yet is held back, so the renderer never shows a
///     half-open construct that flickers into place on the next delta (#2678
///     item 99, port of goose's streaming buffer).
/// </summary>
/// <remarks>
///     Render-thread only, like <see cref="StreamCoalescer"/> and
///     <see cref="StreamBlock"/>. The caller repaints <see cref="SafePrefix"/>
///     every frame; the cut is recomputed from the whole accumulated text, so
///     a construct that later closes simply widens the visible prefix.
/// </remarks>
public sealed class MarkdownStreamBuffer
{
    private readonly StringBuilder _text = new();

    /// <summary>Total characters accumulated, including the held-back tail.</summary>
    public int Length => _text.Length;

    /// <summary>Append one streamed delta. Empty deltas are ignored.</summary>
    public void Append(ReadOnlySpan<char> delta)
    {
        if (!delta.IsEmpty)
        {
            _text.Append(delta);
        }
    }

    /// <summary>
    ///     The longest prefix that contains no unclosed Markdown construct.
    ///     Everything from the first still-open marker onward is withheld.
    /// </summary>
    public string SafePrefix()
    {
        string text = _text.ToString();
        int cut = text.Length;
        cut = Math.Min(cut, UnclosedFenceStart(text));
        cut = Math.Min(cut, UnclosedRunStart(text, "**"));
        cut = Math.Min(cut, UnclosedRunStart(text, "__"));
        cut = Math.Min(cut, UnclosedLinkStart(text));
        return text[..cut];
    }

    /// <summary>Return all accumulated text, including the tail, and reset the buffer.</summary>
    public string Flush()
    {
        string text = _text.ToString();
        _text.Clear();
        return text;
    }

    /// <summary>Discard all accumulated text.</summary>
    public void Clear() => _text.Clear();

    private static int UnclosedFenceStart(string text)
    {
        int lineStart = 0;
        int openStart = -1;
        bool inFence = false;

        // The final pass (i == text.Length) grades the unterminated tail line.
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n')
            {
                continue;
            }

            int marker = FenceMarkerAt(text, lineStart, i, i == text.Length, out bool partial);
            if (marker >= 3)
            {
                if (inFence)
                {
                    inFence = false;
                    openStart = -1;
                }
                else
                {
                    inFence = true;
                    openStart = lineStart;
                }
            }
            else if (partial && !inFence)
            {
                // A short backtick/tilde run at the very tail may still grow
                // into a fence on the next delta; hold it back now.
                return lineStart;
            }

            lineStart = i + 1;
        }

        return inFence ? openStart : text.Length;
    }

    private static int FenceMarkerAt(
        string text,
        int start,
        int end,
        bool isTailLine,
        out bool partial)
    {
        partial = false;
        int i = start;
        while (i < end && (text[i] == ' ' || text[i] == '\t'))
        {
            i++;
        }

        if (i >= end || (text[i] != '`' && text[i] != '~'))
        {
            return 0;
        }

        char marker = text[i];
        int runEnd = i;
        while (runEnd < end && text[runEnd] == marker)
        {
            runEnd++;
        }

        int run = runEnd - i;
        if (run >= 3)
        {
            return run;
        }

        // A one- or two-character run on the final, unterminated line could
        // still become a fence when the next backtick/tilde arrives.
        if (isTailLine && runEnd == end)
        {
            partial = true;
        }

        return 0;
    }

    private static int UnclosedRunStart(string text, string marker)
    {
        int count = 0;
        int last = -1;
        for (int i = 0; i <= text.Length - marker.Length; i++)
        {
            if (i > 0 && text[i - 1] == '\\')
            {
                continue;
            }

            bool matched = true;
            for (int j = 0; j < marker.Length; j++)
            {
                if (text[i + j] != marker[j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            count++;
            last = i;
            i += marker.Length - 1;
        }

        return count % 2 == 1 ? last : text.Length;
    }

    private static int UnclosedLinkStart(string text)
    {
        int lastOpen = -1;
        for (int i = 0; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                lastOpen = i;
                continue;
            }

            if (c != ']' || text[i + 1] != '(' || lastOpen < 0)
            {
                continue;
            }

            int close = text.IndexOf(')', i + 2);
            if (close >= 0)
            {
                i = close;
                lastOpen = -1;
            }
            else
            {
                return lastOpen;
            }
        }

        return text.Length;
    }
}
