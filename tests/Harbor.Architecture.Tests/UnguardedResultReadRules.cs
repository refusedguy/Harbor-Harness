// UnguardedResultReadRules.cs — the guard for issue #602.
//
// WHAT #602 WAS
// -------------
// `IConfigStore.LoadAsync` really does fail: `JsonConfigStore.LoadCore` ends in
// `Result.Try(..., ResultErrors.Message)` and returns
// `Failure("config.json is corrupt: …")` on a `JsonException`. A hand-edited
// config.json with a trailing comma is the ordinary trigger. Two call sites in
// `ReplRunner` then read `(await _configStore.LoadAsync()).Value` — and
// `Result<T>.Value` on a failure THROWS `ResultFailureException(Error)`, it does
// not yield a default. So `harbor ask` died with an unhandled exception whose
// type is neither `IOException` nor `JsonException`, and the composed diagnostic
// ("config.json is corrupt: …") was thrown away.
//
// WHY A NEW FILE, GIVEN CFE0001 EXISTS
// ------------------------------------
// `CSharpFunctionalExtensions.Analyzers` CFE0001 is adopted repo-wide
// (docs/ROP-API-INVENTORY.md §5) and it did NOT catch either site. That is not
// noise suppression, it is a hole in the rule, and the hole is structural:
//
//   CFE0001 registers on `SimpleMemberAccessExpression` and resolves the
//   RECEIVER to an `IPropertySymbol` on `CSharpFunctionalExtensions.Result`.
//
// For `(await store.LoadAsync()).Value` the receiver is a parenthesized
// `AwaitExpression`. There is no symbol to resolve, so the rule skips the node
// entirely. Same for `store.LoadAsync().GetAwaiter().GetResult().Value`, where
// the receiver is an invocation. The guard is therefore blind precisely to the
// SHAPE that reads a Result across an await boundary — which is the shape every
// async call site in this repo uses, and therefore the shape most likely to be
// written next.
//
// So: not "an unguarded .Value appeared somewhere", but "an unguarded .Value
// appeared in the one place the existing backstop cannot see". That is what
// these three rules cover, and nothing wider — a general "no bare .Value" ban
// would collide with the 29 documented exemptions in CfeValueBaselineTests and
// be turned off within a week.
//
//   RULE 1  `(await …).Value`               — the CFE0001 blind spot (#602).
//   RULE 2  `…GetResult().Value`            — the same blind spot, sync spelling.
//   RULE 3  `GetAllAgents()[0]`              — indexes a registry snapshot. This is
//                                             NOT a Result site, so no analyzer can
//                                             ever see it: `ArgumentOutOfRangeException`
//                                             on an empty registry, from every entry
//                                             point. Same PR (#602), same reachability.
//
// SCOPE: `src/` and `apps/` only — shipped code, where a thrown
// `ResultFailureException` reaches a user. `tests/` is excluded on purpose: there
// a thrown exception IS the failure signal (which is exactly why
// Directory.Build.props suppresses CFE0001 under `'tests'`), and a rule that
// flagged the test idiom would be deleted rather than obeyed.
// `contrib/` is unmaintained and not compiled by CI, so scanning it would
// create permanent noise nobody can fix.
//
// NON-VACUITY — the part that makes this worth having
// --------------------------------------------------
// A source scan that matches nothing is indistinguishable from a source scan that
// is broken, and a broken guard is worse than no guard because it is believed.
// Four tests below close that, mirroring TuiReadLineContractRules:
//   * `Scan_FindsTheShippedTree`       — discovery returns a non-trivial set.
//   * `Matcher_*_FlagsTheKnownBad*`    — each pattern fires on a synthetic #602
//                                        snippet. Three positive controls.
//   * `Matcher_*_AcceptsTheFixed*`     — each pattern stays quiet on the fixed
//                                        spelling, so it is discriminating and
//                                        not a noise machine.
//   * `AllowList_RowsAreAllStillReal` / `AllowList_CountIsPinned` — the single
//     documented exception cannot rot into a blanket permission, mirroring
//     `CfeValueBaselineTests.Baseline_ExemptionsAreAllStillReal`.
//
// Raw string literals for the patterns, deliberately (same reason as
// TuiReadLineContractRules): a verbatim @"…" forces every double quote in a regex
// to be doubled, and the pattern IS the specification.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One accepted exception to a rule in <see cref="UnguardedResultReadRules" />.
/// </summary>
/// <param name="File">Repo-relative path.</param>
/// <param name="Why">Why this site is knowingly left unguarded.</param>
internal sealed record ResultReadExemption(string File, string Why);

