// UnguardedResultReadAnalyzer.cs — HARCH003: the unguarded-Result rule as a
// TYPE-resolving analyzer with a dominance model, replacing the regex spelling
// of `tests/Harbor.Architecture.Tests/UnguardedResultReadRules.cs`.
//
// WHAT THE TEXT RULE DETECTS TODAY, AND WHERE IT IS BLIND
// -------------------------------------------------------
// `UnguardedResultReadRules.cs` (788 lines) bans three shapes in `src/` +
// `apps/`:
//
//   AWAIT-VALUE       `(await …).Value`
//   GETRESULT-VALUE   `…GetResult().Value`
//   REGISTRY-ZERO-INDEX `GetAllAgents()[0]`
//
// The first two exist ONLY because `CSharpFunctionalExtensions.Analyzers`
// (CFE0001, adopted repo-wide) cannot see them. CFE0001 registers on
// `SimpleMemberAccessExpression` and resolves the RECEIVER to an
// `IPropertySymbol` on `CSharpFunctionalExtensions.Result`; for
// `(await store.LoadAsync()).Value` the receiver is a parenthesized await
// expression, so there is no symbol to resolve and the node is skipped. The
// sync spelling `store.LoadAsync().GetAwaiter().GetResult().Value` has an
// invocation as its receiver and is skipped for the same reason.
//
// So the text rule is a workaround for a hole in another analyzer, and the
// workaround is TEXTUAL. That costs three things:
//
//   1. It matches a SPELLING, not an invariant. `(await …).Value` and
//      `…GetResult().Value` are two regexes for ONE operation — the read of
//      `Result<T>.Value` — and a third spelling (`MakeResult().Value`) is
//      invisible to both.
//   2. It cannot ask whether the Result was CHECKED. The regex fires on the
//      form, whether or not an `IsSuccess`/`IsFailure` check dominates the
//      read. The rule therefore has to forbid the shape outright, which is
//      why its prose says "read it through a local you have checked" rather
//      than "check it".
//   3. It needs its own comment stripper, its own controls per FORM, and it
//      cannot see a `.Value` read that is genuinely guarded.
//
// An analyzer sees the OPERATION, not the spelling:
//
//   * `SemanticModel.GetOperation(node)` on `(await X).Value` yields an
//     `IPropertyReferenceOperation` whose `Property.ContainingType` IS
//     `Result<T>` — the await, the parentheses and the `ConfigureAwait(false)`
//     are all folded away. The type is resolved, not spelled.
//   * The question "was this Result checked?" becomes a question about the
//     OPERATION TREE: is there an `IsSuccess`/`IsFailure` test on the SAME
//     value that DOMINATES this read?
//
// THE DOMINANCE RULE, PRECISELY
// -----------------------------
// A SITE is a read of the `Value` property of `CSharpFunctionalExtensions.
// Result` or `Result<T>`, resolved by the property's CONTAINING TYPE (never by
// the spelling "Value"), anywhere in the compiled unit.
//
// A site is REPORTED unless one of the following holds. "The same value"
// means the receiver denotes the same storage location:
//
//   * The receiver is a local, parameter, field or property reference, and
//     the fact "that value is a SUCCESS" holds at the read.
//
// The fact "value V is a success" is established by the operation tree:
//
//   (1) BRANCH CONTAINMENT. The read lies in the THEN branch of an `if`
//       whose condition tests `V.IsSuccess`, or in the ELSE branch of an `if`
//       whose condition tests `V.IsFailure`. The same holds for a ternary:
//       `V.IsSuccess ? … V.Value … : …` and `V.IsFailure ? … : … V.Value …`.
//   (2) EARLY EXIT. In the block that contains the read, an EARLIER statement
//       is an `if` whose condition tests `V.IsFailure` and whose THEN branch
//       leaves the flow (return / throw / continue / break) and which has no
//       ELSE. The only way to reach the code after such an `if` is the
//       condition being false, i.e. V is a success. The mirror image holds:
//       `if (V.IsSuccess) { return; }` leaves V known to be a FAILURE, which
//       does NOT protect a `.Value` read.
//   (3) SHORT CIRCUIT. The read is the right operand of `V.IsSuccess && …`
//       (V is a success there), or the left operand of `V.IsFailure || …`
//       (V is a success there).
//   (4) PATTERN. The read's value is a pattern variable declared by
//       `is { IsSuccess: true }` (or `is { IsFailure: false }`) and the read
//       lies in the branch that pattern matched.
//
// Facts are dropped when the value is REASSIGNED between the check and the
// read: `if (V.IsFailure) return; V = other(); use(V.Value);` is reported.
//
// WHY THE FACTS ARE ABOUT A NAME
// ------------------------------
// A "value" here is a STORAGE LOCATION: a local, a parameter, a field or a
// property, identified by its symbol name. Two limits follow, and both are
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
using Microsoft.CodeAnalysis.Operations;

