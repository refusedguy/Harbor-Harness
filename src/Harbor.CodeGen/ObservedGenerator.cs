using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.CodeGen;

/// <summary>
///     Emits the decorator behind the <c>[Observed]</c> marker (issue #1797).
/// </summary>
/// <remarks>
///     <para>
///         The decorator is ordinary C#: a sealed class that implements the
///         observed interface and forwards every member to the wrapped
///         instance, opening a span and recording the call's metrics around
///         the members the policy marks. There is no reflection and no emit
///         anywhere in it, so the mechanism is AOT-safe by construction: the
///         code is compiled, not produced at run time.
///     </para>
///     <para>
///         The marker is matched by METADATA NAME, not by a type reference, so
///         this Roslyn component needs no project reference to
///         <c>Harbor.Abstractions.Contracts</c> — the same convention
///         <c>ToolGlueGenerator</c> uses for <c>ToolInfoAttribute</c>.
///     </para>
///     <para>
///         The policy order is the contract in
///         <c>docs/OBSERVED_INSTRUMENTATION.md</c>: the member's own marker
///         wins, then the declaring type's, then the nearest namespace policy,
///         then the assembly policy. Exactly one statement governs a member and
///         nothing is merged across levels, so what a seam emits is readable
///         from the source alone.
///     </para>
/// </remarks>
[Generator]
public sealed class ObservedGenerator : IIncrementalGenerator
{
    /// <summary>
    ///     Metadata name of the marker. Matched by name (not by a type
    ///     reference) so this component needs no reference to
    ///     Harbor.Abstractions.Contracts. Must track
    ///     src/Harbor.Abstractions.Contracts/ObservedAttribute.cs.
    /// </summary>
    internal const string ObservedAttributeName = "Harbor.Abstractions.Contracts.ObservedAttribute";

    /// <summary>
    ///     Metadata name of the coarse policy marker. Must track
    ///     src/Harbor.Abstractions.Contracts/ObservedAttribute.cs.
    /// </summary>
    internal const string PolicyAttributeName = "Harbor.Abstractions.Contracts.ObservedPolicyAttribute";

    /// <summary>
    ///     The MSBuild property that turns the whole mechanism off. It is read
    ///     through <c>AnalyzerConfigOptionsProvider.GlobalOptions</c>; when the
    ///     property is absent the mechanism is ON, and when it is
    ///     <c>false</c> the generator emits NOTHING — no decorator, no
    ///     instrument, no span call.
    /// </summary>
    /// <remarks>
    ///     The name is public so that the guard can hand the property to the
    ///     generator in-process, instead of repeating the string in the test
    ///     and letting the two drift apart.
    /// </remarks>
    public const string SwitchPropertyName = "build_property.HarborWithObservedInstrumentation";

    /// <summary>
    ///     The line separator of the emitted file. It is spelled out rather
    ///     than taken from <see cref="Environment.NewLine" /> so the same
    ///     compilation produces byte-identical output on every machine.
    /// </summary>
    private const string NewLine = "\n";

    /// <summary>
    ///     The scopes the marker can ask for. A COPY of
    ///     <c>Harbor.Abstractions.Contracts.ObservedScope</c>: the generator
    ///     binds no type from the contracts assembly, so it carries the values
    ///     and reads the member NAME off the attribute's own enum symbol.
    /// </summary>
    private enum ObservedScope
    {
        None = 0,
        Span = 1,
        Metrics = 2,
        SpanAndMetrics = 3,
    }

    /// <summary>Which level of the policy order a statement came from.</summary>
    private enum StatementLevel
    {
        Member,
        Type,
        Policy,
    }

    /// <summary>How a call is made: the shape decides what the body looks like.</summary>
    private enum CallShape
    {
        /// <summary>Not a task: the call is made and its value taken directly.</summary>
        Sync,

        /// <summary>A task with no value: the call is awaited and nothing comes back.</summary>
        Await,

