using Harbor.Ui.Framework.Rendering.Markdown;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Reads rendered markdown back as styled lines.
/// </summary>
/// <remarks>
///     <para>
///         A markdown line is a list of styled spans, and a test that asserts on
///         the text has to flatten it first. The streaming suites each wrote the
///         same flattening.
///     </para>
/// </remarks>
internal static class MarkdownFixtures
{
    internal static List<string> Flatten(IReadOnlyList<MdLine> lines)
    {
        var flat = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in l.Spans)
            {
                sb.Append('[').Append(s.Style).Append(':').Append(s.Text).Append(']');
            }

            flat.Add(sb.ToString());
        }

        return flat;
    }
}
