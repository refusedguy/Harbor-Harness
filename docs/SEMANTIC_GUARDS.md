# The semantic guards: what a rule looks like when it asks the compiler instead of the text

This is the working note of epic [#1724](https://github.com/refusedguy/Harbor-Harness/issues/1724)
("the architecture-test program: audit 110 rules → tiers A/B/C → semantic checks instead of text
matching, −35-40 % of the guard code"). The tier classification of the 110 rules is
[#1725](https://github.com/refusedguy/Harbor-Harness/issues/1725)'s job; this page is about the
second half of the epic: **what a semantic check looks like on three concrete rules, and what it
buys in lines.**

The substrate rule is the one the repository already states, in
[ANALYZERS_AUTHORING.md](./ANALYZERS_AUTHORING.md) §"Which kind of rule are you writing":

> if the fact you need is a type, write an analyzer; if it is a line of text, write a source scan.

This page asks the question the epic asks about the rules that do not hold to that rule yet, and it
answers it with measured numbers rather than with an opinion.

## The three rules

| rule | what it guards | text machinery | semantic substrate |
|---|---|---|---|
| `HttpClientReuseRules` (#1854) | an HTTP client is created once, by a named owner, never per call | 3 regexes over the stripped source | **implemented** — `HARCH005`/`HARCH006`, an analyzer in `src/Harbor.Arch.Analyzers/` |
| `ProcessEnvIsolationRule` (#823) | a test class that writes the process environment carries a parallelism form | a hand-written C# parser: a string lexer, an indentation-based type-boundary model, regex attribute detection | designed below; the blocker is named |
| `ExhaustiveUnionSwitchRule` (#578) | a switch over a Harbor-owned union names every member | a text scanner that reads the arm types out of the source | designed below |

## Rule 1 — `HttpClientReuseRules`: implemented

**What the text rule asked.** Two questions, both answered with a token:

1. "Is a client created here?" — the token `new HttpClient` / `new SocketsHttpHandler`.
2. "Is the creation inside a delegate?" — the token `=> new HttpClient` on the same line.

**What the semantic check asks.** The same two questions, asked of the SYMBOL and of the SYNTAX
TREE:

```csharp
// src/Harbor.Arch.Analyzers/HttpClientCreationAnalyzer.cs
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
```

**What it buys — measured, not asserted.**

* The per-call shape has **three** spellings: an expression-bodied lambda, a lambda with a block
  body, and an anonymous method. The text rule saw **one** of them, because it matched the token
  `=> new`. The planted control in
  `tests/Harbor.Architecture.Tests/Roslyn/HttpClientCreationAnalyzerTests.cs` carries all three,
  and the analyzer reports three.
* A name in a string literal is no longer a hit, and prose needs no comment stripper: the analyzer
  never looks at trivia.
* The differential over the live tree: the analyzer and the text matcher name **the same seven
  sites** — five files, seven creations — in both directions.

**What it costs — also measured.**

| | lines | where |
|---|---|---|
| the rule, before | 450 | `tests/Harbor.Architecture.Tests/` |
| the rule, after | 652 | `tests/Harbor.Architecture.Tests/` |
| the analyzer | 206 | `src/Harbor.Arch.Analyzers/` |
| the controls and the differential | 256 | `tests/Harbor.Architecture.Tests/Roslyn/` |

**The rule's own lines GREW by 202, and the honest conclusion is that this route does not pay for
itself in lines.** The reason is visible in the table above and it is not a detail: this rule
already sits on the shared `ScanRule` engine (#1086), so its text machinery was three regexes and
two declarative tables — **the scaffolding was removed from it long ago**. A semantic check needs a
compilation, a census over every product project, a planted control set and a differential, and
that is more code than the three regexes it replaces.

What the semantic check buys instead is **exactness**, and it buys it where the text rule was
blind. A rule that cannot see the block-bodied lambda is not a cheaper rule; it is a rule with a
hole in the middle of the defect it was written for (#1854's shape, in two of its three spellings).

## Rule 2 — `ProcessEnvIsolationRule`: the hand-written parser, and where the lines are

**What the text rule asks.** For every class under `tests/**` that calls
`Environment.SetEnvironmentVariable(`, does the class carry a `[NotInParallel]` form?

**What it costs today.** The file is **1777 lines**, and the file's own header states the limits of
the scan: it is "line-level over comment-stripped, literal-stripped source, not a C# parser", a
write "split so that `Environment.SetEnvironmentVariable(` never appears whole on one line is
missed", and a type's extent "ends at the next type declared at the same or shallower
indentation". The machinery that answers those questions is **700 lines of the file**:

| block | lines | what it does |
|---|---|---|
| the constants and the regexes | 191-271 | the call's spelling, the attribute's spelling, the quoted key |
| the parser | 1159-1777 | the string lexer (`StripLiterals`, `BlanksLiteral`), the argument reader (`ReadEnvWriteArguments`, `ReadArgument`), the type-extent model (`ParseTypes`, `LeadingWhitespace`), the attribute reader (`ReadParallelism`), and the walk itself |

**What the semantic check looks like.** Every one of those questions is a question about a SYMBOL,
and the symbol answers all of them at once:

```csharp
// The writer: the call, resolved by symbol, with the TYPE that declares it.
private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
{
    var invocation = (InvocationExpressionSyntax)context.Node;

    if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
        is not IMethodSymbol method)
    {
        return;
    }

    if (method.ContainingType?.ToDisplayString() != "System.Environment"
        || method.Name is not ("SetEnvironmentVariable" or "GetEnvironmentVariable" or "GetFolderPath"))
    {
        return;
    }

    // The TYPE that declares the call. The text rule approximated this with an
    // indentation heuristic; the symbol states it.
    if (context.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, context.CancellationToken)
        is not INamedTypeSymbol type)
    {
        return;
    }

    // The ARGUMENT, read as a VALUE. The text rule lexed the line by hand to
    // find the string between two quotes.
    string? variable = null;
    if (invocation.ArgumentList.Arguments.Count > 0
        && context.SemanticModel.GetOperation(invocation.ArgumentList.Arguments[0]) is IArgumentOperation argument)
    {
        variable = argument.Value.ConstantValue.Value as string;
    }

    context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), type.Name, method.Name, variable ?? "<computed>"));
}

// The parallelism form: the type's ATTRIBUTES, read off the symbol. The text
// rule matched `NotInParallel\s*\(([^)]*)\)` in the class header and guessed
// the key out of the quoted string.
private static IReadOnlyList<string> ParallelismKeys(INamedTypeSymbol type)
{
    var keys = new List<string>();

    foreach (AttributeData attribute in type.GetAttributes())
    {
        string? name = attribute.AttributeClass?.Name;
        if (name is not ("NotInParallelAttribute" or "KeyedNotInParallelAttribute"))
        {
            continue;
        }

        foreach (AttributeArgument argument in attribute.ConstructorArguments)
        {
            if (argument.Value is string key)
            {
                keys.Add(key);
            }
        }
    }

    return keys;
}
```

**The win, projected.** −700 lines of hand-rolled parser in the test project, +~260 lines of
analyzer outside it: **about −440 lines in `tests/Harbor.Architecture.Tests/`**, and the four
hand-written statements the text rule could not state (a split call, an interpolation hole, a
nonstandard layout, an aliased attribute) become facts.

**The blocker, stated rather than hidden.** The subject of this rule is the **test tree**, and the
architecture suite has no compilation for it: `RoslynProjectCompilation` builds one compilation per
PRODUCT project, and the 48 test projects are not among them. Compiling the test tree is a cost the
arch suite cannot pay (it is already the slowest project in the repository — #1791 measured 738 s of
the repository's 1128 s of test time inside it), so the implementable version of this rule today is
a Roslyn **syntax** walk (a `CSharpSyntaxTree` per file, no semantic model), which already removes
the string lexer and the indentation model and keeps the two stated limits: an aliased
`System.Environment` and an attribute of another origin named `NotInParallel` are not resolved. The
fully semantic version needs the test tree's compilations, and that is a separate piece of work with
its own measurement.

## Rule 3 — `ExhaustiveUnionSwitchRule`: a half-semantic rule

**What it does today.** The rule is already half semantic: the union's MEMBERS are read by
reflection (`Unions()`, `ReadSubtypesOf`), so a hand-written member list cannot rot. The SWITCH is
not: the arms are read out of the source text.

| block | lines | what it does |
|---|---|---|
| the scanner | 272-620 | reads the source, strips the code (`StripNonCode`), finds the arm block (`ArmBlockStart`), and reads the arm types out of the text (`TryReadArmType`, `IsWildcardArm`) |
| the two source-generated regexes | 484-488 | the statement form and the expression form of an arm |

**What the semantic check looks like.** The rule's own header says the question "is not expressible
against compiled metadata without a full control-flow analysis". That is true of METADATA, and it
is not true of Roslyn's syntax tree, which is where the arms live:

```csharp
// The wildcard arm, in both forms. The statement form has a `default:` label;
// the expression form has a discard pattern.
private static bool HasWildcardArm(SwitchStatementSyntax statement) =>
    statement.Sections.Any(section =>
        section.Labels.Any(label => label.IsKind(SyntaxKind.DefaultSwitchLabel)));

private static bool HasWildcardArm(SwitchExpressionSyntax expression) =>
    expression.Arms.Any(arm => arm.Pattern.IsKind(SyntaxKind.DiscardPatternSyntax));

// The union the switch is over: the GOVERNING TYPE, resolved. The text rule
// inferred it from the arm types, which is why it needed MIN_MEMBERS named
// members before it would consider a switch to be over a union at all.
private static bool IsOverUnion(SemanticModel model, SwitchStatementSyntax statement, out INamedTypeSymbol? type)
{
    type = model.GetTypeInfo(statement.Expression, CancellationToken.None).Type as INamedTypeSymbol;
    return type is not null;
}
```

**The win, projected.** −350 lines of text scanner in the test project, +~200 lines of analyzer
outside it: **about −150 lines in `tests/Harbor.Architecture.Tests/`**, and the "is this switch over
a union" question stops being a heuristic (`MIN_MEMBERS` named members) and becomes a fact.

## What the three rules say about the epic's target

The epic's exit criterion is "35-40 % fewer lines in `tests/Harbor.Architecture.Tests/`". The three
rules above put a number on what "semantic instead of text" is worth, and the number is not the one
the epic expects:

* **On a rule that already sits on the `ScanRule` engine, the semantic rewrite does NOT shrink the
  project.** Rule 1 measured: +202 lines in the test project, +462 in total. What it buys is
  exactness, and exactness is what the rule was missing.
* **On a rule that hand-rolls its own C# parser, the semantic rewrite DOES shrink the project, and
  by a lot.** Rule 2's parser is 700 lines and Rule 3's scanner is 350; together they are the
  biggest single block of removable code the audit has to look at.
* **The lines are not the only currency, and the epic's own text says so** ("Any reduction must be
  covered by a metric — otherwise it is not an optimisation, it is a loss of insurance"). A guard
  that moves from the test suite into the build stops costing test time on every run: the
  architecture suite is the repository's slowest project, and a guard that the compiler applies is
  free there.

The order the three rules suggest, and it is the order the substrate decides and not the order the
file sizes do:

1. **Rules whose invariant CANNOT be expressed as a text scan** — namespace ownership (#452),
   member ownership (#1831) — are already analyzers (`HARCH001`, `HARCH002`).
2. **Rules that hand-roll a C# parser** — #823 and #578 above — are next: the parser is the
   removable code, and the semantic version removes it.
3. **Rules that already sit on the `ScanRule` engine** are last: their text machinery is three
   regexes, the scaffolding is gone, and the semantic version buys exactness at the price of more
   code. Whether that trade is worth making is a question about the rule's DEFECT (does the blind
   spot cover a shape anyone writes?), not about its line count.

## See also

- [ANALYZERS_AUTHORING.md](./ANALYZERS_AUTHORING.md) — the procedure a new analyzer follows, and
  the three controls every rule carries.
- [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) §5 — the catalogue of architecture rules.
- `src/Harbor.Arch.Analyzers/` — the rules the compiler now runs over the product tree.
- `tests/Harbor.Architecture.Tests/Roslyn/` — the rules the architecture suite runs in-process, and
  the pilot the promotion path was written from (#1729).
