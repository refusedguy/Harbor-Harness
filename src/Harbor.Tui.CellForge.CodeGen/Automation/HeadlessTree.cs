using System.Globalization;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen.Automation;

/// <summary>
///     Builds the automation snapshot from the real parsed DSL tree (issue
///     #2438). The nodes, their declared identity and their bounds all come
///     from the same <see cref="SpecArena"/> the generated <c>BuildRoot</c>
///     consumes, so a headless check cannot drift onto a test double.
/// </summary>
public static class HeadlessTree
{
    /// <summary>
    ///     Builds one tree from an arena. Numeric ids follow arena creation
    ///     order, which is the same dense order the retained element tree and
    ///     the generated BuildRoot use. A file with several roots gets a
    ///     synthetic root; an empty arena yields null.
    /// </summary>
    public static AutomationNode? Build(SpecArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        var children = new List<AutomationNode>();
        int next = 0;
        foreach (var root in arena.Roots)
            children.Add(BuildNode(root, ref next));

        return children.Count switch
        {
            0 => null,
            1 => children[0],
            _ => new AutomationNode(0, "Root", null, default, children)
        };
    }

    /// <summary>Pre-order traversal used by locator lookups.</summary>
    public static IReadOnlyList<AutomationNode> Flatten(AutomationNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var nodes = new List<AutomationNode>();
        Append(root, nodes);
        return nodes;
    }

    private static void Append(AutomationNode node, List<AutomationNode> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            Append(child, nodes);
    }

    private static AutomationNode BuildNode(SpecNode node, ref int next)
    {
        int id = ++next;
        var children = new List<AutomationNode>(node.Children.Count);
        foreach (var child in node.Children)
            children.Add(BuildNode(child, ref next));

        return new AutomationNode(id, node.Kind.ToString(), node.Id, ReadBounds(node), children);
    }

    private static AutomationBounds ReadBounds(SpecNode node) =>
        new(ReadInt(node, "x"), ReadInt(node, "y"), ReadInt(node, "width"), ReadInt(node, "height"));

    private static int ReadInt(SpecNode node, string name)
    {
        foreach (var operation in node.Operations)
        {
            if (string.Equals(operation.Name, name, StringComparison.OrdinalIgnoreCase)
                && operation.Arguments.Count > 0
                && int.TryParse(operation.Arguments[0].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return 0;
    }
}
