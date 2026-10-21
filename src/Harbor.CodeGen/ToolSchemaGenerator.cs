using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.CodeGen;

/// <summary>
///     SG4 generator (issue #1780): renders the parameter schema of a tool from
///     its <c>[ToolParam]</c> declaration and emits the tool's
///     <c>ParameterSchema</c>.
/// </summary>
/// <remarks>
///     <para>
///         The parameter schema of a tool is the tool's contract with the model: the JSON
///         the model is allowed to send. Before #1780 every builtin tool carried its own
///         hand-written copy of that JSON — 21 files, 281 lines — and the test project
///         carried a second copy of all of them. Two copies of a contract is how a
///         contract drifts.
///     </para>
///     <para>
///         The declaration now lives once, as <c>[ToolParam]</c> attributes on the tool
///         class, and this generator renders it. The rendered schema is hashed and
///         compared with the pin in
///         <c>src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json</c>: a schema
///         that differs from its pin is a build error, so a change to a tool's arguments
///         cannot reach the model unnoticed.
///     </para>
///     <para>
///         The rendered text is the canonical form of the schema — compact JSON, the
///         keys in declaration order — so the bytes the model sees are a function of the
///         declaration alone. The pin is the SHA-256 of that text.
///     </para>
/// </remarks>
[Generator]
public sealed class ToolSchemaGenerator : IIncrementalGenerator
{
    // Matched by metadata name (not by type reference) so this Roslyn
    // component needs no project reference. Must track the attributes in
    // src/Harbor.Tools.Builtin/Tools/ToolSchemaAttribute.cs and
    // src/Harbor.Tools.Builtin/Tools/ToolParamAttribute.cs.
    private const string SchemaAttributeNamespace = "Harbor.Tools.Builtin";
    private const string SchemaAttributeName = "ToolSchemaAttribute";
    private const string ParamAttributeName = "ToolParamAttribute";
    private const string ToolAttributeNamespace = "Harbor.Abstractions.Tools";
    private const string ToolAttributeName = "ToolInfoAttribute";

    // The pin: one SHA-256 per tool, keyed by the tool class name.
    private const string PinFileName = "tool-schemas.sha256.json";

    /// <summary>The schema of a tool differs from the hash it was pinned with.</summary>
    private static readonly DiagnosticDescriptor DivergenceRule = new(
        "HTOOL001",
        "A tool's parameter schema differs from its pinned hash",
        "The parameter schema of tool '{0}' differs from the hash it was pinned with: it is "
        + "pinned as {1} and the declaration renders as {2}. The schema is the tool's "
        + "contract with the model: a change to it changes what the model may send, and it "
        + "changes the prompts and the recorded sessions that carry it. Re-sign the pin "
        + "(tools/codemod-tool-schemas.py --write-pin) in the same commit, or revert the change.",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The declaration of a schema cannot be rendered.</summary>
    private static readonly DiagnosticDescriptor MalformedRule = new(
        "HTOOL002",
        "A tool's parameter schema is declared wrongly",
        "{0}",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The pin and the declarations do not cover the same tools.</summary>
    private static readonly DiagnosticDescriptor PinCoverageRule = new(
        "HTOOL003",
        "The pinned tool schemas and the declared ones do not agree",
        "{0}",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A tool with a stable name does not declare its schema.</summary>
    private static readonly DiagnosticDescriptor UndeclaredRule = new(
        "HTOOL004",
        "A tool does not declare its parameter schema",
        "The tool '{0}' declares a stable name and therefore has a schema the model is "
        + "shown, but it carries no [ToolSchema] declaration. Declare the schema with "
        + "[ToolParam] attributes, so the schema is rendered from one declaration instead "
        + "of written out by hand.",
        "ToolSchema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The JSON types a parameter may declare.</summary>
    private static readonly string[] KnownTypes = ["string", "integer", "boolean", "object", "array"];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var declared = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                $"{SchemaAttributeNamespace}.{SchemaAttributeName}",
                static ctx => ReadSchema(ctx.TargetSymbol))
            .Where(static data => data is not null);

        var pins = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(PinFileName, StringComparison.Ordinal))
            .Select(static (text, ct) => text.GetText(ct)?.ToString() ?? string.Empty)
            .Collect();

        context.RegisterSourceOutput(
            declared.Collect().Combine(pins),
            static (spc, source) => Execute(spc, source.Left, source.Right));

        // A tool with a stable name has a schema the model is shown. It has
        // to come from a declaration: a hand-written one is a second copy of
        // the contract, which is what #1780 removed.
        var named = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                $"{ToolAttributeNamespace}.{ToolAttributeName}",
                static ctx => ctx.TargetSymbol);

        context.RegisterSourceOutput(
            named.Collect(),
            static (spc, symbols) =>
            {
                foreach (var symbol in symbols)
                {
                    if (HasAttribute(symbol, SchemaAttributeNamespace, SchemaAttributeName))
                        continue;
                    Location location = symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;
                    spc.ReportDiagnostic(Diagnostic.Create(UndeclaredRule, location, symbol.Name));
                }
            });
    }

