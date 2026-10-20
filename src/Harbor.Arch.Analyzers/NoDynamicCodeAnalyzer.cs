// NoDynamicCodeAnalyzer.cs — HARCH001: the dynamic-code ban (#626) as a rule
// the compiler itself runs over every product project.
//
// The rationale, the measurements and the promotion path live in the class
// remarks below and in the project README.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Arch.Analyzers;

/// <summary>
///     HARCH001 — no dynamic-code API in shipped code.
/// </summary>
/// <remarks>
///     <para>
///         The convention this rule encodes is #626's: loading or manufacturing
///         code at run time is invisible to NativeAOT and to every
///         <c>ProjectReference</c> rule, so it is forbidden in product code —
///         with one deliberate exception, the plugin family, where loading
///         code at run time IS the product.
///     </para>
///     <para>
///         The analyzer does not know about that exception. An analyzer sees
///         ONE compilation, and the exception is a statement about a set of
///         projects; the rule that owns the exception owns the decision. This
///         class therefore reports every dynamic-code site, and the wiring that
///         consumes the analyzer decides which projects the rule applies to —
///         the same way the compiler-side ban file for the same convention is
///         simply not handed to the plugin projects.
///     </para>
///     <para>
///         WHY A SYMBOL AND NOT A SPELLING
///     </para>
///     <para>
///         The text rule this analyzer replaces matched tokens. Three
///         consequences followed from that, and all three are gone here:
///         a comment mentioning a banned API was a hit (a syntax tree keeps
///         comments in trivia, where no analyzer looks), a string literal
///         naming a member was a hit (a string is not a symbol), and a type
///         DERIVED from a banned one was a miss (the analyzer resolves the
///         type and walks its base chain).
///     </para>
///     <para>
///         One difference is deliberate and goes the other way: the analyzer
///         does not flag the VALUE TYPES of the emit namespace. Those types
///         describe emitted IL or say how it may be emitted; they do not emit
///         it. The text rule flagged them, because it matched the namespace
///         token.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoDynamicCodeAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id this rule reports under.</summary>
    public const string DiagnosticId = "HARCH001";

    /// <summary>The rule as the compiler sees it.</summary>
    /// <remarks>
    ///     The nine-argument constructor is used positionally on purpose: the
    ///     argument NAMES of the descriptor's constructors are not part of the
    ///     contract this project can check, and a named argument that turns out
    ///     to be spelled differently is a build break in a project that cannot
    ///     be built locally. The order is (id, title, messageFormat, category,
    ///     defaultSeverity, isEnabledByDefault, description, helpLinkUri,
    ///     customTags).
    /// </remarks>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Dynamic code is forbidden in shipped code",
        "'{0}' loads or manufactures code at run time, which is invisible to NativeAOT and to every ProjectReference rule",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "AGENTS.md requires NativeAOT-readiness: no reflection emit, no Assembly.Load. The one place this capability is wanted is the plugin family, where loading code at run time IS the product.",
        null,
        []);

    /// <summary>The type whose <c>Load*</c> methods load an assembly.</summary>
    private const string AssemblyTypeName = "System.Reflection.Assembly";

    /// <summary>The type whose <c>DefineDynamicAssembly</c> manufactures one.</summary>
    private const string AppDomainTypeName = "System.AppDomain";

    /// <summary>The load context whose existence is the plugin family's business.</summary>
    private const string AssemblyLoadContextTypeName = "System.Runtime.Loader.AssemblyLoadContext";

    /// <summary>The namespace whose types emit IL.</summary>
    private const string ReflectionEmitNamespaceName = "System.Reflection.Emit";

    /// <summary>The <c>Assembly</c> members that load code at run time.</summary>
    private static readonly string[] BannedAssemblyMethods = ["Load", "LoadFrom", "LoadFile"];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeTypeOf, SyntaxKind.TypeOfExpression);
        context.RegisterSyntaxNodeAction(AnalyzeBaseList, SyntaxKind.BaseList);
        context.RegisterSyntaxNodeAction(AnalyzeTypeReference, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    /// <summary>
    ///     An invocation of a banned factory: <c>Assembly.Load</c>,
    ///     <c>Assembly.LoadFrom</c>, <c>Assembly.LoadFile</c>,
    ///     <c>AppDomain.DefineDynamicAssembly</c>, or any member of a type in
    ///     the emit namespace (<c>AssemblyBuilder.DefineDynamicAssembly</c>).
    /// </summary>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        INamedTypeSymbol? owner = method.ContainingType;
        if (owner is null)
        {
            return;
        }

        string ownerName = owner.ToDisplayString();
        bool banned =
            (ownerName == AssemblyTypeName && Array.IndexOf(BannedAssemblyMethods, method.Name) >= 0)
            || (ownerName == AppDomainTypeName && method.Name == "DefineDynamicAssembly")
            || IsInEmitNamespace(owner);

        if (banned)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Rule, invocation.GetLocation(), ownerName + "." + method.Name));
        }
    }

    /// <summary>
    ///     Whether a type lives in the emit namespace and is not a value type.
    /// </summary>
    /// <remarks>
    ///     The .NET Core way to manufacture an assembly at run time is
    ///     <c>System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly</c>,
    ///     NOT <c>AppDomain.DefineDynamicAssembly</c>: the latter does not
    ///     exist in .NET Core, .NET 9 or .NET 10, so a text rule that bans the
    ///     token <c>AppDomain.DefineDynamicAssembly</c> bans something that
    ///     cannot appear in compiling code at all. The analyzer sees that by
    ///     resolving the symbol, which is the whole argument for resolving it.
    /// </remarks>
    private static bool IsInEmitNamespace(INamedTypeSymbol type)
    {
        if (type.IsValueType)
        {
            return false;
        }

        return type.ContainingNamespace?.ToDisplayString() == ReflectionEmitNamespaceName;
    }

    /// <summary>An object creation of a banned type: <c>new TypeBuilder(…)</c>.</summary>
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol constructor)
        {
            return;
        }

        if (constructor.ContainingType is { } type && IsBannedType(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), type.ToDisplayString()));
        }
    }

    /// <summary>A <c>typeof</c> of a banned type.</summary>
    private static void AnalyzeTypeOf(SyntaxNodeAnalysisContext context)
    {
        var typeOf = (TypeOfExpressionSyntax)context.Node;

        if (context.SemanticModel.GetTypeInfo(typeOf.Type, context.CancellationToken).Type is INamedTypeSymbol type
            && IsBannedType(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, typeOf.GetLocation(), type.ToDisplayString()));
        }
    }

    /// <summary>
    ///     A base list naming a banned type — the <c>: AssemblyLoadContext</c>
    ///     shape, which is how the plugin family declares its load context and
    ///     the only shape in the live tree.
    /// </summary>
    private static void AnalyzeBaseList(SyntaxNodeAnalysisContext context)
    {
        var baseList = (BaseListSyntax)context.Node;

        foreach (BaseTypeSyntax baseType in baseList.Types)
        {
            if (context.SemanticModel.GetTypeInfo(baseType.Type, context.CancellationToken).Type is INamedTypeSymbol type
                && IsBannedType(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, baseType.GetLocation(), type.ToDisplayString()));
            }
        }
    }

    /// <summary>
    ///     A type REFERENCE to a banned type: a field, a parameter or a local
    ///     declared as <c>AssemblyLoadContext</c>. Without this the rule would
    ///     see only the places the type is constructed, and a rule that sees
    ///     only constructions misses the declaration that makes the capability
    ///     reachable at all.
    /// </summary>
    /// <remarks>
    ///     The action fires for every identifier in the tree, so the symbol is
    ///     resolved ONLY in a type position. Resolving a symbol for every
    ///     identifier would put a semantic-model call on every name in the
    ///     product, which is the difference between a rule that costs nothing
    ///     and a rule that costs the arch suite a minute.
    /// </remarks>
    private static void AnalyzeTypeReference(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode node = context.Node;

        // A generic name owns the identifier inside it; grading both would
        // report one type twice.
        if (node.Parent is GenericNameSyntax)
        {
            return;
        }

        if (!IsTypePosition(node.Parent))
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not INamedTypeSymbol type)
        {
            return;
        }

        if (IsBannedType(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), type.ToDisplayString()));
        }
    }

    /// <summary>
    ///     Whether an identifier sits where a TYPE is written.
    /// </summary>
    private static bool IsTypePosition(SyntaxNode? parent) => parent switch
    {
        VariableDeclarationSyntax => true,
        ParameterSyntax => true,
        PropertyDeclarationSyntax => true,
        FieldDeclarationSyntax => true,
        EventFieldDeclarationSyntax => true,
        LocalDeclarationStatementSyntax => true,
        MethodDeclarationSyntax => true,
        LocalFunctionStatementSyntax => true,
        DelegateDeclarationSyntax => true,
        CastExpressionSyntax => true,
        ObjectCreationExpressionSyntax => true,
        BaseTypeSyntax => true,
        TypeOfExpressionSyntax => true,
        TypeArgumentListSyntax => true,
        NullableTypeSyntax => true,
        ArrayTypeSyntax => true,
        PointerTypeSyntax => true,
        _ => false,
    };

    /// <summary>
    ///     Whether a type is one of the banned ones: the load context, anything
    ///     derived from it, or a type in the emit namespace.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         VALUE TYPES in the emit namespace are excluded deliberately:
    ///         <c>OpCode</c>, <c>ILOpCode</c> and <c>AssemblyBuilderAccess</c>
    ///         DESCRIBE emitted IL or say HOW it may be emitted. They are not
    ///         the capability. The text rule flags the namespace TOKEN, so it
    ///         flags them too, and this is the one place where the analyzer is
    ///         knowingly narrower than the rule it replaces.
    ///     </para>
    /// </remarks>
    private static bool IsBannedType(INamedTypeSymbol type)
    {
        string name = type.ToDisplayString();

        if (name == AssemblyLoadContextTypeName)
        {
            return true;
        }

        for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.ToDisplayString() == AssemblyLoadContextTypeName)
            {
                return true;
            }
        }

        if (type.IsValueType)
        {
            return false;
        }

        return IsInEmitNamespace(type);
    }
}