/// <summary>
///     Enforces that shipped code never reads a <c>Result&lt;T&gt;</c> across an
///     await/GetResult boundary, and never indexes a registry snapshot. See the
///     file header for the incident and the non-vacuity argument.
/// </summary>
public sealed class UnguardedResultReadRules
{
    /// <summary>Repo-relative trees that ship. <c>tests/</c> and <c>contrib/</c> are excluded by design.</summary>
    private static readonly string[] ShippedTrees = ["src", "apps"];

    // ── Rule 1: `.Value` off an await expression ─────────────────────────────
    //
    // Greedy `[^\n]*` then backtrack: the match ends at the LAST `)` on the line
    // that is followed by `.Value`, which is the closing paren of the awaited
    // call in every spelling that matters —
    //   `(await store.LoadAsync().ConfigureAwait(false)).Value` on the RHS of an
    //   assignment, and `(await store.CreateAsync("/tmp", "code", "p", "m")).Value`
    //   in a `var` initialiser.
    private static readonly Regex ValueOffAwait = new(
        """
        \(\s*await\b[^\n]*\)\s*\.\s*Value\b
        """,
        RegexOptions.CultureInvariant);

    // ── Rule 2: `.Value` off a blocked GetResult ─────────────────────────────
    //
    // The synchronous spelling of the same blind spot: the receiver of `.Value`
    // is an invocation, so CFE0001 resolves no Result symbol and skips the node.
    private static readonly Regex ValueOffGetResult = new(
        """
        GetResult\(\s*\)\s*\.\s*Value\b
        """,
        RegexOptions.CultureInvariant);

    // ── Rule 3: `[0]` into a registry snapshot ───────────────────────────────
    //
    // Not a Result site at all, so no analyzer can ever see it. `GetAllAgents()`
    // returns `IReadOnlyList<AgentDefinition>`, and `[0]` on an empty one throws
    // `ArgumentOutOfRangeException` — reachable on a fresh install, or after a
    // failed plugin-load pass empties the registry.
    private static readonly Regex RegistryZeroIndex = new(
        """
        GetAllAgents\(\s*\)\s*\[\s*0\s*\]
        """,
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Every accepted exception. A new row is a claim that a site is knowingly
    ///     broken; <see cref="AllowList_RowsAreAllStillReal" /> verifies the claim
    ///     and <see cref="AllowList_CountIsPinned" /> makes growing it a deliberate
    ///     edit.
    /// </summary>
    private static readonly ResultReadExemption[] AllowList =
    [
        new("apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs",
            "REAL DEFECT, deliberately out of scope for #602 and already baselined by CFE0001 "
            + "(docs/ROP-API-INVENTORY.md §5.3, CfeValueBaselineTests). Fixing it needs a product "
            + "decision about what the settings screen shows when the store fails — CommonConfig "
            + "has no public default instance — not a mechanical change. Listed here so the rule "
            + "is honest about what it is not covering."),
    ];

    // ── Rule 1 + 2: `.Value` across an await / GetResult boundary ────────────

    [Test]
    public async Task ShippedCode_ReadsNoResultValueAcrossAnAwaitOrGetResultBoundary()
    {
        IReadOnlyList<string> files = EnumerateShippedFiles();

        await Assert.That(files.Count).IsGreaterThan(200)
            .Because(
                "Non-vacuity for the scan: src/ + apps/ is several hundred .cs files. A count this "
                + "low means the tree walk or the extension filter stopped matching, and every "
                + "assertion below would be reporting 'clean' about nothing.");

        var violations = new List<string>();
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            violations.AddRange(Matches(file, source, ValueOffAwait));
            violations.AddRange(Matches(file, source, ValueOffGetResult));
        }

        violations.RemoveAll(v => IsAllowlisted(v));

        await Assert.That(violations).IsEmpty()
            .Because(
                "`Result<T>.Value` on a failure THROWS `ResultFailureException` — it does not "
                + "return a default. Read through `await`, the receiver is an AwaitExpression, so "
                + "CFE0001 resolves no Result symbol and skips the node: this is the exact hole "
                + "that let a corrupt config.json kill `harbor ask` with an unhandled exception "
                + "(#602). Guard it, bind it, or apply an explicit default — but read it through a "
                + "local you have checked.");
    }