    /// <summary>Whether the symbol carries an attribute with this namespace and name.</summary>
    private static bool HasAttribute(ISymbol symbol, string attributeNamespace, string attributeName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            INamedTypeSymbol? attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
                continue;
            if (attributeClass.Name != attributeName)
                continue;
            if (attributeClass.ContainingNamespace.ToDisplayString() != attributeNamespace)
                continue;
            return true;
        }

        return false;
    }

    private sealed record ParamData(
        string Name,
        string Type,
        string? Description,
        string[]? Enum,
        bool Required,
        string? ItemsOf);

    private sealed record SchemaData(
        string HintName,
        string Namespace,
        string ClassName,
        bool IsSealed,
        Location Location,
        ImmutableArray<ParamData> Parameters);

    /// <summary>Read the declaration of one tool's schema.</summary>
    private static SchemaData? ReadSchema(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol named)
            return null;

        var parameters = ImmutableArray.CreateBuilder<ParamData>();
        foreach (var attribute in named.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != ParamAttributeName)
                continue;
            if (attribute.ConstructorArguments.Length < 2)
                continue;

            string? name = attribute.ConstructorArguments[0].Value as string;
            string? type = attribute.ConstructorArguments[1].Value as string;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
                continue;

            string? description = null;
            string[]? values = null;
            bool required = false;
            string? itemsOf = null;
            foreach (var namedArgument in attribute.NamedArguments)
            {
                switch (namedArgument.Key)
                {
                    case "Description":
                        description = namedArgument.Value.Value as string;
                        break;
                    case "Enum":
                        values = StringArray(namedArgument.Value);
                        break;
                    case "Required":
                        required = namedArgument.Value.Value is true;
                        break;
                    case "ItemsOf":
                        itemsOf = namedArgument.Value.Value as string;
                        break;
                }
            }

            parameters.Add(new ParamData(name!, type!, description, values, required, itemsOf));
        }

        string ns = named.ContainingNamespace.ToDisplayString();
        string hint = $"ToolSchema_{ns.Replace('.', '_')}_{named.Name}.g.cs";
        Location location = named.Locations.Length > 0 ? named.Locations[0] : Location.None;
        return new SchemaData(hint, ns, named.Name, named.IsSealed, location, parameters.ToImmutable());
    }

    private static string[] StringArray(TypedConstant constant)
    {
        if (constant.Kind != TypedConstantKind.Array || constant.IsNull)
            return [];
        var list = new List<string>();
        foreach (var item in constant.Values)
        {
            if (item.Value?.ToString() is { } text)
                list.Add(text);
        }

        return list.ToArray();
    }

    /// <summary>Render the declared schemas, check them against the pin, emit them.</summary>
    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<SchemaData?> declared,
        ImmutableArray<string> pinTexts)
    {
        string pinText = pinTexts.Length > 0 ? pinTexts[0] : string.Empty;
        Dictionary<string, string> pins = ParsePins(pinText);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var data in declared)
        {
            if (data is null)
                continue;
            if (!seen.Add(data.ClassName))
                continue;

            if (!Validate(data, out string? problem))
            {
                context.ReportDiagnostic(Diagnostic.Create(MalformedRule, data.Location, problem!));
                continue;
            }

            string canonical = Render(data);
            string digest = Digest(canonical);

            if (!pins.TryGetValue(data.ClassName, out string? pinned))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    PinCoverageRule,
                    data.Location,
                    $"The tool '{data.ClassName}' declares a schema, but no hash is pinned for it. "
                    + "Add it to src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json "
                    + "(tools/codemod-tool-schemas.py --write-pin)."));
                continue;
            }

            if (!string.Equals(pinned, digest, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DivergenceRule,
                    data.Location,
                    data.ClassName,
                    pinned,
                    digest));
                continue;
            }

            context.AddSource(data.HintName, SourceText.From(Generate(data, canonical), Encoding.UTF8));
        }

        foreach (string pinned in pins.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (seen.Contains(pinned))
                continue;
            context.ReportDiagnostic(Diagnostic.Create(
                PinCoverageRule,
                Location.None,
                $"A hash is pinned for the tool '{pinned}', but no tool declares that schema. "
                + "Either the tool was renamed or it is gone: drop the pin "
                + "(tools/codemod-tool-schemas.py --write-pin)."));
        }
    }

    /// <summary>Whether the declaration can be rendered at all.</summary>
    private static bool Validate(SchemaData data, out string? problem)
    {
        problem = null;
        if (data.Parameters.Length == 0)
        {
            problem = $"The tool '{data.ClassName}' declares a schema with no parameter.";
            return false;
        }

        var names = new HashSet<(string Scope, string Name)>();
        foreach (var parameter in data.Parameters)
        {
            // The root properties and the properties of an array's item
            // object are two scopes: "oldString" may be one and the other.
            string scope = parameter.ItemsOf ?? string.Empty;
            if (!names.Add((scope, parameter.Name)))
            {
                problem = $"The tool '{data.ClassName}' declares the parameter '{parameter.Name}' twice"
                    + (scope.Length > 0 ? $" in the items of '{scope}'." : ".");
                return false;
            }

            if (!KnownTypes.Contains(parameter.Type, StringComparer.Ordinal))
            {
                problem = $"The parameter '{parameter.Name}' of '{data.ClassName}' declares the "
                    + $"unknown type '{parameter.Type}'. The known types are: "
                    + string.Join(", ", KnownTypes) + ".";
                return false;
            }
        }

        foreach (var parameter in data.Parameters)
        {
            if (parameter.ItemsOf is null)
                continue;

            var parent = data.Parameters.FirstOrDefault(p => p.ItemsOf is null && p.Name == parameter.ItemsOf);
            if (parent is null)
            {
                problem = $"The parameter '{parameter.Name}' of '{data.ClassName}' belongs to the "
                    + $"items of '{parameter.ItemsOf}', which it does not declare.";
                return false;
            }

            if (parent.Type != "array")
            {
                problem = $"The parameter '{parameter.Name}' of '{data.ClassName}' belongs to the "
                    + $"items of '{parameter.ItemsOf}', which is a '{parent.Type}' and not an array.";
                return false;
            }
        }

        return true;
    }

    /// <summary>The canonical JSON of a declared schema: compact, in declaration order.</summary>
    private static string Render(SchemaData data)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"object\",\"properties\":{");
        bool first = true;
        foreach (var parameter in data.Parameters)
        {
            if (parameter.ItemsOf is not null)
                continue;
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append(Quote(parameter.Name)).Append(':');
            RenderProperty(sb, parameter, data);
        }

        sb.Append('}');
        AppendRequired(sb, data.Parameters.Where(p => p.ItemsOf is null && p.Required).Select(p => p.Name));
        sb.Append('}');
        return sb.ToString();
    }

    private static void RenderProperty(StringBuilder sb, ParamData parameter, SchemaData data)
    {
        sb.Append("{\"type\":").Append(Quote(parameter.Type));

        if (parameter.Enum is { Length: > 0 })
        {
            sb.Append(",\"enum\":[");
            for (int i = 0; i < parameter.Enum.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Quote(parameter.Enum[i]));
            }

            sb.Append(']');
        }

        if (parameter.Description is not null)
            sb.Append(",\"description\":").Append(Quote(parameter.Description));

        if (parameter.Type == "array")
        {
            var items = data.Parameters.Where(p => p.ItemsOf == parameter.Name).ToArray();
            if (items.Length > 0)
            {
                sb.Append(",\"items\":{\"type\":\"object\",\"properties\":{");
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    sb.Append(Quote(items[i].Name)).Append(':');
                    RenderProperty(sb, items[i], data);
                }

                sb.Append('}');
                AppendRequired(sb, items.Where(p => p.Required).Select(p => p.Name));
                sb.Append('}');
            }
        }

        sb.Append('}');
    }

    private static void AppendRequired(StringBuilder sb, IEnumerable<string> names)
    {
        string[] required = names.ToArray();
        if (required.Length == 0)
            return;

        sb.Append(",\"required\":[");
        for (int i = 0; i < required.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(Quote(required[i]));
        }

        sb.Append(']');
    }

    /// <summary>A JSON string literal. The escaping matches the canonical form.</summary>
    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>The SHA-256 of the canonical schema, lower-case hex.</summary>
    private static string Digest(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>The pin file: one hash per tool.</summary>
    private static Dictionary<string, string> ParsePins(string text)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        int index = 0;
        while (index < text.Length)
        {
            int open = text.IndexOf('"', index);
            if (open < 0)
                break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0)
                break;
            string key = text.Substring(open + 1, close - open - 1);

            int colon = text.IndexOf(':', close);
            if (colon < 0)
                break;
            int valueOpen = text.IndexOf('"', colon);
            if (valueOpen < 0)
                break;
            int valueClose = text.IndexOf('"', valueOpen + 1);
            if (valueClose < 0)
                break;
            string value = text.Substring(valueOpen + 1, valueClose - valueOpen - 1);

            pins[key] = value;
            index = valueClose + 1;
        }

        return pins;
    }

    /// <summary>The source of one tool's schema.</summary>
    private static string Generate(SchemaData data, string canonical)
    {
        var sb = new StringBuilder();
        Line(sb, "// <auto-generated/>");
        Line(sb, "// SG4 (issue #1780): the parameter schema of a tool, rendered from its [ToolParam] declaration.");
        Line(sb, "#nullable enable");
        sb.Append("namespace ").Append(data.Namespace).Append(';').Append('\n');
        sb.Append("public ");
        if (data.IsSealed)
            sb.Append("sealed ");
        sb.Append("partial class ").Append(data.ClassName).Append('\n');
        sb.Append('{').Append('\n');
        Line(sb, "    private static readonly global::System.Text.Json.JsonDocument Schema =");
        if (canonical.Contains("\"\"\"", StringComparison.Ordinal) || canonical.EndsWith('"'))
        {
            // A raw string literal cannot hold three quotes in a row, and it
            // cannot end with one. The schema never does; if it ever does,
            // the emitted code falls back to an escaped literal rather than
            // failing to compile with a message about the generator.
            Line(sb, "        global::System.Text.Json.JsonDocument.Parse(" + QuoteCSharp(canonical) + ");");
        }
        else
        {
            Line(sb, "        global::System.Text.Json.JsonDocument.Parse(\"\"\"");
            Line(sb, "        " + canonical);
            Line(sb, "        \"\"\");");
        }

        Line(sb, "    public global::System.Text.Json.JsonDocument ParameterSchema => Schema;");
        sb.Append('}').Append('\n');
        return sb.ToString();
    }

    /// <summary>A C# string literal body for a text that may hold quotes.</summary>
    private static string QuoteCSharp(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>One line, always LF: the generated file does not depend on the host.</summary>
    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
