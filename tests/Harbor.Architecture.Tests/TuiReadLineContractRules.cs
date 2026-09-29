// TuiReadLineContractRules.cs — the guard for issue #589.
//
// WHY THIS FILE EXISTS
// --------------------
// `ITuiRenderer.ReadLineAsync` used to return `Task<Result<string>>`, and EVERY
// implementation answered end-of-input with `Result.Success(line ?? string.Empty)`.
// "No line was read" and "the user submitted an empty line" therefore had the same
// representation, and a line-buffered consumer could not tell them apart. The one
// that did — `ReplRunner.RunLineReplAsync` — branched on
// `IsNullOrWhiteSpace(input) → continue`, so a closed stdin or a Ctrl-D sent the
// REPL into a tight loop with no I/O and no exit: 100% CPU, forever.
//
// The fix is a type change (`Maybe<string>`), and a type change alone is exactly
// the kind of thing that rots: the next renderer lands, someone writes
// `Result.Success(line ?? "")` out of habit, and the hang is back with no test
// noticing. `BannedSymbols.txt` cannot express this rule — it names framework
// symbols, and "this method must not fabricate a value" is not a symbol. Hence
// this file.
//
// TWO RULES, TWO KINDS OF PROOF
// -----------------------------
//   1. SHAPE (reflection): `ReadLineAsync` returns `Task<Maybe<string>>` on the
//      interface and on every public implementor. Reflection, not a text scan,
//      because the shape is a property of the compiled type.
//   2. FABRICATION (source): no `ReadLineAsync` body may turn a null/absent read
//      into a present empty value. A source rule because the fabrication is a
//      statement, not a signature — reflection cannot see it.
//
// NON-VACUITY — the part that makes the guard worth having
// --------------------------------------------------------
// A source scan that matches nothing is indistinguishable from a source scan that
// is broken, and a broken guard is worse than no guard because it is believed.
// Two tests below close that: `Matcher_...` runs the SAME matcher against
// synthetic positive and negative controls, and `..._FindsEveryCurrentImplementor`
// requires the discovery step to locate a real, non-trivial set of files. If the
// discovery path or the matcher ever degrades, these go RED instead of quietly
// passing.
//
// DISCOVERY IS DELIBERATE, NOT A HARD-CODED LIST
// ----------------------------------------------
// The implementor set is discovered by globbing the TUI trees for files that
// declare `ReadLineAsync(string prompt`, so a brand-new renderer is covered the
// day it is written rather than the day somebody remembers to edit this file.
// A hard-coded list would be a hand-maintained union — precisely the shape issue
// #578 says ages silently.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Terminal.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Enforces the <c>Maybe&lt;string&gt;</c> contract on <see cref="ITuiRenderer.ReadLineAsync" />
///     and the absence of value fabrication at end of input. See the file header for
///     the incident and the non-vacuity argument.
/// </summary>
public sealed class TuiReadLineContractRules
{
    /// <summary>Repo-relative trees that may declare an <c>ITuiRenderer</c>.</summary>
    private static readonly string[] RendererTrees =
    [
        "src/Harbor.Tui.CellForge",
        "src/Harbor.Tui.CellForge.Engine",
        "src/Harbor.Tui.AnsiPlain",
        "src/Harbor.Tui.NickConsoleEx",
        "src/Harbor.Tui.Notifications",
        "src/Harbor.Tui.Sixel",
        "contrib/tui",
    ];

    /// <summary>
    ///     A read whose result is absent must not be turned into an empty value. Each
    ///     alternative is a spelling of the same fabrication.
    /// </summary>
    private static readonly string[] FabricationPatterns =
    [
        @"\?\?\s*(string\.Empty|\"\")",              // line ?? string.Empty
        @"Maybe\.From\(\s*Console\.ReadLine\(\)\s*\?\?", // Maybe.From(Console.ReadLine() ?? "")
        @"(Result\.Success|Success)\(\s*string\.Empty\s*\)", // Success(string.Empty)
        @"(Result\.Success|Success)\(\s*line\s*\?\?",      // Success(line ?? "")
    ];

    /// <summary>Declaration form that identifies a renderer implementing the interface.</summary>
    private const string DeclarationNeedle = "ReadLineAsync(string prompt";

    // ── Rule 1: shape ────────────────────────────────────────────────────────

