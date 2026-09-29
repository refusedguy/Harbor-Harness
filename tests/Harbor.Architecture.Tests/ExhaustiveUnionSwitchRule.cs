// ExhaustiveUnionSwitchRule.cs — GUARD #3 for docs/PATTERNS.md §"Type unions:
// a switch is a compile error when the union grows" (issue #578).
//
// THE CONVENTION BEING ENFORCED
// -----------------------------
// From #578, quoted:
//
//   1. A `switch` over a sealed type union has no `default`/wildcard arm. Every
//      arm is named. Adding a member to the union is then a compile error at
//      every site, which is the point.
//   2. Where a default is genuinely required (forward compatibility with input
//      from outside the process), it must NOT be silent: it logs, and it
//      increments an observable counter. A default that neither logs nor counts
//      is a bug with a comment.
//   3. A hand-maintained list of names is a union and gets the same treatment.
//   4. Cover the union with a test driven by REFLECTION, so the compiler's
//      exhaustiveness is backed by a runtime check. A hand-written list of test
//      cases is NOT this — it ages exactly like the copy it replaced.
//
// WHY A SOURCE SCAN AND NOT A COMPILER PLUGIN
// --------------------------------------------
// #578 asks for a wildcard-arm rule over a Harbor-owned union, and for the
// per-union reflection test "added as each union is touched by a refactor".
// This file does the FIRST, for the three unions #578 names by example, and
// anchors it with a reflection census — so the answer to "is the union
// exhausted?" comes from the type system, not from a list a human typed.
//
// The scan is text-based because the question ("does this switch have a
// `_ =>` / `default:` arm?") is not expressible against compiled metadata
// without a full control-flow analysis, and a half-correct analysis is worse
// than an honest textual one: it would report exhaustiveness it had not
// verified. What the text scan DOES do is deliberately conservative — it
// requires a switch to name at least MIN_MEMBERS distinct members of a
// registered union before it is considered "a switch over that union" at all,
// so unrelated switches are never touched.
//
// THE BASELINE IS THE POINT
// -------------------------
// 21 sites in the tree currently carry a wildcard arm over one of these unions,
// and they are all REAL findings — #495 (two `AgentEvent -> HarborEvent`
// switches with `_ => null` silently dropping a new event type on one host),
// #556 (`ChatRole` label mapping written 4x, all four `_ =>` arms silently
// relabelling a new role), #567 (a new tool-call state rendered as `running`
// forever). They are listed, each with the issue that owns removing it, and the
// rule holds only if the current set is a SUBSET of the baseline: new wildcard
// arms are red on the spot, and fixing one is a two-line deletion from the
// table.
//
// #578 is explicit that this is the sequencing: "Add the guard in the same PR
// as the refactor of each union. Do NOT try to land one giant enforcement PR."
// A ratchet is what makes that sequencing possible.
//
// NON-VACUITY
// -----------
//   1. UnionCensus_IsLive — the reflection census of each registered union is
//      non-empty and contains the members #578 names. A union renamed out of
//      reach, or an assembly that failed to load, makes the census empty, and
//      an empty census would make every switch "exhaustive" for free.
//   2. NonVacuity_Scan_DetectsAWildcardArmInSyntheticSource — the POSITIVE
//      CONTROL, and it is a real one: the scanner is handed three synthetic
//      source snippets, one of which is a wildcard arm over a real union
//      member. It MUST report that one and MUST NOT report the two exhaustive
//      ones. A scanner that stopped matching is a rule that stopped working.
//   3. WildcardBaseline_IsLive — every baseline row must still correspond to a
//      wildcard arm the scanner really finds, so the table cannot rot into a
//      blanket permission.

using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
// Namespace, not folder: both types are declared in `Harbor.Abstractions.*`
// even though the FILES live under src/Harbor.Abstractions.Contracts/{Events,Models}/.
// Read off the `namespace` line of each file, not inferred from the path.
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.Architecture.Tests;

/// <summary>A Harbor-owned type union the exhaustiveness rule is scoped to.</summary>
/// <param name="Name">Display name, as used in failure text.</param>
/// <param name="Members">
///     Every member of the union, read by REFLECTION. A hand-written list here
///     would be exactly the failure #578 rule 4 forbids ("a hand-written list
///     ages exactly like the copy it replaced"), so this is always computed.
/// </param>
/// <param name="Source">How the members are derived, for the failure message.</param>
internal sealed record RegisteredUnion(string Name, IReadOnlySet<string> Members, string Source);

