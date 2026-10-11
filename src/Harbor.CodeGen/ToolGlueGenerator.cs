using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.CodeGen;

/// <summary>
///     SG3 spike generator (issue 1244): emits scalar ITool glue for partial
///     tool classes carrying <c>ToolInfoAttribute</c> (matched by metadata
///     name, so this project needs no reference to Harbor.Abstractions —
///     same convention as <c>EscapeCodeGenerator</c>).
///     Schema, ValidateArguments and ExecuteAsync stay hand-written.
/// </summary>
[Generator]
public sealed class ToolGlueGenerator : IIncrementalGenerator
{
    private const string AttributeName = "ToolInfoAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tools = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cds
                    && cds.AttributeLists.Count > 0,
                transform: static (ctx, _) =>
                {
                    var symbol = (INamedTypeSymbol)ctx.SemanticModel.GetDeclaredSymbol(ctx.Node)!;
                    var attr = symbol.GetAttributes()
                        .FirstOrDefault(ad => ad.AttributeClass?.Name == AttributeName);
                    return attr is null ? null : ReadToolInfo(symbol, attr);
                })
            .Where(static data => data is not null);

        context.RegisterSourceOutput(tools, static (spc, data) =>
        {
            spc.AddSource(data!.HintName, SourceText.From(GeneratePartial(data), Encoding.UTF8));
        });
    }

    private sealed record ToolGlueData(
        string HintName,
        string Namespace,
        string ClassName,
        bool IsSealed,
        string Name,
        string DisplayName,
        string Description,
        string? GlyphLiteral,
        string ExecutionModeMember,
        string? PromptSnippetLiteral,
        string[] Guidelines);

    private static ToolGlueData? ReadToolInfo(INamedTypeSymbol symbol, AttributeData attr)
    {
        if (attr.ConstructorArguments.Length < 3)
            return null;

        string? name = attr.ConstructorArguments[0].Value?.ToString();
        string? displayName = attr.ConstructorArguments[1].Value?.ToString();
        string? description = attr.ConstructorArguments[2].Value?.ToString();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(displayName) || description is null)
            return null;

        string? glyph = null;
        // Must track ToolInfoAttribute.ExecutionMode's initializer.
        string executionMode = "Parallel";
        string? snippet = null;
        string[] guidelines = [];

        foreach (var na in attr.NamedArguments)
        {
            switch (na.Key)
            {
                case "Glyph":
                    glyph = na.Value.Value?.ToString();
                    break;
                case "ExecutionMode":
                    executionMode = EnumMemberName(na.Value) ?? executionMode;
                    break;
                case "PromptSnippet":
                    snippet = na.Value.Value?.ToString();
                    break;
                case "PromptGuidelines":
                    guidelines = StringArray(na.Value);
                    break;
            }
        }

        string ns = symbol.ContainingNamespace.ToDisplayString();
        string hint = $"ToolGlue_{ns.Replace('.', '_')}_{symbol.Name}.g.cs";
        return new ToolGlueData(hint, ns, symbol.Name, symbol.IsSealed, name!, displayName!, description!, glyph, executionMode, snippet, guidelines);
    }

    private static string? EnumMemberName(TypedConstant tc)
    {
        if (tc.Type is not INamedTypeSymbol enumType || tc.Value is null)
            return null;
        foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.HasConstantValue && Equals(member.ConstantValue, tc.Value))
                return member.Name;
        }
        return null;
    }

    private static string[] StringArray(TypedConstant tc)
    {
        if (tc.Kind != TypedConstantKind.Array || tc.IsNull)
            return [];
        var list = new List<string>();
        foreach (var item in tc.Values)
        {
            if (item.Value?.ToString() is { } s)
                list.Add(s);
        }
        return list.ToArray();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static void AppendLiteral(StringBuilder sb, string? value)
    {
        if (value is null)
            sb.Append("null");
        else
        {
            sb.Append('"');
            sb.Append(Escape(value));
            sb.Append('"');
        }
    }

    private static string GeneratePartial(ToolGlueData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// SG3 spike (issue 1244): scalar ITool glue from [ToolInfo].");
        sb.AppendLine("#nullable enable");
        sb.Append("namespace ");
        sb.Append(d.Namespace);
        sb.AppendLine(";");
        sb.Append("public ");
        if (d.IsSealed)
            sb.Append("sealed ");
        sb.Append("partial class ");
        sb.AppendLine(d.ClassName);
        sb.AppendLine("{");
        sb.Append("    public global::Harbor.Abstractions.Models.Identifiers.ToolName Name => global::Harbor.Abstractions.Models.Identifiers.ToolName.Create(\"");
        sb.Append(Escape(d.Name));
        sb.AppendLine("\");");
        sb.Append("    public string DisplayName => \"");
        sb.Append(Escape(d.DisplayName));
        sb.AppendLine("\";");
        if (d.GlyphLiteral is null)
            sb.AppendLine("    public string Glyph => global::Harbor.Abstractions.Tools.ToolGlyphs.Default;");
        else
        {
            sb.Append("    public string Glyph => \"");
            sb.Append(Escape(d.GlyphLiteral));
            sb.AppendLine("\";");
        }
        sb.Append("    public string Description => \"");
        sb.Append(Escape(d.Description));
        sb.AppendLine("\";");
        sb.Append("    public global::Harbor.Abstractions.Tools.ExecutionMode ExecutionMode => global::Harbor.Abstractions.Tools.ExecutionMode.");
        sb.Append(d.ExecutionModeMember);
        sb.AppendLine(";");
        sb.Append("    public string? PromptSnippet => ");
        AppendLiteral(sb, d.PromptSnippetLiteral);
        sb.AppendLine(";");
        sb.Append("    public global::System.Collections.Generic.IReadOnlyList<string> PromptGuidelines => ");
        if (d.Guidelines.Length == 0)
        {
            sb.AppendLine("global::System.Array.Empty<string>();");
        }
        else
        {
            sb.Append("new string[] { ");
            for (int i = 0; i < d.Guidelines.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append('"');
                sb.Append(Escape(d.Guidelines[i]));
                sb.Append('"');
            }
            sb.AppendLine(" };");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}
