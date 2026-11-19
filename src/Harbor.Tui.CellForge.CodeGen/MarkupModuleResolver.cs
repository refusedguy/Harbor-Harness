using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>One markup file handed to the module resolver.</summary>
/// <param name="Path">Project-relative or absolute path of the file.</param>
/// <param name="Text">Whole file contents.</param>
internal sealed record MarkupModuleSource(string Path, string Text);

/// <summary>One graph finding, already tied to the file that caused it.</summary>
/// <param name="Id">HZ diagnostic id.</param>
/// <param name="Path">Normalized path of the importing file.</param>
/// <param name="Text">Source of the importing file, for the diagnostic location.</param>
/// <param name="Diagnostic">Position and message.</param>
internal sealed record GraphFinding(string Id, string Path, string Text, MarkupDiagnostic Diagnostic);

/// <summary>A parsed module with its imports resolved to stable names.</summary>
/// <param name="Path">Normalized module name.</param>
/// <param name="Identifier">Generated C# identifier for this module.</param>
/// <param name="Text">Source text, used for dev checksums and diagnostics.</param>
/// <param name="Parse">The one parser's result for this module.</param>
/// <param name="ImportResolutions">Raw import path to resolved module name.</param>
/// <param name="HasUses">Whether this module instantiates an imported module.</param>
internal sealed record ResolvedModule(
    string Path,
    string Identifier,
    string Text,
    MarkupParseResult Parse,
    IReadOnlyDictionary<string, string> ImportResolutions,
    bool HasUses)
{
    /// <summary>The module's own arena, parsed from its own text.</summary>
    public SpecArena Arena => Parse.Arena;
}

/// <summary>Deterministic result of resolving a set of markup files into a graph.</summary>
/// <param name="Arena">Arena owning the module graph.</param>
/// <param name="Modules">Modules in stable name order.</param>
/// <param name="Findings">Cycle and missing-import findings.</param>
/// <param name="SequenceCounts">Total cell-diff sequences each module emits.</param>
/// <param name="Identifiers">Module name to generated C# identifier.</param>
/// <param name="BlockedPaths">Modules that take part in a cycle and emit nothing.</param>
internal sealed record ModuleGraphResult(
    SpecArena Arena,
    IReadOnlyList<ResolvedModule> Modules,
    IReadOnlyList<GraphFinding> Findings,
    IReadOnlyDictionary<string, int> SequenceCounts,
    IReadOnlyDictionary<string, string> Identifiers,
    IReadOnlySet<string> BlockedPaths);

/// <summary>
///     Builds the <c>.harbor-ui</c> module graph from the files the build or
///     the dev watcher already has in memory. It never touches the file system:
///     the generator supplies <c>AdditionalText</c>, the watcher supplies the
///     texts it read itself. Paths are resolved deterministically from the
///     importing file's directory, so the same set of files gives the same
///     graph on every host.
/// </summary>
internal static class MarkupModuleResolver
{
    /// <summary>Resolves a set of sources into a module graph.</summary>
    /// <param name="sources">All markup files visible to this build or watcher.</param>
    internal static ModuleGraphResult Resolve(IReadOnlyList<MarkupModuleSource> sources)
    {
        var findings = new List<GraphFinding>();
        var parsed = new Dictionary<string, ParsedSource>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var path = MarkupPaths.Normalize(source.Path);
            var parse = MarkupParser.Parse(source.Text);
            if (!parse.IsValid)
                continue; // HZ0001 is reported by the declaration phase.

            parsed[path] = new ParsedSource(source.Text, parse, MakeIdentifier(path));
        }