/// <summary>
///     Identifies one (file, union) pair. A <c>readonly record struct</c> rather
///     than a <c>ValueTuple</c> for two reasons: it gets structural equality for
///     free (so the baseline needs no <c>IEqualityComparer</c>, which a value
///     tuple key cannot take from <c>StringComparer</c>), and its element names
///     survive into the failure messages.
/// </summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="UnionName">Name of the registered union.</param>
internal readonly record struct SiteKey(string File, string UnionName);

/// <summary>One switch found over a registered union.</summary>
/// <param name="File">Repo-relative path.</param>
/// <param name="Line">1-based line of the <c>switch</c> keyword.</param>
/// <param name="UnionName">The union the arms name.</param>
/// <param name="CoveredMembers">Union members named by the arms.</param>
/// <param name="HasWildcardArm">Whether the arm block contains <c>_ =&gt;</c> or <c>default:</c>.</param>
internal sealed record SwitchSite(
    string File,
    int Line,
    string UnionName,
    IReadOnlyList<string> CoveredMembers,
    bool HasWildcardArm);

/// <summary>
///     Reads the three unions #578 names by example, by reflection, and scans
///     the repository for switches over them.
/// </summary>
internal static partial class UnionExhaustivenessProbe
{
    /// <summary>
    ///     A switch must name at least this many distinct union members before
    ///     it counts as "a switch over that union". Without the floor, a method
    ///     that happens to test two events in an unrelated <c>if</c> chain
    ///     would be graded for exhaustiveness it was never claiming.
    /// </summary>
    internal const int MinMembers = 2;