    // ── Rule 3: no `[0]` into a registry snapshot ────────────────────────────

    [Test]
    public async Task ShippedCode_NeverIndexesZeroIntoTheAgentRegistry()
    {
        IReadOnlyList<string> files = EnumerateShippedFiles();

        var violations = new List<string>();
        foreach (string file in files)
        {
            violations.AddRange(Matches(file, File.ReadAllText(file), RegistryZeroIndex));
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "`GetAllAgents()` returns a list, and `[0]` on an empty one throws "
                + "ArgumentOutOfRangeException. An empty registry is the state of a fresh install "
                + "and of a failed plugin-load pass, and this spelling sat on every CLI entry "
                + "point (#602). Resolve the agent through Maybe<AgentDefinition> and treat None as "
                + "a reported failure.");
    }

    // ── Non-vacuity of the discovery step ────────────────────────────────────

    [Test]
    public async Task Scan_FindsTheShippedTree()
    {
        IReadOnlyList<string> files = EnumerateShippedFiles();

        await Assert.That(files).IsNotEmpty()
            .Because("an empty discovery result makes all three rules vacuously satisfied");

        // Both trees must be represented, or a typo in one entry silently halves
        // the guard's reach without any test going red.
        await Assert.That(files.Any(f => f.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.Ordinal))).IsTrue()
            .Because("the src/ tree must be scanned");
        await Assert.That(files.Any(f => f.Contains($"{Path.DirectorySeparatorChar}apps{Path.DirectorySeparatorChar}", StringComparison.Ordinal))).IsTrue()
            .Because("the apps/ tree must be scanned");

