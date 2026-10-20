// ExtensionAxisDoorHonestyRule.cs — the door-honesty half of the successor to
// #620's ExtensionAxisFreezeRule (issue #1719), and the guard #1758 asks for.
//
// WHY THIS FILE EXISTS
// --------------------
// #1719 lifted the #555 freeze and replaced the closed set with "every axis is
// registered in one list, and its two halves are connected". That grades the
// DECLARATION and the WIRING. It does not grade the ANSWER.
//
// A `Register*` door on `IPluginLoadHost` answers with a `Result`, and that
// answer is the only signal the caller gets. `Result.Success()` means "the
// contribution is registered"; `Result.Failure` means "it was refused, and
// here is why". A door that answers Success while registering nothing is
// #620's defect in a different shape: the interface promises a registration,
// the host says "done", and nothing happened. The caller cannot tell the two
// apart, and neither can the plugin, which concludes its provider is live.
//
// `src/Harbor.Plugins.Host/McpPluginLoadHost.cs` had six of them. The MCP
// stdio route exposes tools only; the other six doors logged "ignoring" and
// answered `Result.Success()`. #1758 is the owner decision that says this is
// a lie and must be caught, and it names the successor rule of #1719 as the
// place where the honest result matters: "while `Success` means 'maybe, and
// not', there is nothing to check".
//
// WHAT IT GRADES
// --------------
// For every implementation of `IPluginLoadHost` in the product (`src/` +
// `apps/`), for every door of the interface that implementation declares: if
// the door's body answers `Result.Success()`, the body must ALSO show an
// effect — a write to the host's state, a mutating call, or a delegation to a
// method of the same type. A door that answers Success and shows no effect
// fails.
//
// The perimeter is the IMPLEMENTATIONS, not the interface: the interface
// declares the doors, the implementations answer them, and the answer is
// where the lie lives. `tests/` is outside the perimeter — a test double
// answers whatever the test needs it to answer, and grading a fixture would
// forbid the very doubles the suite is made of.
//
// HOW THE EFFECT IS READ, AND WHY IT IS A SOURCE SCAN
// ---------------------------------------------------
// The answer is CODE, and code is what a source scan reads. A door's effect is
// a write to the host's state, and the four shapes that take are:
//
//   * a member write — `this._tools = …`;
//   * an indexer write — `_tools[name] = tool`;
//   * a mutating call — `_tools.TryAdd(name, tool)`, `_registry.Add(…)`;
//   * a delegation — `StoreProvider(id, factory)`, a call to a method of the
//     same type, which is the effect happening one frame down.
//
// The scan reads the door's own body, so the delegation case has to be
// VISIBLE there: the call to the helper is in the body even though the write
// is not. That is why the delegation shape is accepted rather than a
// limitation — a door that stores through a helper still shows the call.
//
// The boundary is stated rather than hidden: a door whose effect is performed
// by code the scan cannot see — reflection, a delegate, an event handler
// registered elsewhere — is not recognised as having an effect and the gate
// will ask for the effect to be visible in the door. That is the right way
// round: the gate is about a door that PROMISES a registration, and a promise
// nobody can see is the thing #1758 is about.
//
// NON-VACUITY
// -----------
//   1. TheDoorScanIsLive — the scan really walked a checkout, really found the
//      product's load-host implementation, and really found every door the
//      interface declares. Without it a broken perimeter reports zero doors
//      and the rule below is satisfied by having nothing to look at.
//   2. NegativeControl_TheDoorGraderCatchesAnInjectedViolation — THE PROOF
//      THAT THE GRADER CAN FAIL. It starts from the REAL inventory and asserts
//      it is clean, then injects a synthetic door that answers Success and
//      registers nothing and requires the grader to report it, then injects a
//      synthetic door that DOES store and requires the grader to stay silent.
//      The second half matters as much as the first: a grader that reports
//      everything is as useless as one that reports nothing.

using System.Text.RegularExpressions;
using Harbor.Plugins.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One <c>Register*</c> door implementation found in the product.
/// </summary>
/// <param name="Owner">The type that implements the door.</param>
/// <param name="Door">The door's name, as declared on <c>IPluginLoadHost</c>.</param>
/// <param name="Body">The door's body — everything after the parameter list.</param>
/// <param name="File">Where the implementation was found, repo-relative, for the failure message.</param>
internal readonly record struct DoorImplementation(string Owner, string Door, string Body, string File);

