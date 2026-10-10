// ScanRule.cs — issue #1086, step 2: rule as data.
//
// WHY THIS FILE EXISTS
// --------------------
// Every source-scanning guard in this project re-implements the same five
// moves: enumerate files, strip comments, match a line regex, subtract a
// baseline table, and prove the whole thing is not vacuous (planted positive /
// negative controls + a discovery floor + reasoned + non-stale baseline rows).
// Measured on origin/dev, the scaffolding share is 90–100% in almost every one
// of the 141 files. The prose is the rule; the mechanism is identical.
//
// So the mechanism lives here, once, as data plus one runner:
//   * ScanRule — Id, Trees, Forbidden[], Baseline[], Controls[], MinHits.
//   * ScanForbidden — one (SubId, Pattern, Instead) row: the tabular form.
//   * ScanBaseline — exact ("subId path") or prefix ("src/Harbor.Plugins.")
//     exemption, each carrying its reason as a VALUE (ExemptionReason.Row).
//   * ScanControl — synthetic (File, Source, ExpectSubId): null stays silent.
//   * Func-overload — CustomParse(DisplayPath, RawSource): the escape hatch for
//     rules whose matcher needs file-level facts (TrimUnsafe's resolver-aware
//     serialization verdict, ToolGlyph's name-inventory filter). A rule that
//     cannot be a line regex uses code beside the declaration, not a second
//     rule file — variant B (YAML + generic runner) was evaluated in #1086 and
//     rejected: it destroys review granularity, typing, meta-tests, and the
//     escape hatch itself.
//   * ScanRunner — ScopeFiles / Collect / Evaluate / CheckControls /
//     CheckDiscovery / CheckReasons / StaleBaselineKeys.
//
// WHAT IS DELIBERATELY NOT HERE, AND WHY
// --------------------------------------
//   * PURE-UNIT files (Result/Maybe shape tests, signature tests): no file walk,
//     nothing to fold. Excluded by #1086.
//   * Prose rationale: the ~40 lines of patterns + reasons per rule are
//     incompressible in principle (see #1086 ceiling note) and stay in the
//     rule file. This engine removes mechanism, never argument.
//   * INFRA/META rules (ScanUniverseRule, BuildGraphCoverageRule,
//     EnforcerIntegrityTests, FullLayerMatrixTests): they measure the
//     partition/build universe and must not reuse the walk they measure.
//   * contrib/-rooted walks (ContribBoundaryNameRule): SourceScan prunes
//     contrib/ by construction, so a Trees-based scope cannot see it.
//   * IL-level (Assembly.GetReferencedAssemblies) and csproj-flavored rules:
//     different input (assemblies, XML), later flavors, not this one.
//   * Custom strippers with divergent contracts (literal-blankers, masking
//     lexers — see step-1 PR #1092): they blank string literals where the
//     shared stripper preserves them. Folding changes matcher input. Each such
//     rule migrates via the Func-overload with its stripper inside, never by
//     silently adopting the shared one.
//
// STRIPPING IS CHOSEN BY EXTENSION, NOT BY CALLER
// ----------------------------------------------
// *.cs runs SourceScan.StripComments, *.axaml runs
// SourceScan.StripMarkupComments. The wrong stripper over markup truncates
// every line holding a URL at its `//` (CostUnpricedBitRules proves it with a
// control); choosing here makes that class of mistake unrepresentable.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One located match: which sub-rule, where, what text.</summary>
/// <param name="RuleId">Sub-rule id (the <c>SubId</c> of the row that matched).</param>
/// <param name="File">Repo-relative forward-slashed path, or a synthetic control name.</param>
/// <param name="Line">1-based line in the comment-stripped source.</param>
/// <param name="Text">The matched text, trimmed.</param>
internal readonly record struct ScanHit(string RuleId, string File, int Line, string Text)
{
    /// <summary>The line a failure message prints.</summary>
    internal string Report() => $"{File}:{Line}  [{RuleId}]  {Text}";
}

/// <summary>
///     One banned shape: the sub-id it reports under, the pattern that detects
///     it, and what to do instead — all three printed on failure so a reader
///     never has to open the rule file to learn the rule.
/// </summary>
internal sealed record ScanForbidden(string SubId, Regex Pattern, string Instead);