namespace Harbor.Architecture.Tests;

/// <summary>
///     HARCH003 — a <c>Result</c> is read without a dominating guard.
/// </summary>
/// <remarks>
///     <para>
///         The rule is about the TYPE and about the OPERATION TREE, never about
///         a spelling. A read of <c>Result&lt;T&gt;.Value</c> whose receiver is
///         an await, an invocation or any other computed expression is exactly
///         the shape <c>CFE0001</c> cannot see, and it is reported here — while
///         a read that a dominating <c>IsSuccess</c>/<c>IsFailure</c> check
///         protects is not, which is the thing no text scan can decide.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnguardedResultReadAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id this rule reports under.</summary>
    public const string DiagnosticId = "HARCH003";

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

        // The OPERATION, not the syntax: `GetOperation` on `(await X).Value`
        // is an IPropertyReferenceOperation whose property belongs to
        // `Result<T>` — the shape CFE0001 skips because it resolves SYMBOLS on
        // the receiver instead.
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }

    /// <summary>
    ///     A read of <c>Result&lt;T&gt;.Value</c>: reported unless a dominating
    ///     guard establishes that the value is a success.
    /// </summary>
    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;

        // The NAME first: it is the cheap test, and it keeps the type
        // resolution (which builds strings) off the ~99 % of property reads
        // that are not `Value` at all.
        if (reference.Property is not { Name: "Value" } property)
        {
            return;
        }

        if (!IsResultType(property.ContainingType))
        {
            return;
        }

        string? key = KeyOf(reference.Instance);
        if (key is not null && DominatingFacts(reference, context.SemanticModel).IsSuccess(key))
        {
            return;
        }

        object?[] arguments =
        [
            property.ContainingType.ToDisplayString() + "." + property.Name,
            Describe(reference.Instance),
        ];

        context.ReportDiagnostic(Diagnostic.Create(Rule, LocationOf(reference), arguments));
    }

    /// <summary>
    ///     The facts that hold at a read, gathered by walking the operation
    ///     tree UP from the read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The walk is upward on purpose. A downward walk would have to
    ///         model every control-flow construct to thread a fact environment
    ///         through them; the upward walk asks, for each enclosing
    ///         construct, the one question that matters — "does this construct
    ///         establish a fact about the value at this point?" — and the
    ///         answer is local to the construct.
    ///     </para>
    /// </remarks>
    private static Facts DominatingFacts(IOperation read, SemanticModel model)
    {
        var facts = new Facts();
        IOperation? current = read;

        while (current.Parent is { } parent)
        {
            switch (parent)
            {
                // (1) BRANCH CONTAINMENT — an `if` whose condition tests the value.
                case IIfStatementOperation ifStatement:
                    if (ReferenceEquals(current, ifStatement.Then))
                    {
                        facts.Merge(ConditionFacts(ifStatement.Condition, model).WhenTrue);
                    }
                    else if (ReferenceEquals(current, ifStatement.Else))
                    {
                        facts.Merge(ConditionFacts(ifStatement.Condition, model).WhenFalse);
                    }

                    break;

                // (1) BRANCH CONTAINMENT — a ternary.
                case IConditionalOperation conditional:
                    if (ReferenceEquals(current, conditional.WhenTrue))
                    {
                        facts.Merge(ConditionFacts(conditional.Condition, model).WhenTrue);
                    }
                    else if (ReferenceEquals(current, conditional.WhenFalse))
                    {
                        facts.Merge(ConditionFacts(conditional.Condition, model).WhenFalse);
                    }

                    break;

                // (3) SHORT CIRCUIT — the right operand of `&&` runs only when
                // the left one is true; the left operand of `||` runs only when
                // the left one is false.
                case IBinaryOperation andOperation when andOperation.Kind == BinaryOperatorKind.And:
                    if (ReferenceEquals(current, andOperation.RightOperand))
                    {
                        facts.Merge(ConditionFacts(andOperation.LeftOperand, model).WhenTrue);
                    }

                    break;

                case IBinaryOperation orOperation when orOperation.Kind == BinaryOperatorKind.Or:
                    if (ReferenceEquals(current, orOperation.LeftOperand))
                    {
                        facts.Merge(ConditionFacts(orOperation.LeftOperand, model).WhenFalse);
                    }

                    break;

                // (4) PATTERN — a switch arm whose pattern demands the value.
                case ISwitchCaseOperation switchCase:
                    facts.Merge(SwitchCaseFacts(switchCase));
                    break;

                // (2) EARLY EXIT — the statements BEFORE the read's own
                // statement in this block may have established a fact.
                case IBlockOperation block:
                    facts.Merge(PrecedingStatementFacts(block, current, model));
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
    private static Facts PrecedingStatementFacts(IBlockOperation block, IOperation child, SemanticModel model)
    {
        var facts = new Facts();

        foreach (IOperation statement in block.Operations)
        {
            if (ReferenceEquals(statement, child))
            {
                break;
            }

            ApplyStatement(statement, facts, model);
        }

        return facts;
    }

    /// <summary>
    ///     What one statement does to the facts that hold after it.
    /// </summary>
    private static void ApplyStatement(IOperation statement, Facts facts, SemanticModel model)
    {
        switch (statement)
        {
            // (2) EARLY EXIT. The THEN branch leaves the flow and there is no
            // ELSE, so the only way to reach the code after this `if` is the
            // condition being FALSE.
            case IIfStatementOperation ifStatement
                when ifStatement.Else is null && EndsFlow(ifStatement.Then):
                facts.Merge(ConditionFacts(ifStatement.Condition, model).WhenFalse);
                break;

            // An assignment may have replaced the value the fact was about.
            case IExpressionStatementOperation expressionStatement:
                InvalidateAssigned(expressionStatement.Operation, facts);
                break;
        }
    }

    /// <summary>
    ///     Drops the fact about any value this operation assigns to.
    /// </summary>
    private static void InvalidateAssigned(IOperation operation, Facts facts)
    {
        switch (operation)
        {
            case IAssignmentOperation assignment:
                facts.Remove(KeyOf(assignment.Target));
                return;

            case ICompoundAssignmentOperation compound:
                facts.Remove(KeyOf(compound.Target));
                return;

            case IIncrementOrDecrementOperation increment:
                facts.Remove(KeyOf(increment.Target));
                return;
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            InvalidateAssigned(child, facts);
        }
    }

    /// <summary>
    ///     Whether every path through this branch leaves the flow.
    /// </summary>
    private static bool EndsFlow(IOperation? branch)
    {
        switch (branch)
        {
            case null:
                return false;

            case IReturnOperation:
                return true;

            case IThrowOperation:
                return true;

            case IContinueOperation:
                return true;

            case IBreakOperation:
                return true;

            case IBlockOperation block:
                return block.Operations.Length > 0 && EndsFlow(block.Operations[block.Operations.Length - 1]);

            case IExpressionStatementOperation expressionStatement:
                return EndsFlow(expressionStatement.Operation);

            case IIfStatementOperation ifStatement:
                return ifStatement.Else is not null
                    && EndsFlow(ifStatement.Then)
                    && EndsFlow(ifStatement.Else);

            default:
                return false;
        }
    }

    /// <summary>
    ///     What a condition establishes when it is true, and when it is false.
    /// </summary>
    private readonly record struct ConditionFacts(Facts WhenTrue, Facts WhenFalse);

    /// <summary>
    ///     The facts a condition establishes on each of its two outcomes.
    /// </summary>
    private static ConditionFacts ConditionFacts(IOperation? condition, SemanticModel model)
    {
        var whenTrue = new Facts();
        var whenFalse = new Facts();

        switch (condition)
        {
            case IPropertyReferenceOperation success
                when IsResultFlag(success.Property, "IsSuccess"):
            {
                string? key = KeyOf(success.Instance);
                if (key is not null)
                {
                    whenTrue.Set(key, Fact.Success);
                    whenFalse.Set(key, Fact.Failure);
                }

                break;
            }

            case IPropertyReferenceOperation failure
                when IsResultFlag(failure.Property, "IsFailure"):
            {
                string? key = KeyOf(failure.Instance);
                if (key is not null)
                {
                    whenTrue.Set(key, Fact.Failure);
                    whenFalse.Set(key, Fact.Success);
                }

                break;
            }

            case IUnaryOperation negated when negated.Kind == UnaryOperatorKind.Not:
            {
                ConditionFacts inner = ConditionFacts(negated.Operand, model);
                return new ConditionFacts(inner.WhenFalse, inner.WhenTrue);
            }

            // `A && B` is true when BOTH are true: a fact holds when it holds
            // in either operand's true branch. It is false when either is
            // false, so only a fact that holds in BOTH false branches survives.
            case IBinaryOperation andOperation when andOperation.Kind == BinaryOperatorKind.And:
            {
                ConditionFacts left = ConditionFacts(andOperation.LeftOperand, model);
                ConditionFacts right = ConditionFacts(andOperation.RightOperand, model);
                return new ConditionFacts(
                    left.WhenTrue.Union(right.WhenTrue),
                    left.WhenFalse.Intersect(right.WhenFalse));
            }

            // `A || B` is false when BOTH are false: a fact holds when it
            // holds in either operand's false branch. It is true when either
            // is true, so only a fact that holds in BOTH true branches survives.
            case IBinaryOperation orOperation when orOperation.Kind == BinaryOperatorKind.Or:
            {
                ConditionFacts left = ConditionFacts(orOperation.LeftOperand, model);
                ConditionFacts right = ConditionFacts(orOperation.RightOperand, model);
                return new ConditionFacts(
                    left.WhenTrue.Intersect(right.WhenTrue),
                    left.WhenFalse.Union(right.WhenFalse));
            }
        }

        AddPatternFacts(condition?.Syntax, model, whenTrue, whenFalse);
        return new ConditionFacts(whenTrue, whenFalse);
    }

    /// <summary>
    ///     The facts a property PATTERN establishes: <c>is { IsSuccess: true }</c>
    ///     declares that the matched value is a success.
    /// </summary>
    private static void AddPatternFacts(SyntaxNode? syntax, SemanticModel model, Facts whenTrue, Facts whenFalse)
    {
        if (syntax is null)
        {
            return;
        }

        foreach (IsPatternExpressionSyntax isPattern in syntax.DescendantNodesAndSelf().OfType<IsPatternExpressionSyntax>())
        {
            if (isPattern.Pattern is not RecursivePatternExpressionSyntax recursive)
            {
                continue;
            }

            // The pattern VARIABLE is the storage the fact is about; without a
            // designation the fact is about the matched expression itself.
            ApplyPatternRequirement(
                recursive,
                KeyOfExpression(isPattern.Expression, model),
                whenTrue,
                whenFalse);
        }
    }

    /// <summary>
    ///     The facts a switch arm's pattern establishes.
    /// </summary>
    /// <remarks>
    ///     A case body is entered only when the pattern MATCHED, so the facts
    ///     the pattern demands are the facts that hold in the body.
    /// </remarks>
    private static Facts SwitchCaseFacts(ISwitchCaseOperation switchCase)
    {
        var whenTrue = new Facts();
        var whenFalse = new Facts();

        IOperation? switched = (switchCase.Parent as ISwitchOperation)?.Value;
        string? subjectKey = KeyOf(switched);

        foreach (RecursivePatternExpressionSyntax recursive in switchCase.Syntax
                     .DescendantNodesAndSelf()
                     .OfType<RecursivePatternExpressionSyntax>())
        {
            ApplyPatternRequirement(recursive, subjectKey, whenTrue, whenFalse);
        }

        return whenTrue;
    }

    /// <summary>
    ///     Reads the <c>IsSuccess</c>/<c>IsFailure</c> requirement out of a
    ///     recursive pattern and records what it means for the pattern's value.
    /// </summary>
    private static void ApplyPatternRequirement(
        RecursivePatternExpressionSyntax recursive,
        string? subjectKey,
        Facts whenTrue,
        Facts whenFalse)
    {
        string? key = recursive.Designation is SingleVariableDesignationSyntax designation
            ? designation.Identifier.ValueText
            : subjectKey;

        if (key is null || recursive.PropertyPatternClause is not { } clause)
        {
            return;
        }

        foreach (SubpatternSyntax subpattern in clause.Subpatterns)
        {
            if (subpattern is not PropertySubpatternSyntax propertySubpattern)
            {
                continue;
            }

            string name = propertySubpattern.ExpressionColon.Name.Identifier.ValueText;
            string required = propertySubpattern.ExpressionColon.Pattern.ToString();

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
    private static string? KeyOf(IOperation? instance)
    {
        while (instance is IConversionOperation conversion)
        {
            instance = conversion.Operand;
        }

        return instance switch
        {
            ILocalReferenceOperation local => local.Local.Name,
            IParameterReferenceOperation parameter => parameter.Parameter.Name,
            IFieldReferenceOperation field => field.Field.Name,
            IPropertyReferenceOperation property => property.Property.Name,
            _ => null,
        };
    }

    /// <summary>The key of an expression, resolved through the semantic model.</summary>
    private static string? KeyOfExpression(ExpressionSyntax expression, SemanticModel model)
        => model.GetSymbolInfo(expression).Symbol is { } symbol ? symbol.Name : null;

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

    /// <summary>Whether the property is <c>IsSuccess</c>/<c>IsFailure</c> on a <c>Result</c>.</summary>
    private static bool IsResultFlag(IPropertySymbol property, string name)
        => property.Name == name && IsResultType(property.ContainingType);

    /// <summary>The location of the read: the member NAME, not the whole expression.</summary>
    private static Location LocationOf(IPropertyReferenceOperation reference)
        => reference.Syntax is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Name.GetLocation()
            : reference.Syntax.GetLocation();

    /// <summary>A short description of the receiver, for the message.</summary>
    private static string Describe(IOperation? instance)
    {
        if (instance is null)
        {
            return "<implicit this>";
        }

        string text = instance.Syntax.ToString();
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
