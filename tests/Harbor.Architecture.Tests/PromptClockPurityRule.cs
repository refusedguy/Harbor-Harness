// PromptClockPurityRule.cs — GUARD. (#814)
//
// THE CONVENTION BEING ENFORCED
// ------------------------------
// An ISystemPromptBuilder implementation reads NO clock. The prompt is a pure
// function of its SystemPromptContext, or it is not — and "not" is a cache
// defect, not a formatting detail.
//
// WHY A CLOCK IS WORSE THAN ANY OTHER MISSING KEY FIELD
// -----------------------------------------------------
// #792 was a missing FIELD: a tool's PromptGuidelines reached the prompt and
// not the key, so a guideline edit served the previous turn's text. Fixing it
// meant adding one more field to the key derivation, which was already a pure
// function of the context. The key grew; the contract held.
//
// A clock cannot be fixed that way, and the difference is structural rather
// than a matter of taste. The date is not a context component, so no key can
// carry it: `CachingSystemPromptBuilder` derives the key from
// `SystemPromptContext` alone, and there is no field whose value is "today".
// Adding one would mean either (a) a clock read inside the key derivation —
// which breaks the very purity the decorator documents ("the key derivation is
// pure") and makes the KEY impure instead of the builder, or (b) a permanent
// documented exception to the coverage claim, i.e. a cache that is known to be
// able to serve yesterday's world. Neither is a fix; both are a way of saying
// the builder is not a function of its context and declining to say so.
//
// THE DEFECT THIS RULE MAKES IMPOSSIBLE
// -------------------------------------
// `SystemPromptBuilder` printed `- Today: 2026-09-29` from a wall-clock read
// inside `BuildAsync`. The decorator is constructed in `AgentLoop`'s ctor,
// `IAgentLoop` is a DI singleton, the CLI builds one host around the whole
// REPL, and the dictionary has no TTL, no eviction and no Clear — so the cache
// lives as long as the PROCESS. A session open across 00:00 UTC therefore told
// the model it was yesterday, on every subsequent turn, with no code change and
// no plugin: just a long-lived session. A model handed a stale date can date an
// artifact, or decline to fetch one, on the strength of it — the wrong answer
// reaches the user, not only the cache.
//
// So the line is not allowed back, and neither is any other clock read. The
// shape the rule points at instead: if the model must know the date, the date
// is a SystemPromptContext member, and then the key covers it by construction
// — the #792 fix's own mechanism, applied to a value that belongs there.
//
// SCOPE: WHY THE INTERFACE AND NOT THE `## ` HEADER
// --------------------------------------------------
// The sibling rule `PromptSectionPolicyRule` finds prompt assemblies by the
// `## ` section headers they EMIT, and that signal is right for it: it grades
// the body of the method that emits the header. This rule cannot reuse it,
// because the impurity it forbids is in a DIFFERENT method from the one the
// header identifies — a `DateTimeOffset.UtcNow` folded into a
// `StringBuilder.Append` chain sits in whatever method assembles the section,
// and a builder that emits NO header at all (a decorator, a minimal
// implementation) is just as bound by the contract. The binding thing is the
// INTERFACE: `ISystemPromptBuilder` is the seam the caching decorator wraps,
// which is exactly why an impurity behind it is a cache defect rather than a
// local curiosity. Two implementations exist today; a third is covered the
// moment it declares the interface.
//
// FILE SCOPE WITHIN AN IMPLEMENTATION, AND WHY NOT METHOD SCOPE
// ------------------------------------------------------------
// The check is per FILE, not per method. A prompt builder that reads a clock
// for a purpose other than prompt text (a build-duration diagnostic, say) is
// not a case this rule can adjudicate without a second judgement call about
// whether that value reached the string, and a guard that needs to guess is a
// guard that gets disabled. The cost is bounded and worth stating: a builder
// that logs a timestamp is asked to take the timestamp from its context, or to
// ask for an exemption here with a reason. It is a small price for a rule with
// no escape hatch to erode.
//
// `Stopwatch` IS DELIBERATELY NOT MATCHED
// ---------------------------------------
// A stopwatch measures ELAPSED time; it does not say what day it is. The
// impurity this rule exists for is the DATE — a value that changes under a
// process that is otherwise identical. Timing a build for a log line is a
// legitimate use that this rule has no business blocking, and a rule that
// blocks legitimate work gets switched off, which is worse than the narrow
// hole it was avoiding. `Random` and `Guid` are out of scope for the same
// reason: the rule is about the clock, and widening it is a separate change
// with its own positive control.
//
// PERIMETER
// ---------
// `src/` only. `contrib/` is uncompiled and outside support by owner decision;
// `apps/` composes, it does not assemble prompts. Files under `tests/` are
// excluded because a guard that graded its own fixtures would be grading its
// own positive control. `.worktrees/` is skipped for the reason every scan here
// skips it: the main checkout keeps dozens of them, and a sibling worktree's
// copy of the same file is not a second violation.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — a checkout was walked and the canonical file really is in
//      the prompt-builder set, so a probe whose declaration matcher stopped
//      matching cannot report "no violations" and go green.
//   2. NonVacuity_Scan_DetectsAClockReadInSyntheticSource — THE POSITIVE
//      CONTROL. Four synthetic snippets: a prompt builder with an ambient clock
//      read, a prompt builder reading an INJECTED clock, a clean prompt
//      builder, and a NON-prompt-builder with the same ambient read. The first
//      two MUST be reported, the third and fourth MUST NOT. The fourth is what
//      proves the rule is not "no UtcNow in src/" — 63 of those exist today,
//      in message timestamps, store writes and token TTLs, all legitimate.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The rule below is a ScanRule with a CustomParse: the verdict needs the
// two-phase classification (a file is bound only when it DECLARES an
// `ISystemPromptBuilder` implementation, over the whole stripped text because a
// base list may wrap) plus the wall-clock matcher, which no shared line scan
// expresses. So the rule plugs a thin adapter over the UNCHANGED
// `PromptClockProbe` through the Func-overload — the probe keeps its own
// stripper (`SourceCommentStripper`, carried inside, never silently swapped for
// the shared one) and stays the single implementation the occupancy test and
// the controls drive. The `Forbidden` row holds the probe's own clock-read
// locator (widened to `internal` for exactly this) and carries the failure
// text. Enumeration and the control/discovery verdicts are ScanRunner's; this
// file keeps the issue prose and the test names.

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One wall-clock read the probe found inside a prompt-builder file.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Read">The matched clock read, e.g. <c>DateTimeOffset.UtcNow</c>.</param>
/// <param name="Text">The offending line, trimmed.</param>
internal sealed record PromptClockSite(
    string File,
    int Line,
    string Read,
    string Text);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="PromptBuilders">Files declaring an <c>ISystemPromptBuilder</c> implementation.</param>
