// HarborArchAnalyzers.cs — PILOT for issue #1729: two architecture rules written
// as Roslyn analyzers instead of regex text scans.
//
// WHY THIS FILE EXISTS
// --------------------
// 86 % of the rules in this project (95 of 110) grade the TEXT of the product
// source: they read a file, strip comments, run a regex, subtract a baseline.
// The prose in each rule says why the invariant matters; the mechanism is the
// same five moves, written 95 times (issue #1086).
//
// This file asks a different question: is the ROSLYN ANALYZER the right
// substrate for the rules that #1729 wants to move off text matching? Two
// rules are implemented here as real `DiagnosticAnalyzer`s so the question can
// be answered with numbers instead of an opinion:
//
//   HARCH001  no dynamic-code API outside the plugin family
//              (the matcher half of ReflectionConventionRule, #626)
//   HARCH002  a namespace root is owned by the project that declares it
//              (the R1/R2 half of AbstractionsNamespaceOwnershipRules, #452)
//
// WHAT THE PILOT MEASURED (the numbers are in the PR body and in #1729)
// --------------------------------------------------------------------
//   * The analyzer needs NO comment stripper and NO string-literal masking to
//     reach the same verdict the text scan reaches. The three live occurrences
//     of "Assembly.Load" in the product tree are all inside comments; a syntax
//     tree keeps comments in trivia, where no analyzer looks. The stripper
//     (19 private copies of it across this project, per #1086) becomes
//     unnecessary for this rule.
//   * The analyzer resolves the TYPE, so the rule stops being a spelling
//     match. "AssemblyLoadContext" as a token matches a spelling; the analyzer
//     matches the type, including a type DERIVED from it.
//   * The analyzer can see the DECLARING PROJECT (`compilation.AssemblyName`),
//     which is the fact the text scan cannot get at all. The namespace rule's
//     own R3 exists precisely because a file walk cannot say which assembly
//     declares a namespace — the analyzer is handed that fact with the
//     compilation.
//
// WHERE THIS FILE LIVES, AND WHY IT IS NOT A SEPARATE PROJECT YET
// ----------------------------------------------------------------
// A build-time analyzer has to be referenced by every product project, and
// this repository builds with `TreatWarningsAsErrors` over ~140 projects. The
// first version of any rule that fires on existing code therefore turns the
// build red before anybody has read the rule, which is the wrong order for a
// pilot. Promoting these two rules to a build-time analyzer is a separate,
// deliberate step, and the promotion path is written down in the PR body:
//
//   1. move this file to `src/Harbor.Arch.Analyzers/` (a `net10.0` project
//      with `Microsoft.CodeAnalysis.CSharp` and `PrivateAssets=all`, exactly
//      like `src/Harbor.CodeGen`);
//   2. add `Harbor.Arch.Analyzers` to `FullLayerMatrixTests.OutOfScopeAssemblies`
//      with its reason, so `EnforcerIntegrityTests.SrcProjects_AreAllClassified`
//      accepts the new project;
//   3. reference it from the product projects with
//      `<OutputItemType>Analyzer</OutputItemType>` and declare it in
//      `FullLayerMatrixTests.SourceGeneratorProjects`;
//   4. give each rule a severity row in `.editorconfig`, starting at
//      `suggestion` so the first wave of findings is visible without being
//      fatal.
//
// A `DiagnosticAnalyzer` is an ordinary class, and that is the property that
// makes the pilot worth running: the same class is usable in BOTH places.
// `Compilation.WithAnalyzers(...)` runs it in-process against a compilation
// the test builds — which is what this file's tests do — and the compiler can
// load the very same class as an analyzer later. The promotion is a file move,
// not a rewrite.
//
// THE DIAGNOSTIC IDS ARE PER RULE, NOT PER SEVERITY
// -------------------------------------------------
// One id per rule is a requirement, not a style choice: `.editorconfig`
// configures severity BY ID, so a rule that cannot be switched off without
// switching off its neighbours will be switched off entirely, together with
// them. (This is also the concrete reason a declarative selector engine with
// three shared ids cannot host this rule set — see #1729.)

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

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
///         class therefore reports every dynamic-code site and lets the caller
///         decide, which is why the pilot's test filters by project rather
///         than the analyzer filtering by itself.
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
    ///     <c>Assembly.LoadFrom</c>, <c>Assembly.LoadFile</c> or
    ///     <c>AppDomain.DefineDynamicAssembly</c>.
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
            || (ownerName == AppDomainTypeName && method.Name == "DefineDynamicAssembly");

        if (banned)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Rule, invocation.GetLocation(), ownerName + "." + method.Name));
        }
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

        // The identifier inside a generic name is the generic name's business;
        // grading both would report one type twice.
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
        CastExpressionSyntax => true,
        ObjectCreationExpressionSyntax => true,
        BaseTypeSyntax => true,
        TypeOfExpressionSyntax => true,
        TypeArgumentSyntax => true,
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

        return type.ContainingNamespace?.ToDisplayString() == ReflectionEmitNamespaceName;
    }
}