        /// <summary>A task with a value: the call is awaited and the value is taken.</summary>
        AwaitValue,
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The whole request is a pure function of the compilation and of the
        // switch, so the same inputs always produce the same output.
        var request = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) => IsEnabled(options.GlobalOptions)))
            .Select(static (pair, _) => Analyze(pair.Left, pair.Right));

        context.RegisterSourceOutput(request, static (spc, data) => Emit(spc, data));
    }

    /// <summary>
    ///     Is the mechanism on? Absent means ON: the property is a switch that
    ///     turns the mechanism OFF, and a project that never mentions it gets
    ///     the decorators.
    /// </summary>
    private static bool IsEnabled(AnalyzerConfigOptions options) =>
        !options.TryGetValue(SwitchPropertyName, out string? value)
        || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Analysis
    // ─────────────────────────────────────────────────────────────────────────

    private static ObservedRequest Analyze(Compilation compilation, bool enabled)
    {
        var request = new ObservedRequest();
        if (!enabled)
        {
            return request;
        }

        ObservedStatement? assemblyPolicy = null;
        foreach (AttributeData attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == PolicyAttributeName)
            {
                assemblyPolicy = ReadPolicy(attribute);
            }
        }

        List<INamedTypeSymbol> own = CollectOwnTypes(compilation);
        List<INamedTypeSymbol> referenced = CollectReferencedTypes(compilation);
        List<INamedTypeSymbol> all = own.Concat(referenced).ToList();

        Dictionary<string, ObservedStatement> namespacePolicies = new(StringComparer.Ordinal);
        foreach (INamedTypeSymbol type in all)
        {
            AttributeData? policy = FindAttribute(type, PolicyAttributeName);
            if (policy is null)
            {
                continue;
            }

            ObservedStatement statement = ReadPolicy(policy);
            string ns = NamespaceOf(type);

            if (namespacePolicies.TryGetValue(ns, out ObservedStatement existing))
            {
                if (!existing.SameAs(statement))
                {
                    request.Diagnostics.Add(Diagnostic.Create(
                        ObservedDiagnostics.PolicyConflict,
                        AttributeLocation(policy),
                        ns,
                        existing.Describe(),
                        statement.Describe()));
                }

                continue;
            }

            namespacePolicies[ns] = statement;
        }

        foreach (INamedTypeSymbol type in all)
        {
            BuildDecorator(type, own, namespacePolicies, assemblyPolicy, request);
        }

        // Deterministic order: the same tree always emits the same files in the
        // same order.
        request.Decorators.Sort(static (left, right) => string.CompareOrdinal(left.HintName, right.HintName));
        return request;
    }

    /// <summary>
    ///     The types THIS compilation declares. A seam in this assembly is
    ///     decorated here; a seam in another assembly is decorated there.
    /// </summary>
    private static List<INamedTypeSymbol> CollectOwnTypes(Compilation compilation)
    {
        var found = new List<INamedTypeSymbol>();
        WalkNamespace(compilation.Assembly.GlobalNamespace, publicOnly: false, found);
        return found;
    }

    /// <summary>
    ///     The types of the assemblies this compilation references.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only HARBOR assemblies are walked. The seams live in the
    ///         contract layer, and walking the whole BCL reference set costs
    ///         seconds of build time for types no seam can be in.
    ///     </para>
    ///     <para>
    ///         A seam in a third-party assembly is therefore not decorated.
    ///         That limit is deliberate and stated here rather than left to be
    ///         discovered.
    ///     </para>
    /// </remarks>
    private static List<INamedTypeSymbol> CollectReferencedTypes(Compilation compilation)
    {
        var found = new List<INamedTypeSymbol>();
        foreach (MetadataReference reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }

            string? name = assembly.Identity.Name;
            if (name is null || !name.StartsWith("Harbor.", StringComparison.Ordinal))
            {
                continue;
            }

            WalkNamespace(assembly.GlobalNamespace, publicOnly: true, found);
        }

        return found;
    }

    private static void WalkNamespace(INamespaceSymbol ns, bool publicOnly, List<INamedTypeSymbol> found)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            if (!publicOnly || type.DeclaredAccessibility == Accessibility.Public)
            {
                found.Add(type);
            }

            WalkNestedTypes(type, publicOnly, found);
        }

        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
        {
            WalkNamespace(child, publicOnly, found);
        }
    }

    private static void WalkNestedTypes(INamedTypeSymbol type, bool publicOnly, List<INamedTypeSymbol> found)
    {
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            if (!publicOnly || nested.DeclaredAccessibility == Accessibility.Public)
            {
                found.Add(nested);
            }

            WalkNestedTypes(nested, publicOnly, found);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The decorator
    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildDecorator(
        INamedTypeSymbol type,
        List<INamedTypeSymbol> ownTypes,
        Dictionary<string, ObservedStatement> namespacePolicies,
        ObservedStatement? assemblyPolicy,
        ObservedRequest request)
    {
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Delegate)
        {
            if (FindAttribute(type, ObservedAttributeName) is not null)
            {
                request.Diagnostics.Add(Diagnostic.Create(
                    ObservedDiagnostics.ClassSeamNotInstrumented,
                    LocationOf(type),
                    type.ToDisplayString()));
            }

            return;
        }

        if (type.TypeKind != TypeKind.Interface)
        {
            return;
        }

        ObservedStatement? typeStatement = null;
        if (FindAttribute(type, ObservedAttributeName) is { } typeAttribute)
        {
            typeStatement = ReadObserved(typeAttribute, StatementLevel.Type);
            if (typeStatement.contradicts)
            {
                request.Diagnostics.Add(Diagnostic.Create(
                    ObservedDiagnostics.ScopeContradiction,
                    AttributeLocation(typeAttribute),
                    type.ToDisplayString(),
                    typeStatement.contradiction));
            }
        }

        HashSet<string> excluded = typeStatement is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(typeStatement.Exclude, StringComparer.Ordinal);

        var members = new List<MemberPlan>();
        bool anyInstrumented = false;

        foreach (ISymbol member in OrderedMembers(type))
        {
            (MemberPlan? planned, bool fatal) = PlanMember(member, type, typeStatement, excluded, namespacePolicies, assemblyPolicy, request);
            if (fatal)
            {
                // A shape the generator refuses to express: the seam is left
                // alone and the diagnostic says why.
                return;
            }

            if (planned is not { } plan)
            {
                // A member the decorator inherits rather than implements.
                continue;
            }

            if (plan.IsInstrumented)
            {
                anyInstrumented = true;
            }

            members.Add(plan);
        }

        if (!anyInstrumented)
        {
            // No member is observed, so there is nothing to decorate.
            return;
        }

        if (type.IsGenericType)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.UnsupportedShape,
                LocationOf(type),
                type.ToDisplayString(),
                "a generic interface would need type parameters on the generated decorator"));
            return;
        }

        string className = "Observed" + StripLeadingI(type.Name);
        string ns = NamespaceOf(type);
        foreach (INamedTypeSymbol candidate in ownTypes)
        {
            if (candidate.Name == className && NamespaceOf(candidate) == ns)
            {
                request.Diagnostics.Add(Diagnostic.Create(
                    ObservedDiagnostics.NameCollision,
                    LocationOf(type),
                    className,
                    ns,
                    type.ToDisplayString()));
                return;
            }
        }

        string text = RenderDecorator(type, className, ns, members);
        string hint = "Observed." + ns.Replace('.', '_') + "_" + type.Name + ".g.cs";
        request.Decorators.Add(new DecoratorPlan(hint, text));
    }

    /// <summary>
    ///     The members of the interface and of every interface it inherits,
    ///     de-duplicated and in a stable order.
    /// </summary>
    private static List<ISymbol> OrderedMembers(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<ISymbol>();
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();
        queue.Enqueue(type);

        while (queue.Count > 0)
        {
            INamedTypeSymbol current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (ISymbol member in current.GetMembers())
            {
                if (seen.Add(MemberKey(member)))
                {
                    members.Add(member);
                }
            }

            foreach (INamedTypeSymbol baseInterface in current.AllInterfaces)
            {
                queue.Enqueue(baseInterface);
            }
        }

        members.Sort(static (left, right) =>
        {
            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(MemberKey(left), MemberKey(right));
        });

        return members;
    }

    private static string MemberKey(ISymbol member) => member switch
    {
        IMethodSymbol method => "M:" + method.Name + "(" + string.Join(
            ",",
            method.Parameters.Select(static p => Render(p.Type) + ":" + p.RefKind)) + ")",
        IPropertySymbol property => "P:" + property.Name,
        IEventSymbol @event => "E:" + @event.Name,
        _ => member.Name,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // One member
    // ─────────────────────────────────────────────────────────────────────────

    private static (MemberPlan? Plan, bool Fatal) PlanMember(
        ISymbol member,
        INamedTypeSymbol owner,
        ObservedStatement? typeStatement,
        HashSet<string> excluded,
        Dictionary<string, ObservedStatement> namespacePolicies,
        ObservedStatement? assemblyPolicy,
        ObservedRequest request)
    {
        switch (member)
        {
            case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                return PlanMethod(method, owner, typeStatement, excluded, namespacePolicies, assemblyPolicy, request);

            case IPropertySymbol property:
                return PlanProperty(property, owner, typeStatement, excluded, namespacePolicies, assemblyPolicy, request);

            case IEventSymbol @event:
                return PlanEvent(@event, owner, typeStatement, excluded, namespacePolicies, assemblyPolicy, request);

            default:
                return (null, false);
        }
    }

    private static (MemberPlan? Plan, bool Fatal) PlanMethod(
        IMethodSymbol method,
        INamedTypeSymbol owner,
        ObservedStatement? typeStatement,
        HashSet<string> excluded,
        Dictionary<string, ObservedStatement> namespacePolicies,
        ObservedStatement? assemblyPolicy,
        ObservedRequest request)
    {
        if (!method.IsAbstract)
        {
            // A default interface member keeps the interface's own body: the
            // decorator inherits it instead of replacing it.
            return (null, false);
        }

        if (method.IsStatic)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.UnsupportedShape,
                LocationOf(method),
                method.ToDisplayString(),
                "a static abstract member cannot be implemented by a class"));
            return (null, true);
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.UnsupportedShape,
                LocationOf(method),
                method.ToDisplayString(),
                "a member that returns by reference is not implemented by the generator"));
            return (null, true);
        }

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.Type is IPointerTypeSymbol)
            {
                request.Diagnostics.Add(Diagnostic.Create(
                    ObservedDiagnostics.UnsupportedShape,
                    LocationOf(method),
                    method.ToDisplayString(),
                    $"'{parameter.Name}' is a pointer parameter, which the generator does not emit"));
                return (null, true);
            }
        }

        if (FindAttribute(method, ObservedAttributeName) is { } ownAttribute)
        {
            ObservedStatement own = ReadObserved(ownAttribute, StatementLevel.Member);
            if (own.contradicts)
            {
                request.Diagnostics.Add(Diagnostic.Create(
                    ObservedDiagnostics.ScopeContradiction,
                    AttributeLocation(ownAttribute),
                    method.ToDisplayString(),
                    own.contradiction));
                return (null, true);
            }
        }

        ObservedStatement? statement = ResolveStatement(method, owner, typeStatement, excluded, namespacePolicies, assemblyPolicy);
        string declaration = RenderMethodDeclaration(method);

        if (statement is null || statement.Scope == ObservedScope.None)
        {
            return (Forwarded(declaration, RenderCall(method)), false);
        }

        bool hasReferenceParameter = HasReferenceParameter(method);
        bool lazyReturn = IsLazyReturn(method.ReturnType);
        if (hasReferenceParameter || lazyReturn)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.NotInstrumented,
                LocationOf(method),
                method.ToDisplayString(),
                hasReferenceParameter
                    ? "a member with a ref, out or in parameter is forwarded as it is"
                    : "a member that returns a lazy sequence is forwarded as it is, because the span would close before the sequence is read"));

            return (Forwarded(declaration, RenderCall(method)), false);
        }

        Instrument instrument = BuildInstrument(owner, method, statement);
        string body = RenderInstrumentedBody(method, instrument);
        return (new MemberPlan
        {
            Text = declaration + NewLine + "    {" + NewLine + body + "    }",
            IsInstrumented = true,
            UsesTracer = instrument.EmitsSpan,
            UsesMetrics = instrument.EmitsMetrics,
        }, false);
    }

    private static MemberPlan Forwarded(string declaration, string call) => new()
    {
        Text = declaration + NewLine + "    {" + NewLine + "        " + call + NewLine + "    }",
        IsInstrumented = false,
    };

    private static (MemberPlan? Plan, bool Fatal) PlanProperty(
        IPropertySymbol property,
        INamedTypeSymbol owner,
        ObservedStatement? typeStatement,
        HashSet<string> excluded,
        Dictionary<string, ObservedStatement> namespacePolicies,
        ObservedStatement? assemblyPolicy,
        ObservedRequest request)
    {
        if (!IsAbstractProperty(property))
        {
            return (null, false);
        }

        if (property.IsStatic)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.UnsupportedShape,
                LocationOf(property),
                property.ToDisplayString(),
                "a static abstract member cannot be implemented by a class"));
            return (null, true);
        }

        if (property.SetMethod is { IsInit: true })
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.UnsupportedShape,
                LocationOf(property),
                property.ToDisplayString(),
                "an init-only property is not implemented by the generator"));
            return (null, true);
        }

        string type = Render(property.Type);
        string name = EscapeIdentifier(property.Name);
        string text;
        if (property.GetMethod is null)
        {
            text = "    public " + type + " " + name + NewLine
                + "    {" + NewLine
                + "        set => _inner." + name + " = value;" + NewLine
                + "    }";
        }
        else if (property.SetMethod is null)
        {
            text = "    public " + type + " " + name + " => _inner." + name + ";";
        }
        else
        {
            text = "    public " + type + " " + name + NewLine
                + "    {" + NewLine
                + "        get => _inner." + name + ";" + NewLine
                + "        set => _inner." + name + " = value;" + NewLine
                + "    }";
        }

        // A property is not a call, so it carries no span of its own. The
        // statement still decides whether the seam is observed at all.
        _ = ResolveStatement(property, owner, typeStatement, excluded, namespacePolicies, assemblyPolicy);
        return (new MemberPlan { Text = text, IsInstrumented = false }, false);
    }

    private static (MemberPlan? Plan, bool Fatal) PlanEvent(
        IEventSymbol @event,
        INamedTypeSymbol owner,
        ObservedStatement? typeStatement,
        HashSet<string> excluded,
        Dictionary<string, ObservedStatement> namespacePolicies,
        ObservedStatement? assemblyPolicy,
        ObservedRequest request)
    {
        if (!@event.IsAbstract)
        {
            return (null, false);
        }

        if (@event.IsStatic)
        {
            request.Diagnostics.Add(Diagnostic.Create(
                ObservedDiagnostics.UnsupportedShape,
                LocationOf(@event),
                @event.ToDisplayString(),
                "a static abstract member cannot be implemented by a class"));
            return (null, true);
        }

        string name = EscapeIdentifier(@event.Name);
        string text = "    public event " + Render(@event.Type) + " " + name + NewLine
            + "    {" + NewLine
            + "        add { _inner." + name + " += value; }" + NewLine
            + "        remove { _inner." + name + " -= value; }" + NewLine
            + "    }";

        _ = ResolveStatement(@event, owner, typeStatement, excluded, namespacePolicies, assemblyPolicy);
        return (new MemberPlan { Text = text, IsInstrumented = false }, false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The policy
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The statement that governs a member: its own marker, else the
    ///     declaring type's, else the nearest namespace policy, else the
    ///     assembly policy. Exactly one statement wins and nothing is merged.
    /// </summary>
    private static ObservedStatement? ResolveStatement(
        ISymbol member,
        INamedTypeSymbol owner,
        ObservedStatement? typeStatement,
        HashSet<string> excluded,
        Dictionary<string, ObservedStatement> namespacePolicies,
        ObservedStatement? assemblyPolicy)
    {
        if (FindAttribute(member, ObservedAttributeName) is { } own)
        {
            return ReadObserved(own, StatementLevel.Member);
        }

        if (typeStatement is not null)
        {
            return excluded.Contains(member.Name) ? null : typeStatement;
        }

        string ns = NamespaceOf(owner);
        while (true)
        {
            if (namespacePolicies.TryGetValue(ns, out ObservedStatement? policy))
            {
                return policy;
            }

            if (ns.Length == 0)
            {
                break;
            }

            int lastDot = ns.LastIndexOf('.');
            ns = lastDot < 0 ? string.Empty : ns[..lastDot];
        }

        return assemblyPolicy;
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == metadataName)
            {
                return attribute;
            }
        }

        return null;
    }

    private static ObservedStatement ReadObserved(AttributeData attribute, StatementLevel level)
    {
        var statement = new ObservedStatement { Level = level };

        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string span)
        {
            statement.Span = span;
        }

        foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Span":
                    statement.Span = named.Value.Value as string;
                    break;
                case "Counter":
                    statement.Counter = named.Value.Value as string;
                    break;
                case "Duration":
                    statement.Duration = named.Value.Value as string;
                    break;
                case "Scope":
                    statement.Scope = ReadScope(named.Value);
                    break;
                case "CaptureArguments":
                    statement.CaptureArguments = named.Value.Value is true;
                    break;
                case "CaptureResult":
                    statement.CaptureResult = named.Value.Value as true;
                    break;
                case "Exclude":
                    statement.Exclude = ReadStringArray(named.Value);
                    break;
            }
        }

        statement.ResolveContradiction();
        return statement;
    }

    private static ObservedStatement ReadPolicy(AttributeData attribute)
    {
        var statement = new ObservedStatement { Level = StatementLevel.Policy };

        foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "SpanPrefix":
                    statement.SpanPrefix = named.Value.Value as string;
                    break;
                case "Scope":
                    statement.Scope = ReadScope(named.Value);
                    break;
                case "CaptureArguments":
                    statement.CaptureArguments = named.Value.Value is true;
                    break;
            }
        }

        return statement;
    }

    private static ObservedScope ReadScope(TypedConstant constant)
    {
        if (constant.Type is not INamedTypeSymbol enumType || constant.Value is null)
        {
            return ObservedScope.SpanAndMetrics;
        }

        foreach (IFieldSymbol member in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (!member.HasConstantValue || !Equals(member.ConstantValue, constant.Value))
            {
                continue;
            }

            return member.Name switch
            {
                "None" => ObservedScope.None,
                "Span" => ObservedScope.Span,
                "Metrics" => ObservedScope.Metrics,
                _ => ObservedScope.SpanAndMetrics,
            };
        }

        return ObservedScope.SpanAndMetrics;
    }

    private static string[] ReadStringArray(TypedConstant constant)
    {
        if (constant.Kind != TypedConstantKind.Array || constant.IsNull)
        {
            return [];
        }

        var list = new List<string>();
        foreach (TypedConstant item in constant.Values)
        {
            if (item.Value is string text)
            {
                list.Add(text);
            }
        }

        return list.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Names
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The names the generated code uses: the span, the counter, the
    ///     duration histogram and the tag the status travels in.
    /// </summary>
    private static Instrument BuildInstrument(INamedTypeSymbol owner, IMethodSymbol method, ObservedStatement statement)
    {
        string seam = SnakeCase(StripLeadingI(owner.Name));
        string operation = SnakeCase(StripAsyncSuffix(method.Name));

        string prefix = statement.Level switch
        {
            // A member's own marker names the span outright; the counter and
            // the histogram fall back to the seam.
            StatementLevel.Member => seam,
            // A type's marker names the PREFIX: one span name cannot serve
            // every operation of a type.
            StatementLevel.Type => statement.Span ?? seam,
            _ => statement.SpanPrefix ?? seam,
        };

        string spanName = statement.Level == StatementLevel.Member && statement.Span is not null
            ? statement.Span
            : prefix + "." + operation;

        return new Instrument
        {
            SpanName = spanName,
            CounterName = statement.Counter ?? prefix + ".calls",
            DurationName = statement.Duration ?? prefix + ".duration.ms",
            StatusTag = prefix + ".status",
            EmitsSpan = statement.Scope is ObservedScope.Span or ObservedScope.SpanAndMetrics,
            EmitsMetrics = statement.Scope is ObservedScope.Metrics or ObservedScope.SpanAndMetrics,
            CaptureArguments = statement.CaptureArguments && (statement.Scope is ObservedScope.Span or ObservedScope.SpanAndMetrics),
            CaptureResult = statement.CaptureResult && (statement.Scope is ObservedScope.Span or ObservedScope.SpanAndMetrics),
        };
    }

    private static string StripLeadingI(string name) =>
        name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

    private static string StripAsyncSuffix(string name) =>
        name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;

    /// <summary>
    ///     <c>SessionStore</c> to <c>session_store</c>. The word breaks are
    ///     the ones a reader expects: a lower-to-upper transition, and the end
    ///     of a run of capitals.
    /// </summary>
    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];
            if (!char.IsUpper(current))
            {
                sb.Append(current);
                continue;
            }

            bool previousIsUpper = i > 0 && char.IsUpper(name[i - 1]);
            bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
            if (i > 0 && (!previousIsUpper || nextIsLower))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(current));
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rendering
    // ─────────────────────────────────────────────────────────────────────────

    private static string RenderDecorator(
        INamedTypeSymbol type,
        string className,
        string ns,
        List<MemberPlan> members)
    {
        string fullName = type.ToDisplayString();
        bool needsTracer = members.Any(static m => m.UsesTracer);
        bool needsMetrics = members.Any(static m => m.UsesMetrics);

        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>" + NewLine);
        sb.Append("// Harbor.CodeGen.ObservedGenerator — the [Observed] decorator for " + fullName + ". Issue #1797." + NewLine);
        sb.Append("#nullable enable" + NewLine);
        sb.Append(NewLine);

        if (ns.Length > 0)
        {
            sb.Append("namespace " + ns + ";" + NewLine);
            sb.Append(NewLine);
        }
        sb.Append("/// <summary>" + NewLine);
        sb.Append("///     The generated [Observed] decorator for <c>" + fullName + "</c>." + NewLine);
        sb.Append("/// </summary>" + NewLine);
        sb.Append("/// <remarks>" + NewLine);
        sb.Append("///     Every member is forwarded to the wrapped instance. The members the" + NewLine);
        sb.Append("///     policy marks open a span and record the call's metrics around the" + NewLine);
        sb.Append("///     call; the rest are forwarded as they are." + NewLine);
        sb.Append("/// </remarks>" + NewLine);
        sb.Append("internal sealed class " + className + " : global::" + fullName + NewLine);
        sb.Append("{" + NewLine);
        sb.Append("    private readonly global::" + fullName + " _inner;" + NewLine);

        if (needsTracer)
        {
            sb.Append("    private readonly global::Harbor.Diagnostics.ITracer _tracer;" + NewLine);
        }

        if (needsMetrics)
        {
            sb.Append("    private readonly global::Harbor.Diagnostics.IMetrics _metrics;" + NewLine);
        }

        sb.Append(NewLine);
        sb.Append("    /// <summary>Wraps <paramref name=\"inner\" /> with the observed behaviour.</summary>" + NewLine);
        sb.Append("    public " + className + "(global::" + fullName + " inner");

        if (needsTracer)
        {
            sb.Append(", global::Harbor.Diagnostics.ITracer tracer");
        }

        if (needsMetrics)
        {
            sb.Append(", global::Harbor.Diagnostics.IMetrics metrics");
        }

        sb.Append(")" + NewLine);
        sb.Append("    {" + NewLine);
        sb.Append("        _inner = inner;" + NewLine);

        if (needsTracer)
        {
            sb.Append("        _tracer = tracer;" + NewLine);
        }

        if (needsMetrics)
        {
            sb.Append("        _metrics = metrics;" + NewLine);
        }

        sb.Append("    }" + NewLine);

        foreach (MemberPlan member in members)
        {
            sb.Append(NewLine);
            sb.Append(member.Text + NewLine);
        }

        sb.Append("}" + NewLine);
        return sb.ToString();
    }

    private static string RenderMethodDeclaration(IMethodSymbol method)
    {
        CallShape shape = CallShapeOf(method.ReturnType);
        var sb = new StringBuilder();
        sb.Append("    public ");

        if (shape is CallShape.Await or CallShape.AwaitValue)
        {
            sb.Append("async ");
        }

        sb.Append(Render(method.ReturnType));
        sb.Append(' ');
        sb.Append(EscapeIdentifier(method.Name));

        if (method.TypeParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.TypeParameters.Select(static t => t.Name)));
            sb.Append('>');
        }

        sb.Append('(');
        sb.Append(RenderParameters(method));
        sb.Append(')');

        foreach (ITypeParameterSymbol typeParameter in method.TypeParameters)
        {
            string constraints = RenderConstraints(typeParameter);
            if (constraints.Length > 0)
            {
                sb.Append(' ');
                sb.Append(constraints);
            }
        }

        return sb.ToString();
    }

    private static string RenderParameters(IMethodSymbol method)
    {
        var parts = new List<string>();
        foreach (IParameterSymbol parameter in method.Parameters)
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

            sb.Append(Render(parameter.Type));
            sb.Append(' ');
            sb.Append(EscapeIdentifier(parameter.Name));
            sb.Append(RenderDefaultValue(parameter));
            parts.Add(sb.ToString());
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    ///     The call into the wrapped instance: the arguments, and the
    ///     <c>ref</c>/<c>out</c> modifiers the signature carries.
    /// </summary>
    private static string RenderCall(IMethodSymbol method)
    {
        var parts = new List<string>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            string name = EscapeIdentifier(parameter.Name);
            parts.Add(parameter.RefKind switch
            {
                RefKind.Ref => "ref " + name,
                RefKind.Out => "out " + name,
                RefKind.In => "in " + name,
                _ => name,
            });
        }

        string call = "_inner." + EscapeIdentifier(method.Name) + "(" + string.Join(", ", parts) + ")";
        return method.ReturnType.SpecialType == SpecialType.System_Void ? call + ";" : "return " + call + ";";
    }

    /// <summary>
    ///     The body of an instrumented member: a span around the call, the
    ///     call's own metrics, and the status the call ended in.
    /// </summary>
    private static string RenderInstrumentedBody(IMethodSymbol method, Instrument instrument)
    {
        var sb = new StringBuilder();
        string indent = "        ";
        CallShape shape = CallShapeOf(method.ReturnType);
        ITypeSymbol? value = shape == CallShape.AwaitValue ? ValueOf(method.ReturnType) : null;
        if (shape == CallShape.Sync && method.ReturnType.SpecialType != SpecialType.System_Void)
        {
            value = method.ReturnType;
        }

        // The locals of the emitted body must not collide with a parameter of
        // the member: a parameter named "span" would make the emitted code
        // fail to compile, and the generator must never emit code that does
        // not compile.
        string span = UniqueName(method, "span");
        string start = UniqueName(method, "start");
        string result = UniqueName(method, "observedResult");
        string exception = UniqueName(method, "observedException");

        if (instrument.EmitsSpan)
        {
            sb.Append(indent + "using global::Harbor.Diagnostics.ITelemetrySpan? " + span + " = _tracer.StartSpan(\"" + instrument.SpanName + "\");" + NewLine);
        }

        if (instrument.EmitsMetrics)
        {
            sb.Append(indent + "long " + start + " = global::System.Diagnostics.Stopwatch.GetTimestamp();" + NewLine);
        }

        sb.Append(indent + "try" + NewLine);
        sb.Append(indent + "{" + NewLine);

        if (instrument.CaptureArguments)
        {
            foreach (IParameterSymbol parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None)
                {
                    continue;
                }

                sb.Append(indent + "    " + span + "?.SetTag(\"arg." + parameter.Name + "\", " + EscapeIdentifier(parameter.Name) + ");" + NewLine);
            }
        }

        bool isResult = value is not null && (IsResult(value) || IsResultOf(value));
        string call = "_inner." + EscapeIdentifier(method.Name) + "(" + RenderArguments(method) + ")";

        if (value is null)
        {
            if (shape == CallShape.Await)
            {
                sb.Append(indent + "    await " + call + ".ConfigureAwait(false);" + NewLine);
            }
            else
            {
                sb.Append(indent + "    " + call + ";" + NewLine);
            }

            sb.Append(NewLine);
            AppendRecord(sb, indent + "    ", instrument, "ok", start);
        }
        else
        {
            string awaited = shape == CallShape.AwaitValue ? "await " + call + ".ConfigureAwait(false)" : call;
            sb.Append(indent + "    " + Render(value) + " " + result + " = " + awaited + ";" + NewLine);
            sb.Append(NewLine);

            if (instrument.CaptureResult)
            {
                sb.Append(indent + "    " + span + "?.SetTag(\"result\", " + ResultExpression(value, result) + ");" + NewLine);
            }

            if (isResult)
            {
                sb.Append(indent + "    if (" + result + ".IsFailure)" + NewLine);
                sb.Append(indent + "    {" + NewLine);

                if (instrument.EmitsSpan)
                {
                    sb.Append(indent + "        " + span + "?.SetError(" + result + ".Error);" + NewLine);
                }

                AppendRecord(sb, indent + "        ", instrument, "failure", start);
                sb.Append(indent + "    }" + NewLine);
                sb.Append(indent + "    else" + NewLine);
                sb.Append(indent + "    {" + NewLine);
                AppendRecord(sb, indent + "        ", instrument, "ok", start);
                sb.Append(indent + "    }" + NewLine);
            }
            else
            {
                AppendRecord(sb, indent + "    ", instrument, "ok", start);
            }

            sb.Append(indent + "    return " + result + ";" + NewLine);
        }

        sb.Append(indent + "}" + NewLine);
        sb.Append(indent + "catch (global::System.OperationCanceledException)" + CancellationFilter(method) + NewLine);
        sb.Append(indent + "{" + NewLine);
        AppendRecord(sb, indent + "    ", instrument, "cancelled", start);
        sb.Append(indent + "    throw;" + NewLine);
        sb.Append(indent + "}" + NewLine);
        sb.Append(indent + "catch (global::System.Exception"
            + (instrument.EmitsSpan ? " " + exception : string.Empty) + ")" + NewLine);
        sb.Append(indent + "{" + NewLine);

        if (instrument.EmitsSpan)
        {
            sb.Append(indent + "    " + span + "?.SetError(" + exception + ".Message);" + NewLine);
        }

        AppendRecord(sb, indent + "    ", instrument, "exception", start);
        sb.Append(indent + "    throw;" + NewLine);
        sb.Append(indent + "}" + NewLine);

        return sb.ToString();
    }

    /// <summary>
    ///     The call's own metrics: the counter and the duration histogram,
    ///     tagged with the status the call ended in.
    /// </summary>
    private static void AppendRecord(StringBuilder sb, string indent, Instrument instrument, string status, string start)
    {
        if (!instrument.EmitsMetrics)
        {
            return;
        }

        string tag = "new global::System.Collections.Generic.KeyValuePair<string, object?>(\"" + instrument.StatusTag + "\", \"" + status + "\")";
        sb.Append(indent + "_metrics.Counter(\"" + instrument.CounterName + "\", 1, " + tag + ");" + NewLine);
        sb.Append(indent + "_metrics.Histogram(\"" + instrument.DurationName + "\", global::System.Diagnostics.Stopwatch.GetElapsedTime(" + start + ").TotalMilliseconds, " + tag + ");" + NewLine);
    }

    /// <summary>
    ///     A local name that no parameter of the member already uses. The
    ///     emitted body declares locals, and a local that shadows a
    ///     parameter does not compile.
    /// </summary>
    private static string UniqueName(IMethodSymbol method, string preferred)
    {
        string candidate = preferred;
        int suffix = 1;
        while (HasParameter(method, candidate))
        {
            suffix++;
            candidate = preferred + suffix;
        }

        return candidate;
    }

    private static bool HasParameter(IMethodSymbol method, string name)
    {
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string CancellationFilter(IMethodSymbol method)
    {
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (IsCancellationToken(parameter.Type))
            {
                return " when (" + EscapeIdentifier(parameter.Name) + ".IsCancellationRequested)";
            }
        }

        return string.Empty;
    }

    private static string RenderArguments(IMethodSymbol method)
    {
        var parts = new List<string>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            parts.Add(EscapeIdentifier(parameter.Name));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    ///     The expression a captured result is put in the tag as: the value
    ///     itself for a reference type, its text for a value type.
    /// </summary>
    private static string ResultExpression(ITypeSymbol type, string local) =>
        type.IsValueType ? local + ".ToString()" : local;

    // ─────────────────────────────────────────────────────────────────────────
    // Type helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    ///     The type as it is written in the emitted code: fully qualified and
    ///     prefixed with <c>global::</c>, so the generated file does not
    ///     depend on any using directive.
    /// </summary>
    /// <remarks>
    ///     The special types are the exception: they are written as the
    ///     keywords they are (<c>int</c>, <c>string</c>, <c>bool</c>), and
    ///     <c>global::int</c> is not a type name. A special type nested inside
    ///     a generic argument is fine — <c>List&lt;int&gt;</c> keeps the
    ///     keyword — so the exception is only about the type at hand.
    /// </remarks>
    private static string Render(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Void)
        {
            return "void";
        }

        if (type.SpecialType != SpecialType.None)
        {
            return type.ToDisplayString(TypeFormat);
        }

        return "global::" + type.ToDisplayString(TypeFormat);
    }

    /// <summary>The shape of a call, decided by its return type.</summary>
    private static CallShape CallShapeOf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Arity: 0 or 1 } named)
        {
            return CallShape.Sync;
        }

        if (named.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks")
        {
            return CallShape.Sync;
        }

        if (named.Name is not ("Task" or "ValueTask"))
        {
            return CallShape.Sync;
        }

        return named.Arity == 1 ? CallShape.AwaitValue : CallShape.Await;
    }

    /// <summary>The value a task-like return carries.</summary>
    private static ITypeSymbol? ValueOf(ITypeSymbol type) =>
        type is INamedTypeSymbol { Arity: 1 } named ? named.TypeArguments[0] : null;

    /// <summary>
    ///     Is the return a sequence the caller reads after the call? A span
    ///     around such a call would close before the sequence is read, so the
    ///     generator forwards it instead of measuring it.
    /// </summary>
    private static bool IsLazyReturn(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Arity: 1 } named)
        {
            return false;
        }

        string full = named.OriginalDefinition.ToDisplayString();
        return full is "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.IAsyncEnumerable<T>";
    }

    private static bool HasReferenceParameter(IMethodSymbol method)
    {
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Is this an ABSTRACT interface member, or a default interface
    ///     member (one with a body)? The accessors are what carry the answer.
    /// </summary>
    private static bool IsAbstractProperty(IPropertySymbol property)
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

    private static bool IsCancellationToken(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "CancellationToken", Arity: 0 } named
        && named.ContainingNamespace.ToDisplayString() == "System.Threading";

    private static bool IsResult(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Result", Arity: 0 } named
        && named.ContainingNamespace.ToDisplayString() == "CSharpFunctionalExtensions";

    private static bool IsResultOf(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Result", Arity: 1 } named
        && named.ContainingNamespace.ToDisplayString() == "CSharpFunctionalExtensions";

    private static string RenderConstraints(ITypeParameterSymbol typeParameter)
    {
        var parts = new List<string>();

        if (typeParameter.HasUnmanagedTypeConstraint)
        {
            parts.Add("unmanaged");
        }

        if (typeParameter.HasValueTypeConstraint)
        {
            parts.Add("struct");
        }

        if (typeParameter.HasReferenceTypeConstraint)
        {
            parts.Add("class");
        }

        if (typeParameter.HasNotNullConstraint && typeParameter.ConstraintTypes.Length == 0)
        {
            parts.Add("notnull");
        }

        if (typeParameter.HasConstructorConstraint)
        {
            parts.Add("new()");
        }

        foreach (ITypeSymbol constraint in typeParameter.ConstraintTypes)
        {
            parts.Add(Render(constraint));
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
            return parameter.Type.IsValueType ? " = default" : " = null";
        }

        bool nullableValueType = parameter.Type is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
        };

        if (parameter.Type.IsValueType && !nullableValueType && parameter.Type.SpecialType == SpecialType.None && parameter.Type.TypeKind != TypeKind.Enum)
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
        string typeName = (enumType.FullName ?? enumType.Name).Replace('+', '.');
        foreach (string name in Enum.GetNames(enumType))
        {
            if (Equals(Enum.Parse(enumType, name), value))
            {
                return "global::" + typeName + "." + name;
            }
        }

        return "default";
    }

    /// <summary>
    ///     The C# RESERVED keywords. A parameter whose name is one of them
    ///     has to be escaped again in the emitted code.
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

    private static string NamespaceOf(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();

    private static Location LocationOf(ISymbol symbol) =>
        symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;

    private static Location AttributeLocation(AttributeData attribute)
    {
        SyntaxReference? reference = attribute.ApplicationSyntaxReference;
        return reference is null ? Location.None : reference.GetSyntax().GetLocation();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Emission
    // ─────────────────────────────────────────────────────────────────────────

    private static void Emit(SourceProductionContext spc, ObservedRequest request)
    {
        foreach (Diagnostic diagnostic in request.Diagnostics)
        {
            spc.ReportDiagnostic(diagnostic);
        }

        foreach (DecoratorPlan decorator in request.Decorators)
        {
            spc.AddSource(decorator.HintName, SourceText.From(decorator.Text, Encoding.UTF8));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Model
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class ObservedRequest
    {
        public List<Diagnostic> Diagnostics { get; } = [];

        public List<DecoratorPlan> Decorators { get; } = [];
    }

    private sealed record DecoratorPlan(string HintName, string Text);

    private sealed class MemberPlan
    {
        public string Text { get; init; } = string.Empty;

        public bool IsInstrumented { get; init; }

        public bool UsesTracer { get; init; }

        public bool UsesMetrics { get; init; }
    }

    private sealed class Instrument
    {
        public string SpanName { get; init; } = string.Empty;

        public string CounterName { get; init; } = string.Empty;

        public string DurationName { get; init; } = string.Empty;

        public string StatusTag { get; init; } = string.Empty;

        public bool EmitsSpan { get; init; }

        public bool EmitsMetrics { get; init; }

        public bool CaptureArguments { get; init; }

        public bool CaptureResult { get; init; }
    }

    /// <summary>
    ///     One statement of the policy: what it says and which level said it.
    /// </summary>
    private sealed class ObservedStatement
    {
        public StatementLevel Level { get; set; } = StatementLevel.Member;

        public string? Span { get; set; }

        public string? Counter { get; set; }

        public string? Duration { get; set; }

        public string? SpanPrefix { get; set; }

        public ObservedScope Scope { get; set; } = ObservedScope.SpanAndMetrics;

        public bool CaptureArguments { get; set; }

        public bool CaptureResult { get; set; }

        public string[] Exclude { get; set; } = [];

        /// <summary>True when the statement asks for nothing AND for an instrument.</summary>
        public bool contradicts { get; private set; }

        /// <summary>The name of the instrument the contradiction is about.</summary>
        public string? contradiction { get; private set; }

        /// <summary>
        ///     A statement that says "emit nothing" and also names a span or
        ///     an instrument asks for two opposite things at once. The
        ///     generator refuses to choose, so the contradiction is reported.
        /// </summary>
        public void ResolveContradiction()
        {
            if (Scope != ObservedScope.None)
            {
                return;
            }

            if (Span is not null)
            {
                contradicts = true;
                contradiction = "Span";
                return;
            }

            if (Counter is not null)
            {
                contradicts = true;
                contradiction = "Counter";
                return;
            }

            if (Duration is not null)
            {
                contradicts = true;
                contradiction = "Duration";
            }
        }

        /// <summary>Do two policies say the same thing?</summary>
        public bool SameAs(ObservedStatement other) =>
            string.Equals(SpanPrefix, other.SpanPrefix, StringComparison.Ordinal)
            && ObservedScope == other.Scope
            && CaptureArguments == other.CaptureArguments;

        /// <summary>The statement as a reader would write it down.</summary>
        public string Describe() =>
            "prefix=" + (SpanPrefix ?? "(none)")
            + ", scope=" + ObservedScope
            + ", captureArguments=" + CaptureArguments;
    }
}