/// <summary>
///     Finds the doors the product answers: every implementation of
///     <c>IPluginLoadHost</c> under <c>src/</c> + <c>apps/</c>, and the body of
///     each <c>Register*</c> door it declares.
/// </summary>
internal static class ExtensionAxisDoorProbe
{
    /// <summary>The load host interface whose implementations are graded.</summary>
    internal const string HostInterfaceName = "IPluginLoadHost";

    /// <summary>
    ///     A type declaration and the base list it carries. The base list is what
    ///     says whether the type is a load host.
    /// </summary>
    private readonly record struct TypeDeclaration(string Name, string Bases, int End);

    /// <summary>
    ///     A type declaration. The base list stops at the first <c>{</c> or
    ///     <c>;</c>, so a declaration spread over two lines is still read whole.
    /// </summary>
    private static readonly Regex TypePattern = new(
        @"\b(?:class|record|struct)\s+(?<name>[A-Za-z_]\w*)(?:<[^<>()]*>)?\s*(?::\s*(?<bases>[^{;]*))?",
        RegexOptions.Compiled);

    /// <summary>
    ///     A door declaration: a <c>Register*</c> method that answers with a
    ///     <c>Result</c>. The name is later filtered against the interface's own
    ///     door set, so a host that also has an unrelated <c>Register…</c> helper
    ///     is not graded as a door.
    /// </summary>
    private static readonly Regex DoorPattern = new(
        @"(?:public|internal|protected|private)\s+(?:async\s+)?(?:Task\s*<\s*)?Result(?:\s*>)?\s+(?<name>Register\w*)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Any identifier — used to read a base list as a set of type names.</summary>
    private static readonly Regex IdentifierPattern = new(@"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);

    /// <summary>
    ///     Every door the product answers, found in the product's own source.
    /// </summary>
    /// <param name="repoRoot">The repository root; a missing checkout finds nothing.</param>
    /// <param name="interfaceDoors">The door names the interface declares.</param>
    internal static IReadOnlyList<DoorImplementation> ReadDoors(
        string? repoRoot,
        IReadOnlyList<string> interfaceDoors)
    {
        ArgumentNullException.ThrowIfNull(interfaceDoors);

        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return [];
        }

        var wanted = new HashSet<string>(interfaceDoors, StringComparer.Ordinal);
        var found = new List<DoorImplementation>();

        foreach (string file in ExtensionAxisProbe.EnumerateSources(repoRoot))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            found.AddRange(ReadDoorsIn(
                string.Join("\n", SourceCommentStripper.StripAll(lines)),
                wanted,
                SourceScan.Relative(file)));
        }

        return found;
    }

    /// <summary>
    ///     The doors of one file. Comments are already stripped by the caller, so
    ///     the prose that documents a door cannot be read as the door.
    /// </summary>
    private static IReadOnlyList<DoorImplementation> ReadDoorsIn(
        string text,
        HashSet<string> wanted,
        string file)
    {
        var declarations = new List<TypeDeclaration>();
        foreach (Match match in TypePattern.Matches(text))
        {
            declarations.Add(new TypeDeclaration(
                match.Groups["name"].Value,
                match.Groups["bases"].Value,
                match.Index + match.Length));
        }

        HashSet<string> implementers = ResolveImplementers(declarations);

        var doors = new List<DoorImplementation>();
        foreach (TypeDeclaration declaration in declarations)
        {
            if (!implementers.Contains(declaration.Name))
            {
                continue;
            }

            doors.AddRange(ReadDoorsOf(declaration.Name, text, declaration.End, wanted, file));
        }

        return doors;
    }

    /// <summary>
    ///     The types that implement the load host, directly or through a base
    ///     class declared in the same file. The fixpoint is what makes
    ///     <c>class Derived : BaseHost</c> count when <c>BaseHost</c> is the one
    ///     that implements the interface.
    /// </summary>
    private static HashSet<string> ResolveImplementers(List<TypeDeclaration> declarations)
    {
        var implementers = new HashSet<string>(StringComparer.Ordinal);
        bool changed = true;

        while (changed)
        {
            changed = false;
            foreach (TypeDeclaration declaration in declarations)
            {
                if (implementers.Contains(declaration.Name))
                {
                    continue;
                }

                bool isHost = false;
                foreach (Match identifier in IdentifierPattern.Matches(declaration.Bases))
                {
                    string candidate = identifier.Value;
                    if (candidate == HostInterfaceName || implementers.Contains(candidate))
                    {
                        isHost = true;
                        break;
                    }
                }

                if (!isHost)
                {
                    continue;
                }

                implementers.Add(declaration.Name);
                changed = true;
            }
        }

        return implementers;
    }