        // Build output must never be counted, or a stale obj/ copy of a deleted
        // file keeps a violation alive forever.
        await Assert.That(files.Any(f => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                     || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))).IsFalse()
            .Because("bin/ and obj/ are not source");
    }

    // ── Non-vacuity of the matchers themselves: three positive controls ──────

    [Test]
    public async Task Matcher_AwaitValue_FlagsTheKnownBadSpelling()
    {
        // Verbatim from ReplRunner.RunAskAsync as it shipped.
        const string Bad = """
            var config = (await _configStore.LoadAsync().ConfigureAwait(false)).Value;
            """;

        await Assert.That(ValueOffAwait.IsMatch(Bad)).IsTrue()
            .Because(
                "This is the #602 crash, character for character. A miss means rule 1 is inert and "
                + "the next corrupt-config crash ships straight through a green build.");
    }

    [Test]
    public async Task Matcher_GetResultValue_FlagsTheKnownBadSpelling()
    {
        const string Bad = """
            _common = _commonStore.LoadAsync().GetAwaiter().GetResult().Value;
            """;

        await Assert.That(ValueOffGetResult.IsMatch(Bad)).IsTrue()
            .Because("the sync spelling of the same blind spot must be caught by the same rule");
    }

    [Test]
    public async Task Matcher_RegistryZeroIndex_FlagsTheKnownBadSpelling()
    {
        const string Bad = """
            .Match<AgentDefinition, AgentDefinition>(matched => matched, () => _agentRegistry.GetAllAgents()[0]);
            """;

        await Assert.That(RegistryZeroIndex.IsMatch(Bad)).IsTrue()
            .Because("the empty-registry crash is the second half of #602 and no analyzer covers it");
    }

    // ── Non-vacuity of the matchers: the fixed spellings must pass ───────────

    [Test]
    public async Task Matcher_IgnoresTheBadSpellingWhenItOnlyAppearsInCommentary()
    {
        // This is not hypothetical: writing the fix's "this used to be …" comment
        // tripped the rule on its own fix, because the prose quotes the crash.
        // A guard that flags the explanation of a bug is a guard whose allow-list
        // starts growing on day one, and then it is decoration.
        const string Commented = """
            // #602: this was `(await store.LoadAsync()).Value`, which THROWS.
            // The empty-registry fallback was `GetAllAgents()[0]`, and it threw too.
            /* block form: (await store.LoadAsync().ConfigureAwait(false)).Value */
            /// doc form: GetAllAgents()[0]
            var loaded = await store.LoadAsync().ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                return 1;
            }

            HarborConfig config = loaded.IsSuccess ? loaded.Value : HarborConfig.Default;
            """;

        string[] code = StripComments(Commented).Split('\n');

        await Assert.That(ValueOffAwait.IsMatch(string.Join('\n', code))).IsFalse()
            .Because("rule 1 is about code; the comment above is the fix's own changelog");
        await Assert.That(RegistryZeroIndex.IsMatch(string.Join('\n', code))).IsFalse()
            .Because("rule 3 likewise — a doc comment naming the old expression is not a call to it");
    }

    [Test]
    public async Task Matcher_StillFlagsRealCodeThatCarriesATrailingComment()
    {
        // The mirror of the test above, and the one that stops the stripper from
        // being a loophole: blanking to end-of-line must not blank the CODE that
        // precedes the comment.
        const string Trailing = """
            var config = (await _configStore.LoadAsync().ConfigureAwait(false)).Value; // the #602 crash
            """;

        string[] code = StripComments(Trailing).Split('\n');

        await Assert.That(ValueOffAwait.IsMatch(string.Join('\n', code))).IsTrue()
            .Because("a trailing note must not launder a real violation");
    }

    [Test]
    public async Task Matcher_SurvivesAUrlInAStringLiteral()
    {
        // `//` inside a string is not a comment, and the two appear on the same
        // line here precisely because that is the only arrangement that
        // discriminates. Blank to the first `//` and this violation disappears —
        // a guard that goes blind next to a log message is one somebody will
        // switch off.
        const string WithUrl = """
            _logger.LogError("corrupt at https://example.invalid/config.json"); var n = (await s.LoadAsync()).Value;
            """;

        string[] code = StripComments(WithUrl).Split('\n');

        await Assert.That(ValueOffAwait.IsMatch(string.Join('\n', code))).IsTrue()
            .Because("the string literal must be tracked, not mistaken for the start of a comment");
    }

    [Test]
    public async Task Matcher_AwaitValue_AcceptsTheFixedSpelling()
    {
        // The shape the #602 fix introduces: the load lands in a local, the
        // failure is handled, and the value is read inside an IsSuccess ternary.
        const string Good = """
            var loaded = await _configStore.LoadAsync().ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                Console.Error.WriteLine($"Failed: {loaded.Error}");
                return 1;
            }

            HarborConfig config = loaded.IsSuccess ? loaded.Value : HarborConfig.Default;
            """;

        await Assert.That(ValueOffAwait.IsMatch(Good)).IsFalse()
            .Because(
                "If the fixed spelling is still flagged the rule cannot be adopted. Note what it "
                + "does NOT accept: re-introducing `(await …).Value` anywhere in the method, "
                + "guarded or not — the point is to make the result a named local that must be "
                + "checked, not to add a smarter way to skip the check.");
    }

    [Test]
    public async Task Matcher_GetResultValue_AcceptsTheFixedSpelling()
    {
        const string Good = """
            Result<HarborConfig> loaded = store.LoadAsync().GetAwaiter().GetResult();
            if (loaded.IsFailure)
            {
                return Result.Failure<HarborConfig>(loaded.Error);
            }

            return loaded;
            """;

        await Assert.That(ValueOffGetResult.IsMatch(Good)).IsFalse()
            .Because("the same fix shape, in the sync spelling, must pass");
    }

    [Test]
    public async Task Matcher_RegistryZeroIndex_AcceptsTheFixedSpelling()
    {
        const string Good = """
            var agents = _agentRegistry.GetAllAgents();
            Maybe<AgentDefinition> agent = agents
                .TryFirst(a => a.Name.Value == config.Agent)
                .Or(agents.TryFirst());
            """;

        await Assert.That(RegistryZeroIndex.IsMatch(Good)).IsFalse()
            .Because("absence is Maybe<T>, not an index that throws");
    }

    // ── The allow-list cannot rot ────────────────────────────────────────────

    [Test]
    public async Task AllowList_RowsAreAllStillReal()
    {
        IReadOnlyList<string> files = EnumerateShippedFiles();
        var failures = new List<string>();

        foreach (ResultReadExemption row in AllowList)
        {
            if (RepoPaths.RepoRoot is null)
            {
                throw new InvalidOperationException("[#602] repository root not found; nothing to check.");
            }

            string path = Path.Combine(RepoPaths.RepoRoot, row.File);
            if (!File.Exists(path))
            {
                failures.Add($"{row.File}: allow-listed file no longer exists — delete the row");
                continue;
            }

            string source = File.ReadAllText(path);
            bool stillMatches = ValueOffGetResult.IsMatch(source)
                                || ValueOffAwait.IsMatch(source)
                                || RegistryZeroIndex.IsMatch(source);

            if (!stillMatches)
            {
                failures.Add(
                    $"{row.File}: the pattern this row excuses is gone — the site was fixed, so the "
                    + "row grandfathers nothing and hides the next real violation. Delete it and "
                    + "lower the pinned count.");
            }

            if (string.IsNullOrWhiteSpace(row.Why))
            {
                failures.Add($"{row.File}: row states no reason; a row without a reason is one nobody deletes");
            }
        }

        // A row for a file the scan never visits is dead weight dressed as an
        // exception, and it is the classic way an allow-list becomes a blanket.
        foreach (ResultReadExemption row in AllowList)
        {
            if (!files.Any(f => f.EndsWith(row.File.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal)))
            {
                failures.Add($"{row.File}: allow-listed path is not in the scanned set — the row is unreachable");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(
                "An allow-list row that no longer matches reality is a lie: it excuses nothing today "
                + "and hides the next violation tomorrow." + Environment.NewLine
                + string.Join(Environment.NewLine, failures));
    }

    [Test]
    public async Task AllowList_CountIsPinned()
    {
        await Assert.That(AllowList.Length).IsEqualTo(1)
            .Because(
                "Pinned at exactly one: SettingsViewModel's ctor, which docs/ROP-API-INVENTORY.md §5.3 "
                + "already carries as a known real defect awaiting a product decision. It may only "
                + "shrink. A second row is a second site this rule is choosing not to catch, and it "
                + "must be justified in review rather than added to make a build green.");
    }

    // ── Discovery + match helpers ────────────────────────────────────────────

    /// <summary>Every shipped <c>*.cs</c> file, build output excluded, in a stable order.</summary>
    private static IReadOnlyList<string> EnumerateShippedFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        List<string> found = [];
        foreach (string tree in ShippedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(file);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Repo-relative <c>path:line</c> for each match, for a failure a human can act on.</summary>
    private static IEnumerable<string> Matches(string absolutePath, string source, Regex pattern)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        string relative = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
        string[] raw = source.Split('\n');
        // Match against the COMMENT-STRIPPED projection, report the original text.
        // A guard that flags the prose explaining the bug is a guard that gets
        // deleted: this file's own header quotes the crash, and so does the fix's
        // "this used to be …" comment. A rule which cannot tell code from
        // commentary trains reviewers to reach for the allow-list.
        string[] code = StripComments(source).Split('\n');

        for (int i = 0; i < code.Length && i < raw.Length; i++)
        {
            if (pattern.IsMatch(code[i]))
            {
                yield return $"{relative}:{i + 1}: {raw[i].Trim()}";
            }
        }
    }

    /// <summary>
    ///     Blanks comment content while preserving every character offset and every
    ///     newline, so line numbers in failure messages stay true.
    /// </summary>
    /// <remarks>
    ///     A deliberately small scanner, not a C# parser: it tracks
    ///     <c>//</c>, <c>/* */</c>, regular string literals and char literals, which
    ///     is what it needs to survive the two things that actually appear next to a
    ///     forbidden read — a trailing note about it, and a URL in a log message.
    ///     Known limits, stated rather than hidden: a verbatim or interpolated
    ///     string with an unbalanced quote inside an interpolation hole would
    ///     desynchronise the rest of that line. Every limit here can only cause a
    ///     MISS, never a false accusation, and the three positive controls below
    ///     are what keep the scanner honest.
    /// </remarks>
    private static string StripComments(string source)
    {
        var blanked = new System.Text.StringBuilder(source.Length);
        bool inBlockComment = false;
        bool inString = false;
        bool inChar = false;
        bool escaped = false;

        // An explicit `while` rather than a `for`: the `//` arm has to consume
        // the rest of the line itself, and doing that by nudging a `for` loop's
        // induction variable (i--; then i++ on the next turn) is the shape
        // S127 warns about, for good reason — it is unreadable and it is exactly
        // where an off-by-one hides.
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inBlockComment)
            {
                // Content is blanked, not preserved: a block comment that quotes
                // the forbidden read is still a block comment.
                blanked.Append(c == '\n' ? '\n' : ' ');
                if (c == '*' && next == '/')
                {
                    blanked.Append(' ');
                    i += 2;
                    inBlockComment = false;
                }
                else
                {
                    i += 1;
                }

                continue;
            }

            if (inString)
            {
                blanked.Append(c);
                if (c == '\n')
                {
                    inString = false; // unterminated — recover rather than swallow the file
                }
                else if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                i += 1;
                continue;
            }

            if (inChar)
            {
                blanked.Append(c);
                if (c == '\n')
                {
                    inChar = false;
                }
                else if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '\'')
                {
                    inChar = false;
                }

                i += 1;
                continue;
            }

            if (c == '/' && next == '/')
            {
                // Blank to end of line, keeping the newline itself.
                while (i < source.Length && source[i] != '\n')
                {
                    blanked.Append(' ');
                    i += 1;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                blanked.Append("  ");
                i += 2;
                inBlockComment = true;
                continue;
            }

            blanked.Append(c);
            if (c == '"')
            {
                inString = true;
            }
            else if (c == '\'')
            {
                inChar = true;
            }

            i += 1;
        }

        return blanked.ToString();
    }

    /// <summary>Whether a violation line names an allow-listed file.</summary>
    private static bool IsAllowlisted(string violation)
    {
        int colon = violation.IndexOf(':', StringComparison.Ordinal);
        string file = colon < 0 ? violation : violation[..colon];

        return AllowList.Any(row => string.Equals(row.File, file, StringComparison.Ordinal));
    }
}
