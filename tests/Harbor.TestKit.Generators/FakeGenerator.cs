using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.TestKit.Generators;

/// <summary>
///     Emits CONCRETE test doubles for the interfaces an assembly opts into with
///     <c>[assembly: GenerateFakes(typeof(IFoo), ...)]</c> (issue #1769).
/// </summary>
/// <remarks>
///     <para>
///         The double is a normal C# class — never a <c>DispatchProxy</c>, never a
///         Castle proxy. <c>DispatchProxy</c> is <c>[RequiresDynamicCode]</c>
///         (IL3050) and Castle emits IL at run time; both are banned by
///         <c>docs/ARCHITECTURE_LAYERS.md</c> §5.8 and by the AOT rules. A
///         generated class is ordinary code: AOT-safe, checked by the compiler,
///         and with no run-time dependency on the generator.
///     </para>
///     <para>
///         The opt-in marker is matched by METADATA NAME, not by a type
///         reference, so this Roslyn component needs no project reference to
///         <c>Harbor.TestKit</c> — the same convention
///         <c>src/Harbor.CodeGen/ToolGlueGenerator.cs</c> uses for
///         <c>ToolInfoAttribute</c>.
///     </para>
///     <para>
///         The shape of every emitted member:
///         <list type="bullet">
///             <item>
///                 properties are <c>virtual</c> and settable, so a test configures
///                 the ones it cares about and inherits the rest;
///             </item>
///             <item>
///                 methods are <c>virtual</c> and return a configurable
///                 <c>{Member}Value</c> / <c>{Member}Result</c> property;
///             </item>
///             <item>
///                 a member that returns a <c>Result</c> fails LOUDLY
///                 ("not configured by this test double") until it is configured —
///                 never a silent success, the direction
///                 <c>docs/PATTERNS.md</c> §6 requires of a default;
///             </item>
///             <item>
///                 every call is recorded in <c>RecordedCalls</c> (switch
///                 <c>RecordCalls</c> off in a hot loop);
///             </item>
///             <item>
///                 a default interface member is NOT re-implemented: the double
///                 inherits the interface's own default, which is the one the
///                 production code actually uses.
///             </item>
///         </list>
///     </para>
/// </remarks>
[Generator]
public sealed class FakeGenerator : IIncrementalGenerator
{
    /// <summary>
    ///     Metadata name of the opt-in marker. Matched by name (not by a type
    ///     reference) so this component needs no reference to Harbor.TestKit.
    ///     Must track <c>tests/Harbor.TestKit/GenerateFakesAttribute.cs</c>.
    /// </summary>
    internal const string MarkerAttributeName = "Harbor.TestKit.GenerateFakesAttribute";

    /// <summary>Namespace every generated double lives in.</summary>
    internal const string GeneratedNamespace = "Harbor.TestKit.Generated";

    /// <summary>Name of the private helper that yields an empty async stream.</summary>
    internal const string EmptyStreamHelperName = "GeneratedEmptyStream";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The opt-in list lives on the ASSEMBLY, so the compilation is the only
        // input that carries it. Everything downstream is a pure function of it,
        // which keeps the output deterministic: same compilation, same sources.
        var request = context.CompilationProvider.Select(static (compilation, _) => Analyze(compilation));

        context.RegisterSourceOutput(request, static (spc, data) => Emit(spc, data));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Analysis
    // ─────────────────────────────────────────────────────────────────────────

    private static FakeRequest Analyze(Compilation compilation)
    {
        var request = new FakeRequest();
        var opted = new HashSet<string>(StringComparer.Ordinal);

        foreach (AttributeData attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != MarkerAttributeName)
            {
                continue;
            }

            foreach (TypedConstant argument in attribute.ConstructorArguments)
            {
                // A `params Type[]` constructor reaches metadata as ONE array
                // argument, not one argument per element.
                if (argument.Kind == TypedConstantKind.Array)
                {
                    foreach (TypedConstant item in argument.Values)
                    {
                        AddFake(item, request, opted);
                    }

                    continue;
                }

                AddFake(argument, request, opted);
            }
        }

