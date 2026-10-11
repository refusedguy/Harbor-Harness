// PromptCacheKeyCoverageRules.cs — GUARD for issue #815.
//
// THE CONTRACT BEING ENFORCED
// ---------------------------
// `CachingSystemPromptBuilder` is a memoizing decorator: it answers a repeat
// context from a hash instead of calling the inner builder, and the only thing
// that makes that legal is that the hash covers everything the inner builder
// READS. A context member the renderer reads and the key does not hold is a
// stale prompt served with a cache hit and no log line — this repository's
// #792, where `ToolDescriptor.PromptGuidelines` rendered but did not key, and
// #815, where the environment section renders `ModelInfo.ProviderId` while the
// key held `AgentDefinition.ProviderId`.
//
// The contract is ASYMMETRIC, and the asymmetry is the whole point:
//   - UNDER-keying is a defect. It serves the previous turn's text.
//   - OVER-keying is admissible. It costs one rebuild.
// So this file asserts only the first direction mechanically (`rendered ⊆
// keyed`) and pins the second one to a DECLARED list (`keyed − rendered`), so
// "the key is more than the render" stays a maintained statement with a reason
// on each entry instead of an accident nobody wrote down.
//
// WHY A SOURCE SCAN AND NOT A BEHAVIOURAL TEST
// --------------------------------------------
// A behavioural test can only vary one field per test, so a coverage rule built
// out of them is an enumeration that has to be re-derived by hand every time a
// section is added — which is the property that already failed twice. The
// inventory is a property of two files' TEXT, so it is computed here. The
// behavioural half is not missing, it is one layer down: the stale-string case
// for the model provider lives in
// `Harbor.Application.Tests.CachingSystemPromptBuilderTests`.
//
// HOW A READ IS NAMED
// -------------------
// The renderer does not stop at the member it is reading: it writes
// `context.Tools.Count`, `context.Skills.OrderBy(…)`, `context.ContextFiles.Count`
// where the key writes `context.Tools` and stops. So a match is reduced to the
// context MEMBER it reaches — first segment, plus one more for `Agent`/`Model`,
// the two record-valued members — and both sides go through the same reduction.
// Without it the same member reads as three on one side and one on the other and
// the rule reports all three collections as gaps. (Calibrated against a red CI
// run, which is where that came from: the first version took matches verbatim
// and named every chain with its `context.` prefix still attached.)
//
// SCOPE — and it is total in-tree, which is why this pair and not a walk
// ----------------------------------------------------------------------
// The product has exactly one `ISystemPromptBuilder` implementation
// (`SystemPromptBuilder`), exactly one decorator (`CachingSystemPromptBuilder`),
// and exactly one site that wraps one in the other (`AgentLoop.cs:115`). So
// grading this PAIR is not grading a sample. If a second implementation ever
// appears, this file stops being a coverage claim and must be widened — the
// non-vacuity check below fails first, because the canonical files stop being
// the whole story only when someone adds one.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// A line-level regex over comment-stripped source; not a C# parser.
//
//   1. A context member reached through an intermediate local
//      (`var t = context.Tools[0]; … t.Foo`) is attributed to the collection it
//      came from (`Tools`), not to `Tools.Foo`. The keyed members of the three
//      collections — tool name/description/snippet/guidelines, skill
//      name/description/path, file path/content — are therefore covered by
//      BEHAVIOURAL tests, not by this rule. Read this file's "known limitation"
//      as naming them, not as waving them away.
//   2. Every `context.` chain in the file is collected, including the three in
//      the debug log call rather than in an `Append`. Over-collecting is the
//      safe direction: it can report a gap that does not change the prompt, it
//      cannot miss one that does.
//   3. Only chains rooted at a parameter literally named `context` are seen.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the two canonical files were really read and both sides
//      really produced chains, and a chain that IS keyed came back from the
//      render side, so a matcher that stopped matching cannot report "no gaps".
//   2. NonVacuity_Scan_DetectsAnUnkeyedContextMember — the POSITIVE CONTROL.
//      The real matcher is handed a synthetic pair whose renderer reads
//      `context.Model.ProviderId` and whose key omits it (must report exactly
//      that chain), and the same renderer against a key that DOES hold it (must
//      report nothing).

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One context member the renderer reads.</summary>
/// <param name="Chain">The context member, as named by <see cref="PromptCacheKeyProbe.Canonical" />: <c>Model.ProviderId</c>, <c>Tools</c>.</param>
/// <param name="Line">1-based line of the read.</param>
/// <param name="Text">The line, trimmed.</param>
internal sealed record RenderedContextMember(string Chain, int Line, string Text);

