// ModelRefSingleParserTests.cs — the guard for issue #678.
//
// WHY THIS FILE EXISTS
// --------------------
// `ModelRef.TryParse` (src/Harbor.Abstractions.Contracts/Models/Identifiers/
// Identifiers.cs) is, in the words of the declaration it grew out of, "the only
// function in the repo written to read a `provider/model` reference"
// (ConfigSections.cs:27). It is the only one that gets the two things right that
// check-doc-cites: record-drift ConfigSections.cs:27 now="public const string FallbackAgent = AgentName.Fallback;" [#947: written over `/// <c>kilo-auto</c>). <c>ModelRef.TryPa`; repair deferred to the owner's symbol-rename decision] -->
// a hand-rolled cut does not:
//
//   * it splits with COUNT 2, so a multi-segment model id
//     ("tencent/hy3:free", "kilo-auto/free") keeps every segment — the
//     unlimited slash split that #599 removed returned only "hy3:free";
//   * it normalizes the provider half to lower case and VALIDATES it, so
//     "KiloCode/x" and "bad provider/x" cannot reach a Session unnormalized.
//
// The UI layer re-implemented the same read twice, and not even consistently:
//
//   * SessionFactory.ResolveProviderModelFromConfigAsync concatenated the
//     provider with a slash into a local prefix and stripped it with
//     StartsWith — then handed the RAW provider string on, unnormalized, straight
//     into the Session the app runs on.
//   * OnboardingFlow.TryCommitModel decided the same question by asking whether
//     the typed text contained a slash — "is this text already a reference?"
//     answered by counting separators, which is the read, not a validation.
//
// Neither is duplication for its own sake: both are UI code re-deriving an
// identity rule that belongs to the value object. That is the same defect
// `DefaultModelSingleSourceTests` guards for the default model id (#599), one
// level up: a spelling that is allowed to exist in more than one place is a
// spelling that will disagree.
//
// THE RULE
// --------
// 1. The two UI sites that turn a provider + a model-ish string into an outcome
//    (`OwnedFiles`) must contain NO ad-hoc cut at all, and MUST call into
//    `ModelRef`. The positive half matters as much as the negative one: a file
//    with no cut but no delegation has simply moved the rule somewhere less
//    visible, and only the positive assertion catches that.
// 2. `TryParse` itself must keep cutting — otherwise rule 1 is satisfied by
//    deleting the function.
// 3. Repo-wide, the ad-hoc cut may appear ONLY in `ContractFiles` and in
//    `KnownAdHocCutSites`, which is an inventory with a stated reason per entry.
//    Both directions are asserted, so the inventory is a ratchet: a NEW site
//    fails, and a FIXED site must be removed from the list (a stale entry would
//    silently re-widen the rule the next time someone re-adds a cut there).
//
// WHY A TEXT SCAN AND NOT A SYMBOL BAN
// ------------------------------------
// `BannedSymbols.txt` cannot express this: `String.Split` and `String.IndexOf`
// are far too broad to ban, and the defect is a call site's CORRECTNESS, not an
// API's existence. Same trade-off, same conclusion as
// DefaultModelSingleSourceTests and UnguardedResultReadRules.
//
// THE MATCHER IS TEXTUAL, SO IT HAS FALSE POSITIVES — AND THEY ARE INVENTORIED
// ---------------------------------------------------------------------------
// A glob pattern, an OSC payload, a diff header, a skill name and a filesystem
// path are all slash-separated and none of them is a model reference. They are
// listed in `KnownAdHocCutSites` with a reason that says so, which is what keeps
// the inventory honest instead of aspirational. The list is a decision log.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan skips LINES whose first non-whitespace characters are `//`, `/*` or
// `*`. A `provider/model` string quoted inside a comment is prose, not a cut.
// That is a line-level heuristic, not a C# parser: a cut written across a line
// break, or inside a multi-line block comment, can be missed. The limitation is
// bounded by construction — rule 1 is enforced separately and positively, so the
// two owned files cannot lose the rule by hiding the call.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #678: reading a <c>provider/model</c> reference belongs to
///     <c>ModelRef</c> and to nothing else. The UI sites that used to re-derive
///     it must delegate.
/// </summary>
public sealed class ModelRefSingleParserTests
{
    /// <summary>Product trees scanned for an ad-hoc cut.</summary>
    private static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>
    ///     The one file allowed to cut a reference apart. Named, not globbed,
    ///     because the rule is about this specific function.
    /// </summary>
    private const string TryParseFileRelativePath =
        "src/Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs";

