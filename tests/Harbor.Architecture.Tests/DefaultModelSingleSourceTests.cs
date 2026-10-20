// DefaultModelSingleSourceTests.cs — the guard for issue #599.
//
// WHY THIS FILE EXISTS
// --------------------
// `IdentityConfig` declared the default model id TWICE, seven lines apart, and the
// two copies did not agree:
//
//   public const string FallbackModel = "kilocode/tencent/hy3:free";
//   ModelRef.Create(pid, "tencent/hy3:free".Split('/')[1]),
//
// `"tencent/hy3:free".Split('/')[1]` is `"hy3:free"`, so the value a default
// install actually carried (the typed half, read by `HarborConfig.Model`) was not
// the value the declared constant promised, and the raw, count-unlimited split
// truncated every segment after the first — exactly the shape of the multi-segment
// model ids the project's own docs tell users to set. The copy that agreed with
// `providers/kilocode.json` was the dead one; the broken copy was the live one.
//
// `BannedSymbols.txt` cannot express this rule: `String.Split` is far too broad a
// symbol to ban, and the defect is a call site's CORRECTNESS, not an API's
// existence. So the rule is a source rule — the same shape MaybeAbsenceTests and
// TuiReadLineContractRules use, for the same reason.
//
// THE RULE
// --------
// The default model id may appear inside a C# string literal in exactly ONE
// product file: the one that declares `IdentityConfig.FallbackModel`. Everywhere
// else it must be DERIVED — `IdentityConfig.FallbackModelRef` for the typed form,
// `ProviderPresets.Find(id).DefaultModel` for the provider-declared one.
//
// The rule is VALUE-AGNOSTIC on purpose. The id to hunt for is read out of
// `FallbackModel` at test time, so changing the constant re-points the guard
// instead of silently disarming it — a guard pinned to a hard-coded string quietly
// stops guarding the day the model rolls over.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that
// is broken, and a broken guard is worse than no guard because it is believed.
// Two tests below close that: the discovery step must find a non-trivial file set,
// and the SAME matcher must fire on a planted positive control while staying silent
// on a negative control and on comment prose. If the discovery path, the regex or
// the comment filter ever degrades, these go RED instead of quietly passing.
//
// KNOWN EXEMPTION
// ---------------
// One hit is allowed and is listed in `HandSpelledExemptions` below, carrying the
// reason it cannot be derived today. An exemption is a decision, not an oversight —
// the reason is printed in the failure message, the same way MaybeAbsenceTests
// documents its exemptions.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan skips LINES whose first non-whitespace characters are `//`, `/*` or `*`.
// A model id quoted inside a comment is prose, not code, and is not what this rule
// is about. That is a line-level heuristic, not a C# parser: an id written inside a
// multi-line block comment, or on a line that opens inside a verbatim string, can be
// missed. The limitation is bounded by construction — the only way to bring the bug
// back is to make the second copy DO work, and work lives in string literals on code
// lines.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #599: the default model id has exactly one hand-spelled home — the
///     <c>FallbackModel</c> constant in <c>ConfigSections.cs</c>. Every other
///     spelling must be derived from it.
/// </summary>
public sealed class DefaultModelSingleSourceTests
{
    /// <summary>
    ///     The one file allowed to spell the default model id. Named, not globbed,
    ///     because the rule is about this specific declaration.
    /// </summary>
    private const string DeclarationFileRelativePath =
        "src/Harbor.PolicyConfig/Configuration/ConfigSections.cs";

    /// <summary>Product trees scanned for a second hand-spelled copy.</summary>
    private static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>
    ///     The only files allowed to spell the default model id outside the
    ///     declaration, each with the reason it cannot simply derive it. Adding an
    ///     entry is a decision, not an oversight — the reason is printed in the failure
    ///     message so the next reader knows who owns the debt.
    /// </summary>
    private static readonly Dictionary<string, string> HandSpelledExemptions = new(StringComparer.Ordinal)
    {
        ["apps/Harbor.App.Avalonia/Program.cs"] =
            "The --help banner. apps/Harbor.App.Avalonia is NOT in Harbor.slnx, so CI never compiles "
            + "it — interpolating IdentityConfig.FallbackModel here would ship a line nothing verifies. "
            + "Until the desktop app joins the solution, the banner keeps its literal and this entry "
            + "records the drift risk instead of hiding it.",
    };

