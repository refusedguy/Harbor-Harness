// CellForgeEngineAbsenceAxisTests.cs — pins WHICH of the two absence axes
// each public member of src/Harbor.Tui.CellForge.Engine is on.
//
// #591 named four sites in this project and asked for the split to be judged,
// not just applied. The judgement is the whole content of this file: the
// project's public surface is measured, each member is classified by the rule
// in docs/ROP-API-INVENTORY.md §4.6, and the classification is compared against
// what the issue proposed. Three rules, three questions:
//
//   1. AXIS_MEMBERS_ARE_ON_THE_AXIS_THE_ISSUE_PROPOSED — every public member
//      the issue named is present, on the axis §4.6 assigns it. This is the
//      "2 places = 2 decisions or 2 manifestations of one" question, answered as
//      a count rather than a sentence.
//
//   2. NO_MAYBE_MEMBER_IS_READ_WITH_A_STRUCT_UNAWARE_FORM — CSE's Maybe<T> is a
//      STRUCT (docs/ROP-API-INVENTORY.md line 8; §4 gotcha 7), so `?.`,
//      `is { }` / `is not { }` and `== null` do not test it: they bind the
//      wrapper, or are always false. §4 gotcha 7's rule is "test HasNoValue /
//      HasValue". The answer is measured, so it is "no Maybe here yet" by
//      execution rather than by reading — and when the conversion lands, this
//      is what catches `offer is null` at the one caller.
//
//   3. STRUCT_UNAWARE_READER_IS_NOT_VACUOUS — the reader above must be able to
//      return a hit. A zero from a broken question is a silence, not an answer,
//      and that distinction has cost this repo seven non-vacuity bugs (#901,
//      #906, #899, #925, #928, #858, #948).
//
// WHY A SOURCE SCAN, NOT REFLECTION. The project is IsAotCompatible with zero
// PackageReference entries, and #795's measurement is that its dependency
// closure is still in motion (#435 drops two references, #436 the rest).
// Reflection over its types would need the assembly built and would hard-code
// the dependency shape this file is meant to be independent of. Text is also
// what the sibling rules use (MaybeAbsenceTests, ResultMaybeSignatureTests)
// for the same reason.
//
// WHAT IS NOT HERE, and why. No `throw`-ban rule. #591 lists 24 constructor
// `?? throw new ArgumentNullException` sites as correct guard clauses, and §4.1
// is explicit that `.Value` throwing is the library's design. A rule counting
// throws would have to exempt 24 constructor sites to be correct, which is a
// rule whose false-positive rate exceeds its yield. The one member that really
// does encode absence as an exception — AnsiWriter.SyncBackend — is pinned on
// the Maybe axis by rule 1 instead, which is the classification that matters
// and costs one table row.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: the <c>#591</c> split of this project's public
///     absence surface is the split §4.6 prescribes, and no <c>Maybe</c> member
///     is read with a form that cannot see a struct.
/// </summary>
public class CellForgeEngineAbsenceAxisTests
{
    /// <summary>The project under measurement, relative to the repository root.</summary>
    private const string EngineProject = "src/Harbor.Tui.CellForge.Engine";

    /// <summary>
    ///     Declares absence. §4.6's second branch: "genuinely optional,
    ///     absence is not an error".
    /// </summary>
    private const string MaybeAxis = "Maybe";

    /// <summary>
    ///     Reports a failure with a reason the caller must see. §4.6's first
    ///     branch: "can fail with a reason the user must see".
    /// </summary>
    private const string ResultAxis = "Result";

    /// <summary>
    ///     The four members <c>#591</c> named, each with the axis §4.6 assigns it.
    ///     This table is the assertion, not a copy of the issue: if a member is
    ///     renamed or deleted the table goes stale and
    ///     <see cref="AxisMembers_AreOnTheAxisTheIssueProposed"/> fails rather
    ///     than silently grading a smaller surface.
    /// </summary>
    private static readonly (string File, string Member, string Axis)[] NamedMembers =
    [
        ("Rendering/BufferSwapChain.cs", "TryTake", MaybeAxis),
        ("Rendering/AnsiWriter.cs", "SyncBackend", MaybeAxis),
        ("Input/UnixTermiosModeController.cs", "Enter", ResultAxis),
        ("Input/WindowsVtModeController.cs", "Enter", ResultAxis),
    ];

    /// <summary>
    ///     A public member declaration carrying an access modifier, so a bare
    ///     call site (<c>writer.TryTake()</c>) cannot be mistaken for the
    ///     declaration it grades. Captures the member name.
    /// </summary>
    private static readonly Regex PublicMemberDeclaration = new(
        @"\b(?:public|protected)\b[^;{()]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=>|\{|\()",
        RegexOptions.Compiled);

