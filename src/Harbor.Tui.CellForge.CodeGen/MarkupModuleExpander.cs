using System;
using System.Collections.Generic;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     DEV-ONLY. Turns a resolved module graph into one arena the reload
///     interpreter can apply as a mutation batch. Every imported module is
///     cloned at its <c>.use</c> site: the source arena stays a tree of
///     single-use nodes, and a shared module can be instantiated more than
///     once without two owners pointing at the same node.
/// </summary>
/// <remarks>
///     The walk keeps a visiting set keyed by module name, so a cyclic graph
///     expands to a finite tree instead of recursing forever. The generator
///     does not use this type: emitted code calls each module's own builder.
/// </remarks>
internal static class MarkupModuleExpander
{
    /// <summary>Expands <paramref name="rootPath"/> and everything it imports.</summary>
    internal static SpecArena Expand(ModuleGraphResult graph, string rootPath)
    {
        var byPath = new Dictionary<string, ResolvedModule>(StringComparer.Ordinal);
        foreach (var module in graph.Modules)
            byPath[module.Path] = module;

        var result = new SpecArena();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        AppendModule(MarkupPaths.Normalize(rootPath), result, graph, byPath, visiting);
        return result;
    }

    private static void AppendModule(
        string path,
        SpecArena result,
        ModuleGraphResult graph,
        IReadOnlyDictionary<string, ResolvedModule> byPath,
        HashSet<string> visiting)
    {
        if (!visiting.Add(path))
            return;
        if (!byPath.TryGetValue(path, out var module))
        {
            visiting.Remove(path);
            return;
        }

        foreach (var root in module.Arena.Roots)
            result.AddRoot(Clone(root, result, path, module, graph, byPath, visiting));

        visiting.Remove(path);
    }

    private static SpecNode Clone(
        SpecNode source,
        SpecArena result,
        string modulePath,
        ResolvedModule module,
        ModuleGraphResult graph,
        IReadOnlyDictionary<string, ResolvedModule> byPath,
        HashSet<string> visiting)
    {
        var clone = result.CreateNode(source.Kind, source.SourceLine, source.SourceColumn);
        if (source.Id is not null)
            clone.SetId(source.Id);

        foreach (var operation in source.Operations)
            clone.AddOperation(new SpecOp(operation.Name, operation.Arguments, operation.SourceLine, operation.SourceColumn));

        foreach (var child in source.Children)
            clone.AddChild(Clone(child, result, modulePath, module, graph, byPath, visiting));

        foreach (var operation in source.Operations)
        {
            if (!operation.Name.Equals("use", StringComparison.OrdinalIgnoreCase)
                || operation.Arguments.Count == 0)
                continue;

            var target = MarkupModuleResolver.ResolveUse(
                modulePath, operation.Arguments[0].Text, module, graph.Identifiers);
            if (target is null)
                continue;

            ExpandUseInto(clone, target, result, graph, byPath, visiting);
        }

        return clone;
    }

    private static void ExpandUseInto(
        SpecNode parent,
        string path,
        SpecArena result,
        ModuleGraphResult graph,
        IReadOnlyDictionary<string, ResolvedModule> byPath,
        HashSet<string> visiting)
    {
        if (!visiting.Add(path))
            return;
        if (!byPath.TryGetValue(path, out var module))
        {
            visiting.Remove(path);
            return;
        }

        foreach (var root in module.Arena.Roots)
            parent.AddChild(Clone(root, result, path, module, graph, byPath, visiting));

        visiting.Remove(path);
    }
}