/// <summary>
///     One tolerated violation: the key it excuses plus the reason as a value.
/// </summary>
/// <param name="Key">
///     Exact form <c>"&lt;subId&gt; &lt;repo-relative path&gt;"</c>, or a path
///     prefix when <paramref name="IsPrefix" /> is set (the plugin-family
///     allowance shape).
/// </param>
/// <param name="IsPrefix">Whether <paramref name="Key" /> is a path prefix.</param>
/// <param name="Reason">Why the violation is tolerated, in the row itself.</param>
/// <param name="TrackedBy">Tracking issue URL, when the table has one.</param>
internal sealed record ScanBaseline(string Key, bool IsPrefix, string? Reason, string? TrackedBy = null)
{
    /// <summary>Whether a hit is excused by this row.</summary>
    internal bool Covers(ScanHit hit) =>
        IsPrefix
            ? hit.File.StartsWith(Key, StringComparison.Ordinal)
            : string.Equals($"{hit.RuleId} {hit.File}", Key, StringComparison.Ordinal);
}

/// <summary>
///     One planted control driving the REAL matcher: synthetic source the rule
///     must report (<paramref name="ExpectSubId" /> set) or stay silent on
///     (<see langword="null" /> — comments, near-misses, correct neighbours).
/// </summary>
internal sealed record ScanControl(string File, string Source, string? ExpectSubId);

/// <summary>
///     A source-scanning rule as data: identity, scope, banned shapes,
//     tolerated rows, controls, and the discovery floor. The runner is
///     <see cref="ScanRunner" />; the rule file keeps its prose, its
///     <c>[Test]</c> names, and nothing else.
/// </summary>
internal sealed class ScanRule
{
    /// <summary>Rule id, used verbatim in baseline-table names and messages.</summary>
    internal required string Id { get; init; }

    /// <summary>Repository-relative trees to walk (e.g. <c>["src", "apps"]</c>).</summary>
    internal required string[] Trees { get; init; }

    /// <summary>File glob within each tree. Default <c>*.cs</c>.</summary>
    internal string Glob { get; init; } = "*.cs";

    /// <summary>
    ///     Extra scope filter over repo-relative forward-slashed paths
    ///     (e.g. the provider prefix). Null means the whole walk.
    /// </summary>
    internal Func<string, bool>? InScope { get; init; }

    /// <summary>The banned shapes. At least one; each carries its own sub-id.</summary>
    internal required ScanForbidden[] Forbidden { get; init; }

    /// <summary>Tolerated rows, each with a reason. Empty means fully armed.</summary>
    internal ScanBaseline[] Baseline { get; init; } = [];

    /// <summary>Planted controls over synthetic source.</summary>
    internal ScanControl[] Controls { get; init; } = [];

    /// <summary>
    ///     Discovery floor: minimum files the scope must yield. An empty walk
    ///     satisfies every matcher, so a scope that finds nothing must fail here
    ///     rather than pass everywhere.
    /// </summary>
    internal int MinHits { get; init; } = 1;

    /// <summary>
    ///     Named repo-relative files the scope must contain — the files the
    ///     issue is about. A scope filter that silently stops seeing them is
    ///     broken, not clean.
    /// </summary>
    internal string[] MustContain { get; init; } = [];

    /// <summary>
    ///     Escape hatch: grade one file's raw source with code instead of the
    ///     shared line scan. Receives the display path and the RAW source
    ///     (strip it yourself — usually <see cref="SourceScan.StripComments" />)
    ///     and returns hits. Null means the line scan over
    ///     <see cref="Forbidden" />. One rule in a file may use it while its
    ///     siblings stay tabular.
    /// </summary>
    internal Func<string, string, IEnumerable<ScanHit>>? CustomParse { get; init; }
}

/// <summary>The single runner every <see cref="ScanRule" /> executes through.</summary>
internal static class ScanRunner
{
    /// <summary>
    ///     Scope files as repo-relative forward-slashed paths, sorted. Reads
    ///     through <see cref="SourceScan" /> so every gate agrees on what
    ///     counts as product code.
    /// </summary>
    internal static IReadOnlyList<string> ScopeFiles(ScanRule rule)
    {
        IReadOnlyList<string> absolute = rule.Glob == "*.axaml"
            ? SourceScan.EnumerateXamlFiles(rule.Trees)
            : SourceScan.EnumerateCsFiles(rule.Trees);

        var relative = absolute
            .Select(SourceScan.Relative)
            .Where(p => rule.InScope?.Invoke(p) ?? true)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return relative;
    }