    /// <summary>The doors one implementing type declares, with their bodies.</summary>
    private static IReadOnlyList<DoorImplementation> ReadDoorsOf(
        string owner,
        string text,
        int declarationEnd,
        HashSet<string> wanted,
        string file)
    {
        int open = text.IndexOf('{', declarationEnd);
        if (open < 0)
        {
            return [];
        }

        int close = FindMatchingBrace(text, open);
        if (close < 0)
        {
            return [];
        }

        string body = text[open..(close + 1)];
        var doors = new List<DoorImplementation>();

        foreach (Match match in DoorPattern.Matches(body))
        {
            string name = match.Groups["name"].Value;
            if (!wanted.Contains(name))
            {
                continue;
            }

            // The pattern ends with the opening parenthesis of the parameter list.
            int parametersOpen = match.Index + match.Length - 1;
            int parametersClose = FindMatchingParen(body, parametersOpen);
            if (parametersClose < 0)
            {
                continue;
            }

            string? doorBody = ExtractBody(body, parametersClose + 1);
            if (doorBody is null)
            {
                continue;
            }

            doors.Add(new DoorImplementation(owner, name, doorBody, file));
        }

        return doors;
    }

    /// <summary>
    ///     The body of a method: a braced block, or the expression of an
    ///     expression-bodied member. A member with neither (an interface member,
    ///     an abstract one) has no body and is not graded.
    /// </summary>
    private static string? ExtractBody(string text, int afterParameters)
    {
        int i = afterParameters;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            return null;
        }

        if (text[i] == '{')
        {
            int close = FindMatchingBrace(text, i);
            return close < 0 ? null : text[i..(close + 1)];
        }

        if (text[i] == '=' && i + 1 < text.Length && text[i + 1] == '>')
        {
            int end = FindStatementEnd(text, i + 2);
            return end < 0 ? null : text[i..(end + 1)];
        }

        return null;
    }

    /// <summary>
    ///     The index of the <c>}</c> that closes the <c>{</c> at
    ///     <paramref name="open" />. String and character literals are skipped, so
    ///     a brace inside a message is not counted.
    /// </summary>
    private static int FindMatchingBrace(string text, int open)
    {
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i) - 1;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
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

    /// <summary>The index of the <c>)</c> that closes the <c>(</c> at <paramref name="open" />.</summary>
    private static int FindMatchingParen(string text, int open)
    {
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i) - 1;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
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

    /// <summary>
    ///     The index of the <c>;</c> that ends a statement. Brackets are counted
    ///     so a <c>;</c> inside a lambda or an initialiser does not end it, and
    ///     literals are skipped so a <c>;</c> inside a message does not either.
    /// </summary>
    private static int FindStatementEnd(string text, int from)
    {
        int depth = 0;

        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i) - 1;
                continue;
            }

            if (c is '(' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                depth--;
            }
            else if (c == ';' && depth <= 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     The index just past the literal that starts at <paramref name="start" />.
    ///     A verbatim string's doubled quote is an escape, not the end.
    /// </summary>
    private static int SkipLiteral(string text, int start)
    {
        char quote = text[start];
        bool verbatim = start > 0 && text[start - 1] == '@';

        for (int i = start + 1; i < text.Length; i++)
        {
            char c = text[i];

            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }

                    return i + 1;
                }

                continue;
            }

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == quote)
            {
                return i + 1;
            }
        }

        return text.Length;
    }
}

/// <summary>
///     The grading function for door honesty. Pure, over bodies, so the negative
///     control can hand it an injected door and watch it fail.
/// </summary>
internal static class ExtensionAxisDoorGrader
{
    /// <summary>
    ///     The door answering <c>Result.Success()</c>. The name is required: a
    ///     bare <c>Success</c> would also match a local helper and a message.
    /// </summary>
    private static readonly Regex SuccessPattern = new(@"Result\s*\.\s*Success\b", RegexOptions.Compiled);

    /// <summary>A write to a member of the host: <c>this._tools = …</c>.</summary>
    private static readonly Regex MemberWritePattern = new(
        @"\bthis\s*\.\s*\w+\s*(?:\?\?|\|\||&&|\+|-|\*|/|%|\^|\|)?=(?!=)",
        RegexOptions.Compiled);