/// <summary>Everything one repository scan produced.</summary>
internal sealed record PromptKeyCoverageScan(
    string RenderFile,
    string KeyFile,
    IReadOnlyList<RenderedContextMember> Rendered,
    IReadOnlyList<string> Keyed,
    IReadOnlyList<string> Unkeyed,
    IReadOnlyList<string> OverKeyed);

/// <summary>Finds the context members a prompt renderer reads and the key holds.</summary>
internal static partial class PromptCacheKeyProbe
{
    /// <summary>The prompt renderer: the file whose context reads the key must cover.</summary>
    internal const string CanonicalRenderFile =
        "src/Harbor.Application/Sessions/SystemPromptBuilder.cs";

    /// <summary>The decorator that derives the key.</summary>
    internal const string CanonicalKeyFile =
        "src/Harbor.Application/Sessions/CachingSystemPromptBuilder.cs";

    /// <summary>
    ///     Reads both canonical files and reports the coverage delta.
    /// </summary>
    ///     <remarks>
    ///         A missing checkout reports empty sets rather than throwing, so the rule
    ///         above fails on "no gaps" being unfalsifiable only if the non-vacuity
    ///         check is also read — which is why it exists.
    ///     </remarks>
    internal static PromptKeyCoverageScan Scan(string? repoRoot)
    {
        string[] rendered = ReadLines(repoRoot, CanonicalRenderFile);
        string[] keyed = ReadLines(repoRoot, CanonicalKeyFile);

        var reads = new List<RenderedContextMember>();
        CollectRendered(rendered, reads);

        var keyPaths = new List<string>();
        CollectKeyed(keyed, keyPaths);

        var unkeyed = reads
            .Select(r => r.Chain)
            .Distinct(StringComparer.Ordinal)
            .Where(chain => !keyPaths.Contains(chain, StringComparer.Ordinal))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var overKeyed = keyPaths
            .Where(chain => !reads.Any(r => string.Equals(r.Chain, chain, StringComparison.Ordinal)))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        return new PromptKeyCoverageScan(
            CanonicalRenderFile,
            CanonicalKeyFile,
            reads,
            keyPaths.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList(),
            unkeyed,
            overKeyed);
    }

    /// <summary>Reads a repo-relative file, or nothing when the root/file is absent.</summary>
    private static string[] ReadLines(string? repoRoot, string relative)
    {
        if (repoRoot is null)
        {
            return [];
        }

        string absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            return File.Exists(absolute) ? File.ReadAllLines(absolute) : [];
        }
        catch (IOException)
        {
            // Same trade as PromptSectionPolicyRule: a locked or mid-write file reads
            // as "nothing found", and the liveness check is what turns that into a
            // failure. Throwing here would report an IO problem as a coverage gap.
            return [];
        }
    }

    /// <summary>
    ///     Every <c>context.&lt;path&gt;</c> chain in the renderer, with the line it
    ///     was read on.
    /// </summary>
    internal static void CollectRendered(string[] lines, List<RenderedContextMember> into)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);
        for (int i = 0; i < clean.Length; i++)
        {
            foreach (Match match in ContextChain().Matches(clean[i]))
            {
                into.Add(new RenderedContextMember(
                    Canonical(match.Value),
                    i + 1,
                    clean[i].Trim()));
            }
        }
    }

    /// <summary>
    ///     Every <c>context.&lt;path&gt;</c> chain in the KEY file.
    /// </summary>
    ///     <remarks>
    ///         <para>
    ///             The whole file, not the <c>AppendField</c> call sites. Scoping to the
    ///             call would be the tidier claim, and it is wrong here: the key binds the
    ///             three collections to locals first
    ///             (<c>var tools = context.Tools;</c>) and then walks
    ///             <c>tools[i].Name</c>, so <c>Tools</c>/<c>Skills</c>/<c>ContextFiles</c>
    ///             appear ONLY at that binding and a call-site scan would report all
    ///             three as gaps.
    ///         </para>
    ///         <para>
    ///             The file is the right scope because it holds one method,
    ///             <c>ComputeKey</c>, which is the entire key derivation — every
    ///             <c>context.</c> chain in the file feeds the hash. (Counted by hand at
    ///             the time of writing: 14 chains, all inside <c>ComputeKey</c>; if a
    ///             future edit reads the context somewhere else, that method stops being
    ///             the whole story and this scan has to be narrowed rather than trusted.)
    ///         </para>
    ///     </remarks>
    internal static void CollectKeyed(string[] lines, List<string> into)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);
        for (int i = 0; i < clean.Length; i++)
        {
            foreach (Match match in ContextChain().Matches(clean[i]))
            {
                into.Add(Canonical(match.Value));
            }
        }
    }

    /// <summary>
    ///     Names the CONTEXT MEMBER a raw match reaches.
    /// </summary>
    /// <remarks>
    ///     The raw text reaches past the member it is about: the renderer writes
    ///     <c>context.Tools.Count</c> and <c>context.Skills.OrderBy(…)</c> where the
    ///     key writes <c>context.Tools</c> and stops, so a verbatim comparison makes
    ///     one member look like three on one side and one on the other — and
    ///     reports all three collections as gaps. The first segment names the
    ///     member; <c>Agent</c> and <c>Model</c>, the two record-valued members,
    ///     carry one more. Two segments also drops the trailing <c>.Value</c> of a
    ///     strongly-typed id. Both sides go through THIS function, which is the
    ///     point: canonicalising one side only would report every member as a gap.
    /// </remarks>
    internal static string Canonical(string rawChain)
    {
        string[] parts = rawChain.Split('.');
        if (parts.Length < 2)
        {
            return rawChain;
        }

        string root = parts[1];
        return root is "Agent" or "Model" && parts.Length >= 3
            ? root + "." + parts[2]
            : root;
    }

    /// <summary>A dotted chain rooted at a parameter literally named <c>context</c>.</summary>
    [GeneratedRegex(@"\bcontext(?:\.[A-Za-z_][A-Za-z0-9_]*)+")]
    private static partial Regex ContextChain();

    }

