// RethrowPreservesCauseRules.cs: the guard for issue #1849.
//
// WHY THIS FILE EXISTS
// --------------------
// #1849 audited the `ErrorProne.NET.CoreAnalyzers` package and REJECTED it, but
// its two product findings were real, and both were the same shape: a catch
// block that catches an exception, reads its `.Message`, and throws a NEW
// exception without the caught one. The caught exception was dropped on the
// floor: its type, its stack, and, for a JSON parse error, the position of the
// key that failed. A flattened string was all that survived.
//
//     src/Harbor.Application/Configuration/ConfigStore.cs
//         catch (JsonException je)
//         {
//             throw new InvalidDataException($"config.json is corrupt: {je.Message}");
//         }
//
//     src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs
//         catch (JsonException ex)
//         {
//             throw new IdeRpcException(IdeRpcException.InvalidParams, $"Malformed params: {ex.Message}");
//         }
//
// Both are fixed. The point of THIS file is that nothing would have noticed if
// they came back: the rule they break is prose, and prose does not fail a build.
//
// THE RULE
// --------
// `docs/ANTIPATTERNS.md` §16 ("Catching and re-wrapping exceptions inside
// library code") already says what the wrapper costs ("Loses the original
// stack trace (sometimes)") and prescribes the two ways out: let the exception
// propagate, or convert it to a `Result.Failure` at the boundary. This file
// makes the first half of that mechanical:
//
//     Inside `catch (T ex) { ... }`, a `throw new X(...)` must pass `ex` as the
//     inner exception.
//
// A catch that binds no variable (`catch (IOException) { ... }`) has nothing to
// preserve and is not this rule's business. A bare `throw;` re-throws the
// caught exception with its stack intact and is not this rule's business
// either. A catch that CONVERTS the failure (`return Result.Failure($"...:
// {ex.Message}")`) is the boundary conversion §16 prescribes, and it is not
// this rule's business: the message is the product there, and there is no new
// exception to attach a cause to.
//
// The 23 sites where a generic `catch (Exception ex)` reads only `.Message` and
// converts the failure to a `Result.Failure` / `ToolResult.Error` are all of
// that last kind. They are why this rule is narrow: it is about the NEW
// EXCEPTION, not about the message.
//
// WHY A HOUSE RULE AND NOT THE PACKAGE
// ------------------------------------
// #1848 is adopting `TheExceptionator.Analyzers`, whose EX003 is "a new
// exception thrown inside a catch without the inner", the same invariant as a
// build-time analyzer. This file is not a competing opinion about that
// package; it is the same invariant expressed in the mechanism this repository
// can run TODAY, so that the two fixed sites are guarded by the commit that
// fixes them rather than by a package that is not in the tree yet. When EX003
// lands, this rule has an obvious home: it becomes redundant, and the analyzer
// takes over.
//
// SCOPE
// -----
// `src/` and `apps/`: shipped code, where a lost cause reaches a user. `tests/`
// is excluded on purpose: there a thrown exception IS the failure signal, and a
// rule that flagged the test idiom would be deleted rather than obeyed.
// `contrib/` is unmaintained and not compiled by CI (AGENTS.md), so a rule that
// fires on it describes code that cannot break.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan
// that is broken, and a broken guard is worse than no guard because it is
// believed. Four tests close that:
//
//   * `ShippedCode_PassesTheCaughtExceptionAsTheInnerOne`: the gate.
//   * `Discovery_FindsTheProductTree`: the walk really reaches the product
//     files, including the two this rule was written for.
//   * `Matcher_FiresOnTheShippedShape`: the parser fires on the two shapes as
//     they shipped.
//   * `Matcher_AcceptsTheFixedShapes`: the parser stays quiet on the fixed
//     spelling, on the boundary conversion, on the bare `throw;` and on a
//     catch that binds no variable.
//
// KNOWN LIMITS, stated not hidden
// -------------------------------
// This is a text scan, not a C# parser. It reads the source with comments and
// string-literal CONTENT blanked (the literal's own characters stay, so every
// offset and every line number is true), and it matches the catch clause, the
// block it owns and the `throw new` inside it by shape. The limits are the
// usual ones for a text scan, and every one of them can only cause a MISS, not
// a false accusation:
//
//   * an interpolated string whose hole contains a nested string literal
//     (`$"{Foo("x")}"`) ends the literal early, so the text after it is read as
//     code; the result is a missed site, never a wrong one;
//   * a `throw new` whose arguments span many lines is found, but the reported
//     line is the line the `throw` starts on;
//   * a rethrow that hands the cause on through a local (`var inner = ex;`)
//     is read as a loss, because the scan cannot follow the local.
//
// The last one is the only limit with teeth, and it is the conservative
// direction: the rule asks for the cause to be visible in the throw itself.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #1849: a catch block that binds the caught exception must pass it as
///     the inner exception of any new exception it throws. See the file header
///     for the incident and the non-vacuity argument.
/// </summary>
public sealed class RethrowPreservesCauseRules
{
    /// <summary>The sub-id every violation is reported under.</summary>
    private const string SubId = "LOST-INNER-EXCEPTION";