    /// <summary>A write through an indexer: <c>_tools[name] = tool</c>.</summary>
    private static readonly Regex IndexerWritePattern = new(
        @"\b\w+\s*\[[^\]]*\]\s*(?:\?\?|\|\||&&|\+|-|\*|/|%|\^|\|)?=(?!=)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A mutating call: <c>_tools.TryAdd(…)</c>, <c>_registry.Add(…)</c>. The
    ///     name is anchored to the call, so <c>.SessionStoreIgnored(…)</c> is not a
    ///     <c>Store</c>.
    /// </summary>
    private static readonly Regex MutatingCallPattern = new(
        @"\.\s*(?:Add|AddOrUpdate|AddRange|Append|GetOrAdd|Insert|Put|Push|Register|Set|SetItem|SetValue|Store|TryAdd|TryEnqueue|Enqueue)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     A call to a method of the same type — the effect happening one frame
    ///     down. The name must start with an upper-case letter (a C# method does)
    ///     and must not be qualified by a receiver.
    /// </summary>
    private static readonly Regex BareCallPattern = new(
        @"(?<!new )(?<![.\w])(?<call>[A-Z]\w*)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     Calls that are NOT an effect: the logging helpers. A door whose body is
    ///     "log it and answer Success" is exactly the lie #1758 is about, so a log
    ///     call must not be mistaken for the registration.
    /// </summary>
    private static readonly HashSet<string> NonEffectCalls = new(StringComparer.Ordinal)
    {
        "Log", "LogCritical", "LogDebug", "LogError", "LogInformation", "LogNone", "LogTrace", "LogWarning",
        "Trace", "Debug", "Info", "Information", "Warn", "Warning", "Error", "Fatal",
    };

    /// <summary>
    ///     Every door that answers Success without registering anything.
    /// </summary>
    /// <param name="doors">The doors the product answers.</param>
    internal static IReadOnlyList<AxisDefect> Grade(IReadOnlyList<DoorImplementation> doors)
    {
        ArgumentNullException.ThrowIfNull(doors);

        var defects = new List<AxisDefect>();

        foreach (DoorImplementation door in doors)
        {
            if (!ClaimsSuccess(door.Body) || HasEffect(door.Body))
            {
                continue;
            }

            defects.Add(new AxisDefect(
                AxisDefectKind.SilentSuccess,
                $"{door.Owner}.{door.Door} ({door.File}): the door answers Result.Success() but registers "
                + "nothing — an 'accepted' the host never performed, reported as done (#1758)"));
        }

        return defects;
    }

    /// <summary>Whether the body answers <c>Result.Success()</c>.</summary>
    internal static bool ClaimsSuccess(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SuccessPattern.IsMatch(body);
    }

    /// <summary>
    ///     Whether the body shows an effect: a member write, an indexer write, a
    ///     mutating call, or a delegation to a method of the same type.
    /// </summary>
    internal static bool HasEffect(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (MemberWritePattern.IsMatch(body) || IndexerWritePattern.IsMatch(body) || MutatingCallPattern.IsMatch(body))
        {
            return true;
        }

        foreach (Match match in BareCallPattern.Matches(body))
        {
            if (!NonEffectCalls.Contains(match.Groups["call"].Value))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     Guard for issue #1758, and the answer-half of #1719's successor rule: a
///     door on <c>IPluginLoadHost</c> that answers <c>Result.Success()</c> has to
///     have registered something.
/// </summary>
public sealed class ExtensionAxisDoorHonestyRule
{
    private static readonly Lazy<IReadOnlyList<DoorImplementation>> Doors = new(
        () => ExtensionAxisDoorProbe.ReadDoors(
            RepoPaths.RepoRoot,
            ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost))));

    /// <summary>Every defect the grader finds against the real inventory.</summary>
    private static IReadOnlyList<AxisDefect> Defects => ExtensionAxisDoorGrader.Grade(Doors.Value);

    /// <summary>
    ///     No door answers "done" for a registration it did not perform.
    /// </summary>
    [Test]
    public async Task EveryDoor_ThatAnswersSuccess_ActuallyRegisters()
    {
        var defects = Defects.ToList();

        await Assert.That(string.Join(" | ", defects.Select(d => d.Detail)))
            .IsEqualTo(string.Empty)
            .Because(
                "a door on IPluginLoadHost answers with a Result, and that answer is the only signal the "
                + "caller gets: Success means the contribution was registered, Failure means it was refused "
                + "and says why. A door that answers Success while registering nothing makes 'accepted' and "
                + "'dropped' indistinguishable — a plugin concludes its provider, agent or panel is live when "
                + "it is not, and the caller has no way to tell (#1758). Either store the contribution, or "
                + "answer Result.Failure with the reason the route cannot carry it. "
                + "Silent successes: "
                + (defects.Count == 0 ? "(none)" : string.Join(" | ", defects.Select(d => d.Detail))));
    }

    /// <summary>
    ///     The scan really walked a checkout, really found the product's load
    ///     host, and really found every door the interface declares.
    /// </summary>
    [Test]
    public async Task TheDoorScanIsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the door scan needs a repository checkout; without one it reports zero doors and the "
                   + "rule above is satisfied by having nothing to look at");

        IReadOnlyList<string> interfaceDoors = ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost));

        await Assert.That(interfaceDoors.Count).IsGreaterThan(0)
            .Because("the interface declares the doors; an empty set would make the scan below vacuous");

        var owners = Doors.Value.Select(d => d.Owner).Distinct().ToList();
        await Assert.That(owners.Count).IsGreaterThan(0)
            .Because("no implementation of IPluginLoadHost was found under src/ or apps/; the perimeter is "
                   + "wrong and the rule above is vacuously green");

        var found = Doors.Value.Select(d => d.Door).Distinct().ToList();
        var missing = interfaceDoors.Where(d => !found.Contains(d, StringComparer.Ordinal)).ToList();

        await Assert.That(string.Join(" | ", missing)).IsEqualTo(string.Empty)
            .Because(
                "the scan has to reach every door the interface declares, or the perimeter is wrong. A door "
                + "the scan cannot see is a door it cannot grade. Missing: "
                + (missing.Count == 0 ? "(none)" : string.Join(" | ", missing)));
    }