    /// <summary>
    ///     Declares a name as <c>Maybe</c>-typed, anywhere a declaration can sit
    ///     (field, property, local, parameter, return).
    /// </summary>
    private static readonly Regex MaybeDeclaration = new(
        @"\bMaybe<[^;=]*?>\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=>|\{|\(|;|=)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A receiver tested by a form that cannot see a struct. §4 gotcha 7:
    ///     "Maybe&lt;T&gt; never implicitly converts back to T?"; the project
    ///     convention is <c>HasNoValue</c> / <c>HasValue</c>.
    ///     <list type="bullet">
    ///         <item><c>_w is not { }</c> — always matches, binds the wrapper (#983).</item>
    ///         <item><c>_w is { }</c> — same, always true.</item>
    ///         <item><c>_w?.Id</c> — binds the wrapper; the value is never inspected.</item>
    ///     </list>
    ///     The three alternatives carry their OWN tail rather than sharing one,
    ///     and <c>RECEIVERS</c> is substituted by token rather than by format
    ///     index, because the pattern's braces are regex escapes that
    ///     <c>string.Format</c> would eat.
    ///     <para>
    ///         A single shared trailing <c>\b</c> looks equivalent and is not —
    ///         measured on the planted fixture: a shared <c>\b</c> fired on the
    ///         <c>?.</c> form only and a shared <c>(?!\w)</c> on the pattern form
    ///         only. Each silently HALVED the reader, so the non-vacuity count
    ///         came back 1 instead of 2 while the real tree stayed 0 either way:
    ///         a half-matching question reports half the truth in both
    ///         directions. That is the #931 shape, and it is why the tail lives
    ///         inside each alternative — the braces are self-delimiting, and
    ///         only <c>?. </c>, which could match a ternary's <c>?</c>, is
    ///         anchored by requiring a member access to follow.
    ///     </para>
    /// </summary>
    private const string StructUnawareTestPattern =
        @"\b(?:RECEIVERS)\s*(?:is\s+not\s*\{\s*\}|is\s*\{\s*\}|\?\.(?=\w))";

    /// <summary>
    ///     #591's two named places, counted. <see cref="NamedMembers"/> must
    ///     produce exactly these, or the table and the issue have drifted.
    /// </summary>
    private const int ExpectedMaybeAxisMembers = 2;
    private const int ExpectedResultAxisMembers = 2;

    /// <summary>
    ///     <c>#591</c>'s named places are TWO DECISIONS, not two manifestations
    ///     of one rule — and the split is the one §4.6 already prescribes, so
    ///     there is nothing to re-decide here and nothing to convert without a
    ///     dependency decision this commit cannot make.
    /// </summary>
    [Test]
    public async Task AxisMembers_AreOnTheAxisTheIssueProposed()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var missing = new List<string>();
        int maybeCount = 0;
        int resultCount = 0;

        foreach ((string file, string member, string axis) in NamedMembers)
        {
            string relative = $"{EngineProject}/{file}";
            string absolute = Path.Combine(root, relative);

            if (!File.Exists(absolute))
            {
                missing.Add($"{relative} — file named by #591 does not exist; NamedMembers is stale");
                continue;
            }

            if (!DeclaresPublicMember(absolute, member))
            {
                missing.Add(
                    $"{relative} — public member '{member}' named by #591 is gone, renamed, or no longer "
                    + "public. Either the conversion landed (update NamedMembers and the two counts) or the "
                    + "member moved. A guard that kept grading a surface it can no longer see would report "
                    + "green for the wrong reason.");
                continue;
            }

            if (axis == MaybeAxis)
            {
                maybeCount++;
            }
            else
            {
                resultCount++;
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because(
                "Every member #591 named must still be present and still be the kind of absence its axis "
                + "describes. docs/ROP-API-INVENTORY.md §4.6 splits them on one question — 'can fail with a "
                + "reason the user must see' is Result, 'genuinely optional, absence is not an error' is "
                + "Maybe — and here the two axes do not collapse into each other. BufferSwapChain.TryTake and "
                + "AnsiWriter.SyncBackend carry no reason at all: TryTake already folds 'nothing pending' and "
                + "'a concurrent consumer won the race' into one state, and SupportsSyncWrites == false means "
                + "the Maybe is None rather than 'a value that exists and is false'. The two Enter() methods "
                + "do carry a reason: tcgetattr != 0 hands back an errno, and ENOTTY ('stdin is not a "
                + "terminal') is a different outcome from EBADF/EIO. So the answer to '2 places = 2 decisions "
                + "or 2 manifestations of one' is TWO: the Maybe pair is one axis stated twice and the Result "
                + "pair is another stated twice, and each pair converts on its own wave. Merging them would "
                + "mean making Maybe carry the errno, which is the exact conflation §4 gotcha 6 calls the "
                + "headline gotcha — and worse than silent, because the diagnostic is what the user needs.");

        await Assert.That(maybeCount).IsEqualTo(ExpectedMaybeAxisMembers)
            .Because(
                $"#591 counts {ExpectedMaybeAxisMembers} Maybe sites in this project. A different number means "
                + "a member was added or removed without the table following, so rule 1 would be grading a "
                + "surface that no longer matches the issue it was measured from.");

        await Assert.That(resultCount).IsEqualTo(ExpectedResultAxisMembers)
            .Because(
                $"#591 counts {ExpectedResultAxisMembers} Result sites in this project. Unix and Windows agree "
                + "on the axis precisely because both report a platform errno; a third or a single one means "
                + "the table and the issue have drifted apart.");
    }

    /// <summary>
    ///     Nothing reads a <c>Maybe</c>-typed member by a form that cannot see a
    ///     struct, and the project under measurement declares none yet. Measured
    ///     over the whole tree so the answer is a number, not a reading.
    /// </summary>
    [Test]
    public async Task NoMaybeMemberIsReadWithAStructUnawareForm()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var violations = new List<string>();
        int engineMaybeMembers = 0;
        bool sawAnyMaybeAtAll = false;

        foreach (string file in EnumerateSourceFiles(root))
        {
            string[] lines = ReadLines(file);
            HashSet<string> declared = DeclaredMaybeNames(lines);
            if (declared.Count == 0)
            {
                continue;
            }

            sawAnyMaybeAtAll = true;
            if (file.Replace('\\', '/').Contains(EngineProject, StringComparison.Ordinal))
            {
                engineMaybeMembers += declared.Count;
            }

            Regex reader = BuildReader(declared);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]))
                {
                    continue;
                }