/// <summary>
///     HARCH002 — a namespace root is owned by the project that declares it.
/// </summary>
/// <remarks>
///     <para>
///         The invariant is #452's: a namespace is a layer's vocabulary. Two
///         assemblies declaring the same root means a consumer writing
///         <c>using Harbor.Abstractions.Providers;</c> binds a contract and an
///         implementation at once and cannot tell, by reading, which side of
///         the pyramid the type it just named came from.
///     </para>
///     <para>
///         The text rule that states this invariant needs a THIRD rule (R3) to
///         say that every namespace declaration in the product tree is
///         attributed to at least one assembly — because a file walk cannot
///         say which assembly declares a namespace. It cannot: the shared
///         source folders are compiled into several assemblies at once, and the
///         file walk finds them under a directory that owns no project. This
///         analyzer is HANDED the answer: one compilation is one assembly, and
///         <c>compilation.AssemblyName</c> is the declaring project.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NamespaceOwnershipAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id this rule reports under.</summary>
    public const string DiagnosticId = "HARCH002";

    /// <summary>The rule as the compiler sees it.</summary>
    /// <remarks>
    ///     Positional for the same reason as HARCH001's descriptor: (id, title,
    ///     messageFormat, category, defaultSeverity, isEnabledByDefault,
    ///     description, helpLinkUri, customTags).
    /// </remarks>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A namespace root is owned by the project that declares it",
        "'{0}' is declared by '{1}', which does not own that namespace root",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "A namespace is the layer's vocabulary. Two assemblies declaring the same root means a consumer cannot tell, by reading, which side of the pyramid the type it named came from.",
        null,
        []);

    /// <summary>
    ///     The roots this rule owns, and the projects allowed to declare under
    ///     them.
    /// </summary>
    /// <remarks>
    ///     The table is DATA, not code: it is the one fact about ownership that
    ///     no compiler can supply, and the shape of the exception is a
    ///     decision, not a derivation.
    /// </remarks>
    internal static readonly (string Root, string[] Owners)[] OwnedRoots =
    [
        ("Harbor.Abstractions", ["Harbor.Abstractions", "Harbor.Abstractions.Contracts", "Harbor.Extensions"]),
        ("Harbor.Registries", ["Harbor.Registries"]),
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(
            AnalyzeNamespace,
            SyntaxKind.NamespaceDeclaration,
            SyntaxKind.FileScopedNamespaceDeclaration);
    }

    /// <summary>
    ///     A namespace declaration under an owned root, in a project that does
    ///     not own it.
    /// </summary>
    private static void AnalyzeNamespace(SyntaxNodeAnalysisContext context)
    {
        // Only the OUTERMOST declaration of a file: a nested namespace is part
        // of its parent's full name, and reporting both would count one
        // declaration twice.
        foreach (SyntaxNode ancestor in context.Node.Ancestors())
        {
            if (ancestor is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax)
            {
                return;
            }
        }

        string? fullName = FullNameOf(context.Node);
        if (fullName is null)
        {
            return;
        }

        string assembly = context.Compilation.AssemblyName ?? string.Empty;

        foreach ((string root, string[] owners) in OwnedRoots)
        {
            if (!IsUnder(fullName, root))
            {
                continue;
            }

            if (Array.IndexOf(owners, assembly) >= 0)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), fullName, assembly));
            return;
        }
    }

    /// <summary>
    ///     The fully qualified name of a namespace declaration, enclosing
    ///     namespaces included.
    /// </summary>
    private static string? FullNameOf(SyntaxNode node)
    {
        string own = node switch
        {
            NamespaceDeclarationSyntax declared => declared.Name.ToString(),
            FileScopedNamespaceDeclarationSyntax fileScoped => fileScoped.Name.ToString(),
            _ => string.Empty,
        };

        if (own.Length == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            if (ancestor is NamespaceDeclarationSyntax declared)
            {
                parts.Add(declared.Name.ToString());
            }
            else if (ancestor is FileScopedNamespaceDeclarationSyntax fileScoped)
            {
                parts.Add(fileScoped.Name.ToString());
            }
        }

        parts.Reverse();
        parts.Add(own);

        return string.Join(".", parts);
    }

    /// <summary>
    ///     Whether a namespace name sits under a root, compared SEGMENT BY
    ///     SEGMENT: <c>Harbor.AbstractionsExtra</c> is not under
    ///     <c>Harbor.Abstractions</c>.
    /// </summary>
    private static bool IsUnder(string fullName, string root)
    {
        string[] nameParts = fullName.Split('.');
        string[] rootParts = root.Split('.');

        if (nameParts.Length < rootParts.Length)
        {
            return false;
        }

        for (int i = 0; i < rootParts.Length; i++)
        {
            if (!string.Equals(nameParts[i], rootParts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