    /// <summary>
    ///     The UI sites this issue moves onto the contract. They are held to the
    ///     strict rule — no cut, and an actual <c>ModelRef</c> call — and they are
    ///     excluded from <see cref="KnownAdHocCutSites" /> so the repo-wide
    ///     ratchet below does not have to carry a self-referential entry that
    ///     the very next commit deletes.
    /// </summary>
    /// <remarks>
    ///     #453: `SessionFactory` is no longer one of these, and the move is the
    ///     point rather than a relaxation. It used to qualify the config's
    ///     provider/model itself, because the seam handed it two raw strings. The
    ///     seam now carries <c>ModelRef</c>, so the qualification happens once, at
    ///     the producer (`CommonConfigReaderAdapter`) — and a file that no longer
    ///     touches the model-reference rule should be released from the rule rather
    ///     than kept on it with a call added to satisfy it. The delegation moved
    ///     DOWN the seam to the one place that reads the config.
    /// </remarks>
    private static readonly string[] OwnedFiles =
    [
        // #453: the producer of the shared-config reference, which is where
        // ModelRef.Qualify is called now that the port carries a ModelRef.
        "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs",
        "src/Harbor.Tui.CellForge/Chat/Onboarding/OnboardingFlow.cs",
    ];

    /// <summary>
    ///     Every file outside <see cref="OwnedFiles" /> that may contain an ad-hoc
    ///     cut, each with the reason it is not a violation — either because it is
    ///     the same read done by hand (real debt, still to be paid), or because
    ///     the matcher is textual and this is a slash in something else.
    /// </summary>
    private static readonly Dictionary<string, string> KnownAdHocCutSites = new(StringComparer.Ordinal)
    {
        ["apps/Harbor.App.Cli/Commands/DemoCommand.cs"] =
            "REAL DEBT, out of #678's scope. Splits config.EffectiveModel (a 'provider/model' "
            + "string) by hand to label the demo banner. Same answer ModelRef.TryParse gives.",
        ["apps/Harbor.App.Cli/Commands/ModelCommand.cs"] =
            "REAL DEBT, out of #678's scope. Two sites that qualify typed input by hand — the "
            + "same 'prefix unless the text already carries one' rule OnboardingFlow now "
            + "resolves through the contract.",
        ["apps/Harbor.App.Cli/Repl/ReplRunner.cs"] =
            "REAL DEBT, out of #678's scope. Three sites split the effective model for the "
            + "status line; each is a count-2 split, so none of them truncates.",
        ["src/Harbor.Application/Onboarding/OnboardingWizard.cs"] =
            "REAL DEBT, out of #678's scope. The console wizard's 'prefix unless present' rule, "
            + "written twice in one file. It is the reference implementation OnboardingFlow "
            + "mirrors, which is why the two answers differ from ModelRef.Qualify's — see the "
            + "note on that method before 'unifying' them.",
        ["src/Harbor.Application/Permissions/PermissionPathNormalizer.cs"] =
            "NOT A REFERENCE. A filesystem path prefix. Listed because the matcher is textual, "
            + "not semantic.",
        ["src/Harbor.Application/Skills/SkillUpdater.cs"] =
            "NOT A REFERENCE. A skill NAME may be slash-namespaced.",
        ["src/Harbor.DesignSystem/DesignSystem/TerminalBackgroundProbe.cs"] =
            "NOT A REFERENCE. An OSC terminal payload is slash-separated by the protocol.",
        ["src/Harbor.Hosting/Modules/ToolsCatalog.cs"] =
            "REAL DEBT, out of #678's scope. The composition root resolves (provider, model) "
            + "for the agent registry with the same hand-rolled prefix strip SessionFactory had — "
            + "the same answer ModelRef.Qualify gives.",
        ["src/Harbor.Tools.Builtin/Tools/Glob/GlobTool.cs"] =
            "NOT A REFERENCE. A glob pattern is slash-separated by definition.",
    };