/// <summary>
///     Guard: every <see cref="SystemPromptContext" /> member the prompt renderer
///     reads is held by the cache key, and every member the key holds beyond the
///     render is a declared one.
/// </summary>
public sealed class PromptCacheKeyCoverageRules
{
    private static readonly Lazy<PromptKeyCoverageScan> Report = new(
        () => PromptCacheKeyProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     The key's surplus over the render, declared with a reason each.
    /// </summary>
    ///     <remarks>
    ///         <para>
    ///             Seven, not eight. The eighth <c>AppendField</c> in the agent block is
    ///             <c>SystemPromptAppend</c>, which the renderer DOES read
    ///             (<c>SystemPromptBuilder</c>, "## Additional Instructions") — an
    ///             off-by-one in the original #815 report, which said "eight
    ///             members the builder never reads" and then listed seven.
    ///         </para>
    ///         <para>
    ///             Over-keying, so none of the seven can serve a stale prompt: the worst
    ///             any of them can do is rebuild a prompt whose text is unchanged. They
    ///             are pinned here for two other reasons. (a) An inventory that lives
    ///             only in a review comment is an inventory that rots; this list is
    ///             compared against the code on every gate run. (b) Two of them —
    ///             <c>Agent.ProviderId</c> and <c>Agent.Model</c> — sit exactly where
    ///             the model's own identity belongs in the key and are equal to it today
    ///             only by an unexpressed convention (see the class doc of
    ///             <c>CachingSystemPromptBuilder</c>). That is what made #815 look
    ///             covered on a read: the block reads as "provider + model" while it is
    ///             really "the agent's provider + the model's id".
    ///         </para>
    ///     </remarks>
    private static readonly FrozenSet<string> DeclaredSurplus = new[]
    {
        // Runs apart in the TUI and in `task` sub-agents; separating their entries
        // is worth one rebuild, and nothing else depends on the field.
        "Agent.Name",
        // Shown in the status bar, never in the prompt. Static per process.
        "Agent.DisplayName",
        // Shown in `/agents`, never in the prompt. Static per process.
        "Agent.Description",
        // Re-pointed by `/model` mid-session. Over-keying: a rebuild, never a stale hit.
        "Agent.Model",
        // THE CAMOUFLAGE (#815). The environment section renders the MODEL's
        // provider; this holds the AGENT's. Equal today because every catalog
        // stamps its own registry id, so it looked covered.
        "Agent.ProviderId",
        // Goes into `LlmRequest`, not the prompt.
        "Agent.Temperature",
        // Goes into `LlmRequest`, not the prompt.
        "Agent.ReasoningEffort",
    }.ToFrozenSet(StringComparer.Ordinal);

    // =====================================================================
    // 1. The rule — under-keying.
    // =====================================================================

    /// <summary>
    ///     No context member that the renderer reads is missing from the cache key.
    /// </summary>
    [Test]
    public async Task EveryRenderedContextMember_IsHeldByTheCacheKey()
    {
        var gaps = Report.Value.Unkeyed
            .Select(c =>
            {
                RenderedContextMember? read = Report.Value.Rendered
                    .FirstOrDefault(r => string.Equals(r.Chain, c, StringComparison.Ordinal));

                return read is null
                    ? c
                    : $"{c} ({PromptCacheKeyProbe.CanonicalRenderFile}:{read.Line}) — {read.Text}";
            })
            .ToList();

        await Assert.That(gaps).IsEmpty()
            .Because(
                "the cache serves a repeat context WITHOUT calling the inner builder, so the hash is "
                + "the only thing standing between a hit and the previous turn's text. A context member "
                + "the renderer reads and the key does not hold is exactly that failure: a hit, no miss "
                + "count, no log line, and the model reads a prompt describing something else. #792 "
                + "(PromptGuidelines) and #815 (Model.ProviderId, where the key held the AGENT's provider "
                + "instead) are both this. Over-keying is the admissible direction — it costs one "
                + "rebuild — so add the field to `ComputeKey`, do not remove the read. Gaps: "
                + (gaps.Count == 0 ? "(none)" : string.Join("\n  ", gaps)));
    }

    // =====================================================================
    // 2. The rule — over-keying, declared.
    // =====================================================================

    /// <summary>
    ///     Every context member the key holds that the renderer does not read is a
    ///     declared one, with a reason on the declaration.
    /// </summary>
    [Test]
    public async Task EveryOverKeyedContextMember_IsDeclaredAsSurplus()
    {
        var undeclared = Report.Value.OverKeyed
            .Where(c => !DeclaredSurplus.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var vanished = DeclaredSurplus
            .Where(c => !Report.Value.OverKeyed.Contains(c, StringComparer.Ordinal))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        await Assert.That(undeclared).IsEmpty()
            .Because(
                "a member the key holds and the renderer never reads is over-keying, which is legal — "
                + "it costs one rebuild, never a stale hit — but only when it is a DELIBERATE choice. "
                + "An undeclared one is how this key became unreadable: `Agent.ProviderId` sat exactly "
                + "where the model's provider belongs, equal to it today by convention alone, and read "
                + "as coverage. Add it to `DeclaredSurplus` with its reason, or take it out of the key. "
                + "Undeclared: " + (undeclared.Count == 0 ? "(none)" : string.Join(", ", undeclared)));

        await Assert.That(vanished).IsEmpty()
            .Because(
                "a declared surplus member that the key no longer holds is a stale entry in the "
                + "declaration — the inventory stops describing the code, which is the failure mode this "
                + "file exists to stop. Drop the line. No longer in the key: "
                + (vanished.Count == 0 ? "(none)" : string.Join(", ", vanished)));
    }

    // =====================================================================
    // 3. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really read both files, and both sides really produced chains.
    /// </summary>
    ///     <remarks>
    ///     Without this a matcher that stopped matching reports an empty gap list and
    ///     rule 1 is satisfied by having found nothing.
    /// </remarks>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan reads two files out of a checkout; without one it reports zero gaps and "
                   + "rule 1 is satisfied by having nothing to look at");

        PromptKeyCoverageScan report = Report.Value;

        await Assert.That(report.Rendered.Count).IsGreaterThan(0)
            .Because("no context member was read out of " + PromptCacheKeyProbe.CanonicalRenderFile
                   + ", so the rule above is green because the render side is empty, not because the "
                   + "key covers it");

        await Assert.That(report.Keyed.Count).IsGreaterThan(0)
            .Because("no context member was read out of " + PromptCacheKeyProbe.CanonicalKeyFile
                   + ", so every gap rule 1 could report is vacuous");

        // A member the key DOES hold, coming back from the RENDER side: proof that
        // the render scan finds members which are then matched against the key,
        // not just members which happen to be missing from it.
        await Assert.That(string.Join(" | ", report.Rendered.Select(r => r.Chain)))
            .Contains("Model.Id")
            .Because(
                "'- Model: <provider>/<id>' is rendered from `context.Model.Id` and `context.Model` IS "
                + "in the key, so this chain must come back from the render scan. If it does not, the "
                + "render scan is not reading the file the way rule 1 assumes. Rendered: "
                + (report.Rendered.Count == 0 ? "(nothing)" : string.Join(" | ", report.Rendered.Select(r => r.Chain))));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The real matcher is handed a synthetic pair whose
    ///     renderer reads a context member the key omits — it MUST report exactly
    ///     that member — and the same renderer against a key that holds it, which it
    ///     MUST NOT report.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsAnUnkeyedContextMember()
    {
        const string render = """
            public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
            {
                builder.Append("- Model: ").Append(context.Model.ProviderId).Append('/').Append(context.Model.Id);
                builder.Append("- cwd: ").Append(context.WorkingDirectory);
                return Task.FromResult(builder.ToString());
            }
            """;

        // The key as #815 shipped it: the AGENT's provider, the MODEL's id. The
        // renderer in the snippet above reads the model's provider, so this pair is
        // the defect, and the probe must name exactly that one chain.
        const string keyMissingProvider = """
            private static string ComputeKey(SystemPromptContext context)
            {
                var sb = new StringBuilder(512);
                AppendField(sb, context.Agent.ProviderId);
                AppendField(sb, context.Model.Id);
                AppendField(sb, context.WorkingDirectory);
                return sb.ToString();
            }
            """;

        const string keyComplete = """
            private static string ComputeKey(SystemPromptContext context)
            {
                var sb = new StringBuilder(512);
                AppendField(sb, context.Agent.ProviderId);
                AppendField(sb, context.Model.ProviderId);
                AppendField(sb, context.Model.Id);
                AppendField(sb, context.WorkingDirectory);
                return sb.ToString();
            }
            """;

        var missingReads = new List<RenderedContextMember>();
        PromptCacheKeyProbe.CollectRendered(render.Split('\n'), missingReads);
        var missingKeyed = new List<string>();
        PromptCacheKeyProbe.CollectKeyed(keyMissingProvider.Split('\n'), missingKeyed);

        await Assert.That(missingReads.Select(r => r.Chain).Distinct().ToList())
            .IsEquivalentTo(new[] { "Model.ProviderId", "Model.Id", "WorkingDirectory" })
            .Because(
                "the control snippet must hand the matcher all three chains. A miss means the "
                + "`context.` chain matcher stopped matching, and every assertion below is then "
                + "passing on an empty read. Read: "
                + (missingReads.Count == 0 ? "(nothing)" : string.Join(" | ", missingReads.Select(r => r.Chain))));

        await Assert.That(missingReads.First(r => r.Chain == "Model.ProviderId").Text)
            .Contains("context.Model.ProviderId")
            .Because(
                "the report must carry the offending LINE so a failure names somewhere the reader can "
                + "act; the whole value of a source guard is the location it hands back");

        var gaps = missingReads
            .Select(r => r.Chain)
            .Distinct(StringComparer.Ordinal)
            .Where(c => !missingKeyed.Contains(c, StringComparer.Ordinal))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        await Assert.That(gaps).IsEquivalentTo(new[] { "Model.ProviderId" })
            .Because(
                "this pair IS issue #815: the renderer reads the model's provider and the key holds the "
                + "agent's. The probe must report that one chain and only that one — the agent's "
                + "provider is over-keying (harmless) and must NOT appear here. Reported: "
                + (gaps.Count == 0 ? "(nothing)" : string.Join(" | ", gaps)));

        var completeReads = new List<RenderedContextMember>();
        PromptCacheKeyProbe.CollectRendered(render.Split('\n'), completeReads);
        var completeKeyed = new List<string>();
        PromptCacheKeyProbe.CollectKeyed(keyComplete.Split('\n'), completeKeyed);

        var stillGapped = completeReads
            .Select(r => r.Chain)
            .Distinct(StringComparer.Ordinal)
            .Where(c => !completeKeyed.Contains(c, StringComparer.Ordinal))
            .ToList();

        await Assert.That(stillGapped).IsEmpty()
            .Because(
                "the second pair is the shape the rule REQUIRES — same renderer, key now holding the "
                + "member it reads. Rule 1 must not fire on it, or the rule is unsatisfiable. Reported: "
                + (stillGapped.Count == 0 ? "(nothing)" : string.Join(" | ", stillGapped)));
    }
}