    /// <summary>Repository roots the source scan walks.</summary>
    private static readonly string[] ScanRoots = ["src", "apps", "contrib/tui", "contrib/apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", "external", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The registered unions, built by reflection.
    /// </summary>
    /// <remarks>
    ///     <b>Why these three.</b> #578 names <c>AgentEvent -&gt; HarborEvent</c>
    ///     (#495), the <c>ChatRole</c> label mapping (#556) and the tool-call
    ///     lifecycle (#567). AgentEvent and LlmEvent are the two record unions
    ///     the whole event pipeline is built on; ChatRole is the one enum whose
    ///     mapping is duplicated four times.
    /// </remarks>
    public static IReadOnlyList<RegisteredUnion> Unions()
    {
        var agentEvent = ReadJsonDerivedTypes(typeof(AgentEvent));
        var llmEvent = ReadJsonDerivedTypes(typeof(LlmEvent));
        var chatRole = Enum.GetNames<ChatRole>().ToHashSet(StringComparer.Ordinal);

        return
        [
            new("AgentEvent", agentEvent, "Harbor.Abstractions.Events.AgentEvent "
                + "(src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs), "
                + "[JsonDerivedType] attributes read by reflection"),
            new("LlmEvent", llmEvent, "Harbor.Abstractions.Events.LlmEvent "
                + "(src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs), "
                + "[JsonDerivedType] attributes read by reflection"),
            new("ChatRole", chatRole, "Harbor.Abstractions.Models.ChatRole "
                + "(src/Harbor.Abstractions.Contracts/Models/ChatLine.cs), "
                + "Enum.GetNames read by reflection"),
        ];
    }

    /// <summary>
    ///     Reads the closed member set off a polymorphic base record.
    ///     <c>[JsonDerivedType]</c> is the union's own declaration of its
    ///     members — the hand-maintained list #578 rule 3 is about — so reading
    ///     it is both the census and a consistency check on that list. The
    ///     attribute's <c>DerivedType</c> property is public, so no
    ///     string-keyed reflection is needed.
    /// </summary>
    private static IReadOnlySet<string> ReadJsonDerivedTypes(Type baseType)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonDerivedTypeAttribute attribute in baseType
                     .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false))
        {
            members.Add(attribute.DerivedType.Name);
        }

        return members;
    }

    /// <summary>
    ///     Scans the repository for switches over the registered unions.
    ///     Returns an empty list when there is no checkout — the caller
    ///     reports that as a failure, never as "nothing to check".
    /// </summary>
    public static IReadOnlyList<SwitchSite> Scan(IReadOnlyList<RegisteredUnion> unions, string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return [];
        }

        var sites = new List<SwitchSite>();
        foreach (string file in EnumerateSources(repoRoot))
        {
            string relative = MakeRelative(repoRoot, file);
            sites.AddRange(Scan(unions, repoRoot, relative));
        }

        return sites;
    }

    /// <summary>
    ///     Scans ONE repo-relative source file. This is the production scan
    ///     path — the positive control calls it directly with a synthetic file,
    ///     so "the scanner can fail" is a statement about the real matcher and
    ///     not about a reimplementation of it.
    /// </summary>
    public static IReadOnlyList<SwitchSite> Scan(
        IReadOnlyList<RegisteredUnion> unions,
        string root,
        string relativeFile)
    {
        string file = Path.Combine(root, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            return [];
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            return [];
        }

        var allMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (RegisteredUnion union in unions)
        {
            allMembers.UnionWith(union.Members);
        }

        var sites = new List<SwitchSite>();
        ScanFile(relativeFile, lines, unions, allMembers, sites);
        return sites;
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (string root in ScanRoots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            IEnumerable<string> found = Directory.EnumerateFiles(
                absolute, "*.cs", SearchOption.AllDirectories);

            foreach (string path in found)
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

    private static void ScanFile(
        string relativeFile,
        string[] lines,
        IReadOnlyList<RegisteredUnion> unions,
        HashSet<string> allMembers,
        List<SwitchSite> sites)
    {
        string[] clean = lines.Select(StripNonCode).ToArray();
        var depthBefore = new int[clean.Length];
        int depth = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            depthBefore[i] = depth;
            depth += clean[i].Count(c => c == '{') - clean[i].Count(c => c == '}');
        }

        for (int i = 0; i < clean.Length; i++)
        {
            if (!clean[i].Contains("switch", StringComparison.Ordinal))
            {
                continue;
            }

            int start = ArmBlockStart(clean, i);
            if (start < 0)
            {
                continue;
            }

            int blockDepth = depthBefore[start];
            int end = start + 1;
            while (end < clean.Length && depthBefore[end] >= blockDepth)
            {
                end++;
            }

            var covered = new HashSet<string>(StringComparer.Ordinal);
            bool wildcard = false;
            for (int k = start; k < end; k++)
            {
                string line = clean[k].Trim();
                if (TryReadArmType(line, out string? armType) && allMembers.Contains(armType))
                {
                    covered.Add(armType);
                }

                if (IsWildcardArm(line))
                {
                    wildcard = true;
                }
            }

            if (covered.Count < MinMembers)
            {
                continue;
            }

            foreach (RegisteredUnion union in unions)
            {
                var hit = covered.Where(union.Members.Contains).OrderBy(m => m, StringComparer.Ordinal).ToList();
                if (hit.Count < MinMembers)
                {
                    continue;
                }

                sites.Add(new SwitchSite(relativeFile, i + 1, union.Name, hit, wildcard));
            }
        }
    }

    /// <summary>
    ///     Index of the line whose brace opens the arm block — the
    ///     <c>switch</c> line itself when it carries the brace (the statement
    ///     form), otherwise the next few lines (the expression form, where the
    ///     brace follows the pattern).
    /// </summary>
    private static int ArmBlockStart(string[] clean, int switchLine)
    {
        if (clean[switchLine].Contains('{'))
        {
            return switchLine;
        }

        for (int k = switchLine; k < Math.Min(switchLine + 5, clean.Length); k++)
        {
            if (clean[k].Contains('{'))
            {
                return k;
            }
        }

        return -1;
    }

    private static bool IsWildcardArm(string line) =>
        line.StartsWith("_ =>", StringComparison.Ordinal)
        || line.StartsWith("default =>", StringComparison.Ordinal)
        || line.StartsWith("default:", StringComparison.Ordinal);

    /// <summary>
    ///     Reads the union member an arm names, from either C# switch form:
    ///     <c>case AgentStartEvent started:</c> or <c>AgentStartEvent =&gt; …</c>.
    ///     Qualified patterns (<c>Harbor.Events.AgentStartEvent</c>) are reduced
    ///     to the leaf name.
    /// </summary>
    private static bool TryReadArmType(string line, out string member)
    {
        member = string.Empty;

        Match caseMatch = CaseArm().Match(line);
        if (caseMatch.Success)
        {
            member = caseMatch.Groups[1].Value;
            return true;
        }

        Match exprMatch = ExpressionArm().Match(line);
        if (exprMatch.Success)
        {
            // The arm pattern may be written `Namespace.Type =>` or
            // `Namespace . Type =>`; both reduce to the leaf member name.
            string qualified = exprMatch.Groups[1].Value.Replace(" ", string.Empty);
            member = qualified[(qualified.LastIndexOf('.') + 1)..];
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"\bcase\s+([A-Za-z_]\w*)")]
    private static partial Regex CaseArm();

    [GeneratedRegex(@"^([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)(?:\s+[\w.]+)?\s*=>")]
    private static partial Regex ExpressionArm();

    private static string MakeRelative(string repoRoot, string path)
    {
        string relative = Path.GetRelativePath(repoRoot, path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Lexical states for <see cref="StripNonCode" />.</summary>
    private enum StripState
    {
        /// <summary>Ordinary code.</summary>
        Code,

        /// <summary>Inside a "…".</summary>
        String,

        /// <summary>Inside a @"…", where "" escapes a quote.</summary>
        VerbatimString,

        /// <summary>Inside a '…'.</summary>
        Char,

        /// <summary>Inside a /* … */ block.</summary>
        BlockComment,
    }

    /// <summary>
    ///     Blanks out comments, string and char literals so brace counting and
    ///     arm matching cannot be fooled by a <c>switch</c> inside a doc
    ///     comment or a <c>"{ // 3</c> inside a string.
    /// </summary>
    private static string StripNonCode(string line)
    {
        var output = new StringBuilder(line.Length);
        StripState state = StripState.Code;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            switch (state)
            {
                case StripState.Code:
                    if (c == '/' && next == '/')
                    {
                        return output.ToString();
                    }

                    if (c == '/' && next == '*')
                    {
                        state = StripState.BlockComment;
                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        // "" is an empty verbatim string that opens and closes
                        // on this line; " is its opening quote.
                        state = next == '"' ? StripState.VerbatimString : StripState.String;
                        continue;
                    }

                    if (c == '@' && next == '"')
                    {
                        state = StripState.VerbatimString;
                        i++;
                        continue;
                    }

                    if (c == '\'')
                    {
                        state = StripState.Char;
                        continue;
                    }

                    output.Append(c);
                    continue;

                case StripState.String:
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = StripState.Code;
                    }

                    continue;

                case StripState.VerbatimString:
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            i++;
                        }
                        else
                        {
                            state = StripState.Code;
                        }
                    }

                    continue;

                case StripState.Char:
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '\'')
                    {
                        state = StripState.Code;
                    }

                    continue;

                case StripState.BlockComment:
                    if (c == '*' && next == '/')
                    {
                        state = StripState.Code;
                        i++;
                    }

                    continue;

                default:
                    throw new InvalidOperationException($"[union-probe] unknown strip state {state}.");
            }
        }

        return output.ToString();
    }
}

