// InnerExceptionRules.cs — the guard for issue #1848.
//
// WHAT #1848 FOUND
// ----------------
// Two sites in the product threw a new exception inside a catch and dropped the
// caught exception on the floor. The MESSAGE survived; the stack trace and the
// cause did not.
//
//   * src/Harbor.PolicyConfig/Configuration/ConfigStore.cs — a corrupt
//     config.json became `InvalidDataException("config.json is corrupt: …")`
//     with the JsonException interpolated into the text. The user still sees
//     the same failure string, and the inner exception now carries the stack
//     trace and the JSON position of the key that failed to parse, which is
//     what a bug report needs.
//   * src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs — a malformed `params`
//     became `IdeRpcException(InvalidParams, "Malformed params: …")` with the
//     JsonException interpolated into the text. Same message, same JSON-RPC
//     code, and the inner exception now rides along.
//
// WHY THIS FILE EXISTS AND NOT A PACKAGE
// ---------------------------------------
// The issue collected a candidate analyzer package for exactly this shape
// (`TheExceptionator.Analyzers`, rules EX003 and EX021) and this file is where
// the rule lives on. The package was REJECTED, and the reason is mechanical
// rather than a matter of taste: its nuspec declares its Roslyn dependencies
// with `exclude="Build,Analyzers"`, and every version of the Roslyn packages
// carries EXACT version constraints (`[3.3.1, 3.3.1]`). Against this
// repository's central Roslyn pin (5.6.0) the restore fails with NU1608 on
// every product project, and the same edges leave the compile and runtime
// assets of Microsoft.CodeAnalysis.CSharp (and, at any version consistent with
// the central pin, of Humanizer.Core) in the closure of every project that
// references the package. The nine analyzer packages this repository already
// uses declare NO dependencies at all, because the Roslyn assemblies come from
// the compiler; this one is the only one that declares them, and it is the
// only one that breaks the restore.
//
// A lesson has to be encoded as a RULE, not as a comment: a comment is
// invisible to every tool that reads the tree, and the next `throw new
// Something("…" + ex.Message)` would land without anybody noticing.
//
// WHAT IS ENFORCED
// ----------------
//   1. LOST INNER EXCEPTION. A `throw new X(…)` inside a catch that NAMES the
//      exception it caught must pass that exception to X. The caught variable
//      has to arrive as an argument; a message that interpolates it
//      (`$"… {ex.Message}"`) keeps the text and drops the stack trace, which
//      is the defect.
//   2. A TYPE THAT CANNOT CARRY A CAUSE. A custom exception type must declare
//      both `(string message)` and `(string message, Exception innerException)`.
//      A type without the second constructor is the reason rule 1 has to be
//      enforced at every call site instead of once, at the type.
//
// THE SCOPE OF THE FIRST RULE, STATED
// -----------------------------------
// The rule is the one the rejected package shipped, transcribed: a throw
// statement whose expression is an object creation, inside a catch clause that
// declares a variable, where no argument of the creation is that variable as a
// bare identifier. Two shapes it deliberately does NOT cover: a `throw;`
// rethrow (nothing is lost) and a throw outside any catch (there is no caught
// exception to carry).
//
// A `throw new X(ex.InnerException)` is reported, and that is right: the
// original exception's own cause is not the exception the catch named, and
// throwing it directly loses the stack trace (the rejected package had a
// separate rule for that shape, EX013, and this file does not restate it).
//
// THE SCOPE OF THE SECOND RULE, STATED
// ------------------------------------
// Syntactic, not semantic: a class whose base list names a type ending in
// `Exception` must declare both constructors. A class that derives from
// `Exception` through a type this rule cannot see is out of scope, and the
// tree has none: all four exception types in `src/` + `apps/` derive from
// `System.Exception` directly.
//
// NON-VACUITY
// -----------
// Every rule here can pass for the wrong reason — a scan that matches nothing,
// a scope that finds no files — and a guard that passes for the wrong reason is
// worse than no guard, because it is believed. So: the scope must really yield
// the product tree and still contain the two files the fix touched; the matcher
// must fire on the planted shapes and stay silent on the correct ones; and the
// controls assert the COUNT, not just the presence: the planted offender must
// be reported EXACTLY once, and the near-miss exactly not at all.