    /// <summary>
    ///     THE PROOF THAT THE GRADER CAN FAIL. A synthetic door that answers
    ///     Success and registers nothing has to be reported; a synthetic door
    ///     that stores the contribution must not be.
    /// </summary>
    [Test]
    public async Task NegativeControl_TheDoorGraderCatchesAnInjectedViolation()
    {
        // ---- The real inventory is clean. ----
        var clean = ExtensionAxisDoorGrader.Grade(Doors.Value);

        await Assert.That(string.Join(" | ", clean.Select(d => d.Detail))).IsEqualTo(string.Empty)
            .Because(
                "the negative control below proves the grader reports an INJECTED violation; that is only "
                + "evidence if the real inventory reports nothing, otherwise the report could have come from "
                + "a pre-existing defect. Real defects: "
                + (clean.Count == 0 ? "(none)" : string.Join(" | ", clean.Select(d => d.Detail))));

        // ---- INJECTION: a door that answers Success and registers nothing. ----
        // The body is what the probe extracts — everything AFTER the parameter
        // list — so the control feeds the grader the same shape it grades.
        var inert = new DoorImplementation(
            "SyntheticHost",
            "RegisterTelemetry",
            """
            {
                _logger.LogInformation("not exposed over this route: {Id}", id);
                return Result.Success();
            }
            """,
            "(synthetic)");

        var injected = ExtensionAxisDoorGrader.Grade([inert]);

        await Assert.That(injected.Any(d => d.Kind is AxisDefectKind.SilentSuccess
                                            && d.Detail.Contains("RegisterTelemetry", StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "a door that answers Result.Success() without registering anything is the defect #1758 is "
                + "about — the shape McpPluginLoadHost had six of. If the grader does not report it, the "
                + "gate enforces nothing. Reported: "
                + (injected.Count == 0 ? "(none)" : string.Join(" | ", injected.Select(d => d.Detail))));

        // ---- THE OTHER HALF: a door that DOES store must stay silent. ----
        var effective = new DoorImplementation(
            "SyntheticHost",
            "RegisterTelemetry",
            """
            {
                _sinks[id] = new TelemetrySink();
                return Result.Success();
            }
            """,
            "(synthetic)");

        var reported = ExtensionAxisDoorGrader.Grade([effective]);

        await Assert.That(string.Join(" | ", reported.Select(d => d.Detail))).IsEqualTo(string.Empty)
            .Because(
                "a grader that reports every door is as useless as one that reports none — it would fail the "
                + "product's honest doors and the rule would be turned off. A door that stores the "
                + "contribution is not a violation. Reported: "
                + (reported.Count == 0 ? "(none)" : string.Join(" | ", reported.Select(d => d.Detail))));
    }
}