    /// <summary>
    ///     An ad-hoc read of a slash-separated reference, in any of the three
    ///     spellings this repo actually uses: cutting a segment
    ///     (<c>Split('/')</c> / <c>IndexOf('/')</c>), probing for one
    ///     (<c>Contains('/')</c>), or building a <c>"provider/"</c> prefix and
    ///     stripping it with <c>StartsWith</c>.
    /// </summary>
    private static readonly Regex AdHocCut = new(
        @"\.(?:Split|IndexOf|Contains)\s*\(\s*(?:'/'|""/"")|\+\s*""/""\s*;|\.StartsWith\s*\(\s*prefix\b",
        RegexOptions.Compiled);

    /// <summary>An actual delegation to the contract: a call, not a mention in prose.</summary>
    private static readonly Regex DelegatesToContract = new(
        @"\bModelRef\.(?:TryParse|Qualify)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     A file, a 1-based line number, and the offending line — the tuple every
    ///     failure message is built from.
    /// </summary>
    private sealed record CutSite(string RelativePath, int Line, string Text);

    /// <summary>
    ///     Rule 1, negative half: the UI sites that used to re-derive the read
    ///     contain no ad-hoc cut of their own any more.
    /// </summary>
    [Test]
    public async Task OwnedFiles_ContainNoAdHocCut()
    {
        string root = RequireRepoRoot();
        List<CutSite> cuts = [.. OwnedFiles.SelectMany(f => FindCutSites(root, f))];

        await Assert.That(cuts.Count).IsEqualTo(0)
            .Because(
                "these files used to re-derive the reading of a 'provider/model' reference: "
                + Describe(cuts)
                + ". ModelRef.TryParse / ModelRef.Qualify is the only function in the repo written "
                + "to read one — it splits with count 2 (multi-segment model ids survive), "
                + "normalizes and validates the provider half, and cannot leave a raw string to "
                + "reach a Session. See issue #678.");
    }

    /// <summary>
    ///     Rule 1, positive half. A file with no cut but no delegation has not
    ///     fixed anything — it has moved the rule somewhere less visible, and only
    ///     this assertion notices.
    /// </summary>
    [Test]
    public async Task OwnedFiles_DelegateToTheContract()
    {
        string root = RequireRepoRoot();
        var silent = new List<string>();

        foreach (string file in OwnedFiles)
        {
            if (CountDelegations(root, file) == 0)
            {
                silent.Add(file);
            }
        }

        await Assert.That(silent.Count).IsEqualTo(0)
            .Because(
                "these files must call ModelRef.TryParse / ModelRef.Qualify, not merely stop "
                + "cutting strings: " + string.Join(", ", silent)
                + ". A hand-rolled prefix check that was replaced by nothing is the same bug "
                + "in a quieter shape. See issue #678.");
    }

    /// <summary>
    ///     #453: the shared-config seam qualifies through the contract, and the
    ///     qualification lives at the PRODUCER. The rule above proves an owned file
    ///     delegates; this one names WHICH file must, so that moving the delegation
    ///     somewhere the ratchet cannot see fails loudly instead of quietly
    ///     emptying <see cref="OwnedFiles" />.
    /// </summary>
    [Test]
    public async Task TheConfigSeam_QualifiesAtTheProducer_NotInTheConsumer()
    {
        string root = RequireRepoRoot();
        const string producer = "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs";
        const string consumer = "src/Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs";

        await Assert.That(CountDelegations(root, producer)).IsGreaterThan(0)
            .Because(
                "CommonConfigReaderAdapter is the one place that turns the config's two raw "
                + "fields into a ModelRef. It must call ModelRef.Qualify: that is what rejects a "
                + "blank or invalid provider half and normalizes it, and it is the reason the "
                + "seam can hand out one value with one absence.");

        await Assert.That(CountDelegations(root, consumer)).IsEqualTo(0)
            .Because(
                "SessionFactory used to call ModelRef.Qualify itself, because the port handed it "
                + "two raw strings to qualify. It now receives a ModelRef, so a second "
                + "qualification would be a re-derivation of a question the value has already "
                + "answered — and the type would stop being the single place the rule lives. If "
                + "the port's carrier is ever widened back to two strings, this fails and the "
                + "qualification belongs here again.");
    }

    /// <summary>
    ///     The <c>== 0</c> above is only a verdict if the file was READ. It was not:
    ///     <see cref="CountDelegations" /> catches <see cref="IOException" />, and
    ///     <c>FileNotFoundException</c> derives from it, so a renamed or moved file
    ///     reports "this file does no hand-rolled qualification" — the <b>true</b>
    ///     answer — and the rule passes having never looked at the file. This is the
    ///     #591 shape (a plausible 0) reached through a swallowed exception rather
    ///     than a bad matcher, and it is load-bearing here because the consumer this
    ///     rule names is a path constant: <c>src/Harbor.Ui.Framework.Sessions/…</c>
    ///     is exactly the kind of path a refactor moves, and #439 measured that move
    ///     as live. Two of the three call sites cannot notice (a missing file makes
    ///     them red); only the <c>== 0</c> one can, so it is the one checked here.
    /// </summary>
    [Test]
    public async Task ADelegationCount_NeverReportsZeroForAFileItCouldNotRead()
    {
        string root = RequireRepoRoot();

        await Assert.That(
                () => CountDelegations(root, "src/Harbor.Architecture.Tests/NoSuchFile-439.cs"))
            .Throws<FileNotFoundException>()
            .Because(
                "a count over a file that is not there is not a count of zero, it is an absent "
                + "measurement. Letting it read as zero turns the consumer assertion above into a "
                + "permanent green light the moment SessionFactory.cs is moved or renamed — which is "
                + "the exact event #439's Sessions/ relocation would cause, and the reason the "
                + "sibling guards in ProviderModelAbsenceRules already assert file existence.");
    }

    /// <summary>
    ///     Rule 2: the sanctioned function still cuts. Without this, rules 1 could
    ///     be satisfied by deleting <c>TryParse</c> and the UI would go back to
    ///     string surgery with nothing to delegate to.
    /// </summary>
    [Test]
    public async Task TheContract_StillCutsTheReference()
    {
        string root = RequireRepoRoot();

        await Assert.That(FindCutSites(root, TryParseFileRelativePath).Count).IsGreaterThan(0)
            .Because(
                TryParseFileRelativePath + " must contain the count-2 split that ModelRef.TryParse "
                + "is. If the function moved or was renamed, point this guard at the new home "
                + "instead of deleting the check.");
    }

    /// <summary>
    ///     Rule 3, forward direction: no ad-hoc cut outside the contract and the
    ///     inventory.
    /// </summary>
    [Test]
    public async Task EveryAdHocCut_IsEitherTheContractOrInventoryed()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<string> files = EnumerateProductCsFiles(root);

        var unexpected = new List<CutSite>();
        foreach (string file in files)
        {
            if (file == TryParseFileRelativePath
                || OwnedFiles.Contains(file)
                || KnownAdHocCutSites.ContainsKey(file))
            {
                continue;
            }

            unexpected.AddRange(FindCutSites(root, file));
        }

        await Assert.That(unexpected.Count).IsEqualTo(0)
            .Because(
                "a new hand-rolled read of a slash-separated string appeared in "
                + Describe(unexpected)
                + ". If it really is not a model reference, add the file to KnownAdHocCutSites "
                + "WITH the reason; if it is one, call ModelRef. See issue #678.");
    }

