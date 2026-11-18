using System.Text;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Renders the parsed IR as a deterministic text tree for snapshot tests.
/// </summary>
/// <remarks>
///     <para>
///         The dump is a function of the arena alone. It never starts a
///         renderer, reads the clock or touches the file system, so the same
///         input always produces the same bytes on every host. Line endings
///         are always LF and indentation is always two spaces per level.
///     </para>
///     <para>
///         Each node line carries its kind, 1-based source span and claim
///         state. Each operation line carries its name, arguments and span.
///         Children follow their parent's operations, one level deeper, in
///         source order. Strings are quoted and escaped; other literals are
///         written as their canonical text.
///     </para>
/// </remarks>
public static class SpecTreeDumper
{
    /// <summary>Renders all roots of an arena as a text tree.</summary>
    /// <param name="arena">Arena to render.</param>
    /// <returns>The dump, ending with a newline when the arena has nodes.</returns>
    /// <exception cref="System.ArgumentNullException">The arena is null.</exception>
    public static string DebugTree(SpecArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        var output = new StringBuilder();
        foreach (var root in arena.Roots)
            AppendNode(output, root, 0);
        return output.ToString();
    }

    /// <summary>Renders one node and its descendants as a text tree.</summary>
    /// <param name="node">Root of the subtree to render.</param>
    /// <returns>The dump, ending with a newline.</returns>
    /// <exception cref="System.ArgumentNullException">The node is null.</exception>
    public static string DebugTree(SpecNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var output = new StringBuilder();
        AppendNode(output, node, 0);
        return output.ToString();
    }

    private static void AppendNode(StringBuilder output, SpecNode node, int depth)
    {
        AppendIndent(output, depth);
        output.Append("node ").Append(node.Kind)
            .Append('@').Append(node.SourceLine).Append(':').Append(node.SourceColumn)
            .Append(" claimed=").Append(node.IsClaimed ? "true" : "false")
            .Append('\n');

        foreach (var op in node.Operations)
        {
            AppendIndent(output, depth + 1);
            output.Append("op ").Append(op.Name).Append('(');
            for (var index = 0; index < op.Arguments.Count; index++)
            {
                if (index > 0)
                    output.Append(", ");
                AppendValue(output, op.Arguments[index]);
            }

            output.Append(")@").Append(op.SourceLine).Append(':').Append(op.SourceColumn)
                .Append('\n');
        }

        foreach (var child in node.Children)
            AppendNode(output, child, depth + 1);
    }

    private static void AppendValue(StringBuilder output, SpecValue value)
    {
        if (value.Kind == SpecValueKind.String)
            output.Append('"').Append(Escape(value.Text)).Append('"');
        else
            output.Append(value.Text);
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

    private static void AppendIndent(StringBuilder output, int depth) =>
        output.Append(' ', depth * 2);
}
