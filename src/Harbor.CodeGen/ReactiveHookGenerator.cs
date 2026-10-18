using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.CodeGen;

/// <summary>
///     Incremental source generator for <c>[Reactive]</c> field hooks
///     (issue #1542). For every <c>[ObservableProperty]</c>-carrying field
///     annotated <c>[Reactive]</c> in a class that derives
///     <c>ReactiveViewModel</c>, emits the <c>On&lt;Name&gt;Changed</c>
///     partial-method implementation that the CommunityToolkit.Mvvm
///     generated setter calls: a MarkRepaint/MarkLayout call chosen from the
///     attribute's <c>Repaint</c>/<c>Layout</c> kind, optionally preceded by
///     the coerce branch (<c>NonNegative</c> / <c>NonNull</c>) that
///     re-assigns the coerced value through the generated property — the
///     setter re-enters the hook and marks the dirty kind there.
///
///     This replaces the hand-written <c>partial void OnXChanged...</c> /
///     <c>Validate_X</c> / <c>Watch_X</c> boilerplate in the view models:
///     the <c>[Reactive]</c> attribute is the single record of the
///     invalidate kind. A manual <c>On&lt;Name&gt;Changed</c> for a
///     <c>[Reactive]</c> field now stops the build (duplicate
///     partial-method implementation) — on purpose, it would double-fire.
///     Matched by metadata name (not by type reference) so this netstandard2.0
///     Roslyn component needs no project reference; must track
///     <c>ReactiveAttribute</c> in
///     src/Harbor.Terminal.Abstractions/ViewModels/ReactiveViewModel.cs.
///     AOT-safe: the generated hook only touches the VM's own members.
/// </summary>
[Generator]
public sealed class ReactiveHookGenerator : IIncrementalGenerator
{
    private const string ReactiveAttributeName = "ReactiveAttribute";
    private const string ObservablePropertyAttributeName = "ObservablePropertyAttribute";
    private const string ReactiveViewModelBaseName = "ReactiveViewModel";

    /// <summary>Per-field everything needed to emit one hook.</summary>
    private sealed record HookData(
        string Namespace,
        (string Name, string Access)[] Nesting,
        string ClassName,
        string HookName,
        string TypeName,
        bool Layout,
        bool NonNegative,
        bool NonNull,
        int Order);

