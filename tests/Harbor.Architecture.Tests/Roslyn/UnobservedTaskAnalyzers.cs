// UnobservedTaskAnalyzers.cs — HARCH003: a Task that nobody observes.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #1852 asked for the fire-and-forget rule to stop being a SPELLING
// match. The rule it meant is `AvaloniaFireAndForgetRules.cs`, whose detector
// asks "does this line contain a call whose NAME looks async-shaped?":
//
//     (?:\w*Async|Task\.Run|Task\.Delay|Task\.WhenAll|Task\.Factory\.StartNew)\s*\(
//     |\.AsTask\s*\(\)
//     |\.ContinueWith\s*\(
//
// That is a question about a token. This file asks the other question: "is
// the VALUE of this expression a Task?" — which is a question about a TYPE,
// and a type can be resolved.
//
// The two questions disagree in both directions, and both directions matter:
//
//   * A method called `Fetch()` that returns a Task is an unobserved Task.
//     The spelling matcher cannot see it; the type can.
//   * A method called `DoWorkAsync()` that returns `int` is NOT a Task. The
//     spelling matcher flags it — a false positive on correct code, which is
//     how a guard gets deleted — and the type does not.
//
// The house pattern for this class of rule is the Roslyn pilot (#1729),
// whose two rules now live in `src/Harbor.Arch.Analyzers/`: one diagnostic id
// per rule, a descriptor
// built POSITIONALLY (the argument names of `DiagnosticDescriptor`'s
// constructors are not part of any contract this project can check, and a
// named argument that turns out to be spelled differently is a build break in
// a project that cannot be built locally), the message naming the symbol, and
// the analyzer reporting EVERYTHING so that the CALLER owns the scope.
//
// THE TWO SHAPES — AND WHAT IS DELIBERATELY NOT ONE OF THEM
// --------------------------------------------------------
//   1. The explicit discard: `_ = <Task-typed expression>`. The value is
//      thrown away, so nobody can await it, return it, store it or observe
//      its exception.
//   2. The expression-bodied void member: `public void M() => Produce();`,
//      where `Produce()` returns a Task. There is no `_ =` to grep for; the
//      compiler accepts it in a void member without a word, which is exactly
//      how the worst site in the desktop shell survived its original audit.
//
// Shape 2 is the shape the retired text rule covered, and the two rules cover
// the same two shapes. The gap that is NOT closed here is the BLOCK-bodied
// void member (`public void M() { Produce(); }`): the retired rule did not
// cover it either, so closing it is new work that needs its own audit of
// every void member in the tree — and an audit that cannot be run locally is
// an audit that lands as a surprise. The compiler already covers the async
// case (CS4014). The honest statement of the limit is in the file this one
// replaces, and it is a limit of the RULE, not of this analyzer.
//
// WHAT THE ANALYZER DOES NOT KNOW
// -------------------------------
// Whether a project is inside the rule's perimeter, and whether a given site
// is an accepted exception. An analyzer sees ONE compilation; a perimeter is a
// statement about a set of projects, and an exception is a statement about a
// site. Both belong to the caller — `UnobservedTaskRuleTests.cs` — which
// owns the scope and the gate.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     HARCH003 — a Task-typed value is discarded and nobody will observe its
///     outcome.
/// </summary>
/// <remarks>
///     <para>
///         The defect is §FP-003's: a fault on a Task nobody observes is not
///         reported anywhere. .NET does NOT surface an unobserved fault at the
///         throw site — the exception is parked on the Task, and the
///         process-wide <c>TaskScheduler.UnobservedTaskException</c> event
///         fires only at finalization, which for a process that lives for
///         hours is effectively never.
///     </para>
///     <para>
///         The verdict is a TYPE verdict. The expression's static type is
///         resolved, and the type is asked whether it is a Task — including a
///         type DERIVED from <c>Task</c>, which is the shape a spelling match
///         cannot reach at all.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnobservedTaskAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id this rule reports under.</summary>
    public const string DiagnosticId = "HARCH003";

    /// <summary>The rule as the compiler sees it.</summary>
    /// <remarks>
    ///     The nine-argument constructor is used positionally on purpose, for
    ///     the same reason as the promoted descriptors: the argument NAMES of
    ///     the constructors are not part of a contract this project can
    ///     check. The order is (id, title, messageFormat, category,
    ///     defaultSeverity, isEnabledByDefault, description, helpLinkUri,
    ///     customTags).
    /// </remarks>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A Task is discarded without an observer",
        "'{0}' is discarded and nobody observes it: a fault on it is lost",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "A fault on a Task nobody observes is reported nowhere: .NET parks the exception on the Task and only raises TaskScheduler.UnobservedTaskException at finalization, which for a long-lived process is never. Await the Task, return it, store it, or route it through TaskFireAndForget.Forget(task, ex => log(ex)).",
        null,
        []);

    /// <summary>The namespace the BCL's Task types live in.</summary>
    private const string TasksNamespaceName = "System.Threading.Tasks";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(AnalyzeArrowExpression, SyntaxKind.ArrowExpressionClause);
    }

    /// <summary>
    ///     The explicit discard: <c>_ = expr</c> where <paramref name="expr" />
    ///     is a Task.
    /// </summary>
    /// <remarks>
    ///     The left side is the identifier <c>_</c>. A DECLARED variable named
    ///     <c>_</c> is a store rather than a discard, and the caller owns that
    ///     exception — the rule reports the shape, and the shape is the one
    ///     every C# programmer reads as "the value is thrown away".
    /// </remarks>
    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;

        if (assignment.Left is not IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            return;
        }

        if (!IsTaskLike(
                context.SemanticModel.GetTypeInfo(assignment.Right, context.CancellationToken).Type,
                out string typeName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.Right.GetLocation(), typeName));
    }

    /// <summary>
    ///     The expression-bodied void member: <c>public void M() =&gt; Produce();</c>,
    ///     where <c>Produce()</c> returns a Task.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only a MEMBER's expression body counts. A lambda's body is an
    ///         expression too, and a lambda's shape is decided by its TARGET,
    ///         not by the member that happens to contain it.
    ///     </para>
    ///     <para>
    ///         A member that returns something else cannot drop a Task: the
    ///         expression's type would not compile. An ASYNC member is left
    ///         alone: the compiler already reports a bare Task call there
    ///         (CS4014), and a rule that duplicates the compiler is a rule
    ///         that has to be demoted when the compiler's severity changes.
    ///     </para>
    /// </remarks>
    private static void AnalyzeArrowExpression(SyntaxNodeAnalysisContext context)
    {
        var arrow = (ArrowExpressionClauseSyntax)context.Node;

        if (arrow.Parent is not (MethodDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AccessorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConstructorDeclarationSyntax))
        {
            return;
        }

        // `_ = expr` in an expression body is the EXPLICIT discard, and the
        // assignment rule already reports it. Reporting it twice would make
        // the finding count mean nothing.
        if (arrow.Expression is AssignmentExpressionSyntax)
        {
            return;
        }

        if (context.SemanticModel.GetEnclosingSymbol(arrow.SpanStart, context.CancellationToken) is not IMethodSymbol method)
        {
            return;
        }

        if (method.ReturnType.SpecialType != SpecialType.System_Void || method.IsAsync)
        {
            return;
        }

        if (!IsTaskLike(
                context.SemanticModel.GetTypeInfo(arrow.Expression, context.CancellationToken).Type,
                out string typeName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, arrow.Expression.GetLocation(), typeName));
    }

    /// <summary>
    ///     Whether a type is a Task: <c>Task</c>, <c>Task&lt;T&gt;</c>,
    ///     <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c>, or anything DERIVED from
    ///     <c>Task</c>.
    /// </summary>
    /// <param name="type">The resolved type, or <c>null</c> when it could not be resolved.</param>
    /// <param name="typeName">The name the diagnostic reports — the type itself, so the message names the symbol.</param>
    private static bool IsTaskLike(ITypeSymbol? type, out string typeName)
    {
        typeName = string.Empty;

        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        // A type PARAMETER is not a Task. Its shape is not known here, and a
        // generic member that turns out to produce one is the caller's
        // exception to own, not this rule's to guess.
        if (named.TypeKind == TypeKind.TypeParameter)
        {
            return false;
        }

        if (IsFrameworkTask(named, "Task") || IsFrameworkTask(named, "ValueTask"))
        {
            typeName = named.ToDisplayString();
            return true;
        }

        // A type DERIVED from Task. This is the shape a spelling match
        // cannot reach: the derived type's own name says nothing about
        // being asynchronous, and the base type is the only fact that says
        // it is.
        for (INamedTypeSymbol? baseType = named.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (IsFrameworkTask(baseType, "Task"))
            {
                typeName = named.ToDisplayString();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether a named type is the framework's <paramref name="simpleName" />
    ///     or its generic form.
    /// </summary>
    /// <remarks>
    ///     The metadata name of the generic form carries the arity
    ///     (<c>Task`1</c>), and the display name of the generic form carries
    ///     the type argument (<c>Task&lt;int&gt;</c>) — so neither the
    ///     display name nor the metadata name alone identifies both shapes.
    /// </remarks>
    private static bool IsFrameworkTask(INamedTypeSymbol type, string simpleName) =>
        type.ContainingNamespace?.ToDisplayString() == TasksNamespaceName
        && (type.MetadataName == simpleName || type.MetadataName == simpleName + "`1");
}