    /// <summary>On-disk scope files as (display path, raw source) pairs.</summary>
    internal static List<(string DisplayPath, string Source)> ReadSources(IReadOnlyList<string> relativePaths)
    {
        var sources = new List<(string, string)>(relativePaths.Count);
        foreach (string relative in relativePaths)
        {
            string full = Path.Combine(
                RepoPaths.RepoRoot ?? ".",
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (SourceScan.TryReadAllText(full) is { } text)
            {
                sources.Add((relative, text));
            }
        }

        return sources;
    }

    /// <summary>
    ///     Every hit of <paramref name="rule" /> over already-read sources,
    ///     baseline included. Controls drive this directly with synthetic
    ///     source — the only way "it can fail" means anything.
    /// </summary>
    internal static List<ScanHit> Collect(ScanRule rule, IEnumerable<(string DisplayPath, string Source)> sources)
    {
        var hits = new List<ScanHit>();
        foreach ((string displayPath, string rawSource) in sources)
        {
            if (rule.CustomParse is not null)
            {
                hits.AddRange(rule.CustomParse(displayPath, rawSource));
                continue;
            }

            string stripped = rule.Glob == "*.axaml"
                ? SourceScan.StripMarkupComments(rawSource)
                : SourceScan.StripComments(rawSource);
            string[] lines = stripped.Split('\n');
            foreach (ScanForbidden banned in rule.Forbidden)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Match match = banned.Pattern.Match(lines[i]);
                    if (match.Success)
                    {
                        hits.Add(new ScanHit(banned.SubId, displayPath, i + 1, match.Value.Trim()));
                    }
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     The failures for <paramref name="rule" /> over the live scope:
    ///     every hit the baseline does not excuse, as printable lines.
    /// </summary>
    internal static List<string> Evaluate(ScanRule rule) =>
        EvaluateOver(ReadSources(ScopeFiles(rule)), rule);

    /// <summary>The failures over explicit sources (the control path).</summary>
    internal static List<string> EvaluateOver(
        IEnumerable<(string DisplayPath, string Source)> sources, ScanRule rule)
    {
        var failures = new List<string>();
        foreach (ScanHit hit in Collect(rule, sources))
        {
            if (rule.Baseline.Any(row => row.Covers(hit)))
            {
                continue;
            }

            failures.Add(hit.Report());
        }

        return failures;
    }

    /// <summary>
    ///     Baseline keys with no hit behind them: a permission for a problem
    ///     that no longer exists. Prefix rows are occupancy-checked by the
    ///     rule's own allowance test instead — a prefix names a permission,
    ///     not a site.
    /// </summary>
    internal static List<string> StaleBaselineKeys(
        ScanRule rule, IEnumerable<(string DisplayPath, string Source)> sources)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScanHit hit in Collect(rule, sources))
        {
            live.Add($"{hit.RuleId} {hit.File}");
        }

        return
        [
            .. rule.Baseline
                .Where(row => !row.IsPrefix && !live.Contains(row.Key))
                .Select(row => row.Key),
        ];
    }

    /// <summary>
    ///     The controls verdict: every planted offender reported under its
    ///     expected sub-id, every silent control silent. One message per
    ///     deviation.
    /// </summary>
    internal static List<string> CheckControls(ScanRule rule)
    {
        var failures = new List<string>();
        foreach (ScanControl control in rule.Controls)
        {
            IReadOnlyList<string> reported = Collect(rule, [(control.File, control.Source)])
                .Where(hit => !rule.Baseline.Any(row => row.Covers(hit)))
                .Select(hit => hit.RuleId)
                .ToList();

            if (control.ExpectSubId is null)
            {
                if (reported.Count > 0)
                {
                    failures.Add(
                        $"{control.File} must stay silent, reported [{string.Join(", ", reported)}]");
                }
            }
            else if (reported.Count != 1 || reported[0] != control.ExpectSubId)
            {
                failures.Add(
                    $"{control.File} must report [{control.ExpectSubId}], reported [{string.Join(", ", reported)}]");
            }
        }

        return failures;
    }

    /// <summary>
    ///     Discovery verdict: the scope yields at least
    ///     <see cref="ScanRule.MinHits" /> files and contains every
    ///     <see cref="ScanRule.MustContain" /> entry.
    /// </summary>
    internal static List<string> CheckDiscovery(ScanRule rule)
    {
        var failures = new List<string>();
        IReadOnlyList<string> files = ScopeFiles(rule);

        if (RepoPaths.RepoRoot is null)
        {
            failures.Add("no repository root — the scan found no files and every matcher passes for the wrong reason");
            return failures;
        }

        if (files.Count < rule.MinHits)
        {
            failures.Add(
                $"scope yields {files.Count} file(s), floor is {rule.MinHits} — the walk is broken rather than the code clean");
        }

        foreach (string expected in rule.MustContain)
        {
            if (!files.Contains(expected, StringComparer.Ordinal))
            {
                failures.Add($"{expected} is in this rule's scope and the scope stopped seeing it");
            }
        }

        return failures;
    }

    /// <summary>Every baseline row states why it is tolerated, in the row itself.</summary>
    internal static List<string> CheckReasons(ScanRule rule) =>
    [
        .. ExemptionReason.RowsWithoutAReason(
            $"{rule.Id}.Baseline",
            rule.Baseline.Select(row => (row.Key, new ExemptionReason.Row(row.Reason, row.TrackedBy)))),
    ];
}