    /// <summary>
    ///     Reads the value out of the constant declaration. Tolerant of spacing and
    ///     of the alignment the file uses.
    /// </summary>
    private static readonly Regex FallbackModelDeclaration = new(
        @"FallbackModel\s*=\s*""(?<value>[^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     One C# <c>string</c> literal on a line. Matched rather than searched as
    ///     raw text, so a model id mentioned in prose outside quotes on a code line
    ///     is not mistaken for a second copy.
    /// </summary>
    private static readonly Regex QuotedLiteral = new(
        @"""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     A file, a 1-based line number, and the offending line — the tuple every
    ///     failure message is built from.
    /// </summary>
    private sealed record LiteralSite(string RelativePath, int Line, string Text);

    /// <summary>The single banned shape's sub-id, shared by the rule data and the parser.</summary>
    private const string HandSpelledSubId = "HAND-SPELLED-DEFAULT-MODEL";

    // MECHANISM (#1086, step 2, conveyor batch 8)
    // -------------------------------------------
    // The ban below is a ScanRule: the QuotedLiteral shape as a Forbidden row
    // carrying the existing Regex object directly, five planted controls, the
    // declaration file plus the documented exemption as the MustContain floor,
    // a discovery floor. Enumeration, matching and the control/discovery
    // verdicts are ScanRunner's; this file keeps the issue prose, the patterns,
    // the exemption table, and the test names.
    //
    // The verdict is ParseLiterals through the Func-overload, carrying the
    // original line scan verbatim -- FindLiteralLines over the raw source lines,
    // the //-line skip and the quoted-literal Contains filter untouched --
    // with the declaration file and the HandSpelledExemptions hatch as the
    // rule's half. A file-wide baseline row would excuse a second copy in the
    // same file, and a second copy inside the declaration file is exactly the
    // defect #599 reported — so the baseline stays empty and the hatch stays a
    // file filter inside the parser. The planted controls grade this parser,
    // so "it can fail" means something.
    //
    // The rule is built per test from the LIVE value
    // (BuildRule(ExtractModelIdHalf(ReadDeclaredModelId(root)))), not stored
    // as static data: the guard is value-agnostic on purpose, and static data
    // would pin a stale string the day the model rolls over. Scope parity is
    // exact: Trees ["src", "apps"] walks what EnumerateProductCsFiles walks —
    // no *.cs path under src/ or apps/ carries a /tests/, /contrib/ or
    // /.worktrees/ segment, and both sides drop /obj/ + /bin/.

    /// <summary>The hand-spelled-copy ban as data: one shape, five planted controls, a floor.</summary>
    private static ScanRule BuildRule(string modelHalf) => new()
    {
        Id = "DefaultModelSingleSource",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                HandSpelledSubId,
                QuotedLiteral,
                "derive it from IdentityConfig.FallbackModelRef (typed) or from "
                + "ProviderPresets.Find(id).DefaultModel (provider-declared) instead. "
                + "See issue #599."),
        ],
        Controls =
        [
            new ScanControl(
                "Planted/Literal.cs",
                "internal static class Planted\n{\n    public const string Model = \"" + modelHalf + "\";\n}",
                HandSpelledSubId),
            new ScanControl(
                "Planted/TrailingComment.cs",
                "    var m = \"kilocode/" + modelHalf + "\"; // trailing comment",
                HandSpelledSubId),
            new ScanControl(
                "NearMiss/DifferentModel.cs",
                "internal static class Planted\n{\n    public const string Model = \"some/other-model\";\n}",
                null),
            new ScanControl(
                "NearMiss/LineComment.cs",
                "// prose mentioning \"" + modelHalf + "\" in a comment",
                null),
            new ScanControl(
                "NearMiss/DocComment.cs",
                "/// <c>" + modelHalf + "</c> in a doc comment.",
                null),
        ],
        MinHits = 500,
        MustContain = [DeclarationFileRelativePath, .. HandSpelledExemptions.Keys],
        CustomParse = (displayPath, rawSource) => ParseLiterals(displayPath, rawSource, modelHalf),
    };

    /// <summary>
    ///     The custom parser: the original literal scan over one file's raw source —
    ///     the comment-line skip and the quoted-literal filter verbatim (via
    ///     <see cref="FindLiteralLines" />), with the declaration file and the
    ///     documented exemptions as the rule's hatch.
    /// </summary>
    private static IEnumerable<ScanHit> ParseLiterals(string displayPath, string rawSource, string modelHalf)
    {
        if (string.Equals(displayPath, DeclarationFileRelativePath, StringComparison.Ordinal)
            || HandSpelledExemptions.ContainsKey(displayPath))
        {
            yield break;
        }

        string[] lines = rawSource.Split('\n');
        foreach (int index in FindLiteralLines(lines, modelHalf))
        {
            yield return new ScanHit(HandSpelledSubId, displayPath, index + 1, lines[index].Trim());
        }
    }

    /// <summary>
    ///     The default model id is spelled in a string literal in exactly one product
    ///     file — the one declaring the constant — plus whatever a documented exemption
    ///     names, and nothing else.
    /// </summary>
    [Test]
    public async Task DefaultModelId_IsSpelledOnlyWhereTheRuleAllows()
    {
        string root = RequireRepoRoot();
        string modelId = ExtractModelIdHalf(ReadDeclaredModelId(root));

        // The ScanRule half: every hit outside the declaration and the
        // documented exemptions. Those two stay a file filter inside the
        // parser rather than baseline rows (see the MECHANISM note above).
        List<string> illegal = ScanRunner.Evaluate(BuildRule(modelId));

        IReadOnlyList<string> files = EnumerateProductCsFiles(root);
        List<LiteralSite> sites = [.. files.SelectMany(f => FindLiteralSites(root, f, modelId))];

        await Assert.That(illegal.Count).IsEqualTo(0)
            .Because(
                "the default model id (" + modelId + ") is hand-spelled in " + string.Join(", ", illegal)
                + " — derive it from IdentityConfig.FallbackModelRef (typed) or from "
                + "ProviderPresets.Find(id).DefaultModel (provider-declared) instead. "
                + "If the file is genuinely a documented exception, add it to "
                + "HandSpelledExemptions WITH the reason. See issue #599.");

        // The count closes the other half of the original bug: the two disagreeing
        // copies were BOTH in the declaration file, 7 lines apart, so a rule that only
        // looked at "other files" would have waved that through. One hit in the
        // declaration, one per exemption, no more.
        int allowedHits = 1
            + HandSpelledExemptions.Keys.Sum(p => FindLiteralSites(root, p, modelId).Count());
        await Assert.That(sites.Count).IsEqualTo(allowedHits)
            .Because(
                "the default model id must be spelled " + allowedHits + " time(s) in product code — once "
                + "in " + DeclarationFileRelativePath + " plus the documented exemptions — but found "
                + Describe(sites) + ". A second copy inside the declaration file is exactly the "
                + "defect #599 reported.");
    }

    /// <summary>
    ///     The exemption list stays honest in both directions: an entry whose file no
    ///     longer exists, or no longer contains the literal, is dead weight that would
    ///     silently widen the rule the next time someone re-adds a copy there. A reason
    ///     is mandatory, not optional — the list is a decision log, not a mute button.
    /// </summary>
    [Test]
    public async Task EveryExemption_StillMatchesAFileThatStillSpellsTheId()
    {
        string root = RequireRepoRoot();
        string modelId = ExtractModelIdHalf(ReadDeclaredModelId(root));
        IReadOnlyList<string> files = EnumerateProductCsFiles(root);

        foreach ((string path, string reason) in HandSpelledExemptions)
        {
            await Assert.That(reason.Length > 0).IsTrue()
                .Because("exemption " + path + " must state why it cannot derive the value");
            await Assert.That(files.Contains(path)).IsTrue()
                .Because("exemption " + path + " names a file the scan does not see — drop the entry");
            await Assert.That(FindLiteralSites(root, path, modelId).Count).IsGreaterThan(0)
                .Because(
                    "exemption " + path + " no longer spells the default model id, so it is no longer "
                    + "an exception — delete the entry and let the rule apply.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 1: the discovery step must find a real, non-trivial file
    ///     set that contains the declaration file. A scan rooted at a typo finds zero
    ///     files and then "passes" everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_IncludingTheDeclaration()
    {
        string root = RequireRepoRoot();
        string modelId = ExtractModelIdHalf(ReadDeclaredModelId(root));
        List<string> discovery = ScanRunner.CheckDiscovery(BuildRule(modelId));

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/ + apps/ hold far more than 500 C# files, including the declaration file "
                + "and the documented exemption. A smaller count -- or a scope that stopped seeing "
                + "those files -- means the tree walk broke and the rule above would be "
                + "reporting 'clean' about nothing. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     The planted controls -- every literal shape and every near-miss -- drive
    ///     the REAL parser: offenders reported under their sub-id, near-misses silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        string root = RequireRepoRoot();
        string modelId = ExtractModelIdHalf(ReadDeclaredModelId(root));
        List<string> failures = ScanRunner.CheckControls(BuildRule(modelId));

        await Assert.That(failures).IsEmpty()
            .Because(
                "the five planted controls must agree with the five matcher cases above through "
                + "the rule's own parser -- otherwise the rule guards nothing, or forbids the fix. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Non-vacuity, parts 2–5: the SAME matcher must fire on a planted literal,
    ///     stay silent on a literal that names a different model, stay silent on
    ///     comment prose, and still fire on a code line that merely ends in a comment.
    ///     This is what separates "the guard is green" from "the guard is looking at
    ///     nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedLiteral_AndStaysSilentOtherwise()
    {
        string modelId = ExtractModelIdHalf(ReadDeclaredModelId(RequireRepoRoot()));

        string[] positive =
        [
            "internal static class Planted",
            "{",
            "    public const string Model = \"" + modelId + "\";",
            "}"
        ];

        string[] negative =
        [
            "internal static class Planted",
            "{",
            "    public const string Model = \"some/other-model\";",
            "}"
        ];

        await Assert.That(FindLiteralLines(positive, modelId).Count).IsGreaterThan(0)
            .Because("a planted copy of the default model id must be detected, or the guard is blind");
        await Assert.That(FindLiteralLines(negative, modelId).Count).IsEqualTo(0)
            .Because("a literal naming a DIFFERENT model must not trip the guard, or the guard is noise");
        await Assert.That(FindLiteralLines(["// prose mentioning \"" + modelId + "\" in a comment"], modelId).Count)
            .IsEqualTo(0)
            .Because("comment lines are prose, not a second copy");
        await Assert.That(FindLiteralLines(["/// <c>" + modelId + "</c> in a doc comment."], modelId).Count)
            .IsEqualTo(0)
            .Because("doc-comment lines are prose, not a second copy");
        await Assert.That(FindLiteralLines(["    var m = \"kilocode/" + modelId + "\"; // trailing comment"], modelId).Count)
            .IsGreaterThan(0)
            .Because("a code line with a trailing comment is still a code line");
    }

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");
    }

    private static string ReadDeclaredModelId(string root)
    {
        string text = File.ReadAllText(Path.Combine(root, DeclarationFileRelativePath));
        Match match = FallbackModelDeclaration.Match(text);
        return match.Success
            ? match.Groups["value"].Value
            : throw new InvalidOperationException(
                "No FallbackModel = \"…\" declaration found in " + DeclarationFileRelativePath
                + " — the guard cannot police a constant it cannot see.");
    }

    private static string ExtractModelIdHalf(string qualified)
    {
        int slash = qualified.IndexOf('/');
        return slash < 0 ? qualified : qualified[(slash + 1)..];
    }

    private static string Describe(IReadOnlyList<LiteralSite> sites)
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

    private static List<LiteralSite> FindLiteralSites(string root, string relativePath, string modelId)
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
            .. FindLiteralLines(lines, modelId).Select(index => new LiteralSite(relativePath, index + 1, lines[index]))
        ];
    }

    /// <summary>
    ///     0-based line numbers whose string literals name the default model id.
    ///     Comment-only lines are skipped (see the file header for why, and for the
    ///     limit that comes with it).
    /// </summary>
    private static IReadOnlyList<int> FindLiteralLines(IReadOnlyList<string> lines, string modelId)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            foreach (Match match in QuotedLiteral.Matches(lines[i]))
            {
                if (match.Groups["value"].Value.Contains(modelId, StringComparison.Ordinal))
                {
                    hits.Add(i);
                    break;
                }
            }
        }

        return hits;
    }
}