        return request;
    }

    private static void AddFake(TypedConstant argument, FakeRequest request, HashSet<string> opted)
    {
        if (argument.Kind != TypedConstantKind.Type || argument.Value is not INamedTypeSymbol symbol)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                FakeDiagnostics.NotAnInterface,
                Location.None,
                argument.ToString()));
            return;
        }

        string fullName = symbol.ToDisplayString();

        if (symbol.TypeKind != TypeKind.Interface)
        {
            request.Diagnostics.Add(Diagnostic.Create(FakeDiagnostics.NotAnInterface, Location.None, fullName));
            return;
        }

        if (!opted.Add(fullName))
        {
            request.Diagnostics.Add(Diagnostic.Create(FakeDiagnostics.DuplicateOptIn, Location.None, fullName));
            return;
        }

        GeneratedFake? fake = BuildFake(symbol, request.Diagnostics);
        if (fake is not null)
        {
            request.Fakes.Add(fake);
        }
    }

    private static GeneratedFake? BuildFake(INamedTypeSymbol symbol, List<Diagnostic> diagnostics)
    {
        if (symbol.IsGenericType || symbol.IsUnboundGenericType)
        {
            diagnostics.Add(Diagnostic.Create(
                FakeDiagnostics.UnsupportedShape,
                Location.None,
                symbol.ToDisplayString(),
                "a generic interface would need type parameters on the generated class"));
            return null;
        }

        var fake = new GeneratedFake
        {
            InterfaceName = symbol.ToDisplayString(),
            ClassName = "Fake" + StripLeadingI(symbol.Name),
        };

        // Every interface in the hierarchy contributes its abstract members; a
        // default interface member (one with a body) is skipped, because the
        // double must keep the interface's own default.
        var interfaces = new List<INamedTypeSymbol> { symbol };
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        CollectBaseInterfaces(symbol, interfaces, visited);

        var members = new List<ISymbol>();
        var memberKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (INamedTypeSymbol iface in interfaces)
        {
            foreach (ISymbol member in iface.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    {
                        if (!method.IsAbstract)
                        {
                            // Default interface member — inherited, not re-implemented.
                            continue;
                        }

                        if (method.IsStatic)
                        {
                            diagnostics.Add(Diagnostic.Create(
                                FakeDiagnostics.UnsupportedShape,
                                Location.None,
                                symbol.ToDisplayString(),
                                $"'{method.Name}' is a static abstract member, which a class cannot implement"));
                            return null;
                        }

                        if (method.RefKind != RefKind.None)
                        {
                            diagnostics.Add(Diagnostic.Create(
                                FakeDiagnostics.UnsupportedShape,
                                Location.None,
                                symbol.ToDisplayString(),
                                $"'{method.Name}' returns by reference, which the generator does not emit"));
                            return null;
                        }

                        if (!memberKeys.Add(MemberKey(method)))
                        {
                            continue;
                        }

                        members.Add(method);
                        break;
                    }

                    case IPropertySymbol property:
                    {
                        if (!IsAbstractMember(property))
                        {
                            // Default interface member — inherited, not re-implemented.
                            continue;
                        }

                        if (property.IsStatic)
                        {
                            diagnostics.Add(Diagnostic.Create(
                                FakeDiagnostics.UnsupportedShape,
                                Location.None,
                                symbol.ToDisplayString(),
                                $"'{property.Name}' is a static abstract member, which a class cannot implement"));
                            return null;
                        }

                        if (!memberKeys.Add(MemberKey(property)))
                        {
                            continue;
                        }

                        members.Add(property);
                        break;
                    }
                }
            }
        }

        // Deterministic order: the same interface always produces the same file.
        members = members
            .OrderBy(static m => m.Name, StringComparer.Ordinal)
            .ThenBy(static m => m.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(MemberKey, StringComparer.Ordinal)
            .ToList();

        var names = new HashSet<string>(StringComparer.Ordinal);

        // An overloaded method name would give two members the same configurable
        // value property (`PromptAsyncResult` twice). An overload therefore gets
        // NO configurable property: it returns the generated default inline, and
        // a test that needs a different answer overrides that overload.
        var overloads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (IGrouping<string, ISymbol> group in members
                     .Where(static m => m is IMethodSymbol)
                     .GroupBy(static m => m.Name, StringComparer.Ordinal))
        {
            overloads[group.Key] = group.Count();
        }

        foreach (ISymbol member in members)
        {
            switch (member)
            {
                case IMethodSymbol method:
                {
                    GeneratedMethod? generated = BuildMethod(method, symbol, diagnostics, overloads[method.Name] == 1);
                    if (generated is null)
                    {
                        return null;
                    }

                    // An overloaded method legitimately repeats its name; what must
                    // be unique is the configurable value property it generates.
                    bool nameTaken = !names.Add(generated.Name);
                    if (overloads[method.Name] == 1 && nameTaken)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            FakeDiagnostics.DuplicateMember,
                            Location.None,
                            symbol.ToDisplayString(),
                            generated.Name));
                        return null;
                    }

                    if (generated.ValuePropertyName is not null && !names.Add(generated.ValuePropertyName))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            FakeDiagnostics.DuplicateMember,
                            Location.None,
                            symbol.ToDisplayString(),
                            generated.ValuePropertyName));
                        return null;
                    }

                    fake.Methods.Add(generated);
                    break;
                }

                case IPropertySymbol property:
                {
                    GeneratedProperty generated = BuildProperty(property);
                    if (!names.Add(generated.Name))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            FakeDiagnostics.DuplicateMember,
                            Location.None,
                            symbol.ToDisplayString(),
                            generated.Name));
                        return null;
                    }

                    fake.Properties.Add(generated);
                    break;
                }
            }
        }

        if (fake.Methods.Any(static m => m.IsAsyncEnumerable))
        {
            if (!names.Add(EmptyStreamHelperName))
            {
                diagnostics.Add(Diagnostic.Create(
                    FakeDiagnostics.DuplicateMember,
                    Location.None,
                    symbol.ToDisplayString(),
                    EmptyStreamHelperName));
                return null;
            }

            fake.NeedsEmptyStream = true;
        }

        return fake;
    }

    private static void CollectBaseInterfaces(
        INamedTypeSymbol type,
        List<INamedTypeSymbol> collected,
        HashSet<INamedTypeSymbol> visited)
    {
        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (!visited.Add(iface))
            {
                continue;
            }

            collected.Add(iface);
            CollectBaseInterfaces(iface, collected, visited);
        }
    }

    /// <summary>
    ///     Is this property an ABSTRACT interface member, or a default interface
    ///     member (one with a body)? The property symbol's own
    ///     <c>IsAbstract</c> is not enough on its own — the accessors are what
    ///     carry the answer, and a default member's getter HAS a body.
    /// </summary>
    private static bool IsAbstractMember(IPropertySymbol property)
    {
        if (!property.IsAbstract)
        {
            return false;
        }

        if (property.GetMethod is { } getter && !getter.IsAbstract)
        {
            return false;
        }

        if (property.SetMethod is { } setter && !setter.IsAbstract)
        {
            return false;
        }

        return true;
    }

    private static string MemberKey(ISymbol member) => member switch
    {
        IMethodSymbol method => "M:" + method.Name + "(" + string.Join(
            ",",
            method.Parameters.Select(static p => Render(p.Type) + ":" + p.RefKind)) + ")",
        IPropertySymbol property => "P:" + property.Name,
        _ => member.Name,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Member shapes
    // ─────────────────────────────────────────────────────────────────────────

    private static GeneratedProperty BuildProperty(IPropertySymbol property) => new()
    {
        Name = EscapeIdentifier(property.Name),
        Type = Render(property.Type),
        DefaultValue = DefaultValueFor(property.Type, property.Name),
    };

    private static GeneratedMethod? BuildMethod(
        IMethodSymbol method,
        INamedTypeSymbol owner,
        List<Diagnostic> diagnostics,
        bool configurable)
    {
        var generated = new GeneratedMethod { Name = EscapeIdentifier(method.Name) };

        // Generic method: copy the type parameters and their constraints.
        if (method.TypeParameters.Length > 0)
        {
            generated.TypeParameterList = "<" + string.Join(", ", method.TypeParameters.Select(static t => t.Name)) + ">";

            var constraints = new List<string>();
            foreach (ITypeParameterSymbol typeParameter in method.TypeParameters)
            {
                string rendered = RenderConstraints(typeParameter);
                if (rendered.Length > 0)
                {
                    constraints.Add(rendered);
                }
            }

            generated.Constraints = constraints.Count == 0 ? string.Empty : " " + string.Join(" ", constraints);
        }

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.Type is IPointerTypeSymbol)
            {
                diagnostics.Add(Diagnostic.Create(
                    FakeDiagnostics.UnsupportedShape,
                    Location.None,
                    owner.ToDisplayString(),
                    $"'{method.Name}' takes a pointer parameter, which the generator does not emit"));
                return null;
            }

            generated.Parameters.Add(new GeneratedParameter
            {
                Name = EscapeIdentifier(parameter.Name),
                Type = Render(parameter.Type),
                RefKind = parameter.RefKind,
                IsParams = parameter.IsParams,
                IsValueType = parameter.Type.IsValueType,
                IsCancellationToken = IsCancellationToken(parameter.Type),
                DefaultValue = RenderDefaultValue(parameter),
            });
        }

        // The return type decides what the configurable value property looks like.
        ITypeSymbol returnType = method.ReturnType;

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            generated.ReturnType = "void";
            generated.Body = string.Empty;
            return generated;
        }

        if (IsTask(returnType))
        {
            if (returnType is INamedTypeSymbol { Arity: 1 } task)
            {
                ITypeSymbol value = task.TypeArguments[0];
                generated.ReturnType = "global::System.Threading.Tasks.Task<" + Render(value) + ">";
                if (configurable)
                {
                    generated.ValuePropertyType = Render(value);
                    generated.ValuePropertyName = ValuePropertyName(method.Name, value);
                    generated.ValuePropertyDefault = DefaultValueFor(value, method.Name);
                    generated.Body = "return global::System.Threading.Tasks.Task.FromResult(" + generated.ValuePropertyName + ");";
                }
                else
                {
                    generated.Body = "return global::System.Threading.Tasks.Task.FromResult(" + DefaultValueFor(value, method.Name) + ");";
                }

                return generated;
            }

            generated.ReturnType = "global::System.Threading.Tasks.Task";
            generated.Body = "return global::System.Threading.Tasks.Task.CompletedTask;";
            return generated;
        }

        if (IsValueTask(returnType))
        {
            if (returnType is INamedTypeSymbol { Arity: 1 } valueTask)
            {
                ITypeSymbol value = valueTask.TypeArguments[0];
                generated.ReturnType = "global::System.Threading.Tasks.ValueTask<" + Render(value) + ">";
                if (configurable)
                {
                    generated.ValuePropertyType = Render(value);
                    generated.ValuePropertyName = ValuePropertyName(method.Name, value);
                    generated.ValuePropertyDefault = DefaultValueFor(value, method.Name);
                    generated.Body = "return new global::System.Threading.Tasks.ValueTask<" + generated.ValuePropertyType + ">(" + generated.ValuePropertyName + ");";
                }
                else
                {
                    generated.Body = "return new global::System.Threading.Tasks.ValueTask<" + Render(value) + ">(" + DefaultValueFor(value, method.Name) + ");";
                }

                return generated;
            }

            generated.ReturnType = "global::System.Threading.Tasks.ValueTask";
            generated.Body = "return global::System.Threading.Tasks.ValueTask.CompletedTask;";
            return generated;
        }

        if (IsAsyncEnumerable(returnType))
        {
            generated.IsAsyncEnumerable = true;
            generated.ReturnType = Render(returnType);
            if (configurable)
            {
                generated.ValuePropertyType = Render(returnType);
                generated.ValuePropertyName = ValuePropertyName(method.Name, returnType);
                generated.ValuePropertyDefault = DefaultValueFor(returnType, method.Name);
                generated.Body = "return " + generated.ValuePropertyName + ";";
            }
            else
            {
                generated.Body = "return " + DefaultValueFor(returnType, method.Name) + ";";
            }

            return generated;
        }

        // Everything else is returned as-is: a value, a disposable, a struct.
        // The configurable property holds the value itself.
        generated.ReturnType = Render(returnType);
        if (configurable)
        {
            generated.ValuePropertyType = Render(returnType);
            generated.ValuePropertyName = ValuePropertyName(method.Name, returnType);
            generated.ValuePropertyDefault = DefaultValueFor(returnType, method.Name);
            generated.Body = "return " + generated.ValuePropertyName + ";";
        }
        else
        {
            generated.Body = "return " + DefaultValueFor(returnType, method.Name) + ";";
        }

        return generated;
    }

    private static string ValuePropertyName(string memberName, ITypeSymbol valueType) =>
        IsCfeResult(valueType) || IsCfeResultOf(valueType, out _)
            ? memberName + "Result"
            : memberName + "Value";

    private static string RenderConstraints(ITypeParameterSymbol typeParameter)
    {
        var parts = new List<string>();

        if (typeParameter.HasUnmanagedTypeConstraint)
        {
            parts.Add("unmanaged");
        }
        else if (typeParameter.HasValueTypeConstraint)
        {
            parts.Add("struct");
        }
        else if (typeParameter.HasReferenceTypeConstraint)
        {
            parts.Add("class");
        }

        foreach (ITypeSymbol constraint in typeParameter.ConstraintTypes)
        {
            parts.Add(Render(constraint));
        }

        // "notnull" only when nothing else says it: a type constraint already
        // implies it, and spelling both out is noise the compiler may flag.
        if (typeParameter.HasNotNullConstraint && typeParameter.ConstraintTypes.Length == 0)
        {
            parts.Add("notnull");
        }

        if (typeParameter.HasConstructorConstraint)
        {
            parts.Add("new()");
        }

        return parts.Count == 0 ? string.Empty : "where " + typeParameter.Name + " : " + string.Join(", ", parts);
    }

    private static string RenderDefaultValue(IParameterSymbol parameter)
    {
        if (!parameter.IsOptional)
        {
            return string.Empty;
        }

        object? value = parameter.ExplicitDefaultValue;

        if (value is null)
        {
            // A reference type with no explicit value defaults to null; a value
            // type defaults to its zero. `default` covers both without lying.
            return parameter.Type.IsValueType ? " = default" : " = null";
        }

        ITypeSymbol type = parameter.Type;
        bool nullableValueType = type is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
        };

        // A struct whose default is not a literal we can render (CancellationToken,
        // DateTimeOffset, a struct of structs) is rendered as `default` — always
        // correct, never a wrong literal.
        if (type.IsValueType && !nullableValueType && type.SpecialType == SpecialType.None && type.TypeKind != TypeKind.Enum)
        {
            return " = default";
        }

        return " = " + RenderConstant(value);
    }

    private static string RenderConstant(object value) => value switch
    {
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        char character => "'" + character + "'",
        bool flag => flag ? "true" : "false",
        Enum enumeration => RenderEnum(enumeration),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => "default",
    };

    private static string RenderEnum(Enum value)
    {
        Type enumType = value.GetType();
        // A nested enum's FullName uses '+', which is not valid C# in a
        // qualified name — the source form uses '.'.
        string typeName = (enumType.FullName ?? enumType.Name).Replace('+', '.');
        foreach (string name in Enum.GetNames(enumType))
        {
            if (Equals(Enum.Parse(enumType, name), value))
            {
                return $"global::{typeName}.{name}";
            }
        }

        return "default";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Type helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static string Render(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static bool IsTask(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Task", Arity: 1 or 0 } named
        && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

    private static bool IsValueTask(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "ValueTask", Arity: 1 or 0 } named
        && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

    private static bool IsAsyncEnumerable(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "IAsyncEnumerable", Arity: 1 } named
        && named.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    private static bool IsCancellationToken(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "CancellationToken", Arity: 0 } named
        && named.ContainingNamespace.ToDisplayString() == "System.Threading";

    private static bool IsCfeResult(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Result", Arity: 0 } named
        && named.ContainingNamespace.ToDisplayString() == "CSharpFunctionalExtensions";

    private static bool IsCfeResultOf(ITypeSymbol type, out ITypeSymbol argument)
    {
        argument = null!;
        if (type is not INamedTypeSymbol { Name: "Result", Arity: 1 } named
            || named.ContainingNamespace.ToDisplayString() != "CSharpFunctionalExtensions")
        {
            return false;
        }

        argument = named.TypeArguments[0];
        return true;
    }

    private static bool IsCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
        {
            return true;
        }

        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        return named.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IReadOnlyList<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.List<T>";
    }

    /// <summary>
    ///     The default value of a generated member.
    /// </summary>
    /// <remarks>
    ///     A <c>Result</c> fails loudly: <c>default(Result&lt;T&gt;)</c> is a
    ///     SUCCESS carrying a null value, which is the worst possible default —
    ///     it looks like the member worked and hands the caller a null. The
    ///     generated default is therefore an explicit failure.
    /// </remarks>
    internal static string DefaultValueFor(ITypeSymbol type, string label)
    {
        if (IsCfeResult(type))
        {
            return "global::CSharpFunctionalExtensions.Result.Failure(\"" + label + " is not configured by this test double.\")";
        }

        if (IsCfeResultOf(type, out ITypeSymbol argument))
        {
            return "global::CSharpFunctionalExtensions.Result.Failure<" + Render(argument)
                + ">(\"" + label + " is not configured by this test double.\")";
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return "string.Empty";
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return "false";
        }

        if (type is INamedTypeSymbol named)
        {
            switch (named.OriginalDefinition.ToDisplayString())
            {
                case "System.Text.Json.JsonDocument":
                    return "global::System.Text.Json.JsonDocument.Parse(\"{\\\"type\\\":\\\"object\\\"}\")";

                case "System.Threading.Channels.Channel<T>":
                    return "global::System.Threading.Channels.Channel.CreateUnbounded<"
                        + Render(named.TypeArguments[0]) + ">()";

                case "System.IDisposable":
                    return "global::Harbor.TestKit.Generated.FakeDisposable.Instance";

                case "System.Collections.Generic.IAsyncEnumerable<T>":
                    return EmptyStreamHelperName + "<" + Render(named.TypeArguments[0]) + ">()";

                // #557: a tool MUST declare a safety profile, and the policy
                // derivation reads it unguarded — a null profile would turn a
                // double into an NRE inside the guard it is supposed to feed.
                case "Harbor.Abstractions.Permissions.ToolSafetyProfile":
                    return "global::Harbor.Abstractions.Permissions.ToolSafetyProfile.Opaque";
            }
        }

        if (IsCollection(type))
        {
            return "[]";
        }

        if (type.IsReferenceType)
        {
            return "null!";
        }

        return "default";
    }

    /// <summary>
    ///     The C# RESERVED keywords. A parameter or member whose name is one of
    ///     them (the interface writes it as <c>@event</c>) has to be escaped
    ///     again in the emitted code — <c>ref AgentEvent event</c> would not
    ///     compile.
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    private static string EscapeIdentifier(string name) =>
        ReservedKeywords.Contains(name) ? "@" + name : name;

    private static string StripLeadingI(string name) =>
        name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

    // ─────────────────────────────────────────────────────────────────────────
    // Emission
    // ─────────────────────────────────────────────────────────────────────────

    private static void Emit(SourceProductionContext spc, FakeRequest request)
    {
        foreach (Diagnostic diagnostic in request.Diagnostics)
        {
            spc.ReportDiagnostic(diagnostic);
        }

        foreach (GeneratedFake fake in request.Fakes)
        {
            spc.AddSource(fake.ClassName + ".g.cs", SourceText.From(RenderFake(fake), Encoding.UTF8));
        }
    }

    private static string RenderFake(GeneratedFake fake)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// FakeGenerator (Harbor.TestKit.Generators) — DO NOT EDIT. Issue #1769.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace " + GeneratedNamespace + ";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("///     Generated test double for <c>" + fake.InterfaceName + "</c>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("/// <remarks>");
        sb.AppendLine("///     <para>");
        sb.AppendLine("///         Every member is <c>virtual</c>: a test overrides the ones it needs and");
        sb.AppendLine("///         sets the <c>*Value</c> / <c>*Result</c> properties for the rest.");
        sb.AppendLine("///     </para>");
        sb.AppendLine("///     <para>");
        sb.AppendLine("///         A member that returns a <c>Result</c> fails loudly until it is");
        sb.AppendLine("///         configured — the default is \"not configured by this test double\",");
        sb.AppendLine("///         never a silent success.");
        sb.AppendLine("///     </para>");
        sb.AppendLine("/// </remarks>");
        sb.AppendLine("public partial class " + fake.ClassName + " : global::" + fake.InterfaceName);
        sb.AppendLine("{");

        sb.AppendLine("    private readonly List<global::Harbor.TestKit.Generated.FakeCall> _calls = [];");
        sb.AppendLine("    private readonly object _callsLock = new();");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Whether calls are recorded in <see cref=\"RecordedCalls\" />. Turn it off in a hot loop.</summary>");
        sb.AppendLine("    public bool RecordCalls { get; set; } = true;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Every call made on this double, in order.</summary>");
        sb.AppendLine("    public global::System.Collections.Generic.IReadOnlyList<global::Harbor.TestKit.Generated.FakeCall> RecordedCalls");
        sb.AppendLine("    {");
        sb.AppendLine("        get");
        sb.AppendLine("        {");
        sb.AppendLine("            lock (_callsLock)");
        sb.AppendLine("            {");
        sb.AppendLine("                return [.. _calls];");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Records one call. An override that needs the log records it too.</summary>");
        sb.AppendLine("    protected void RecordCall(string member, params object?[] arguments)");
        sb.AppendLine("    {");
        sb.AppendLine("        lock (_callsLock)");
        sb.AppendLine("        {");
        sb.AppendLine("            _calls.Add(new global::Harbor.TestKit.Generated.FakeCall(member, arguments));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        foreach (GeneratedProperty property in fake.Properties)
        {
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Generated default for <c>" + property.Name + "</c>.</summary>");
            sb.AppendLine("    public virtual " + property.Type + " " + property.Name + " { get; set; } = " + property.DefaultValue + ";");
        }

        foreach (GeneratedMethod method in fake.Methods)
        {
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Generated implementation of <c>" + method.Name + "</c>.</summary>");
            sb.AppendLine("    public virtual " + method.ReturnType + " " + method.Name
                + method.TypeParameterList + "(" + RenderParameters(method) + ")" + method.Constraints);
            sb.AppendLine("    {");

            foreach (GeneratedParameter parameter in method.Parameters)
            {
                if (parameter.RefKind == RefKind.Out)
                {
                    sb.AppendLine("        " + parameter.Name + " = " + (parameter.IsValueType ? "default" : "null!") + ";");
                }
            }

            sb.AppendLine("        if (RecordCalls)");
            sb.AppendLine("        {");
            sb.AppendLine("            RecordCall(nameof(" + method.Name + ")" + RenderRecordedArguments(method) + ");");
            sb.AppendLine("        }");
            sb.AppendLine();

            if (method.Body.Length > 0)
            {
                sb.AppendLine("        " + method.Body);
            }

            sb.AppendLine("    }");

            if (method.ValuePropertyName is not null)
            {
                sb.AppendLine();
                sb.AppendLine("    /// <summary>Configurable return value of <see cref=\"" + method.Name + "\" />.</summary>");
                sb.AppendLine("    public virtual " + method.ValuePropertyType + " " + method.ValuePropertyName
                    + " { get; set; } = " + method.ValuePropertyDefault + ";");
            }
        }

        if (fake.NeedsEmptyStream)
        {
            sb.AppendLine();
            sb.AppendLine("    private static async global::System.Collections.Generic.IAsyncEnumerable<T> " + EmptyStreamHelperName + "<T>()");
            sb.AppendLine("    {");
            sb.AppendLine("        await global::System.Threading.Tasks.Task.CompletedTask;");
            sb.AppendLine("        yield break;");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string RenderParameters(GeneratedMethod method)
    {
        var parts = new List<string>();

        foreach (GeneratedParameter parameter in method.Parameters)
        {
            var sb = new StringBuilder();

            if (parameter.IsParams)
            {
                sb.Append("params ");
            }

            switch (parameter.RefKind)
            {
                case RefKind.Ref:
                    sb.Append("ref ");
                    break;
                case RefKind.Out:
                    sb.Append("out ");
                    break;
                case RefKind.In:
                    sb.Append("in ");
                    break;
            }

            sb.Append(parameter.Type);
            sb.Append(' ');
            sb.Append(parameter.Name);
            sb.Append(parameter.DefaultValue);

            parts.Add(sb.ToString());
        }

        return string.Join(", ", parts);
    }

    private static string RenderRecordedArguments(GeneratedMethod method)
    {
        var parts = new List<string>();

        foreach (GeneratedParameter parameter in method.Parameters)
        {
            // A CancellationToken is noise in a call log, and an `out` parameter
            // cannot be read before it is assigned.
            if (parameter.RefKind == RefKind.Out || parameter.IsCancellationToken)
            {
                continue;
            }

            parts.Add(parameter.Name);
        }

        return parts.Count == 0 ? string.Empty : ", " + string.Join(", ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Model
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class FakeRequest
    {
        public List<GeneratedFake> Fakes { get; } = [];

        public List<Diagnostic> Diagnostics { get; } = [];
    }

    private sealed class GeneratedFake
    {
        public string InterfaceName { get; init; } = string.Empty;

        public string ClassName { get; init; } = string.Empty;

        public List<GeneratedProperty> Properties { get; } = [];

        public List<GeneratedMethod> Methods { get; } = [];

        public bool NeedsEmptyStream { get; set; }
    }

    private sealed class GeneratedProperty
    {
        public string Name { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string DefaultValue { get; init; } = string.Empty;
    }

    private sealed class GeneratedMethod
    {
        public string Name { get; init; } = string.Empty;

        public string ReturnType { get; set; } = string.Empty;

        public string TypeParameterList { get; set; } = string.Empty;

        public string Constraints { get; set; } = string.Empty;

        public List<GeneratedParameter> Parameters { get; } = [];

        public string? ValuePropertyName { get; set; }

        public string? ValuePropertyType { get; set; }

        public string? ValuePropertyDefault { get; set; }

        public string Body { get; set; } = string.Empty;

        public bool IsAsyncEnumerable { get; set; }
    }

    private sealed class GeneratedParameter
    {
        public string Name { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public RefKind RefKind { get; init; }

        public bool IsParams { get; init; }

        public bool IsValueType { get; init; }

        public bool IsCancellationToken { get; init; }

        public string DefaultValue { get; init; } = string.Empty;
    }
}
