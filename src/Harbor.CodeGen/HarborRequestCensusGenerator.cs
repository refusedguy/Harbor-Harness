using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.CodeGen;

/// <summary>
///     Emits the <c>HarborRequest</c> union census at compile time (issue #2596).
/// </summary>
/// <remarks>
///     <para>
///         The old census called <c>typeof(HarborRequest).Assembly.GetTypes()</c>
///         while the IPC server was running. Under NativeAOT or trimming, type
///         enumeration reads metadata that may no longer be present, and a
///         <c>ReflectionTypeLoadException</c> degraded to a partial list. The
///         dispatcher used that list as its startup guard, so a partial census
///         could declare a server healthy while leaving a request type unhandled.
///     </para>
///     <para>
///         Here the same closed set is read from Roslyn symbols while the server
///         is being compiled. Every concrete subtype of
///         <c>Harbor.Ipc.Protocol.HarborRequest</c> in the declaring assembly is
///         written into a source file as a <c>typeof(T)</c> reference. At run time
///         there is no metadata query at all, and each emitted <c>typeof</c>
///         keeps its type reachable under trimming. The wire union itself is not
///         touched: the <c>[Union(n, typeof(T))]</c> tags in
///         <c>Harbor.Ipc.Abstractions</c> stay as they are.
///     </para>
///     <para>
///         The generator is off by default and is enabled only by the IPC server
///         project through <c>HarborGenerateIpcRequestCensus</c>. That keeps the
///         generated partial class out of compilations which do not declare it.
///     </para>
/// </remarks>
[Generator]
public sealed class HarborRequestCensusGenerator : IIncrementalGenerator
{
    /// <summary>Metadata name of the union whose members are enumerated.</summary>
    internal const string RequestMetadataName = "Harbor.Ipc.Protocol.HarborRequest";

    /// <summary>
    ///     MSBuild switch that enables emission. Absent or false means no source
    ///     is produced, so other consumers of Harbor.CodeGen are unaffected.
    /// </summary>
    public const string SwitchPropertyName = "build_property.HarborGenerateIpcRequestCensus";

    private const string NewLine = "\n";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var request = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) => IsEnabled(options.GlobalOptions)))
            .Select(static (pair, _) => Analyze(pair.Left, pair.Right));

        context.RegisterSourceOutput(request, static (spc, data) => Emit(spc, data));
    }

    private static bool IsEnabled(AnalyzerConfigOptions options) =>
        options.TryGetValue(SwitchPropertyName, out string? value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static Census? Analyze(Compilation compilation, bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        INamedTypeSymbol? union = compilation.GetTypeByMetadataName(RequestMetadataName);
        if (union is null)
        {
            return null;
        }

        var members = new List<INamedTypeSymbol>();
        CollectSubtypes(union.ContainingAssembly.GlobalNamespace, union, members);

        if (members.Count == 0)
        {
            // Do not emit an empty census: that would make the startup guard
            // vacuous. With no generated member, the hand-written partial fails
            // to compile, which is the loud failure this guard exists to give.
            return null;
        }

        members.Sort(static (left, right) =>
        {
            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0
                ? byName
                : string.CompareOrdinal(
                    left.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    right.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        });

        return new Census(
            members.Select(m => m.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray());
    }

    private static void CollectSubtypes(
        INamespaceSymbol scope,
        INamedTypeSymbol union,
        List<INamedTypeSymbol> sink)
    {
        foreach (INamedTypeSymbol type in scope.GetTypeMembers())
        {
            CollectType(type, union, sink);
        }

        foreach (INamespaceSymbol child in scope.GetNamespaceMembers())
        {
            CollectSubtypes(child, union, sink);
        }
    }

    private static void CollectType(INamedTypeSymbol type, INamedTypeSymbol union, List<INamedTypeSymbol> sink)
    {
        if (type.TypeKind == TypeKind.Class && !type.IsAbstract && DerivesFrom(type, union))
        {
            sink.Add(type);
        }

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            CollectType(nested, union, sink);
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol union)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, union))
            {
                return true;
            }
        }

        return false;
    }

    private static void Emit(SourceProductionContext spc, Census? census)
    {
        if (census is null)
        {
            return;
        }

        var source = new StringBuilder();
        source.Append("// <auto-generated/>").Append(NewLine);
        source.Append("// HarborRequestCensusGenerator. DO NOT EDIT.").Append(NewLine);
        source.Append("#nullable enable").Append(NewLine);
        source.Append(NewLine);
        source.Append("namespace Harbor.Ipc.Protocol;").Append(NewLine);
        source.Append(NewLine);
        source.Append("public static partial class HarborRequestTypes").Append(NewLine);
        source.Append('{').Append(NewLine);
        source.Append("    /// <summary>").Append(NewLine);
        source.Append("    ///     Every concrete <see cref=\"HarborRequest\"/> subtype, enumerated at").Append(NewLine);
        source.Append("    ///     compile time from the type system. No runtime metadata scan.").Append(NewLine);
        source.Append("    /// </summary>").Append(NewLine);
        source.Append("    public static global::System.Collections.Generic.IReadOnlyList<global::System.Type> All { get; } =").Append(NewLine);
        source.Append("    [").Append(NewLine);

        foreach (string type in census.Types)
        {
            source.Append("        typeof(").Append(type).Append("),").Append(NewLine);
        }

        source.Append("    ];").Append(NewLine);
        source.Append('}').Append(NewLine);

        spc.AddSource("HarborRequestTypes.Census.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private sealed record Census(IReadOnlyList<string> Types);
}