    /// <summary>The trees that ship. <c>tests/</c> and <c>contrib/</c> are excluded by design.</summary>
    private static readonly string[] ShippedTrees = ["src", "apps"];

    /// <summary>
    ///     A catch clause that BINDS the caught exception: the type, then the
    ///     variable. A catch with no variable does not match, and a catch with a
    ///     filter matches the same way. The filter is skipped when the block is
    ///     located, because a property pattern in a filter has braces of its own.
    /// </summary>
    private static readonly Regex CatchClause = new(
        @"\bcatch\s*\(\s*(?<type>[^()]*?)\s+(?<var>@?[A-Za-z_][A-Za-z0-9_]*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A construction of a new exception: <c>throw new X(…)</c>.</summary>
    private static readonly Regex ThrowNew = new(
        @"\bthrow\s+new\s+[A-Za-z_][A-Za-z0-9_.]*(?:<[^<>]*>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One catch clause and the block it owns.</summary>
    /// <param name="BlockStart">Index of the block's opening brace.</param>
    /// <param name="BlockEnd">Index of the block's closing brace.</param>
    /// <param name="Variable">The caught exception's variable, without the verbatim marker.</param>
    private readonly record struct CatchSite(int BlockStart, int BlockEnd, string Variable);

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     No shipped catch block throws a new exception without the caught one
    ///     as its inner exception.
    /// </summary>
    [Test]
    public async Task ShippedCode_PassesTheCaughtExceptionAsTheInnerOne()
    {
        List<string> violations = Scan();

        await Assert.That(violations).IsEmpty()
            .Because(
                "A new exception thrown inside a catch without the caught one as its inner "
                + "exception drops the cause: its type, its stack and, for a JSON parse error, "
                + "the position of the key that failed. docs/ANTIPATTERNS.md §16 says the wrapper "
                + "'loses the original stack trace' and this rule makes that paragraph fail a build. "
                + "The fix is one argument: pass the caught exception as the inner exception, and "
                + "keep the message exactly as it was. Offenders: "
                + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The walk really reaches the product tree, and it still sees the two
    ///     files this rule was written for. A scope that stopped seeing them
    ///     would report 'clean' for the wrong reason.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheProductTree()
    {
        IReadOnlyList<string> files = SourceScan.EnumerateCsFiles(ShippedTrees);

        await Assert.That(files.Count).IsGreaterThan(500)
            .Because(
                "src/ + apps/ hold far more than 500 C# files. A smaller count means the walk "
                + "broke, and every assertion in this file would then be reporting 'clean' about "
                + "nothing.");

        await Assert.That(files.Any(f => f.EndsWith(
                $"{Path.DirectorySeparatorChar}ConfigStore.cs", StringComparison.Ordinal))).IsTrue()
            .Because("src/Harbor.Application/Configuration/ConfigStore.cs is one of the two sites "
                   + "this rule exists for; a scope that stopped seeing it is broken, not clean");

        await Assert.That(files.Any(f => f.EndsWith(
                $"{Path.DirectorySeparatorChar}IdeSessionBridge.cs", StringComparison.Ordinal))).IsTrue()
            .Because("src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs is the other one");
    }

    /// <summary>
    ///     The positive control: the parser fires on the two shapes as they
    ///     shipped. A parser that stopped working would report nothing and the
    ///     gate above would go green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnTheShippedShape()
    {
        // Verbatim from ConfigStore.cs as it shipped.
        const string ConfigStore = """
            try
            {
                string json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.RawConfigDto);
            }
            catch (JsonException je)
            {
                throw new InvalidDataException($"config.json is corrupt: {je.Message}");
            }
            """;

        // Verbatim from IdeSessionBridge.cs as it shipped.
        const string IdeBridge = """
            try
            {
                return p.Deserialize(IdeJsonContext.Default.IdeInjectPromptParams);
            }
            catch (JsonException ex)
            {
                throw new IdeRpcException(IdeRpcException.InvalidParams, $"Malformed params: {ex.Message}");
            }
            """;

        // The same shape with the cause dropped entirely, which no message
        // mentions. The rule is about the new exception, not about the text.
        const string CauseDropped = """
            catch (IOException ex)
            {
                throw new InvalidOperationException("read failed");
            }
            """;

        await Assert.That(Find("ConfigStore.cs", ConfigStore).Count).IsEqualTo(1)
            .Because("this is the #1849 finding, character for character. A miss means the rule is "
                   + "inert and a corrupt config.json can lose its cause again without anything "
                   + "turning red.");

        await Assert.That(Find("IdeSessionBridge.cs", IdeBridge).Count).IsEqualTo(1)
            .Because("this is the other #1849 finding");

        await Assert.That(Find("Dropped.cs", CauseDropped).Count).IsEqualTo(1)
            .Because("a rethrow that drops the cause altogether loses more than one that flattens "
                   + "it into a message, so it must be reported too");
    }

    /// <summary>
    ///     The negative controls: the fixed spellings, the boundary conversion
    ///     §16 prescribes, the bare rethrow, and the catch that binds nothing.
    ///     A rule that flags these is a noise machine, and a noise machine gets
    ///     switched off.
    /// </summary>
    [Test]
    public async Task Matcher_AcceptsTheFixedShapes()
    {
        // The fixed ConfigStore spelling.
        const string ConfigStoreFixed = """
            catch (JsonException je)
            {
                throw new InvalidDataException($"config.json is corrupt: {je.Message}", je);
            }
            """;

        // The fixed IdeSessionBridge spelling.
        const string IdeBridgeFixed = """
            catch (JsonException ex)
            {
                throw new IdeRpcException(IdeRpcException.InvalidParams, $"Malformed params: {ex.Message}", ex);
            }
            """;

        // The boundary conversion: a Result, not a new exception.
        const string BoundaryConversion = """
            catch (Exception ex)
            {
                return Result.Failure<AgentMessage>($"Line parse failed: {ex.Message}");
            }
            """;

        // The bare rethrow: the stack is intact by construction.
        const string BareRethrow = """
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            """;

        // A catch that binds no variable has no cause to preserve.
        const string NoVariable = """
            catch (IOException)
            {
                throw new InvalidOperationException("read failed");
            }
            """;

        await Assert.That(Find("ConfigStoreFixed.cs", ConfigStoreFixed).Count).IsEqualTo(0)
            .Because("this is the fixed spelling. If the rule still flags it, the rule forbids the "
                   + "fix and cannot be adopted.");

        await Assert.That(Find("IdeBridgeFixed.cs", IdeBridgeFixed).Count).IsEqualTo(0)
            .Because("the same fix shape, in the other file");

        await Assert.That(Find("Boundary.cs", BoundaryConversion).Count).IsEqualTo(0)
            .Because("converting the failure to a Result at the boundary is what docs/ANTIPATTERNS.md "
                   + "§16 prescribes, and there is no new exception to attach a cause to");

        await Assert.That(Find("BareRethrow.cs", BareRethrow).Count).IsEqualTo(0)
            .Because("`throw;` re-throws the caught exception with its stack. That is the opposite "
                   + "of the defect");

        await Assert.That(Find("NoVariable.cs", NoVariable).Count).IsEqualTo(0)
            .Because("a catch that binds no variable caught nothing to preserve");
    }

    // =====================================================================
    // 3. The scan.
    // =====================================================================

    /// <summary>Every violation in the shipped trees, as printable lines.</summary>
    private static List<string> Scan()
    {
        var violations = new List<string>();
        foreach (string file in SourceScan.EnumerateCsFiles(ShippedTrees))
        {
            if (SourceScan.TryReadAllText(file) is not { } source)
            {
                continue;
            }

            foreach (ScanHit hit in Find(SourceScan.Relative(file), source))
            {
                violations.Add(hit.Report());
            }
        }

        return violations;
    }

    /// <summary>The violations in one snippet, for the controls.</summary>
    private static IReadOnlyList<ScanHit> Find(string displayPath, string source) =>
        Parse(displayPath, source).ToList();

    /// <summary>
    ///     The parser: the catch clauses of one file, the blocks they own, and
    ///     the <c>throw new</c> inside each block that does not carry the caught
    ///     exception as its inner exception.
    /// </summary>
    private static IEnumerable<ScanHit> Parse(string displayPath, string rawSource)
    {
        string source = Project(rawSource);
        string[] rawLines = rawSource.Split('\n');

        var catches = new List<CatchSite>();
        foreach (Match match in CatchClause.Matches(source))
        {
            string variable = match.Groups["var"].Value;
            if (variable.StartsWith('@'))
            {
                variable = variable[1..];
            }

            int blockStart = FindBlockStart(source, match.Index + match.Length);
            if (blockStart < 0)
            {
                continue;
            }

            int blockEnd = FindMatching(source, blockStart, '{', '}');
            if (blockEnd < 0)
            {
                continue;
            }

            catches.Add(new CatchSite(blockStart, blockEnd, variable));
        }

        foreach (Match thrown in ThrowNew.Matches(source))
        {
            int open = thrown.Index + thrown.Length - 1;
            int close = FindMatching(source, open, '(', ')');
            if (close < 0)
            {
                continue;
            }

            // The catch that owns this throw is the INNERMOST one around it: a
            // throw inside a nested catch belongs to that catch, not to the one
            // outside it.
            CatchSite? owner = null;
            foreach (CatchSite candidate in catches)
            {
                if (candidate.BlockStart < thrown.Index && thrown.Index < candidate.BlockEnd
                    && (owner is null || candidate.BlockStart > owner.Value.BlockStart))
                {
                    owner = candidate;
                }
            }

            if (owner is null || owner.Value.Variable.Length == 0)
            {
                continue;
            }

            if (PassesAsInner(owner.Value.Variable, source[(open + 1)..close]))
            {
                continue;
            }

            int line = CountLines(source, thrown.Index);
            yield return new ScanHit(SubId, displayPath, line, rawLines[line - 1].Trim());
        }
    }

    /// <summary>
    ///     Whether the caught exception is handed to the new exception as its
    ///     inner exception. A member read (<c>ex.Message</c>) and an
    ///     interpolation of it are not a hand-over: only the exception object
    ///     itself, as an argument of its own, is.
    /// </summary>
    private static bool PassesAsInner(string variable, string arguments)
    {
        var standalone = new Regex(
            $@"(?<![\w.@]){Regex.Escape(variable)}(?![\w.])",
            RegexOptions.CultureInvariant);

        return standalone.IsMatch(arguments);
    }

    /// <summary>
    ///     The opening brace of the block a catch clause owns. A <c>when</c>
    ///     filter is skipped first: a property pattern in a filter has braces of
    ///     its own, and reading one of them as the block would move the whole
    ///     search.
    /// </summary>
    private static int FindBlockStart(string source, int afterCatch)
    {
        int i = afterCatch;
        while (i < source.Length && char.IsWhiteSpace(source[i]))
        {
            i++;
        }

        if (i + 4 <= source.Length
            && source.AsSpan(i, 4).SequenceEqual("when")
            && (i + 4 >= source.Length || !IsIdentifierChar(source[i + 4])))
        {
            int open = source.IndexOf('(', i);
            if (open < 0)
            {
                return -1;
            }

            int close = FindMatching(source, open, '(', ')');
            if (close < 0)
            {
                return -1;
            }

            i = close + 1;
        }

        return source.IndexOf('{', i);
    }

    /// <summary>
    ///     The index of the bracket that closes the one at
    ///     <paramref name="openIndex" />, or -1 when there is none.
    /// </summary>
    private static int FindMatching(string source, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            char c = source[i];
            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>The 1-based line of an index.</summary>
    private static int CountLines(string source, int index)
    {
        int lines = 1;
        for (int i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>Whether a character can be part of an identifier.</summary>
    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    ///     The source with comment CONTENT and string-literal CONTENT blanked.
    ///     Every character offset and every newline is preserved, so a line
    ///     number computed over the projection is the line number of the real
    ///     source, and a brace inside a string cannot be read as a block.
    /// </summary>
    /// <remarks>
    ///     The literal's own quote characters stay, because the string's
    ///     INTERPOLATION HOLE is code and is blanked with the rest of the
    ///     literal: <c>$"{ex.Message}"</c> mentions the exception, and a rule
    ///     about the exception must not be fooled by the text of a message.
    /// </remarks>
    private static string Project(string source)
    {
        var blanked = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    blanked.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                blanked.Append("  ");
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '\n')
                    {
                        blanked.Append('\n');
                        i++;
                        continue;
                    }

                    if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
                    {
                        blanked.Append("  ");
                        i += 2;
                        break;
                    }

                    blanked.Append(' ');
                    i++;
                }

                continue;
            }

            // A string or char literal, possibly behind a verbatim (`@`) or
            // interpolated (`$`) marker.
            int literalStart = i;
            bool verbatim = false;
            bool interpolated = false;
            while (i < source.Length && (source[i] == '@' || source[i] == '$'))
            {
                if (source[i] == '@')
                {
                    verbatim = true;
                }
                else
                {
                    interpolated = true;
                }

                i++;
            }

            if (i < source.Length && (source[i] == '"' || source[i] == '\''))
            {
                i = BlankLiteral(source, blanked, i, verbatim, interpolated);
                continue;
            }

            // Not a literal after all: the markers were ordinary characters.
            blanked.Append(source, literalStart, i - literalStart);
            if (i < source.Length)
            {
                blanked.Append(source[i]);
                i++;
            }
        }

        return blanked.ToString();
    }

    /// <summary>
    ///     Blanks one string or char literal, starting at its opening quote, and
    ///     returns the index after its closing quote.
    /// </summary>
    private static int BlankLiteral(string source, StringBuilder blanked, int quoteIndex, bool verbatim, bool interpolated)
    {
        char quote = source[quoteIndex];
        blanked.Append(quote);
        int i = quoteIndex + 1;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n')
            {
                // An unterminated literal: stop here rather than swallow the file.
                return i;
            }

            if (interpolated && c == '{')
            {
                // The hole is code, and it is blanked with the literal: the rule
                // is about the exception object, not about the text of a message.
                blanked.Append(' ');
                int depth = 1;
                i++;
                while (i < source.Length && depth > 0)
                {
                    if (source[i] == '{')
                    {
                        depth++;
                    }
                    else if (source[i] == '}')
                    {
                        depth--;
                    }

                    blanked.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == quote)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                {
                    blanked.Append("  ");
                    i += 2;
                    continue;
                }

                blanked.Append(quote);
                return i + 1;
            }

            if (!verbatim && c == '\\' && i + 1 < source.Length)
            {
                blanked.Append("  ");
                i += 2;
                continue;
            }

            blanked.Append(' ');
            i++;
        }

        return i;
    }
}