/// <param name="ClockReads">Wall-clock reads found in those files.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
internal sealed record PromptClockScan(
    IReadOnlyList<string> PromptBuilders,
    IReadOnlyList<PromptClockSite> ClockReads,
    int FilesScanned);

/// <summary>Finds prompt-builder files and the wall-clock reads inside them.</summary>
internal static partial class PromptClockProbe
{
    /// <summary>
    /// The file whose clock read this rule was opened over. The scan does not
    /// depend on it — the interface declaration does the work — but the
    /// non-vacuity check names it so a probe that finds nothing fails loudly
    /// instead of reporting an empty violation list.
    /// </summary>
    internal const string CanonicalFile =
        "src/Harbor.Application/Sessions/SystemPromptBuilder.cs";

    /// <summary>Repository roots the scan walks.</summary>
    private static readonly string[] ScanRoots = ["src"];

    /// <summary>
    /// Directory names never descended into. <c>.worktrees</c> is the one that
    /// bites: the main checkout holds dozens of sibling worktrees, and each
    /// carries its own copy of the canonical file.
    /// </summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Walks the repository. A missing checkout scans nothing, which the rule
    /// reports as a failure rather than as a pass.
    /// </summary>
    internal static PromptClockScan Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new PromptClockScan([], [], 0);
        }

        var builders = new List<string>();
        var reads = new List<PromptClockSite>();
        int scanned = 0;

        foreach (string file in EnumerateSources(repoRoot))
        {
            string relative = MakeRelative(repoRoot, file);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            scanned++;
            ScanSource(relative, lines, builders, reads);
        }

        return new PromptClockScan(builders, reads, scanned);
    }

    /// <summary>
    /// Scans already-read lines. Exposed so the positive control drives the
    /// REAL matcher — comment stripping and declaration detection included —
    /// instead of a second implementation of it, which is the only way "it can
    /// fail" means anything.
    /// </summary>
    internal static void ScanSource(
        string relativeFile,
        string[] lines,
        List<string> builders,
        List<PromptClockSite> reads)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);
        if (clean.Length == 0)
        {
            return;
        }

        // The file is bound by the contract if it DECLARES an implementation.
        // Tested on the whole comment-stripped text rather than line by line,
        // because a base list is allowed to wrap:
        //   public sealed class Foo
        //       : ISystemPromptBuilder
        // The `[^;{}]` classes keep the match inside one declaration — it
        // cannot run past the opening brace into another type, and cannot run
        // past a `;` into an unrelated statement.
        if (!PromptBuilderDeclaration().IsMatch(string.Join('\n', clean)))
        {
            return;
        }

        if (!builders.Contains(relativeFile, StringComparer.Ordinal))
        {
            builders.Add(relativeFile);
        }

        for (int i = 0; i < clean.Length; i++)
        {
            foreach (Match match in ClockRead().Matches(clean[i]))
            {
                reads.Add(new PromptClockSite(relativeFile, i + 1, match.Value.Trim(), clean[i].Trim()));
            }
        }
    }

    /// <summary>
    /// A type declaration whose base list names <c>ISystemPromptBuilder</c>. The
    /// leading alternation is what keeps a CONSUMER out: a DI line such as
    /// <c>services.AddSingleton&lt;ISystemPromptBuilder&gt;(…)</c> has no type
    /// keyword before the interface name, so it is not a declaration and not a
    /// prompt builder.
    /// </summary>
    [GeneratedRegex(@"\b(?:class|record|struct)\b[^;{}]{0,400}?:\s*[^;{}]{0,400}?\bISystemPromptBuilder\b")]
    private static partial Regex PromptBuilderDeclaration();

    /// <summary>
    /// A read of the wall clock. Three shapes, because the impurity wears
    /// three costumes:
    ///   1. the ambient statics — <c>DateTimeOffset.UtcNow</c>, <c>DateTime.Now</c>,
    ///      <c>DateTime.Today</c>, <c>DateOnly.FromDateTime</c>;
    ///   2. an INJECTED clock, by method name — <c>_clock.GetUtcNow()</c>. Named
    ///      rather than typed, because the field is spelled
    ///      <c>_timeProvider</c> or <c>_clock</c> or <c>_time</c> and a
    ///      type-shaped matcher would miss all of them while still catching the
    ///      static nobody injects;
    ///   3. <c>Environment.TickCount</c> — a monotonic clock, same impurity.
    /// A <c>.Today()</c> helper call is included for the same reason as (2): a
    /// date can arrive through a project-local helper, and the name is all this
    /// scan has to go on.
    /// </summary>
    /// <remarks>
    /// The receiver is part of the match (<c>clock.GetUtcNow</c>, not
    /// <c>GetUtcNow</c>) because the value is quoted in the failure message, and
    /// a message naming only the method cannot tell a reader which of several
    /// clocks in one method was read. The call's <c>(</c> is left to a
    /// lookahead: including it would put a bracket in the middle of the quoted
    /// name, and the positive control asserts on the exact string.
    /// </remarks>
    /// <remarks>
    ///     Internal since #1086: the ScanRule below documents its banned shape
    ///     with this same locator rather than a copy, so the row cannot drift
    ///     from the parser. The runner never executes the row — the rule grades
    ///     through the probe — it carries the failure text.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:DateTimeOffset|DateTime|DateOnly)\s*\.\s*(?:UtcNow|Now|Today|UtcDate|Date|LocalDateTime|FromDateTime|FromDateTimeUtc)\b"
        + @"|[\w\]\)]*\s*\.\s*(?:Get(?:Utc|Local)Now|Today)(?=\s*\()"
        + @"|\bEnvironment\s*\.\s*TickCount\d*")]
    internal static partial Regex ClockRead();

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (string root in ScanRoots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                if (SkippedDirectories.Any(dir =>
                        path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard: an <see cref="Harbor.Abstractions.Sessions.ISystemPromptBuilder" />
///     implementation reads no clock. The prompt is cached for the life of the
///     process, and a value that moves under an otherwise identical context is
///     a value no key derived from that context can cover (#814).
/// </summary>
public sealed class PromptClockPurityRule
{
    private const string SubId = "PROMPT-BUILDER-WALL-CLOCK";

    private static readonly Lazy<PromptClockScan> Report = new(
        () => PromptClockProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>The rule as data: one documented shape, the probe as parser, controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "PromptClockPurity",
        Trees = ["src"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                PromptClockProbe.ClockRead(),
                "remove the read rather than key it — keying it would put a clock inside the key "
                + "derivation and break the purity the decorator documents. A date the model genuinely "
                + "needs belongs in SystemPromptContext, where #792's mechanism already covers it."),
        ],
        Controls =
        [
            // A prompt builder with an ambient clock read — the defect this rule
            // exists for — must be reported.
            new ScanControl("src/Synthetic/Ambient.cs", """
                public sealed class AmbientDateBuilder : ISystemPromptBuilder
                {
                    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
                    {
                        var sb = new StringBuilder();
                        sb.Append("- Today: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
                        return Task.FromResult(sb.ToString());
                    }
                }
                """, SubId),
            // Injecting the clock makes the impurity TESTABLE, not absent — the
            // value still moves under an identical context, so the cache is still
            // wrong. The obvious "fix" must not walk straight past the rule.
            new ScanControl("src/Synthetic/Injected.cs", """
                public sealed class InjectedDateBuilder(TimeProvider clock) : ISystemPromptBuilder
                {
                    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
                    {
                        var sb = new StringBuilder();
                        sb.Append("- Today: ").Append(clock.GetUtcNow().ToString("yyyy-MM-dd"));
                        return Task.FromResult(sb.ToString());
                    }
                }
                """, SubId),
            // The shape the rule REQUIRES: every value on the prompt comes off the
            // context. Also proves the parser is not matching the interface NAME
            // or the word `Today` as prose.
            new ScanControl("src/Synthetic/Clean.cs", """
                public sealed class CleanBuilder : ISystemPromptBuilder
                {
                    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
                    {
                        var sb = new StringBuilder();
                        sb.Append("- Working directory: ").Append(context.WorkingDirectory);
                        sb.Append("- Model: ").Append(context.Model.Id);
                        return Task.FromResult(sb.ToString());
                    }
                }
                """, null),
            // The same wall-clock read outside any prompt builder. This is the
            // control that keeps the rule from degenerating into "no UtcNow
            // anywhere in src/" — a claim src/ violates dozens of times over, in
            // message timestamps, store writes and token TTLs, all legitimate.
            new ScanControl("src/Synthetic/Factory.cs", """
                public static class SummaryMessageFactory
                {
                    public static AssistantMessage Create(string id) => new(id, "s", DateTimeOffset.UtcNow);
                }
                """, null),
        ],
        MinHits = 100,
        MustContain = [PromptClockProbe.CanonicalFile],
        CustomParse = ParseClockReads,
    };

    /// <summary>
    ///     The custom parser: the clock-read verdict over one file's raw source.
    ///     A thin adapter over the unchanged <see cref="PromptClockProbe" /> — the
    ///     probe keeps its own stripper and its declaration classification, and the
    ///     planted controls grade identically to product files because they drive
    ///     this same path.
    /// </summary>
    private static IEnumerable<ScanHit> ParseClockReads(string displayPath, string rawSource)
    {
        string[] lines = rawSource
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var builders = new List<string>();
        var reads = new List<PromptClockSite>();
        PromptClockProbe.ScanSource(displayPath, lines, builders, reads);
        foreach (PromptClockSite site in reads)
        {
            yield return new ScanHit(SubId, site.File, site.Line, site.Read);
        }
    }

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     No prompt builder reads the wall clock. Not ambient
    ///     <c>DateTimeOffset.UtcNow</c>, and not an injected
    ///     <c>TimeProvider</c> either: the decorator wrapping this seam caches
    ///     the result for the life of the process, so a clock read behind it
    ///     serves yesterday's date to every later turn of a session left open
    ///     across midnight. If the model needs the date, put it in
    ///     <c>SystemPromptContext</c> — a context member is covered by the key
    ///     by construction, which is the whole point of #792's fix.
    /// </summary>
    [Test]
    public async Task PromptBuilder_ReadsNoWallClock()
    {
        List<string> offenders = ScanRunner.Evaluate(Rule);

        await Assert.That(offenders).IsEmpty()
            .Because(
                "an ISystemPromptBuilder implementation must be a pure function of its "
                + "SystemPromptContext. CachingSystemPromptBuilder wraps this seam, is built in "
                + "AgentLoop's constructor over a DI-singleton IAgentLoop, and has no TTL, no "
                + "eviction and no Clear — so its entries live as long as the PROCESS. A wall-clock "
                + "read is not a context component, so NO key derived from the context can cover it: "
                + "a session open across 00:00 UTC serves yesterday's date on every later turn, and "
                + "a model handed a stale date can act on it. This is why the fix is to remove the "
                + "read rather than to key it — keying it would put a clock inside the key derivation "
                + "and break the purity that decorator documents. A date the model genuinely needs "
                + "belongs in SystemPromptContext, where #792's mechanism already covers it. "
                + "Offending sites: " + (offenders.Count == 0 ? "(none)" : string.Join("\n  ", offenders)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really classified the
    ///     canonical file as a prompt builder. Without this, a probe whose
    ///     declaration matcher broke would find no prompt builders, report zero
    ///     violations, and satisfy the rule above by having graded nothing.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero violations "
                   + "and the rule is satisfied by having nothing to look at");

        PromptClockScan report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/; the root or the file filter is wrong and "
                   + "every rule here is vacuously green");

        await Assert.That(string.Join(" | ", report.PromptBuilders))
            .Contains(PromptClockProbe.CanonicalFile)
            .Because(
                "the scan did not classify " + PromptClockProbe.CanonicalFile
                + " as an ISystemPromptBuilder implementation, which is the file this rule was "
                + "opened over. Detection is by the interface in the type's base list, so a miss "
                + "means that signal stopped matching — not that the file is clean. Found: "
                + (report.PromptBuilders.Count == 0 ? "(nothing)" : string.Join(" | ", report.PromptBuilders)));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. Four synthetic snippets, two of them prompt
    ///     builders with a clock read and two of them not. The probe MUST
    ///     report the first two and neither of the last two.
    /// </summary>
    /// <remarks>
    ///     The fourth snippet is the one that keeps the rule honest: it holds
    ///     the same <c>DateTimeOffset.UtcNow</c> as the first, outside any
    ///     prompt builder, and MUST come back clean. <c>src/</c> holds dozens
    ///     of legitimate wall-clock reads — message timestamps, store writes,
    ///     token TTLs — so a probe that ignored the interface would be red on
    ///     the moment this file was written.
    /// </remarks>
    [Test]
    public async Task NonVacuity_Scan_DetectsAClockReadInSyntheticSource()
    {
        // The four snippets live on Rule.Controls — two prompt builders with a
        // clock read that MUST be reported, a clean builder and a non-builder
        // that MUST NOT — so the control drives the REAL parser rather than a
        // second implementation of it.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the ambient and injected clock reads must be reported, and the clean builder and the "
                + "non-builder must stay silent. The fourth snippet is the one that keeps the rule "
                + "honest: it holds the same DateTimeOffset.UtcNow as the first, outside any prompt "
                + "builder, and a probe that ignored the interface would be red on the moment this "
                + "file was written. "
                + string.Join("; ", failures));

        // And the reads are quoted exactly: the ambient defect by its static, the
        // injected one by METHOD NAME (the field is spelled _timeProvider or
        // _clock or _time — a type-shaped matcher catches only the static nobody
        // injects, and the obvious "fix" would walk straight past the rule).
        List<ScanHit> ambient = ScanRunner.Collect(Rule, [(Rule.Controls[0].File, Rule.Controls[0].Source)]);
        List<ScanHit> injected = ScanRunner.Collect(Rule, [(Rule.Controls[1].File, Rule.Controls[1].Source)]);

        await Assert.That(string.Join(" | ", ambient.Select(static h => h.Text)))
            .Contains("DateTimeOffset.UtcNow")
            .Because("the first snippet IS the defect this rule exists for: a wall-clock read folded "
                   + "into the prompt assembly, so every turn of a session left open across 00:00 UTC "
                   + "keeps answering with the date the cache was filled at. Scanned: "
                   + (ambient.Count == 0 ? "(nothing)" : string.Join(" | ", ambient.Select(static h => h.Text))));

        await Assert.That(string.Join(" | ", injected.Select(static h => h.Text)))
            .Contains("clock.GetUtcNow")
            .Because("injecting a TimeProvider makes the impurity testable, not absent: the value still "
                   + "moves under an identical context, so the cache is still wrong. Scanned: "
                   + (injected.Count == 0 ? "(nothing)" : string.Join(" | ", injected.Select(static h => h.Text))));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: it is wired from the first
    ///     row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }
}
