// HttpClientCreationAnalyzer.cs — HARCH005 and HARCH006: where an HTTP client
// is created, answered by the TYPE and not by the spelling.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #1854 found an HTTP client minted per OAuth request, and the rule that
// guards it (`tests/Harbor.Architecture.Tests/HttpClientReuseRules.cs`) used to
// ask its two questions with a REGEX over the comment-stripped source:
//
//   1. "Is a client created HERE?" — the token `new HttpClient` or
//      `new SocketsHttpHandler`.
//   2. "Is the creation inside a DELEGATE?" — the token `=> new HttpClient` on
//      the same line.
//
// Both are questions about a TOKEN, and a token is a spelling. This file asks
// the two questions about a TYPE:
//
//   1. The created object is resolved by symbol. `new HttpClient()`,
//      `new System.Net.Http.HttpClient()`, `new HttpClient(handler)` and the
//      target-typed `HttpClient client = new();` are ONE fact, and the
//      analyzer sees one fact. The regex saw four spellings and needed a
//      near-miss control for each.
//   2. "Inside a delegate" is a question about the SYNTAX TREE: is the
//      creation under an anonymous function? The regex could only see an
//      expression-bodied lambda, because that is the shape whose token
//      sequence starts with `=>`.
//
// THE TWO BLIND SPOTS THE TEXT MATCHER HAD, AND WHAT HAPPENS TO THEM
// ------------------------------------------------------------------
//   * A name in a COMMENT was a hit. The regex needed a comment stripper, and
//     the stripper is a lexer of its own. The analyzer never looks at trivia.
//   * A name in a STRING LITERAL was a hit. `CollectiblePluginLoadContext.cs`
//     carries the text `new HttpClient()` in a comment today; the analyzer does
//     not see it, and neither does the compiler.
//
// THE ANALYZER DOES NOT KNOW
// --------------------------
// Whether a project is inside the rule's perimeter, and whether a given site
// is an accepted exception. An analyzer sees ONE compilation; a perimeter is a
// statement about a set of projects, and an exception is a statement about a
// site. Both belong to the caller — `HttpClientReuseRules` — which owns the
// scope, the allowance table and the gate. This analyzer reports EVERY site it
// sees, and the caller decides which ones are allowed.
//
// WHY TWO IDS AND NOT ONE
// -----------------------
// The two rules are different rules, and the id is the switch: "a client is
// created" and "a client is created inside a delegate" have to be demotable
// independently, and one id per rule is what makes that possible
// (`docs/ANALYZERS_AUTHORING.md` §"One id per rule, and one rule per id").

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Arch.Analyzers;

/// <summary>
///     HARCH005 — an HTTP client is created at this site.
///     HARCH006 — the creation sits inside a delegate, so it is minted per call.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HttpClientCreationAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id the creation rule reports under.</summary>
    public const string CreationDiagnosticId = "HARCH005";

    /// <summary>The diagnostic id the per-call rule reports under.</summary>
    public const string DelegateDiagnosticId = "HARCH006";

    /// <summary>
    ///     The rule as the compiler sees it.
    /// </summary>
    /// <remarks>
    ///     The nine-argument constructor is used positionally on purpose: the
    ///     argument NAMES of the descriptor's constructors are not part of any
    ///     contract this project can check, and a named argument that turns out
    ///     to be spelled differently is a build break in a project that cannot be
    ///     built locally. The order is (id, title, messageFormat, category,
    ///     defaultSeverity, isEnabledByDefault, description, helpLinkUri,
    ///     customTags).
    ///     <para>
    ///         The ninth argument is NOT written here, and the reason is where
    ///         this file lives: the ninth is a <c>params</c> array of custom
    ///         tags, and an empty array passed to a params parameter is what
    ///         S3878 forbids in a product tree. Omitting it leaves the same
    ///         descriptor: the tags stay empty, because nothing was passed.
    ///     </para>
    /// </remarks>
    private static readonly DiagnosticDescriptor CreationRule = new(
        CreationDiagnosticId,
        "An HTTP client is created here",
        "'{0}' is created here: one client, owned by a named owner, built once (#1854)",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "A client built per call is a private connection pool per call, with its own DNS lifetime and no owner to dispose it (#1854). Take IHttpClientFactory.CreateClient(name) for a provider, the shared McpHttpClientFactory for a remote MCP transport, or the process-wide client the allowance table names.",
        null);

    /// <summary>
    ///     The per-call rule as the compiler sees it.
    /// </summary>
    /// <remarks>
    ///     The descriptor is built positionally for the same reason as the one
    ///     above, and the ninth argument is omitted for the same reason.
    /// </remarks>
    private static readonly DiagnosticDescriptor DelegateRule = new(
        DelegateDiagnosticId,
        "An HTTP client is created inside a delegate",
        "'{0}' is created inside a delegate, and a delegate is invoked per call (#1854)",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "A delegate that CREATES a client mints one per invocation: a private connection pool per call, with its own DNS lifetime and no owner to dispose it (#1854). Build the client where its owner lives and keep it — a field, a lazy field, or the one client the owning component already has.",
        null);

    /// <summary>The client type the rule is about.</summary>
    private const string HttpClientTypeName = "System.Net.Http.HttpClient";

    /// <summary>The handler type the rule is about: one layer below the client.</summary>
    private const string SocketsHandlerTypeName = "System.Net.Http.SocketsHttpHandler";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CreationRule, DelegateRule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Both spellings of a construction: `new HttpClient(…)` and the
        // target-typed `HttpClient client = new();`. The second one is a
        // DIFFERENT syntax node, and it is the same defect wearing a different
        // hat — the text rule needed a second regex for it, and this analyzer
        // needs a second SyntaxKind.
        context.RegisterSyntaxNodeAction(
            AnalyzeCreation,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression);
    }

    /// <summary>
    ///     A client construction, in either spelling.
    /// </summary>
    /// <remarks>
    ///     The type is resolved through the semantic model, so the created
    ///     object is the same fact whatever the spelling. A target-typed
    ///     <c>new()</c> has no type of its own: the model gives the type the
    ///     conversion targets, which is the client.
    /// </remarks>
    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context)
    {
        var expression = (ExpressionSyntax)context.Node;

        ITypeSymbol? created = context.SemanticModel
            .GetTypeInfo(expression, context.CancellationToken)
            .Type;

        if (created is not INamedTypeSymbol named)
        {
            return;
        }

        string typeName = named.ToDisplayString();
        if (typeName != HttpClientTypeName && typeName != SocketsHandlerTypeName)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(CreationRule, expression.GetLocation(), typeName));

        if (IsInsideADelegate(expression))
        {
            context.ReportDiagnostic(Diagnostic.Create(DelegateRule, expression.GetLocation(), typeName));
        }
    }

    /// <summary>
    ///     Whether the node sits inside an anonymous function — a lambda or an
    ///     anonymous method.
    /// </summary>
    /// <remarks>
    ///     The text rule this analyzer replaces answered this question by
    ///     matching <c>=&gt; new HttpClient</c> on one line, which is the shape
    ///     of an EXPRESSION-BODIED lambda and of nothing else. A lambda with a
    ///     block body, and an anonymous method, were both invisible to it — and
    ///     both are the defect the rule exists for, because a delegate that
    ///     creates a client is invoked per call whichever way its body is
    ///     written.
    /// </remarks>
    private static bool IsInsideADelegate(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }
}