        var names = parsed.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in names)
            identifiers[path] = parsed[path].Identifier;

        // Resolve each raw import against the set of known modules. A missing
        // target is a finding, not an exception: one broken import must not
        // take the generator or the dev watcher down.
        var resolvedImports = new Dictionary<string, List<SpecImport>>(StringComparer.Ordinal);
        var resolutions = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var path in names)
        {
            var item = parsed[path];
            var byRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            var imports = new List<SpecImport>();
            foreach (var import in item.Parse.Imports)
            {
                var resolved = MarkupPaths.ResolveKnown(path, import.Path, identifiers);
                if (resolved is null)
                {
                    findings.Add(new GraphFinding(
                        MarkupDiagnostics.MissingImportId,
                        path,
                        item.Text,
                        new MarkupDiagnostic(
                            import.Line,
                            import.Column,
                            $"Imported module '{import.Path}' was not found in this compilation.")));
                    continue;
                }

                byRaw[import.Path] = resolved;
                imports.Add(new SpecImport(resolved, import.Line, import.Column));
            }

            resolvedImports[path] = imports;
            resolutions[path] = byRaw;
        }

        // The graph lives in a SpecArena so an imported spec is reachable by
        // its stable name and cycle detection stays IR-only.
        var graph = new SpecArena();
        foreach (var path in names)
            graph.AddModule(new SpecModule(path, parsed[path].Parse.Arena, resolvedImports[path]));

        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cycle in graph.DetectImportCycles())
        {
            foreach (var name in cycle.Chain)
                blocked.Add(name);

            var owner = cycle.Chain.Count >= 2 ? cycle.Chain[cycle.Chain.Count - 2] : cycle.Chain.FirstOrDefault() ?? "";
            if (!parsed.TryGetValue(owner, out var ownerSource))
                continue;

            findings.Add(new GraphFinding(
                MarkupDiagnostics.CyclicImportId,
                owner,
                ownerSource.Text,
                new MarkupDiagnostic(cycle.Line, cycle.Column, cycle.Message)));
        }

        // A module that imports a blocked module cannot emit a call to a
        // builder that was never generated. Propagate the block upstream so a
        // cycle cannot leave a dangling reference in the compilation.
        bool propagated;
        do
        {
            propagated = false;
            foreach (var path in names)
            {
                if (blocked.Contains(path))
                    continue;
                foreach (var target in resolutions[path].Values)
                {
                    if (!blocked.Contains(target))
                        continue;
                    blocked.Add(path);
                    propagated = true;
                    break;
                }
            }
        }
        while (propagated);

        var modules = new List<ResolvedModule>();
        foreach (var path in names)
        {
            var item = parsed[path];
            modules.Add(new ResolvedModule(
                path,
                item.Identifier,
                item.Text,
                item.Parse,
                resolutions[path],
                HasUses(item.Parse.Arena)));
        }

        var moduleMap = new Dictionary<string, ResolvedModule>(StringComparer.Ordinal);
        foreach (var module in modules)
            moduleMap[module.Path] = module;

        // Sequence counts include the sequences an imported module contributes
        // at every `.use` site. They are memoized and cycle-guarded so a graph
        // with a cycle still resolves instead of recursing forever.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var module in modules)
            CountModule(module.Path, moduleMap, identifiers, findings, counts, new HashSet<string>(StringComparer.Ordinal));

        return new ModuleGraphResult(graph, modules, findings, counts, identifiers, blocked);
    }

    /// <summary>Resolves one <c>.use</c> path against a module's imports.</summary>
    /// <returns>The resolved module name, or null when it is not in this graph.</returns>
    internal static string? ResolveUse(
        string modulePath,
        string rawPath,
        ResolvedModule module,
        IReadOnlyDictionary<string, string> identifiers)
    {
        if (module.ImportResolutions.TryGetValue(rawPath, out var declared))
            return declared;

        return MarkupPaths.ResolveKnown(modulePath, rawPath, identifiers);
    }

    /// <summary>Checksum over every source in the graph, in stable name order.</summary>
    internal static string GraphChecksum(IReadOnlyList<MarkupModuleSource> sources)
    {
        var text = new StringBuilder();
        foreach (var source in sources
                     .OrderBy(static item => MarkupPaths.Normalize(item.Path), StringComparer.Ordinal))
        {
            text.Append(MarkupPaths.Normalize(source.Path)).Append('\n').Append(source.Text).Append('\n');
        }

        return MarkupChecksum.Compute(text.ToString());
    }

    private static void CountModule(
        string path,
        Dictionary<string, ResolvedModule> modules,
        IReadOnlyDictionary<string, string> identifiers,
        List<GraphFinding> findings,
        Dictionary<string, int> counts,
        HashSet<string> visiting)
    {
        if (counts.ContainsKey(path))
            return;
        if (!visiting.Add(path))
            return; // A cycle was already diagnosed; keep counting finite.
        if (!modules.TryGetValue(path, out var module))
            return;

        var total = 0;
        foreach (var root in module.Arena.Roots)
            CountNode(root, path, module, modules, identifiers, findings, counts, visiting, ref total);

        visiting.Remove(path);
        counts[path] = total;
    }

    private static void CountNode(
        SpecNode node,
        string modulePath,
        ResolvedModule module,
        Dictionary<string, ResolvedModule> modules,
        IReadOnlyDictionary<string, string> identifiers,
        List<GraphFinding> findings,
        Dictionary<string, int> counts,
        HashSet<string> visiting,
        ref int total)
    {
        total++; // Begin
        if (node.Kind == SpecKind.Text)
            total++; // Text

        foreach (var operation in node.Operations)
        {
            if (!operation.Name.Equals("use", StringComparison.OrdinalIgnoreCase)
                || operation.Arguments.Count == 0)
                continue;

            var raw = operation.Arguments[0].Text;
            var target = ResolveUse(modulePath, raw, module, identifiers);
            if (target is null)
            {
                findings.Add(new GraphFinding(
                    MarkupDiagnostics.MissingImportId,
                    modulePath,
                    module.Text,
                    new MarkupDiagnostic(
                        operation.SourceLine,
                        operation.SourceColumn,
                        $"Imported module '{raw}' was not found in this compilation.")));
                continue;
            }

            CountModule(target, modules, identifiers, findings, counts, visiting);
            if (counts.TryGetValue(target, out var imported))
                total += imported;
        }

        foreach (var child in node.Children)
            CountNode(child, modulePath, module, modules, identifiers, findings, counts, visiting, ref total);
    }

    private static bool HasUses(SpecArena arena)
    {
        foreach (var node in arena.Nodes)
        {
            foreach (var operation in node.Operations)
            {
                if (operation.Name.Equals("use", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string MakeIdentifier(string path)
    {
        var output = new StringBuilder("HarborUi_");
        foreach (var c in path)
            output.Append(char.IsLetterOrDigit(c) ? c : '_');
        return output.ToString();
    }

    private sealed record ParsedSource(string Text, MarkupParseResult Parse, string Identifier);
}

/// <summary>Deterministic path handling: forward slashes, no disk access.</summary>
internal static class MarkupPaths
{
    /// <summary>Normalizes separators and removes the leading slash.</summary>
    internal static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    ///     Resolves a path written in <paramref name="importingPath"/> against
    ///     that file's directory. Dot and double-dot segments collapse without
    ///     touching the file system, so the result is identical on every host.
    /// </summary>
    internal static string Resolve(string importingPath, string rawPath)
    {
        var basePath = Normalize(importingPath);
        var raw = Normalize(rawPath);
        if (raw.Length == 0)
            return raw;

        var slash = basePath.LastIndexOf('/');
        var combined = slash >= 0 ? basePath[..slash] + "/" + raw : raw;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    /// <summary>
    ///     Finds the module an import names. Markup may write paths relative to
    ///     the importing file (<c>"Shared.harbor-ui"</c>) or relative to the
    ///     project root (<c>"Fixtures/Shared.harbor-ui"</c>). The file-relative
    ///     candidate wins when both resolve, which keeps nested imports local;
    ///     the root-relative fallback makes project paths deterministic.
    /// </summary>
    internal static string? ResolveKnown(
        string importingPath,
        string rawPath,
        IReadOnlyDictionary<string, string> identifiers)
    {
        var fileRelative = Resolve(importingPath, rawPath);
        if (identifiers.ContainsKey(fileRelative))
            return fileRelative;

        var rootRelative = Normalize(rawPath);
        return identifiers.ContainsKey(rootRelative) ? rootRelative : null;
    }
}