    [Test]
    public async Task ReadLineAsync_Interface_ReturnsMaybeOfString()
    {
        MethodInfo method = typeof(ITuiRenderer).GetMethod(nameof(ITuiRenderer.ReadLineAsync))!;

        await Assert.That(method.ReturnType).IsEqualTo(typeof(Task<Maybe<string>>))
            .Because(
                "End of input is absence, not failure: there is no error message to report, so the " +
                "contract is Maybe<string>. A Result<string> forces every implementation to invent a " +
                "value for 'no input', which is what wedged the line REPL at 100% CPU (#589).");
    }

    [Test]
    public async Task ReadLineAsync_EveryImplementor_MatchesTheInterfaceShape()
    {
        // GetInterfaceMap, not GetMethod(name): an EXPLICIT implementation
        // (`Task<Maybe<string>> ITuiRenderer.ReadLineAsync(...)`) is invisible to
        // name lookup, so a name-based walk would report "no method, nothing to
        // check" for exactly the shape most likely to be migrated by hand.
        Type[] implementors = FindRendererTypes();

        await Assert.That(implementors.Length).IsGreaterThanOrEqualTo(1)
            .Because(
                "Non-vacuity: Harbor.Tui.AnsiPlain is a direct project reference of this test project, " +
                "so at least one implementor must always be reachable. Zero would mean the walk " +
                "silently stopped and every assertion below became vacuous.");

        List<string> offenders = [];
        int checkedMethods = 0;

        foreach (Type type in implementors)
        {
            InterfaceMapping map = type.GetInterfaceMap(typeof(ITuiRenderer));
            for (int i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name != nameof(ITuiRenderer.ReadLineAsync))
                {
                    continue;
                }

                checkedMethods++;
                MethodInfo target = map.TargetMethods[i];
                if (target.ReturnType != typeof(Task<Maybe<string>>))
                {
                    offenders.Add($"{type.FullName}.{target.Name} -> {target.ReturnType}");
                }
            }
        }

        await Assert.That(checkedMethods).IsGreaterThanOrEqualTo(implementors.Length)
            .Because(
                "Every implementor reached by the walk must contribute its ReadLineAsync mapping. A " +
                "short count means the interface-map lookup missed implementations, which is the " +
                "exact blind spot this test exists to cover.");