using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #1848: an exception thrown inside a catch carries the caught
///     exception, and a custom exception type can carry one at all. See the
///     file header for the incident and for the two rules' stated scope.
/// </summary>
public sealed class InnerExceptionRules
{
    /// <summary>The sub-id a lost inner exception is reported under.</summary>
    private const string LostInnerSubId = "lost-inner-exception";

    /// <summary>The sub-id an exception type that cannot carry a cause is reported under.</summary>
    private const string NoInnerConstructorSubId = "exception-without-inner-constructor";

    /// <summary>
    ///     The banned spelling of rule 1, for the record. The runner does not
    ///     execute it: the question this rule asks ("is this throw inside a
    ///     catch, and does it carry the caught exception") is not a line
    ///     question, so the matcher is code. The row documents the shape.
    /// </summary>
    private static readonly Regex LostInnerSpelling = new(
        @"\bthrow\s+new\s+[A-Za-z_][\w.]*\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     The banned spelling of rule 2, for the record, on the same terms as
    ///     <see cref="LostInnerSpelling" />: an exception type that names no
    ///     inner-exception constructor.
    /// </summary>
    private static readonly Regex NoInnerConstructorSpelling = new(
        @"\bclass\s+[A-Za-z_]\w*\s*:\s*[A-Za-z_][\w.]*Exception\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Rule 1 and rule 2 as data: the scope, the two banned shapes, the
    ///     planted controls, and the discovery floor. The matcher is
    ///     <see cref="Parse" />; the framework owns the walk, the controls and
    ///     the verdicts.
    /// </summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "InnerException",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                LostInnerSubId,
                LostInnerSpelling,
                "Pass the caught exception to the new one: `throw new Something(message, ex)`. A message "
                + "that interpolates the exception (`$\"… {ex.Message}\"`) keeps the text and throws "
                + "away the stack trace and the cause, and the report a user sends for a corrupt "
                + "config is exactly the report that needs them (#1848)."
            ),
            new ScanForbidden(
                NoInnerConstructorSubId,
                NoInnerConstructorSpelling,
                "An exception type declares both constructors: `(string message)` and "
                + "`(string message, Exception innerException)`. A type that cannot carry a cause "
                + "pushes the decision to every call site that wraps one (#1848)."
            ),
        ],
        Controls =
        [
            // The shape #1848 found, verbatim: the caught exception interpolated
            // into the message and nothing else.
            new ScanControl(
                "Planted/InterpolatedMessage.cs",
                """
                public sealed class Interpolated
                {
                    public void Run()
                    {
                        try { Work(); }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException($"payload is not valid JSON: {ex.Message}");
                        }
                    }

                    private static void Work() { }
                }
                """,
                LostInnerSubId),
            // The same shape without the interpolation: the exception is simply
            // not passed.
            new ScanControl(
                "Planted/NoInner.cs",
                """
                public sealed class NoInner
                {
                    public void Run()
                    {
                        try { Work(); }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException("payload is not valid JSON");
                        }
                    }

                    private static void Work() { }
                }
                """,
                LostInnerSubId),
            // The RIGHT way: the caught exception rides along as an argument.
            new ScanControl(
                "NearMiss/CarriesTheInner.cs",
                """
                public sealed class CarriesTheInner
                {
                    public void Run()
                    {
                        try { Work(); }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException("payload is not valid JSON", ex);
                        }
                    }

                    private static void Work() { }
                }
                """,
                null),
            // A rethrow loses nothing: the caught exception is still the one
            // that propagates.
            new ScanControl(
                "NearMiss/Rethrow.cs",
                """
                public sealed class Rethrow
                {
                    public void Run()
                    {
                        try { Work(); }
                        catch (JsonException ex)
                        {
                            throw;
                        }
                    }

                    private static void Work() { }
                }
                """,
                null),
            // A throw outside a catch has no caught exception to carry.
            new ScanControl(
                "NearMiss/OutsideACatch.cs",
                """
                public sealed class OutsideACatch
                {
                    public void Run() => throw new InvalidOperationException("no catch here");
                }
                """,
                null),
            // A catch that names no exception cannot lose one.
            new ScanControl(
                "NearMiss/CatchWithoutAVariable.cs",
                """
                public sealed class CatchWithoutAVariable
                {
                    public void Run()
                    {
                        try { Work(); }
                        catch (JsonException)
                        {
                            throw new InvalidDataException("payload is not valid JSON");
                        }
                    }

                    private static void Work() { }
                }
                """,
                null),
            // Rule 2's offender: a type that cannot carry a cause.
            new ScanControl(
                "Planted/ExceptionWithoutInner.cs",
                """
                public sealed class WithoutInner : Exception
                {
                    public WithoutInner(string message) : base(message) { }
                }
                """,
                NoInnerConstructorSubId),
            // Rule 2's near-miss: the type that CAN carry one.
            new ScanControl(
                "NearMiss/ExceptionWithInner.cs",
                """
                public sealed class WithInner : Exception
                {
                    public WithInner(string message) : base(message) { }

                    public WithInner(string message, Exception innerException) : base(message, innerException) { }
                }
                """,
                null),
            // Rule 2 does not reach a class that is not an exception.
            new ScanControl(
                "NearMiss/NotAnException.cs",
                """
                public sealed class NotAnException
                {
                    public NotAnException(string message) { }
                }
                """,
                null),
        ],
        MinHits = 500,
        MustContain =
        [
            // The two files #1848's fix touched. A scope that stopped seeing
            // them is broken, not clean.
            "src/Harbor.PolicyConfig/Configuration/ConfigStore.cs",
            "src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs",
        ],
        CustomParse = Parse,
    };

    /// <summary>
    ///     The matcher: the two questions above, over one file's source. The
    ///     Roslyn parser is what makes the first one exact — a catch block, a
    ///     throw statement inside it and the argument list of the creation are
    ///     syntax, and a text matcher would have to re-implement a C# lexer to
    ///     answer them.
    /// </summary>
    private static IEnumerable<ScanHit> Parse(string displayPath, string rawSource)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(rawSource).GetRoot();

        foreach (ThrowStatementSyntax throwStatement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            if (throwStatement.Expression is not ObjectCreationExpressionSyntax creation)
            {
                continue;
            }

            string? caught = CaughtVariable(throwStatement);
            if (caught is null)
            {
                continue;
            }

            bool carries = creation.ArgumentList?.Arguments.Any(argument =>
                argument.Expression is IdentifierNameSyntax identifier
                && identifier.Identifier.ValueText == caught) == true;

            if (carries)
            {
                continue;
            }

            yield return new ScanHit(
                LostInnerSubId,
                displayPath,
                throwStatement.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                $"throw new {creation.Type}(…) inside a catch that caught '{caught}'");
        }

        foreach (ClassDeclarationSyntax classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!DerivesFromException(classDeclaration))
            {
                continue;
            }

            bool hasMessageConstructor = false;
            bool hasInnerConstructor = false;

            foreach (ConstructorDeclarationSyntax constructor in classDeclaration.Members.OfType<ConstructorDeclarationSyntax>())
            {
                SeparatedSyntaxList<ParameterSyntax> parameters = constructor.ParameterList.Parameters;

                if (parameters.Count == 1 && TypeText(parameters[0]) == "string")
                {
                    hasMessageConstructor = true;
                }
                else if (parameters.Count == 2
                    && TypeText(parameters[0]) == "string"
                    && IsExceptionType(TypeText(parameters[1])))
                {
                    hasInnerConstructor = true;
                }
            }

            if (hasMessageConstructor && hasInnerConstructor)
            {
                continue;
            }

            yield return new ScanHit(
                NoInnerConstructorSubId,
                displayPath,
                classDeclaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                $"{classDeclaration.Identifier.ValueText} declares no (string message, Exception innerException) constructor");
        }
    }

    /// <summary>
    ///     The variable a catch clause names, or <c>null</c> when the throw is
    ///     not inside a catch that names one. The ancestors are walked by hand
    ///     rather than through a helper, because "the nearest enclosing catch"
    ///     is the whole question and the walk is three lines.
    /// </summary>
    private static string? CaughtVariable(ThrowStatementSyntax throwStatement)
    {
        for (SyntaxNode? node = throwStatement.Parent; node is not null; node = node.Parent)
        {
            if (node is not CatchClauseSyntax catchClause)
            {
                continue;
            }

            string? name = catchClause.Declaration?.Identifier.ValueText;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        return null;
    }

    /// <summary>Whether a class declaration names an exception type as its base.</summary>
    private static bool DerivesFromException(ClassDeclarationSyntax classDeclaration)
    {
        if (classDeclaration.BaseList is null)
        {
            return false;
        }

        foreach (BaseTypeSyntax baseType in classDeclaration.BaseList.Types)
        {
            if (IsExceptionType(baseType.Type.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a type name is an exception type.</summary>
    private static bool IsExceptionType(string typeText) =>
        typeText.EndsWith("Exception", StringComparison.Ordinal);

    /// <summary>
    ///     The text of a parameter's type, without the nullable annotation: a
    ///     `string?` parameter is still a string parameter, and a constructor
    ///     that takes one is still the (string message) constructor.
    /// </summary>
    private static string TypeText(ParameterSyntax parameter)
    {
        string text = parameter.Type?.ToString() ?? string.Empty;
        return text.EndsWith("?", StringComparison.Ordinal) ? text[..^1] : text;
    }

    /// <summary>
    ///     Rule 1 and rule 2 over the product tree: nothing is reported. A
    ///     throw inside a catch carries the caught exception, and every custom
    ///     exception type can carry one.
    /// </summary>
    [Test]
    public async Task No_Throw_Inside_A_Catch_Loses_Its_Inner_Exception()
    {
        List<string> violations = ScanRunner.Evaluate(Rule);

        await Assert.That(violations).IsEmpty().Because(
            "A `throw new X(…)` inside a catch that names the caught exception must pass that "
            + "exception to X. A message that interpolates it keeps the text and throws away the "
            + "stack trace and the cause: the two sites #1848 found (ConfigStore.cs and "
            + "IdeSessionBridge.cs) did exactly that, and the report a user sends for a corrupt "
            + "config is the report that needs the stack. And a custom exception type must declare "
            + "both `(string message)` and `(string message, Exception innerException)`, or the "
            + "decision to carry a cause is pushed to every call site that wraps one. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     The matcher fires on the planted shapes and stays quiet on the
    ///     correct ones. The framework asserts the COUNT as well as the
    ///     presence: the planted offender must be reported exactly once, and
    ///     the near-miss exactly not at all. "Reports everything" and "reports
    ///     nothing" are the same broken detector.
    /// </summary>
    [Test]
    public async Task The_Matcher_Separates_The_Offenders_From_The_Correct_Shapes()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty().Because(
            "the planted shapes must be detected, or the rule guards nothing; the throw that carries "
            + "the caught exception, the rethrow, the throw outside a catch, the catch without a "
            + "variable and the exception type that declares both constructors must stay quiet, or "
            + "the rule forbids the fix and is permanently red. "
            + string.Join("; ", failures));
    }

    /// <summary>
    ///     The scope really is the product tree, and it still contains the two
    ///     files the fix touched. A scope that stopped seeing them enforces
    ///     nothing.
    /// </summary>
    [Test]
    public async Task The_Scan_Finds_The_Product_Tree_And_The_Fixed_Files()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty().Because(
            "The rule needs a repository root, must really find the product tree (well over 500 "
            + "source files under src/ and apps/), and must still contain the two files #1848's "
            + "fix touched: a scope that quietly stopped seeing them is broken, not clean. "
            + string.Join("; ", discovery));
    }
}