    /// <summary>
    ///     Rule 3, reverse direction, plus honesty about the reasons. An entry
    ///     whose file no longer exists, or no longer cuts anything, is dead weight
    ///     that would silently re-widen the rule the next time someone re-adds a cut
    ///     there — so a fixed site has to be REMOVED from the list, which is the
    ///     ratchet's whole point.
    /// </summary>
    [Test]
    public async Task EveryInventoryEntry_StillNamesAFileThatStillCuts()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<string> files = EnumerateProductCsFiles(root);

        foreach ((string path, string reason) in KnownAdHocCutSites)
        {
            await Assert.That(reason.Trim().Length > 0).IsTrue()
                .Because("inventory entry " + path + " must state why it is not a violation");
            await Assert.That(files.Contains(path)).IsTrue()
                .Because("inventory entry " + path + " names a file the scan does not see — drop the entry");
            await Assert.That(FindCutSites(root, path).Count).IsGreaterThan(0)
                .Because(
                    "inventory entry " + path + " no longer contains an ad-hoc cut, so it is no "
                    + "longer an exception — delete the entry and let the rule apply to it.");
        }
    }

    /// <summary>
    ///     The inventory must not name a file that has become a violation: either
    ///     because the entry was never there, or because an owned file was added by
    ///     copy-paste.
    /// </summary>
    [Test]
    public async Task TheInventory_NamesOnlyFilesOutsideTheOwnedSet()
    {
        foreach (string path in KnownAdHocCutSites.Keys)
        {
            await Assert.That(OwnedFiles.Contains(path)).IsFalse()
                .Because(
                    path + " is a file this guard holds to the strict rule, so it must not also "
                    + "be listed as an exception — the two rules would contradict each other.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 1: the discovery step must find a real, non-trivial
    ///     file set containing the contract file and both owned files. A scan
    ///     rooted at a typo finds zero files and then "passes" everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheContractAndTheOwnedFiles()
    {
        IReadOnlyList<string> files = EnumerateProductCsFiles(RequireRepoRoot());

        await Assert.That(files.Count).IsGreaterThan(200)
            .Because("src/ + apps/ hold an order of magnitude more than 200 C# files; a smaller count means the glob broke.");
        await Assert.That(files.Contains(TryParseFileRelativePath)).IsTrue()
            .Because(TryParseFileRelativePath + " must be inside the scanned set, or the guard polices nothing.");
        foreach (string file in OwnedFiles)
        {
            await Assert.That(files.Contains(file)).IsTrue()
                .Because(file + " must be inside the scanned set, or rule 1 is vacuous.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME matcher must fire on each of the three
    ///     planted spellings, stay silent on a same-shaped line that is not one of
    ///     them, and stay silent on comment prose. This is what separates "the
    ///     guard is green" from "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnTheKnownSpellings_AndStaysSilentOtherwise()
    {
        await Assert.That(Scan(["var p = model.Split('/', 2);"]).Count).IsGreaterThan(0)
            .Because("the count-2 split is the spelling #599 was about; the matcher must see it");
        await Assert.That(Scan(["int i = raw.IndexOf('/');"]).Count).IsGreaterThan(0)
            .Because("IndexOf('/') is the same read spelled differently");
        await Assert.That(Scan(["if (!input.Contains('/')) return 1;"]).Count).IsGreaterThan(0)
            .Because("the Contains('/') probe is the OnboardingFlow spelling this issue exists for");
        await Assert.That(Scan(["string prefix = provider + \"/\";", "if (m.StartsWith(prefix)) return m;"]).Count)
            .IsGreaterThan(0)
            .Because("building a provider prefix and stripping it is the SessionFactory spelling");

        await Assert.That(Scan(["string[] s = line.Split(\"|\", StringSplitOptions.None);"]).Count).IsEqualTo(0)
            .Because("a non-slash split is not this rule, or the guard is noise");
        await Assert.That(Scan(["if (name.Contains('x')) return true;"]).Count).IsEqualTo(0)
            .Because("the matcher looks for a slash argument, not for Contains calls in general");
        await Assert.That(Scan(["int i = raw.IndexOf('-');"]).Count).IsEqualTo(0)
            .Because("the same holds for IndexOf");
        await Assert.That(Scan(["if (m.StartsWith(prefixName)) return m;"]).Count).IsEqualTo(0)
            .Because("only a StartsWith on a variable named 'prefix' is this rule's spelling");
        await Assert.That(Scan(["// model.Split('/', 2) — prose about the old code"]).Count).IsEqualTo(0)
            .Because("comment lines are prose, not a cut");
        await Assert.That(Scan(["/// <c>value.Split('/')</c> in a doc comment."]).Count).IsEqualTo(0)
            .Because("doc-comment lines are prose, not a cut");
        await Assert.That(Scan(["    var p = m.Split('/', 2); // trailing comment"]).Count).IsGreaterThan(0)
            .Because("a code line with a trailing comment is still a code line");
    }

    /// <summary>
    ///     Non-vacuity, part 3: the delegation matcher must recognise a call and
    ///     must not be satisfied by the same identifier written in a comment — the
    ///     XML docs of both owned files name <c>ModelRef.TryParse</c> while
    ///     explaining the rule, and reading those as delegation would disarm the
    ///     positive half of rule 1.
    /// </summary>
    [Test]
    public async Task DelegationMatcher_SeesACall_AndNotAMention()
    {
        await Assert.That(CountDelegationsIn(["var r = ModelRef.Qualify(p, m);"]).Count).IsEqualTo(1)
            .Because("a real call must be recognised");
        await Assert.That(CountDelegationsIn(["var r = ModelRef.TryParse(value);"]).Count).IsEqualTo(1)
            .Because("a real call must be recognised");
        await Assert.That(CountDelegationsIn(["/// ModelRef.TryParse is the only function that reads one."]).Count)
            .IsEqualTo(0)
            .Because("a doc comment naming the function is prose, not delegation");
        await Assert.That(CountDelegationsIn(["// see ModelRef.Qualify"]).Count).IsEqualTo(0)
            .Because("a line comment naming the function is prose, not delegation");
    }

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");
    }

    private static string Describe(IReadOnlyList<CutSite> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.Line));

    private static IReadOnlyList<string> EnumerateProductCsFiles(string root)
    {
        var files = new List<string>();
        foreach (string tree in ProductTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Build output only. No worktree filter here: the walk starts at
            // <root>/src and <root>/apps, so a nested checkout under <root>/.worktrees
            // is out of reach anyway — and matching on it would be actively wrong,
            // because a checkout that ITSELF lives in .worktrees would filter itself
            // out and the scan would find nothing.
            files.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p))
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal)
               || normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    private static List<CutSite> FindCutSites(string root, string relativePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(root, relativePath));
        }
        catch (IOException)
        {
            return [];
        }

        return
        [
            .. Scan(lines)
                .Select(index => new CutSite(relativePath, index + 1, lines[index].Trim()))
        ];
    }

    /// <summary>
    ///     0-based line numbers holding an ad-hoc cut. Comment-only lines are
    ///     skipped (see the file header for why, and for the limit that comes
    ///     with it).
    /// </summary>
    private static IReadOnlyList<int> Scan(IReadOnlyList<string> lines)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsCommentLine(lines[i]))
            {
                continue;
            }

            if (AdHocCut.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static int CountDelegations(string root, string relativePath)
    {
        try
        {
            return CountDelegationsIn(File.ReadAllLines(Path.Combine(root, relativePath))).Count;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<int> CountDelegationsIn(IReadOnlyList<string> lines)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!IsCommentLine(lines[i]) && DelegatesToContract.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith('*');
    }
}
