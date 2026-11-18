using System;
using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Lowers the #2489 <see cref="SpecArena"/> IR into a mutation batch.
///     Numeric ids come from arena node order, so a spec always produces the
///     same batch. Structural commands (create, append, root) always appear;
///     a "text" operation becomes setText and the remaining operations become
///     one structural setStyle per node.
/// </summary>
internal static class SpecArenaMutationLowering
{
    internal static MutationBatch Lower(SpecArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        var commands = new List<MutationCommand>();
        var ids = new Dictionary<SpecNode, int>();

        for (int i = 0; i < arena.Nodes.Count; i++)
        {
            var node = arena.Nodes[i];
            int id = i + 1;
            ids[node] = id;
            commands.Add(MutationCommand.CreateElement(id, node.Kind));

            var properties = new List<(string Name, string Value)>();
            foreach (var operation in node.Operations)
            {
                if (operation.Name.Equals("new", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (operation.Name.Equals("text", StringComparison.OrdinalIgnoreCase)
                    && operation.Arguments.Count > 0
                    && operation.Arguments[0].Kind == SpecValueKind.String)
                {
                    commands.Add(MutationCommand.SetText(id, operation.Arguments[0].Text));
                    continue;
                }

                if (operation.Arguments.Count > 0)
                    properties.Add((operation.Name, operation.Arguments[0].Text));
            }

            if (properties.Count > 0)
                commands.Add(MutationCommand.SetStyle(id, MutationStyle.Create(properties.ToArray())));
        }

        foreach (var node in arena.Nodes)
        {
            foreach (var child in node.Children)
                commands.Add(MutationCommand.AppendChild(ids[node], ids[child]));
        }

        foreach (var root in arena.Roots)
            commands.Add(MutationCommand.SetRoot(ids[root]));

        return new MutationBatch(commands);
    }
}
