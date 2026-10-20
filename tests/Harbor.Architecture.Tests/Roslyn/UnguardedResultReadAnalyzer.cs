// UnguardedResultReadAnalyzer.cs — HARCH004: the unguarded-Result rule as a
// TYPE-resolving analyzer with a dominance model, replacing the regex spelling
// of `tests/Harbor.Architecture.Tests/UnguardedResultReadRules.cs`.
//
// WHAT THE TEXT RULE DETECTS TODAY, AND WHERE IT IS BLIND
// -------------------------------------------------------
// `UnguardedResultReadRules.cs` (788 lines) bans three shapes in `src/` +
// `apps/`:
//
//   AWAIT-VALUE        `(await …).Value`
//   GETRESULT-VALUE    `…GetResult().Value`
//   REGISTRY-ZERO-INDEX `GetAllAgents()[0]`
//
// The first two exist ONLY because `CSharpFunctionalExtensions.Analyzers`
// (CFE0001, adopted repo-wide) cannot see them. CFE0001 resolves the
// RECEIVER of `.Value` to an `IPropertySymbol` on
// `CSharpFunctionalExtensions.Result` and asks "is this a Result?". For
// `(await store.LoadAsync()).Value` the receiver is a parenthesized await
// expression — an expression, not a symbol — so the question cannot be
// asked and the node is skipped. The sync spelling
// `store.LoadAsync().GetAwaiter().GetResult().Value` has an invocation as its
// receiver and is skipped for the same reason.
//
// So the text rule is a workaround for a hole in another analyzer, and the
// workaround is TEXTUAL. That costs three things:
//
//   1. It matches a SPELLING, not an invariant. `(await …).Value` and
//      `…GetResult().Value` are two regexes for ONE read of `Result<T>.Value`,
//      and a third spelling (`MakeResult().Value`) is invisible to both.
//   2. It cannot ask whether the Result was CHECKED. The regex fires on the
//      form, whether or not an `IsSuccess`/`IsFailure` check dominates the
//      read. That is why the rule's prose says "read it through a local you
//      have checked" instead of "check it".
//   3. It needs its own comment stripper, its own controls per FORM, and it
//      cannot see a `.Value` read that is genuinely guarded.
//
// WHAT THIS ANALYZER RESOLVES INSTEAD
// -----------------------------------
// The blind spot is not about the syntax tree; it is about WHICH symbol is
// resolved. CFE0001 resolves the RECEIVER. This analyzer resolves the
// PROPERTY BEING READ:
//
//     memberAccess.Name  ->  IPropertySymbol `Value`
//                         ->  ContainingType == CSharpFunctionalExtensions.Result<T>
//
// `Value`'s containing type is `Result<T>` whatever stands in front of it —
// an await, an invocation, a field, a local, a conditional access. The type
// is a fact about the PROPERTY, not about the expression in front of it, so
// the shape CFE0001 cannot see is the shape this rule sees first.
//
// The second half of the rule asks the question the text rule cannot: "is
// there an `IsSuccess`/`IsFailure` check on the SAME value that DOMINATES
// this read?" That is a question about the STRUCTURE around the read (the
// branches, the early exits, the short circuits, the patterns) and about the
// TYPES of the values involved.
//
// WHY THE DOMINANCE WALK IS OVER THE SYNTAX TREE
// ------------------------------------------------
// Roslyn 5.x's operation tree has no `if` statement at all — `OperationKind`
// has no `IfStatement`, and `IIfStatementOperation` no longer exists (the `if`
// lives in the flow graph, which is not part of the public operation API). A
// dominance analysis over the operation tree would therefore have to be
// written against a CFG that is not public API. The structure a dominance
// analysis needs — branches, blocks, early exits, short circuits, patterns —
// is all in the SYNTAX tree, and every symbol it needs is resolved by the
// semantic model. So: the syntax tree carries the structure, the semantic
// model carries the types, and the TYPE-RESOLVED property is what makes the
// rule see the blind spot at all.
//
// THE DOMINANCE RULE, PRECISELY
// -----------------------------
// A SITE is a read of the `Value` property of `CSharpFunctionalExtensions.
// Result` or `Result<T>`, resolved by the property's CONTAINING TYPE (never
// by the spelling "Value"), anywhere in the compiled unit.
//
// A site is REPORTED unless the fact "that value is a SUCCESS" holds at the
// read. "The same value" means the receiver denotes the same storage
// location — a local, a parameter, a field or a property — identified by its
// name.
//
// The fact is established by the structure around the read:
//
//   (1) BRANCH CONTAINMENT. The read lies in the THEN branch of an `if`
//       whose condition tests `V.IsSuccess`, or in the ELSE branch of an `if`
//       whose condition tests `V.IsFailure`. The same holds for a ternary:
//       `V.IsSuccess ? … V.Value … : …` and `V.IsFailure ? … : … V.Value …`.
//       The mirror image does NOT protect a read: `V.IsFailure ? … V.Value
//       … : …` reads the value of a FAILED result.
//   (2) EARLY EXIT. In the block that contains the read, an EARLIER statement
//       is an `if` whose condition tests `V.IsFailure` and whose THEN branch
//       leaves the flow (return / throw / continue / break) and which has no
//       ELSE. The only way to reach the code after such an `if` is the
//       condition being false, i.e. V is a success. The mirror image holds:
//       `if (V.IsSuccess) { return; }` leaves V known to be a FAILURE, which
//       does NOT protect a `.Value` read.
//   (3) SHORT CIRCUIT. The read is the right operand of `V.IsSuccess && …`
//       (V is a success there), or the right operand of `V.IsFailure || …`
//       (the right operand runs only when the left one is false, so V is a
//       success there).
//   (4) PATTERN. The read's value is a pattern variable declared by
//       `is { IsSuccess: true }` (or `is { IsFailure: false }`) and the read
//       lies in the branch the pattern matched.
//
// Facts are dropped when the value is REASSIGNED between the check and the
// read: `if (V.IsFailure) return; V = other(); use(V.Value);` is reported.
//
// WHY THE FACTS ARE ABOUT A NAME
// ------------------------------
// A "value" here is a STORAGE LOCATION: a local, a parameter, a field or a
// property, identified by its symbol's name. Two limits follow, and both are
// stated rather than hidden:
//
//   * Two different values with the same name (a local and a field, or two
//     locals in sibling scopes) share a key. The consequence is a MISSED
//     report, never a false accusation.
//   * A field or a computed property may change between the check and the
//     read without an assignment the analyzer can see. The consequence is
//     again a MISSED report.
//
// Both limits are in the safe direction: the analyzer under-reports rather
// than inventing findings.
//
// WHAT THIS RULE IS NOT
// ---------------------
//   * It does not read `.Error`. `Result<T>.Error` throws on the SUCCESS
//     path, so it needs the INVERTED polarity (a failure must be known), the
//     tree reads it at ~5x the rate of `.Value`, and no gate covers it today.
//     That is a separate rule with a separate baseline, not a widening of
//     this one.
//   * It does not cover `Result<T>` shapes that are hand-rolled (the
//     `HandRolledResultShapeTests` territory) — those are not the
//     `CSharpFunctionalExtensions.Result` type and no symbol resolves to it.
//   * It does not cover the text rule's third form, `GetAllAgents()[0]`. That
//     is not a Result site at all; it is a NAME convention standing in for
//     "index a collection of unknown length with a constant index". The
//     honest type-based rule for it is a different rule with a much wider
//     blast radius, and it stays with the text rule for now.
//
// SEVERITY
// --------
// One id per rule, so `.editorconfig` can configure THIS rule without
// touching its neighbours (the same reason the pilot's HARCH001/HARCH002 are
// separate ids). The analyzer is not a build-time analyzer yet — it runs
// IN-PROCESS from the architecture test suite, the way the pilot's do — so
// there is no `.editorconfig` row to add until promotion.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     HARCH004 — a <c>Result</c> is read without a dominating guard.
/// </summary>
/// <remarks>
///     <para>
///         The rule is about the TYPE and about the STRUCTURE around the read,
///         never about a spelling. A read of <c>Result&lt;T&gt;.Value</c> whose
///         receiver is an await, an invocation or any other computed
///         expression is exactly the shape <c>CFE0001</c> cannot see, and it is
///         reported here — while a read that a dominating
///         <c>IsSuccess</c>/<c>IsFailure</c> check protects is not, which is
///         the thing no text scan can decide.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnguardedResultReadAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id this rule reports under.</summary>
    public const string DiagnosticId = "HARCH004";

    /// <summary>The rule as the compiler sees it.</summary>
    /// <remarks>
    ///     The nine-argument constructor is used positionally, for the same
    ///     reason as the pilot's descriptors (HARCH001/HARCH002): the argument
    ///     NAMES of the descriptor's constructors are not part of the contract
    ///     this project can check. The order is (id, title, messageFormat,
    ///     category, defaultSeverity, isEnabledByDefault, description,
    ///     helpLinkUri, customTags).
    /// </remarks>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A Result is read without a dominating guard",
        "'{0}' is read on '{1}', which no IsSuccess/IsFailure check dominates",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "Result<T>.Value throws ResultFailureException when the result is a failure, so an "
        + "unguarded read turns a reported failure into an unhandled exception. Check the "
        + "result first, or read it through a local you have checked.",
        null,
        []);

    /// <summary>The namespace the <c>Result</c> types live in.</summary>
    private const string ResultNamespace = "CSharpFunctionalExtensions";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The member access, and the PROPERTY behind it: `Value`'s containing
        // type is `Result<T>` whatever the receiver is — an await, an
        // invocation, a field, a local. Resolving the receiver instead (what
        // CFE0001 does) is exactly what makes the await shape invisible.
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    /// <summary>
    ///     A read of <c>Result&lt;T&gt;.Value</c>: reported unless a dominating
    ///     guard establishes that the value is a success.
    /// </summary>
    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        if (memberAccess.Name.Identifier.ValueText != "Value")
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol property)
        {
            return;
        }

        if (!IsResultType(property.ContainingType))
        {
            return;
        }

        string? key = KeyOf(context.SemanticModel, memberAccess.Expression, context.CancellationToken);
        if (key is not null && DominatingFacts(context, memberAccess).IsSuccess(key))
        {
            return;
        }

        object?[] arguments =
        [
            property.ContainingType.ToDisplayString() + "." + property.Name,
            Describe(memberAccess.Expression),
        ];

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation(), arguments));
    }

    /// <summary>
    ///     The facts that hold at a read, gathered by walking the structure
    ///     around it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The walk is upward on purpose. A downward walk would have to
    ///         model every construct to thread a fact environment through them;
    ///         the upward walk asks, for each enclosing construct, the one
    ///         question that matters — "does this construct establish a fact
    ///         about the value at this point?" — and the answer is local to the
    ///         construct.
    ///     </para>
    /// </remarks>
    private static Facts DominatingFacts(SyntaxNodeAnalysisContext context, SyntaxNode read)
    {
        var facts = new Facts();
        SyntaxNode? current = read;

        while (current.Parent is { } parent)
        {
            switch (parent)
            {
                // (1) BRANCH CONTAINMENT — an `if` whose condition tests the value.
                case IfStatementSyntax ifStatement:
                    if (current == ifStatement.Statement)
                    {
                        facts.Merge(ConditionFacts(context, ifStatement.Condition).WhenTrue);
                    }
                    else if (current == ifStatement.Else)
                    {
                        facts.Merge(ConditionFacts(context, ifStatement.Condition).WhenFalse);
                    }

                    break;

                // (1) BRANCH CONTAINMENT — a ternary.
                case ConditionalExpressionSyntax conditional:
                    if (current == conditional.WhenTrue)
                    {
                        facts.Merge(ConditionFacts(context, conditional.Condition).WhenTrue);
                    }
                    else if (current == conditional.WhenFalse)
                    {
                        facts.Merge(ConditionFacts(context, conditional.Condition).WhenFalse);
                    }

                    break;

                // (3) SHORT CIRCUIT — the right operand of `&&` runs only when
                // the left one is true, and the right operand of `||` runs only
                // when the left one is false.
                case BinaryExpressionSyntax andAlso when andAlso.IsKind(SyntaxKind.LogicalAndExpression):
                    if (current == andAlso.Right)
                    {
                        facts.Merge(ConditionFacts(context, andAlso.Left).WhenTrue);
                    }

                    break;

                case BinaryExpressionSyntax orElse when orElse.IsKind(SyntaxKind.LogicalOrExpression):
                    if (current == orElse.Right)
                    {
                        facts.Merge(ConditionFacts(context, orElse.Left).WhenFalse);
                    }

                    break;

                // (4) PATTERN — a switch section whose pattern demands the value.
                case SwitchSectionSyntax section:
                    facts.Merge(SwitchSectionFacts(context, section));
                    break;

                case SwitchExpressionArmSyntax arm:
                    facts.Merge(SwitchArmFacts(context, arm));
                    break;

                // (2) EARLY EXIT — the statements BEFORE the read's own
                // statement in this block may have established a fact.
                case BlockSyntax block:
                    facts.Merge(PrecedingStatementFacts(context, block, current));
                    break;
            }

            current = parent;
        }

        return facts;
    }

    /// <summary>
    ///     The facts the statements before <paramref name="child" /> establish
    ///     inside <paramref name="block" />.
    /// </summary>
    private static Facts PrecedingStatementFacts(SyntaxNodeAnalysisContext context, BlockSyntax block, SyntaxNode child)
    {
        var facts = new Facts();

        foreach (StatementSyntax statement in block.Statements)
        {
            if (statement == child)
            {
                break;
            }

            ApplyStatement(context, statement, facts);
        }

        return facts;
    }

    /// <summary>
    ///     What one statement does to the facts that hold after it.
    /// </summary>
    private static void ApplyStatement(SyntaxNodeAnalysisContext context, StatementSyntax statement, Facts facts)
    {
        // (2) EARLY EXIT. The THEN branch leaves the flow and there is no
        // ELSE, so the only way to reach the code after this `if` is the
        // condition being FALSE.
        if (statement is IfStatementSyntax ifStatement
            && ifStatement.Else is null
            && EndsFlow(ifStatement.Statement))
        {
            facts.Merge(ConditionFacts(context, ifStatement.Condition).WhenFalse);
            return;
        }

        // An assignment may have replaced the value the fact was about.
        InvalidateAssigned(context, statement, facts);
    }

    /// <summary>
    ///     Drops the fact about any value this syntax assigns to.
    /// </summary>
    private static void InvalidateAssigned(SyntaxNodeAnalysisContext context, SyntaxNode node, Facts facts)
    {
        if (node is AssignmentExpressionSyntax assignment)
        {
            facts.Remove(KeyOf(context.SemanticModel, assignment.Left, context.CancellationToken));
            return;
        }

        foreach (SyntaxNode child in node.ChildNodes())
        {
            InvalidateAssigned(context, child, facts);
        }
    }

    /// <summary>
    ///     Whether every path through this branch leaves the flow.
    /// </summary>
    private static bool EndsFlow(StatementSyntax? branch)
    {
        switch (branch)
        {
            case null:
                return false;

            case ReturnStatementSyntax:
                return true;

            case ThrowStatementSyntax:
                return true;

            case ContinueStatementSyntax:
                return true;

            case BreakStatementSyntax:
                return true;

            case BlockSyntax block:
                return block.Statements.Count > 0 && EndsFlow(block.Statements[block.Statements.Count - 1]);

            case IfStatementSyntax ifStatement:
                return ifStatement.Else is not null
                    && EndsFlow(ifStatement.Statement)
                    && EndsFlow(ifStatement.Else.Statement);

            default:
                return false;
        }
    }

    /// <summary>
    ///     What a condition establishes when it is true, and when it is false.
    /// </summary>
    private readonly record struct ConditionOutcome(Facts WhenTrue, Facts WhenFalse);

    /// <summary>
    ///     The facts a condition establishes on each of its two outcomes.
    /// </summary>
    private static ConditionOutcome ConditionFacts(SyntaxNodeAnalysisContext context, ExpressionSyntax? condition)
    {
        var whenTrue = new Facts();
        var whenFalse = new Facts();

        switch (condition)
        {
            case PrefixUnaryExpressionSyntax negated when negated.IsKind(SyntaxKind.LogicalNotExpression):
            {
                ConditionOutcome inner = ConditionFacts(context, negated.Operand);
                return new ConditionOutcome(inner.WhenFalse, inner.WhenTrue);
            }

            // `A && B` is true when BOTH are true: a fact holds when it holds
            // in either operand's true branch. It is false when either is
            // false, so only a fact that holds in BOTH false branches survives.
            case BinaryExpressionSyntax andAlso when andAlso.IsKind(SyntaxKind.LogicalAndExpression):
            {
                ConditionOutcome left = ConditionFacts(context, andAlso.Left);
                ConditionOutcome right = ConditionFacts(context, andAlso.Right);
                return new ConditionOutcome(
                    left.WhenTrue.Union(right.WhenTrue),
                    left.WhenFalse.Intersect(right.WhenFalse));
            }

            // `A || B` is false when BOTH are false: a fact holds when it
            // holds in either operand's false branch. It is true when either
            // is true, so only a fact that holds in BOTH true branches survives.
            case BinaryExpressionSyntax orElse when orElse.IsKind(SyntaxKind.LogicalOrExpression):
            {
                ConditionOutcome left = ConditionFacts(context, orElse.Left);
                ConditionOutcome right = ConditionFacts(context, orElse.Right);
                return new ConditionOutcome(
                    left.WhenTrue.Intersect(right.WhenTrue),
                    left.WhenFalse.Union(right.WhenFalse));
            }
        }

        // A plain `V.IsSuccess` / `V.IsFailure` test.
        if (condition is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.ValueText is "IsSuccess" or "IsFailure"
            && context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is IPropertySymbol property
            && IsResultType(property.ContainingType))
        {
            string? key = KeyOf(context.SemanticModel, memberAccess.Expression, context.CancellationToken);
            if (key is not null)
            {
                bool success = memberAccess.Name.Identifier.ValueText == "IsSuccess";
                whenTrue.Set(key, success ? Fact.Success : Fact.Failure);
                whenFalse.Set(key, success ? Fact.Failure : Fact.Success);
            }
        }

        // A pattern: `V is { IsSuccess: true }`.
        ApplyPatternFacts(context, condition, whenTrue, whenFalse);

        return new ConditionOutcome(whenTrue, whenFalse);
    }

    /// <summary>
    ///     The facts a property PATTERN establishes: <c>is { IsSuccess: true }</c>
    ///     declares that the matched value is a success.
    /// </summary>
    private static void ApplyPatternFacts(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax condition,
        Facts whenTrue,
        Facts whenFalse)
    {
        if (condition is not IsPatternExpressionSyntax isPattern
            || isPattern.Pattern is not RecursivePatternSyntax recursive)
        {
            return;
        }

        ApplyPatternRequirement(context, recursive, isPattern.Expression, whenTrue, whenFalse);
    }

    /// <summary>
    ///     The facts a switch SECTION's pattern establishes.
    /// </summary>
    private static Facts SwitchSectionFacts(SyntaxNodeAnalysisContext context, SwitchSectionSyntax section)
    {
        var facts = new Facts();

        ExpressionSyntax? switched = (section.Parent as SwitchStatementSyntax)?.Expression;

        foreach (SwitchLabelSyntax label in section.Labels)
        {
            if (label is CasePatternSwitchLabelSyntax patternLabel
                && patternLabel.Pattern is RecursivePatternSyntax recursive)
            {
                // The case body is entered only when the pattern MATCHED, so
                // only the facts the pattern demands hold there.
                ApplyPatternRequirement(context, recursive, switched, facts, new Facts());
            }
        }

        return facts;
    }

    /// <summary>
    ///     The facts a switch EXPRESSION arm's pattern establishes.
    /// </summary>
    private static Facts SwitchArmFacts(SyntaxNodeAnalysisContext context, SwitchExpressionArmSyntax arm)
    {
        var facts = new Facts();

        ExpressionSyntax? switched = (arm.Parent as SwitchExpressionSyntax)?.GoverningExpression;

        if (arm.Pattern is RecursivePatternSyntax recursive)
        {
            // The arm is entered only when the pattern MATCHED.
            ApplyPatternRequirement(context, recursive, switched, facts, new Facts());
        }

        return facts;
    }

    /// <summary>
    ///     Reads the <c>IsSuccess</c>/<c>IsFailure</c> requirement out of a
    ///     pattern and records what it means for the matched value.
    /// </summary>
    private static void ApplyPatternRequirement(
        SyntaxNodeAnalysisContext context,
        RecursivePatternSyntax recursive,
        ExpressionSyntax? switched,
        Facts whenTrue,
        Facts whenFalse)
    {
        // The pattern VARIABLE is the storage the fact is about; without a
        // designation the fact is about the matched value.
        string? key = recursive.Designation is SingleVariableDesignationSyntax designation
            ? designation.Identifier.ValueText
            : KeyOf(context.SemanticModel, switched, context.CancellationToken);

        if (key is null || recursive.PropertyPatternClause is not { } clause)
        {
            return;
        }

        foreach (SubpatternSyntax subpattern in clause.Subpatterns)
        {
            // A property subpattern reads `Name: pattern`. The typed accessor
            // for the NAME is not a stable surface — Roslyn 4.x called the
            // node `PropertySubpatternSyntax` and 5.x calls it
            // `SubpatternSyntax` — so the requirement is read from the
            // subpattern's own two-token shape. Only `IsSuccess: true` and
            // `IsFailure: false` establish a fact; every other shape
            // establishes none, which is the safe direction (a missed report,
            // never a false accusation). The TYPE the rule judges is still
            // resolved by symbol.
            string shape = subpattern.ToString();
            int colon = shape.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string name = shape[..colon].Trim();
            string required = shape[(colon + 1)..].Trim();

            bool demandsSuccess =
                (name == "IsSuccess" && required == "true")
                || (name == "IsFailure" && required == "false");

            bool demandsFailure =
                (name == "IsSuccess" && required == "false")
                || (name == "IsFailure" && required == "true");

            if (demandsSuccess)
            {
                whenTrue.Set(key, Fact.Success);
                whenFalse.Set(key, Fact.Failure);
            }
            else if (demandsFailure)
            {
                whenTrue.Set(key, Fact.Failure);
                whenFalse.Set(key, Fact.Success);
            }
        }
    }

    /// <summary>
    ///     The key of the value a receiver denotes: the storage location's
    ///     name, or nothing when the receiver is a computed expression.
    /// </summary>
    /// <remarks>
    ///     A computed receiver (an invocation, an await, a conditional
    ///     expression) has NO key, and therefore no fact can ever protect it.
    ///     That is correct rather than convenient: two syntactically equal
    ///     calls are two different results, so a check on one cannot protect
    ///     the other.
    /// </remarks>
    private static string? KeyOf(SemanticModel model, ExpressionSyntax? expression, CancellationToken cancellationToken)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        if (expression is null)
        {
            return null;
        }

        if (model.GetSymbolInfo(expression, cancellationToken).Symbol is not { } symbol)
        {
            return null;
        }

        return symbol.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.Field or SymbolKind.Property
            ? symbol.Name
            : null;
    }

    /// <summary>
    ///     Whether the type is <c>CSharpFunctionalExtensions.Result</c> or
    ///     <c>Result&lt;T&gt;</c> — the METADATA name, so the generic and the
    ///     non-generic cannot be confused with a same-named type elsewhere.
    /// </summary>
    private static bool IsResultType(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        INamedTypeSymbol definition = type.OriginalDefinition;

        return definition.ContainingNamespace?.ToDisplayString() == ResultNamespace
            && definition.MetadataName is "Result" or "Result`1";
    }

    /// <summary>A short description of the receiver, for the message.</summary>
    private static string Describe(ExpressionSyntax? expression)
    {
        if (expression is null)
        {
            return "<implicit this>";
        }

        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        if (expression is null)
        {
            return "<implicit this>";
        }

        string text = expression.ToString();
        return text.Length <= 60 ? text : text[..57] + "...";
    }

    /// <summary>What is known about one value.</summary>
    private enum Fact
    {
        /// <summary>Nothing is known.</summary>
        Unknown = 0,

        /// <summary>The value is a success: reading its <c>Value</c> is safe.</summary>
        Success = 1,

        /// <summary>The value is a failure: reading its <c>Value</c> throws.</summary>
        Failure = 2,
    }

    /// <summary>
    ///     The facts that hold about the values in scope, keyed by storage
    ///     name.
    /// </summary>
    private sealed class Facts
    {
        private readonly Dictionary<string, Fact> _facts = new(StringComparer.Ordinal);

        /// <summary>Whether the value is known to be a success.</summary>
        internal bool IsSuccess(string key)
            => _facts.TryGetValue(key, out Fact fact) && fact == Fact.Success;

        /// <summary>Records what is known about a value.</summary>
        internal void Set(string key, Fact fact) => _facts[key] = fact;

        /// <summary>Forgets what was known about a value.</summary>
        internal void Remove(string? key)
        {
            if (key is not null)
            {
                _facts.Remove(key);
            }
        }

        /// <summary>Adds the other facts; the other side wins on a conflict.</summary>
        internal void Merge(Facts other)
        {
            foreach ((string key, Fact fact) in other._facts)
            {
                _facts[key] = fact;
            }
        }

        /// <summary>
        ///     The facts that hold in BOTH: only a fact both sides agree on
        ///     survives.
        /// </summary>
        internal Facts Intersect(Facts other)
        {
            var result = new Facts();

            foreach ((string key, Fact fact) in _facts)
            {
                if (other._facts.TryGetValue(key, out Fact otherFact) && otherFact == fact)
                {
                    result._facts[key] = fact;
                }
            }

            return result;
        }

        /// <summary>
        ///     The facts that hold in EITHER side, except the ones the two
        ///     sides disagree about — a disagreement means neither holds.
        /// </summary>
        internal Facts Union(Facts other)
        {
            var result = new Facts();

            foreach ((string key, Fact fact) in _facts)
            {
                result._facts[key] = fact;
            }

            foreach ((string key, Fact fact) in other._facts)
            {
                if (result._facts.TryGetValue(key, out Fact existing) && existing != fact)
                {
                    result._facts.Remove(key);
                    continue;
                }

                result._facts[key] = fact;
            }

            return result;
        }
    }
}