/// <summary>
///     Guard for docs/PATTERNS.md §"Type unions": a <c>switch</c> over a
///     Harbor-owned union carries no wildcard arm, and the set of sites that
///     still carry one is a ratchet that may shrink but not grow.
/// </summary>
public sealed class ExhaustiveUnionSwitchRule
{
    private static readonly Lazy<IReadOnlyList<RegisteredUnion>> Registry = new(UnionExhaustivenessProbe.Unions);

    private static readonly Lazy<IReadOnlyList<SwitchSite>> Sites = new(() =>
        UnionExhaustivenessProbe.Scan(Registry.Value, RepoPaths.RepoRoot));

    /// <summary>
    ///     Every site that currently carries a wildcard arm over a registered
    ///     union, each with the issue that owns removing it. A row is a CLAIM
    ///     that the drift is real; <see cref="WildcardBaseline_IsLive" />
    ///     verifies it.
    /// </summary>
    private static readonly Dictionary<SiteKey, string> WildcardBaseline = new()
    {
            // #495 — two AgentEvent -> HarborEvent projections whose `_ => null`
            // silently dropped a new event type on one host. The wire union is
            // intentionally narrower than AgentEvent, so the arm is *justified*
            // here; what is missing is the log + counter #578 rule 2 demands.
            [new("src/Harbor.Ipc.InProcess/InProcessHarborClient.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/495",
            [new("src/Harbor.Ipc.InProcess/InProcessHarborClient.cs", "LlmEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/495",
            [new("src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/495",
            [new("src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs", "LlmEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/495",

            // #578 rule 1, the reducers. The Store + reducer half of the
            // convention: every arm named, no default inventing an answer.
            [new("src/Harbor.Ui.Framework.State/State/ChatAppReducer.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/578",
            [new("src/Harbor.Ui.Framework.State/State/ChatAppReducer.cs", "LlmEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/578",
            [new("src/Harbor.Ui.Framework.Reducers/AppReducer.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/578",
            [new("src/Harbor.Ui.Framework.Reducers/AppReducer.cs", "LlmEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/578",
            [new("src/Harbor.Ui.Framework.Reducers/ChatViewReducer.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/578",
            [new("src/Harbor.Ui.Framework.Reducers/SessionsReducer.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/578",

            // #556 — `ChatRole -> (label, markdown?)` written four times, all
            // four `_ =>` arms silently relabelling a new role.
            [new("src/Harbor.Ui.Framework.ViewModels/ViewModels/ChatLineViewModel.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("src/Harbor.Ui.Framework.State/ToolCallKey.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("src/Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("src/Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("src/Harbor.Storage.Jsonl/JsonlLineParser.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("contrib/tui/Harbor.Tui.SpectreTui/View/ChatMessageFormatter.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("contrib/tui/Harbor.Tui.SpectreTui/View/ChatMarkup.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("contrib/tui/Harbor.Tui.TerminalGui/Rendering/TerminalGuiColorMapper.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("contrib/tui/Harbor.Tui.Termina/Rendering/TerminaColorMapper.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",
            [new("contrib/tui/Harbor.Tui.RazorConsole/Rendering/RazorColorMapper.cs", "ChatRole")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/556",

            // #575 — the canonical renderer. ChatScreenBridge's 18-arm switch
            // has no wildcard, which is why the drift there was INVISIBLE:
            // CompactionFailedEvent simply fell through and did nothing.
            [new("apps/Harbor.App.Avalonia/Hosting/UiEventRouter.cs", "AgentEvent")] =
                "https://github.com/refusedguy/Harbor-Harness/issues/575",
        };

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     A <c>switch</c> over a Harbor-owned union carries no wildcard arm.
    ///     The set of sites that still carry one must be a SUBSET of the
    ///     baseline: a new wildcard arm is red on the spot, and fixing an old
    ///     one only ever removes a row.
    /// </summary>
    [Test]
    public async Task SwitchOverHarborUnion_HasNoWildcardArm()
    {
        var failures = new List<string>();

        foreach (SwitchSite site in Sites.Value.Where(s => s.HasWildcardArm))
        {
            if (WildcardBaseline.ContainsKey(new SiteKey(site.File, site.UnionName)))
            {
                continue;
            }

            failures.Add(
                $"{site.File}:{site.Line} — a `switch` over {site.UnionName} has a wildcard arm "
                + $"(`_ =>` / `default:`) covering {string.Join(", ", site.CoveredMembers)}. "
                + "docs/PATTERNS.md §'Type unions': every arm is named, so adding a member to the "
                + "union is a compile error at every site. That is the point. If this default is "
                + "genuinely required for forward compatibility with input from outside the process, "
                + "it must log AND increment an observable counter — a default that neither logs nor "
                + "counts is a bug with a comment. Otherwise add a baseline row naming the issue "
                + "that owns fixing it, and do not widen an existing row.");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     Liveness on the union census. Every registered union must be
    ///     non-empty AND must contain the members #578 names. An empty census
    ///     would make every switch in the tree "exhaustive" for free, which is
    ///     the same failure mode as a NetArchTest rule naming a non-existent
    ///     assembly.
    /// </summary>
    [Test]
    public async Task UnionCensus_IsLive()
    {
        IReadOnlyList<RegisteredUnion> unions = Registry.Value;
        var failures = new List<string>();

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["AgentEvent"] =
            [
                // #495's silent drop and #575's CompactionFailedEvent gap.
                "AgentStartEvent", "MessageUpdateEvent", "ToolExecutionStartEvent",
                "ToolExecutionEndEvent", "CompactionFailedEvent", "PluginBlockedEvent",
                "SessionChangedEvent", "AgentEndEvent",
            ],
            ["LlmEvent"] = ["TextDeltaEvent", "ThinkingDeltaEvent", "ToolCallStartEvent", "StepFinishEvent"],
            ["ChatRole"] = ["User", "Assistant", "Tool", "ToolResult", "System", "Error"],
        };

        // Checked BEFORE the per-union loop: a registered union whose name is not
        // in `expected` would otherwise reach `expected[union.Name]` and throw
        // KeyNotFoundException instead of reporting the drift.
        foreach (RegisteredUnion union in unions)
        {
            if (!expected.ContainsKey(union.Name))
            {
                failures.Add(
                    $"union '{union.Name}' is registered but the census check does not name it, so "
                    + "it would never be verified. Add it to `expected`.");
            }
        }

        foreach (RegisteredUnion union in unions)
        {
            if (union.Members.Count == 0)
            {
                failures.Add(
                    $"union '{union.Name}' has an EMPTY member census ({union.Source}). The scan "
                    + "grades switches against this set, so an empty set makes every switch in the "
                    + "tree look exhaustive.");
                continue;
            }

            if (!expected.TryGetValue(union.Name, out string[]? named))
            {
                continue; // already reported above
            }

            var missing = named
                .Where(member => !union.Members.Contains(member))
                .ToList();

            if (missing.Count > 0)
            {
                failures.Add(
                    $"union '{union.Name}' is missing {string.Join(", ", missing)} ({union.Source}). "
                    + "These are the members issues #495/#556/#575 name, so a census without them is "
                    + "not reading the real union.");
            }
        }

        await Assert.That(unions.Count).IsEqualTo(expected.Count)
            .Because("the rule is scoped to the three unions #578 names; a fourth entry that the "
                   + "baseline does not cover would be graded against nothing");

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The scanner is handed three synthetic snippets
    ///     and must report a wildcard arm in exactly one of them: the one that
    ///     names real union members AND has a <c>_ =&gt;</c> arm. The two
    ///     exhaustive snippets must not be reported. A scanner whose regexes
    ///     stopped matching would report nothing and this test would be red.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsAWildcardArmInSyntheticSource()
    {
        IReadOnlyList<RegisteredUnion> unions = Registry.Value;
        var allMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (RegisteredUnion union in unions)
        {
            allMembers.UnionWith(union.Members);
        }

        await Assert.That(allMembers.Contains("AgentStartEvent")).IsTrue()
            .Because("the synthetic snippets below name AgentStartEvent and MessageEndEvent; if the "
                   + "census does not contain them, the control proves nothing");
        await Assert.That(allMembers.Contains("MessageEndEvent")).IsTrue();

        var withWildcard = """
            private void Handle(AgentEvent evt)
            {
                switch (evt)
                {
                    case AgentStartEvent started:
                        break;
                    case MessageEndEvent ended:
                        break;
                    _ => null,
                }
            }
            """;

        var exhaustive = """
            private void Handle(AgentEvent evt)
            {
                switch (evt)
                {
                    case AgentStartEvent started:
                        break;
                    case MessageEndEvent ended:
                        break;
                }
            }
            """;

        var unrelated = """
            private void Handle(int value, string name)
            {
                switch (value)
                {
                    case 1:
                        return;
                    case 2:
                        return;
                    default:
                        return;
                }
            }
            """;

        var reported = new List<SwitchSite>();
        CollectSites("Synthetic.cs", withWildcard, unions, reported);
        CollectSites("Synthetic.cs", exhaustive, unions, reported);
        CollectSites("Synthetic.cs", unrelated, unions, reported);

        var wildcards = reported.Where(s => s.HasWildcardArm).ToList();
        var exhaustiveSites = reported.Where(s => !s.HasWildcardArm).ToList();

        await Assert.That(wildcards.Count).IsEqualTo(1)
            .Because("exactly one of the three synthetic snippets has a wildcard arm over real union "
                   + "members, and the scanner must find it. A miss means the arm matchers stopped "
                   + "working and the rule is enforcing nothing.");

        await Assert.That(wildcards[0].UnionName).IsEqualTo("AgentEvent")
            .Because("the snippet arms name AgentEvent members, so that is the union it switches over");

        await Assert.That(exhaustiveSites.Count).IsEqualTo(1)
            .Because("the exhaustive snippet names two union members with no wildcard arm and must be "
                   + "reported as a clean site; the unrelated int switch must not be reported at all");

        await Assert.That(exhaustiveSites[0].HasWildcardArm).IsFalse();
    }

    /// <summary>
    ///     Every baseline row must still correspond to a wildcard arm the scan
    ///     really finds, and every row must name a tracking issue. A row that
    ///     has been fixed is dead weight that grandfathers a name nobody can
    ///     find; a row without an owner is a debt with nobody watching it.
    /// </summary>
    [Test]
    public async Task WildcardBaseline_IsLive()
    {
        var real = Sites.Value
            .Where(s => s.HasWildcardArm)
            .Select(s => new SiteKey(s.File, s.UnionName))
            .ToHashSet();

        var unionNames = Registry.Value.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var (key, trackedBy) in WildcardBaseline)
        {
            if (!real.Contains(key))
            {
                failures.Add(
                    $"baseline row '{key.File}' / {key.UnionName} is stale — the scan finds no "
                    + $"wildcard arm there any more. Delete the row and close {trackedBy}.");
            }

            if (!trackedBy.Contains("https://github.com/", StringComparison.Ordinal))
            {
                failures.Add($"baseline row '{key.File}' / {key.UnionName} has no tracking issue URL "
                    + $"(got '{trackedBy}')");
            }

            if (!unionNames.Contains(key.UnionName))
            {
                failures.Add($"baseline row '{key.File}' names union '{key.UnionName}', which is not "
                    + "registered — the row grandfathers a site the rule never grades");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("A baseline row that no longer matches reality is a lie: it grandfathers a file "
                   + "under a key nobody checks, and lets a NEW wildcard arm hide behind it. "
                   + string.Join("\n", failures));
    }

    // =====================================================================
    // 3. Scan integrity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really found switches. Both
    ///     the file count and a specific known site are asserted, because a
    ///     silent "no checkout" (a trimmed test host) is exactly how a source
    ///     rule goes green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task Scan_FoundAKnownSite()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the source scan needs a repository checkout; without one it would report zero "
                   + "wildcard arms and the rule would be satisfied by having nothing to look at");

        IReadOnlyList<SwitchSite> sites = Sites.Value;

        await Assert.That(sites.Count).IsGreaterThan(0)
            .Because("the scan found no switches over any registered union; the arm matchers or the "
                   + "roots are wrong");

        // The site #575 is about, asserted by name: ChatScreenBridge's switch
        // over AgentEvent. It is the canonical renderer's 18-arm switch and it
        // carries no wildcard arm — which is precisely why the missing
        // CompactionFailedEvent arm was invisible.
        bool foundBridge = sites.Any(s =>
            s.File.EndsWith("ChatScreenBridge.cs", StringComparison.Ordinal)
            && s.UnionName == "AgentEvent");

        await Assert.That(foundBridge).IsTrue()
            .Because("the scan must find ChatScreenBridge's AgentEvent switch — the one #575 was "
                   + "filed against. If it cannot, the scan is not reaching src/Harbor.Tui.CellForge");
    }

    // =====================================================================
    // 4. Plumbing.
    // =====================================================================

    /// <summary>
    ///     Runs the scanner over one in-memory snippet. Exposed so the positive
    ///     control can drive the REAL scanner rather than a reimplementation of
    ///     it, which is the only way "it can fail" means anything.
    /// </summary>
    private static void CollectSites(
        string virtualFile,
        string source,
        IReadOnlyList<RegisteredUnion> unions,
        List<SwitchSite> sink)
    {
        // A throwaway directory under the temp path, so the production scan
        // path (StripNonCode → brace depth → arm match) is the one exercised.
        string dir = Path.Combine(Path.GetTempPath(), "harbor-union-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, virtualFile), source);
            sink.AddRange(UnionExhaustivenessProbe.Scan(unions, dir, virtualFile));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leaked temp directory must never fail a build.
            }
        }
    }
}