        await Assert.That(offenders).IsEmpty()
            .Because(
                "A renderer must not declare its own ReadLineAsync return type. If one does, the " +
                "absence case is unhandled there even though the interface itself compiles.");
    }

    // ── Rule 2: fabrication ──────────────────────────────────────────────────

    [Test]
    public async Task ReadLineAsync_NoImplementation_FabricatesAValueForAbsentInput()
    {
        (string File, IReadOnlyList<string> Hits)[] scanned = ScanImplementorBodies();
        IReadOnlyList<string> files = [.. scanned.Select(s => s.File)];

        await Assert.That(files.Count).IsGreaterThanOrEqualTo(5)
            .Because(
                "Non-vacuity for the source rule: the scan must actually reach the renderer bodies. " +
                "Fewer than 5 files means the glob or the declaration needle stopped matching.");

        List<string> violations = [];
        foreach ((string file, IReadOnlyList<string> hits) in scanned)
        {
            foreach (string hit in hits)
            {
                violations.Add($"{file}: {hit}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A renderer that answers 'no line was read' with an empty string cannot be " +
                "distinguished from a blank submission by the consumer. That is the #589 hang. " +
                "Return Maybe.None.");
    }

    // ── Non-vacuity of the matcher itself ────────────────────────────────────

    [Test]
    public async Task Matcher_FabricationPatterns_FlagTheKnownBadSpelling()
    {
        const string Bad = """
            public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
            {
                string? line = Console.ReadLine();
                return Task.FromResult(Maybe.From(line ?? string.Empty));
            }
            """;

        IReadOnlyList<string> hits = MatchFabrications(Bad);

        await Assert.That(hits.Count).IsGreaterThan(0)
            .Because("The #589 spelling is a real positive control; a miss means the rule is inert.");
    }

    [Test]
    public async Task Matcher_FabricationPatterns_AcceptTheFixedSpelling()
    {
        const string Good = """
            public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
            {
                string? line = Console.ReadLine();
                return Task.FromResult(Maybe.From(line));
            }
            """;

        IReadOnlyList<string> hits = MatchFabrications(Good);

        await Assert.That(hits).IsEmpty()
            .Because("The fixed spelling must not be flagged, or the guard cannot be adopted.");
    }

    [Test]
    public async Task Matcher_FabricationPatterns_FlagResultEmptyNotJustNullCoalesce()
    {
        const string Bad = """
            public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
                => Task.FromResult(Maybe.From(Result.Success(string.Empty).Value));
            """;

        IReadOnlyList<string> hits = MatchFabrications(Bad);

        await Assert.That(hits.Count).IsGreaterThan(0)
            .Because(
                "'Cannot read input at all' is spelled several ways; the guard must catch the shapes " +
                "a future renderer is likely to write, not only the one that shipped the bug.");
    }

    // ── Discovery helpers ────────────────────────────────────────────────────

    /// <summary>Every public type in the loaded Harbor inventory that implements <see cref="ITuiRenderer" />.</summary>
    private static Type[] FindRendererTypes()
    {
        List<Type> found = [];
        foreach (Assembly assembly in ArchitectureTestHelpers.LoadHarborAssemblies().Values)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null).Select(t => t!)];
            }

            found.AddRange(types.Where(t =>
                t is { IsClass: true, IsAbstract: false } &&
                typeof(ITuiRenderer).IsAssignableFrom(t)));
        }

        return [.. found];
    }

    /// <summary>Repo-relative source files that declare an <c>ITuiRenderer.ReadLineAsync</c>.</summary>
    internal static string[] FindImplementorFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        List<string> found = [];
        foreach (string tree in RendererTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (text.Contains(DeclarationNeedle, StringComparison.Ordinal))
                {
                    found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>Scans the <c>ReadLineAsync</c> BODY of every discovered implementor.</summary>
    private static (string File, IReadOnlyList<string> Hits)[] ScanImplementorBodies()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        List<(string, IReadOnlyList<string>)> scanned = [];
        foreach (string relative in FindImplementorFiles())
        {
            string text = File.ReadAllText(Path.Combine(root, relative));

            // Scope to the method body. A whole-file scan reports unrelated
            // `?? string.Empty` elsewhere in the renderer (Sixel does string
            // surgery on image bytes) and would cry wolf on the first honest hit.
            // The interface and the abstract declaration have no body and are
            // skipped by design — the doc comment above the signature is not code.
            if (TryExtractBody(text, DeclarationNeedle, out string body))
            {
                scanned.Add((relative, MatchFabrications(body)));
            }
        }

        return [.. scanned];
    }

    /// <summary>
    ///     Extracts the body of the member declaring <paramref name="needle" />: a
    ///     brace-balanced block, or an expression body up to its terminating
    ///     semicolon. Returns <c>false</c> for a declaration with no body
    ///     (interface member, abstract member).
    /// </summary>
    internal static bool TryExtractBody(string fileText, string needle, out string body)
    {
        int start = fileText.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
        {
            body = string.Empty;
            return false;
        }

        int i = start + needle.Length;

        // Skip the remainder of the parameter list, which the needle stops in the
        // middle of: `ReadLineAsync(string prompt, CancellationToken ct = default)`
        // followed by a body. The needle already consumed the opening paren, so
        // the depth starts at 1; tracking it means a default value containing a
        // call cannot terminate the list early.
        int parenDepth = 1;
        while (i < fileText.Length)
        {
            char c = fileText[i];
            if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    i++;
                    break;
                }
            }

            i++;
        }

        while (i < fileText.Length && char.IsWhiteSpace(fileText[i]))
        {
            i++;
        }

        if (i >= fileText.Length)
        {
            body = string.Empty;
            return false;
        }

        if (fileText[i] == ';')
        {
            // `public Task<Maybe<string>> ReadLineAsync(string prompt, ...);`
            body = string.Empty;
            return false;
        }

        if (fileText[i] == '=' && i + 1 < fileText.Length && fileText[i + 1] == '>')
        {
            int end = fileText.IndexOf(';', i);
            body = end < 0 ? fileText[i..] : fileText[i..end];
            return true;
        }

        if (fileText[i] != '{')
        {
            body = string.Empty;
            return false;
        }

        int depth = 0;
        for (int j = i; j < fileText.Length; j++)
        {
            char c = fileText[j];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    body = fileText[i..(j + 1)];
                    return true;
                }
            }
        }

        // Unbalanced — treat as no body rather than scanning half a method.
        body = string.Empty;
        return false;
    }

    /// <summary>Fabrication hits in one body, one message per distinct pattern that fired.</summary>
    private static IReadOnlyList<string> MatchFabrications(string body)
    {
        List<string> hits = [];
        foreach (string pattern in FabricationPatterns)
        {
            if (Regex.IsMatch(body, pattern, RegexOptions.CultureInvariant))
            {
                hits.Add(pattern);
            }
        }

        return hits;
    }
}