    /// <summary>
    ///     Fully-qualified + special-type keywords (<c>string</c>, `int`)
    ///     + nullable-annotation carrying, so the emitted hook signature
    ///     matches the toolkit-declared <c>On&lt;Name&gt;Changed</c> partial
    ///     byte-for-byte even for <c>string?</c>-typed fields.
    /// </summary>
    private static readonly SymbolDisplayFormat TypeDisplay =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var hooks = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is VariableDeclaratorSyntax
                    && node.Parent is VariableDeclarationSyntax
                    && node.Parent.Parent is FieldDeclarationSyntax,
                transform: static (ctx, _) => ReadHook((IFieldSymbol)ctx.SemanticModel.GetDeclaredSymbol(ctx.Node)!))
            .Where(static h => h is not null)
            .Collect();

        context.RegisterSourceOutput(hooks, static (spc, list) =>
        {
            foreach (HookData[] group in list
                .Where(static h => h is not null)
                .Cast<HookData>()
                .GroupBy(static h => (h.Namespace, h.ClassName))
                .Select(static g => g.OrderBy(static h => h.Order).ToArray())
                .OrderBy(static g => g[0].Namespace, StringComparer.Ordinal)
                .ThenBy(static g => g[0].ClassName, StringComparer.Ordinal))
            {
                spc.AddSource(
                    $"ReactiveHooks_{Sanitize(group[0].ClassName)}.g.cs",
                    SourceText.From(GenerateHooks(group), Encoding.UTF8));
            }
        });
    }

    private static HookData? ReadHook(IFieldSymbol field)
    {
        // [ObservableProperty] is a prerequisite in two ways: the toolkit
        // declares the On<Name>Changed partials only for its generated
        // properties, and the coerce branch re-assigns through that
        // generated property.
        if (field.IsStatic
            || field.ContainingType.TypeKind != TypeKind.Class
            || !HasAttributeNamed(field, ObservablePropertyAttributeName)
            || !HasAttributeNamed(field, ReactiveAttributeName))
        {
            return null;
        }

        // Hooks call MarkRepaint/MarkLayout/NonNegative/NonNull — members of
        // the ReactiveViewModel base. Classes elsewhere get nothing silently.
        if (!DerivesReactiveViewModel(field.ContainingType))
            return null;

        AttributeData attr = field.GetAttributes().First(ad => ad.AttributeClass?.Name == ReactiveAttributeName);
        bool layout = false;
        bool nonNegative = false;
        bool nonNull = false;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Layout":
                    layout = named.Value.Value is bool b && b;
                    break;
                case "NonNegative":
                    nonNegative = named.Value.Value is bool b2 && b2;
                    break;
                case "NonNull":
                    nonNull = named.Value.Value is bool b3 && b3;
                    break;
            }
        }

        var nesting = new List<(string Name, string Access)>();
        for (INamedTypeSymbol? c = field.ContainingType; c is not null; c = c.ContainingType)
        {
            nesting.Add((c.MetadataName, Access(c.DeclaredAccessibility)));
        }

        return new HookData(
            field.ContainingType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            nesting.ToArray(),
            field.ContainingType.MetadataName,
            HookName: PropertyName(field.Name),
            field.Type.ToDisplayString(TypeDisplay),
            layout,
            nonNegative,
            nonNull,
            field.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0);
    }

    private static bool HasAttributeNamed(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(ad => ad.AttributeClass?.Name == name);

    private static bool DerivesReactiveViewModel(ITypeSymbol type)
    {
        for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name == ReactiveViewModelBaseName)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The CommunityToolkit.Mvvm rule: a field named
    ///     <c>lowerCamel</c>/<c>_lowerCamel</c>/<c>m_lowerCamel</c> generates
    ///     the <c>UpperCamel</c> property; the hook suffix is that name.
    /// </summary>
    private static string PropertyName(string fieldName)
    {
        // The CMT rule: m_lowerCamel and _lowerCamel both map to UpperCamel
        // — m_ strips TWO characters, a _ strips ONE (the naive [2..] for
        // both ate the first letter after _ and produced OnSStreamingChanged).
        string stem = fieldName.StartsWith("m_", StringComparison.Ordinal)
            ? fieldName[2..]
            : fieldName.StartsWith("_", StringComparison.Ordinal)
                ? fieldName[1..]
                : fieldName;
        return stem.Length == 0 ? fieldName : char.ToUpperInvariant(stem[0]) + stem[1..];
    }

    private static string Access(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private => "private",
        _ => "internal",
    };

    private static string Sanitize(string s) => s.Replace('.', '_').Replace('+', '_');

    private static string GenerateHooks(HookData[] data)
    {
        HookData first = data[0];
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// ReactiveHookGenerator (issue #1542): On<Name>Changed hooks for [Reactive] fields — DO NOT EDIT.");
        sb.AppendLine("#nullable enable");
        if (!string.IsNullOrEmpty(first.Namespace))
        {
            sb.Append("namespace ").Append(first.Namespace).AppendLine(";");
        }

        // Nesting shell: a nested partial declaration must repeat its
        // enclosing partial containers (test Stubs nest inside the fixture).
        // Indentation is level-arithmetic, not string mutation: an indent
        // string would be re-assigned inside a loop (S1643).
        int level = 0;
        for (int i = first.Nesting.Length - 1; i > 0; i--)
        {
            Indent(sb, level).Append(first.Nesting[i].Access).Append(" partial class ").Append(first.Nesting[i].Name).AppendLine();
            Indent(sb, level).AppendLine("{");
            level++;
        }

        // The hooked class itself: no sealed/abstract — a partial
        // declaration may carry those at most once (the source's own does).
        Indent(sb, level).Append(first.Nesting[0].Access).Append(" partial class ").Append(first.Nesting[0].Name).AppendLine();
        Indent(sb, level).AppendLine("{");
        level++;

        foreach (HookData h in data)
        {
            if (!h.NonNegative && !h.NonNull)
            {
                Indent(sb, level).Append("partial void On").Append(h.HookName).Append("Changed(").Append(h.TypeName)
                    .Append(" value) => Mark").Append(h.Layout ? "Layout" : "Repaint").AppendLine("();");
                continue;
            }

            Indent(sb, level).Append("partial void On").Append(h.HookName).Append("Changed(").Append(h.TypeName).Append(" value)").AppendLine();
            Indent(sb, level).AppendLine("{");
            int inner = level + 1;
            Indent(sb, inner).Append(h.TypeName).Append(" coerced = ");
            if (h.NonNull)
            {
                sb.Append("NonNull(value);").AppendLine();
                Indent(sb, inner).Append("if (!global::System.String.Equals(coerced, value, global::System.StringComparison.Ordinal))").AppendLine();
            }
            else
            {
                sb.Append("NonNegative(value);").AppendLine();
                Indent(sb, inner).Append("if (coerced != value)").AppendLine();
            }

            Indent(sb, inner).AppendLine("{");
            Indent(sb, inner).Append("    ").Append(h.HookName).Append(" = coerced;").AppendLine();
            Indent(sb, inner).Append("    return;").AppendLine();
            Indent(sb, inner).AppendLine("}");
            Indent(sb, inner).Append(h.Layout ? "MarkLayout();" : "MarkRepaint();").AppendLine();
            Indent(sb, level).AppendLine("}");
        }

        level--;
        Indent(sb, level).AppendLine("}");
        for (int i = 1; i < first.Nesting.Length; i++)
        {
            level--;
            Indent(sb, level).AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Level arithmetic instead of an indent string: no string is
    ///     created, concatenated, or compounded inside a loop (S1643) and
    ///     every line keeps the same 4-space step.
    /// </summary>
    private static StringBuilder Indent(StringBuilder sb, int level)
    {
        sb.Append(' ', 4 * level);
        return sb;
    }
}