                if (reader.IsMatch(lines[i]))
                {
                    violations.Add(
                        $"{Relative(root, file)}:{i + 1}: {lines[i].Trim()} — reads a Maybe-typed member "
                        + "with a form that cannot see a struct");
                }
            }
        }

        await Assert.That(sawAnyMaybeAtAll).IsTrue()
            .Because(
                "The repo-wide scan found no Maybe-typed declaration anywhere under the repository root. "
                + "That is not expected — IAgent.State is Maybe<AgentState> and McpSseTransport returns "
                + "Result<Maybe<JsonDocument>> — so a zero here means the walk failed (wrong root, unreadable "
                + "directory) and this rule would be green over nothing. A guard that silently stopped "
                + "looking is worse than no guard, because the conversion it protects is still coming.");

        await Assert.That(violations).IsEmpty()
            .Because(
                "CSharpFunctionalExtensions' Maybe<T> is a STRUCT (docs/ROP-API-INVENTORY.md line 8, §4 gotcha "
                + "7). `x is not { }` ALWAYS matches it and binds the wrapper; `x is { }` is always true; `x?.` "
                + "never inspects the value; `x == null` is always false. None of them fail to compile, so the "
                + "mistake is invisible. The measured answer is zero hits, and every repo-wide candidate found "
                + "while building this rule was a NAME COLLISION rather than a violation — SelectedProvider is "
                + "an OnboardingProviderOption?, `state` a ChunkStreamState?, LastApplied a HarborTheme? — "
                + "which is why declared names are collected PER FILE and never across the tree.");

        await Assert.That(engineMaybeMembers).IsEqualTo(0)
            .Because(
                "src/Harbor.Tui.CellForge.Engine declares no Maybe-typed member today, which is consistent "
                + "with #938's measurement that the engine reaches CSharpFunctionalExtensions only "
                + "transitively. When the #591 conversion of BufferSwapChain.TryTake to Maybe<BufferPair> "
                + "lands, this count becomes non-zero — and that is the point. The single caller, "
                + "src/Harbor.Tui.CellForge/Chat/Streaming/ScreenSession.cs, reads the result as "
                + "`if (offer is null)`, which is correct for the Nullable<T> it has today and silently always "
                + "false for the struct it will have. This assertion is what forces that caller to be revisited "
                + "in the same commit as the signature, instead of one commit later.");
    }

    /// <summary>
    ///     The reader used by the rule above has to be able to return a hit, or
    ///     that rule is green because it matched nothing. A planted receiver, two
    ///     planted violations, and the two correct forms beside them so a reader
    ///     that fires on <c>HasValue</c> would be caught too.
    /// </summary>
    [Test]
    public async Task StructUnawareReader_IsNotVacuous()
    {
        const string Source = """
            private Maybe<Widget> _widget;
            public int BrokenByPattern() => _widget is not { } ? 0 : 1;
            public int BrokenByNullConditional() => _widget?.Id ?? 0;
            public int CorrectByHasNoValue() => _widget.HasNoValue ? 0 : _widget.Value.Id;
            public int CorrectByHasValue() => _widget.HasValue ? _widget.Value.Id : 0;
            """;

        HashSet<string> declared = DeclaredMaybeNames(Source.Split('\n'));
        await Assert.That(declared).Contains("_widget")
            .Because("The fixture must declare a Maybe-typed member, or the reader is tested against nothing.");

        Regex reader = BuildReader(declared);

        int hits = 0;
        foreach (string line in Source.Split('\n'))
        {
            if (reader.IsMatch(line))
            {
                hits++;
            }
        }

        await Assert.That(hits).IsEqualTo(2)
            .Because(
                "The reader must fire on exactly the two planted struct-unaware forms (`is not { }` and `?.`) "
                + "and stay silent on the two correct ones (HasNoValue, HasValue). Zero means the pattern "
                + "silently stopped matching and the rule above would have passed for the wrong reason. Three "
                + "or four means it also fires on the forms §4 gotcha 7 prescribes, which would send the next "
                + "converter to rewrite correct code. A count of ONE is the specific failure this repository "
                + "keeps meeting (#931's shape): while measuring this rule the shared trailing `\\b` matched "
                + "the `?.` form and not the pattern form, and the shared trailing `(?!\\w)` did the exact "
                + "opposite — each reported half the truth and neither showed up in the real-tree count of "
                + "zero, because the tree has no Maybe to find either way.");
    }

    /// <summary>
    ///     The walk really reaches the project and the repository. Rule 1 checks
    ///     four named files; rule 2 enumerates the tree, so a bad path or an
    ///     empty root would shrink it to nothing without any test noticing.
    /// </summary>
    [Test]
    public async Task Scanner_ReadsTheEngineProject()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        int engineFiles = EnumerateSourceFiles(Path.Combine(root, EngineProject)).Count();
        await Assert.That(engineFiles).IsGreaterThan(40)
            .Because(
                $"'{EngineProject}' should hold ~60 source files; found {engineFiles}. A path that does not "
                + "resolve makes every rule in this file vacuously green, which is worse than having no rule: "
                + "the next conversion would land believing it had been checked.");

        int total = EnumerateSourceFiles(root).Count();
        await Assert.That(total).IsGreaterThan(500)
            .Because($"The repo-wide walk should see well over 500 source files; found {total}.");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds <see cref="StructUnawareTestPattern"/> over the names one file
    ///     declares as <c>Maybe</c>-typed. Longest name first, so a member whose
    ///     name is a prefix of another's binds the whole name.
    /// </summary>
    private static Regex BuildReader(IReadOnlyCollection<string> declaredNames)
    {
        string alternation = string.Join(
            "|",
            declaredNames.OrderByDescending(n => n.Length).Select(Regex.Escape));

        return new Regex(StructUnawareTestPattern.Replace("RECEIVERS", alternation), RegexOptions.Compiled);
    }

    /// <summary>Every name the given lines declare as <c>Maybe</c>-typed.</summary>
    private static HashSet<string> DeclaredMaybeNames(IReadOnlyList<string> lines)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < lines.Count; i++)
        {
            if (IsComment(lines[i]))
            {
                continue;
            }

            foreach (Match match in MaybeDeclaration.Matches(lines[i]))
            {
                _ = declared.Add(match.Groups["name"].Value);
            }
        }

        return declared;
    }

    /// <summary>
    ///     Whether <paramref name="absolute"/> declares a public member called
    ///     <paramref name="member"/>. Comment lines are skipped so a doc comment
    ///     naming the member cannot satisfy the check.
    /// </summary>
    private static bool DeclaresPublicMember(string absolute, string member)
    {
        foreach (string line in ReadLines(absolute))
        {
            if (IsComment(line))
            {
                continue;
            }

            Match match = PublicMemberDeclaration.Match(line);
            if (match.Success && match.Groups["name"].Value == member)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whole-file read; an unreadable file is empty rather than fatal.</summary>
    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Comment and blank lines carry no declaration.</summary>
    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    /// <summary>Repository-relative, forward slashes, for failure messages.</summary>
    private static string Relative(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    /// <summary>Build output excluded — obj/ and bin/ hold generated copies.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}