// SlashCommandRouterShapeRule.cs — GUARD for issue #565: the contracts layer
// must not declare a slash-command ROUTER again, and must not declare the
// `Result<bool>` shape whose negative case is a routing decision rather than a
// failure.
//
// THE DEFECT
// ----------
// `Harbor.Abstractions.Tui.ISlashCommandRouter` (ITuiRenderer.cs) declared
// four members and was implemented by nothing and called by nothing. Its
// `TryHandleAsync` returned `Task<Result<bool>>` where, per the interface's own
// doc, `false` means "this input was not a slash command" — not a failure, and
// not a value any caller ever read. `rg 'Success\(false\)' src/ apps/ contrib/`
// returned nothing, so the negative case had no producer at all.
//
// The consequence is not the dead type, it is the TRAP: it is the shape the
// next author copies, and copying it means shipping a second slash-command
// router that diverges from the wired one. That is the same drift class as
// #462 (five divergent command registries), #486 (a hand-built dispatcher
// bypassing the container) and #603 (a `Result` the dispatcher assigned and
// dropped) — this one is simply the registry nobody registered.
//
// WHY TWO RULES AND NOT ONE
// -------------------------
// A ban on the NAME alone is satisfied by a rename. `ISlashCommandRegistry`,
// `ISlashCommands`, `ICommandRouter` all defeat it while reproducing the defect
// exactly. So the shape is banned too, structurally and by reflection: an
// interface that declares a `TryHandleAsync` returning `Task<Result<bool>>`.
// That signature is unique in the whole tree today (one hit, the deleted line),
// so the shape rule has no false positives and can be applied to every loaded
// Harbor assembly rather than a hand-picked prefix list.
//
// WHAT IS DELIBERATELY *NOT* BANNED
// ---------------------------------
// `ISlashCommand` and `ICommandContext` are ALIVE — six CLI commands implement
// them (`PermissionsCommand`, `ModelCommand`, `ConfigCommand`, `AuthCommand`,
// `SetupCommand`, `AgentCommand`) plus `SimpleCommandContext` and the
// `CatalogSlashCommand` adapter. The router was the dead half, not the whole
// family. `TheDeletionWasSurgical_…` pins this so a future sweep cannot take
// the command contracts with it.
//
// And the router's CONCEPT is not gone, it is typed: `SlashCommandOutcome`
// (bool ShouldQuit, int ExitCode), added by #603, is what the REPL actually
// dispatches into. `TheTypedOutcomeThatReplacedTheRouter_IsPresent` pins that
// too, so this file reads as "the bool contract was replaced", not as "slash
// routing has no contract" — which is the accusation a bare ban invites.
//
// SCOPE, AND WHY THE DOCS ARE OUT OF IT
// -------------------------------------
// The source scan covers `src/`, `apps/` and `tests/` — code and the `.md`
// inventories next to it. It deliberately does NOT cover `docs/`: the specs
// under docs/specs/ are point-in-time design records, and 01-architecture.md
// describes the DI registration that was planned in 2025 and never built.
// Rewriting a spec to match today's source is the wrong direction; leaving one
// mention of a type that no longer exists is not a lie a reader can be harmed
// by, because the spec already describes a different codebase.
//
// NON-VACUITY
// -----------
// A ban that matches nothing is indistinguishable from a ban that is satisfied.
// Three things close that:
//
//   1. NonVacuity_TheAssemblyThatHeldTheRouter_IsStillBeingRead — names the
//      types declared in the very file the interface was deleted from. A
//      renamed namespace, a failed assembly load, or a typo'd prefix is red
//      here rather than vacuously green.
//   2. NonVacuity_TheShapeRule_FiresOnARealInterface — the positive control: a
//      private interface declared in THIS file with the exact banned signature.
//      It trips the SHAPE rule while carrying a different name, so the two rules
//      are proven to be independent. Two negative controls follow: `Task<bool>`
//      and a typed outcome must both be left alone, so the gate discriminates
//      instead of flagging every TryHandle in the tree.
//   3. The source scan asserts a file-count floor, and its single exclusion —
//      this file, which must name the banned identifier to test it — is matched
//      by name. Rename this file and the exclusion misses, the scan finds its
//      own comments, and the gate goes red. It fails closed.
//
// The assembly-level rules exclude THIS ASSEMBLY, which matters more than it
// looks: `LoadHarborAssemblies()` keys off `name.StartsWith("Harbor")`, and
// this project is called `Harbor.Architecture.Tests`, so without the exclusion
// the positive controls below would be flagged by the very rule they exist to
// test, and the gate would be red forever. The exclusion is by assembly
// IDENTITY (a reference comparison), not by a name that could be typo'd.
//
// System.Reflection is a global using in this project (GlobalUsings.cs), so
// Assembly / MethodInfo / BindingFlags / ReflectionTypeLoadException need no
// using here — and adding one would only duplicate the global.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Harbor.Architecture.Tests;

/// <summary>One type in a governed assembly and which ban, if any, it trips.</summary>
/// <param name="AssemblyName">Simple name of the assembly the type came from.</param>
/// <param name="TypeName">Full CLR name of the type.</param>
/// <param name="Verdict">Which of the two bans the type trips.</param>
internal readonly record struct GovernedType(string AssemblyName, string TypeName, RouterVerdict Verdict);

/// <summary>
///     Which ban a type trips. Both are checked, and a type can trip both, so a
///     failure message can say which rule fired rather than only that something
///     did.
/// </summary>
/// <param name="HasBannedName">The type is named the deleted router.</param>
/// <param name="HasBannedShape">
///     The type is an interface declaring a <c>TryHandleAsync</c> that returns
///     <c>Task&lt;Result&lt;bool&gt;&gt;</c> — the bool-that-is-a-routing-decision.
/// </param>
internal readonly record struct RouterVerdict(bool HasBannedName, bool HasBannedShape)
{
    /// <summary>True when either ban fires.</summary>
    internal bool IsBanned => HasBannedName || HasBannedShape;
}

/// <summary>
///     The classifier both rules go through, so neither can be satisfied by
///     weakening the other's half. Internal rather than private because the
///     positive control must call the SAME code the rules call.
/// </summary>
internal static class SlashRouterProbe
{
    /// <summary>The type name issue #565 deleted.</summary>
    internal const string BannedTypeName = "ISlashCommandRouter";

    /// <summary>The method name whose bool return was the defect.</summary>
    internal const string BannedMethodName = "TryHandleAsync";

    /// <summary>Classifies one type.</summary>
    /// <param name="type">The type to classify.</param>
    internal static RouterVerdict Classify(Type type)
    {
        bool bannedName = string.Equals(type.Name, BannedTypeName, StringComparison.Ordinal);

        bool bannedShape = false;
        if (type.IsInterface)
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!string.Equals(method.Name, BannedMethodName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReturnsTaskResultOfBool(method.ReturnType))
                {
                    bannedShape = true;
                    break;
                }
            }
        }

        return new RouterVerdict(bannedName, bannedShape);
    }

    /// <summary>
    ///     True for exactly <c>Task&lt;Result&lt;bool&gt;&gt;</c>. Decided from the
    ///     generic arguments rather than by the type's NAME, so a rename of
    ///     <c>Result&lt;T&gt;</c> cannot quietly turn the rule into a no-op.
    /// </summary>
    /// <param name="type">A method's return type.</param>
    internal static bool ReturnsTaskResultOfBool(Type type)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return false;
        }

        Type inner = type.GetGenericArguments()[0];
        return inner.IsGenericType
            && inner.GetGenericTypeDefinition() == typeof(Result<>)
            && inner.GetGenericArguments()[0] == typeof(bool);
    }

    /// <summary>
    ///     Types of an assembly, tolerating the ones that failed to load. The
    ///     liveness test is what stops "returned nothing" from reading as
    ///     "found no banned type".
    /// </summary>
    /// <param name="asm">Assembly to read.</param>
    internal static IReadOnlyList<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(static t => t is not null).Select(static t => t!)];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }
}

/// <summary>
///     Issue #565: <c>Harbor.Abstractions.Tui.ISlashCommandRouter</c> is gone, and
///     neither its name nor its <c>Result&lt;bool&gt;</c> routing shape is
///     allowed back into a contracts assembly.
/// </summary>
public sealed class SlashCommandRouterShapeRule
{
    /// <summary>
    ///     Trees the source scan walks. <c>src/</c> for the contract itself,
    ///     <c>apps/</c> for the composition root that would host a second
    ///     router, <c>tests/</c> for the tests that would pin it.
    /// </summary>
    private static readonly string[] ScannedTrees = ["src", "apps", "tests"];

    /// <summary>
    ///     This file is the one exclusion from the source scan, and it is
    ///     excluded by NAME on purpose. The file has to spell the banned
    ///     identifier to test for it, so it cannot be in its own scan; naming it
    ///     rather than filtering "any file that mentions it" means a rename of
    ///     this file makes the exclusion miss and the gate goes RED. Fail-closed.
    /// </summary>
    private const string SelfRelativePath = "tests/Harbor.Architecture.Tests/SlashCommandRouterShapeRule.cs";

    /// <summary>
    ///     Every type in every loaded Harbor assembly, EXCEPT this project's own.
    ///     The exclusion is by assembly identity — a reference comparison, so
    ///     there is no name to typo — because <c>LoadHarborAssemblies()</c>
    ///     accepts anything starting with <c>Harbor</c>, and this assembly is
    ///     <c>Harbor.Architecture.Tests</c>. Without it the positive controls
    ///     below are flagged by the very rule they exist to test.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<GovernedType>> Governed = new(Collect);

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     RULE 1 — the name, in IL. No loaded Harbor assembly declares a type
    ///     called <c>ISlashCommandRouter</c>.
    /// </summary>
    [Test]
    public async Task NoHarborAssembly_DeclaresTheBannedRouterName()
    {
        var violations = Governed.Value
            .Where(static t => t.Verdict.HasBannedName)
            .Select(static t => $"{t.AssemblyName}!{t.TypeName}")
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(violations).IsEmpty()
            .Because(
                "issue #565: this interface had zero implementors and zero consumers, and its "
                + "TryHandleAsync returned Result<bool> whose `false` meant \"not a slash command\" — "
                + "a routing decision, not a failure, and a value no caller read. It was the shape the "
                + "next author copies, and a second slash-command router is the same drift as #462 and "
                + "#486. If you need routing, dispatch into the CLI's real dispatcher or extend "
                + "SlashCommandOutcome; do not redeclare the contract. Offenders: "
                + string.Join(", ", violations));
    }

    /// <summary>
    ///     RULE 2 — the shape, in IL. No interface declares a
    ///     <c>TryHandleAsync</c> returning <c>Task&lt;Result&lt;bool&gt;&gt;</c>.
    ///     Catches the ban-defeating rename, and it is what keeps this file from
    ///     being a string match against one identifier.
    /// </summary>
    [Test]
    public async Task NoHarborInterface_DeclaresTheBoolRoutingShape()
    {
        var violations = Governed.Value
            .Where(static t => t.Verdict.HasBannedShape)
            .Select(static t => $"{t.AssemblyName}!{t.TypeName}")
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(violations).IsEmpty()
            .Because(
                "a `Result<bool>` whose negative case is \"this was not a command\" is a bool in a "
                + "Result-shaped coat: it is not a failure, and a caller that only wants to know whether "
                + "the input matched has been handed a channel it must unwrap to discover. That was "
                + "ISlashCommandRouter.TryHandleAsync, and `Result.Success(false)` had no producer "
                + "anywhere in the tree. Use a typed outcome (SlashCommandOutcome) or a plain bool. "
                + "Offenders: " + string.Join(", ", violations));
    }

    /// <summary>
    ///     RULE 3 — the name, in source rather than in IL. A resurrected
    ///     <c>&lt;see cref&gt;</c> in a doc comment, a README inventory row, or a
    ///     test that names the type is the advertisement that produces the second
    ///     router; the IL rules above cannot see any of it.
    /// </summary>
    [Test]
    public async Task NoSourceFile_MentionsTheBannedRouterName()
    {
        string root = RepoRootOrFail();
        var violations = new List<string>();
        int scanned = 0;

        foreach (string relative in EnumerateScannableFiles(root))
        {
            scanned++;

            if (MentionsBannedName(File.ReadAllText(Path.Combine(root, relative))))
            {
                violations.Add(relative);
            }
        }

        await Assert.That(scanned).IsGreaterThan(200)
            .Because(
                "non-vacuity: " + string.Join(", ", ScannedTrees) + " hold far more than 200 source "
                + "files (thousands at the time of writing). Fewer means the walk stopped matching — a "
                + "renamed project, a moved marker — and this rule became vacuous. RepoPaths.RepoRoot "
                + "was " + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(violations).IsEmpty()
            .Because(
                "no code, no doc comment and no inventory row may name the deleted router. A dangling "
                + "<see cref> is a CS1574 the moment somebody moves the type, and a README row "
                + "advertising an abstraction with no implementation is how the second router gets "
                + "written. Note that this file is excluded BY NAME only — see SelfRelativePath. "
                + "Offenders: " + string.Join(", ", violations));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really reads the assembly the router was deleted from, and
    ///     the governed set really excludes the only assembly that is allowed to
    ///     contain a banned shape. This is the direct answer to the NetArchTest
    ///     trap: a scan that cannot see <c>Harbor.Abstractions</c> finds no
    ///     banned type and reports success. The types named are all from
    ///     <c>ITuiRenderer.cs</c> — the exact file the interface was removed
    ///     from — so a namespace move, a failed load or a typo is red here.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheAssemblyThatHeldTheRouter_IsStillBeingRead()
    {
        IReadOnlyDictionary<string, Assembly> loaded = ArchitectureTestHelpers.LoadHarborAssemblies();

        await Assert.That(loaded.ContainsKey("Harbor.Abstractions")).IsTrue()
            .Because("Harbor.Abstractions is the assembly the router was declared in. If it is not "
                   + "loaded, every ban in this file is satisfied by an empty scan");

        var names = SlashRouterProbe.SafeGetTypes(loaded["Harbor.Abstractions"])
            .Select(static t => t.FullName ?? t.Name)
            .ToHashSet(StringComparer.Ordinal);

        // All four are declared in src/Harbor.Abstractions/Tui/ITuiRenderer.cs —
        // the exact file the router was removed from, and the only place in the
        // assembly that still matches the file name. ITuiRenderer ITSELF is not
        // one of them: it moved to Harbor.Terminal.Abstractions when the TUI
        // contracts were split out, so the file kept the name and lost the type.
        // Probing by file name would be probing a type that has not been in this
        // assembly for a long time, which fails the liveness test for a reason
        // that has nothing to do with whether the scan works.
        string[] expected =
        [
            "Harbor.Abstractions.Tui.IInputHandler",
            "Harbor.Abstractions.Tui.KeyPressEventArgs",
            "Harbor.Abstractions.Tui.ISlashCommand",
            "Harbor.Abstractions.Tui.ICommandContext",
        ];

        var missing = expected.Where(n => !names.Contains(n)).ToList();

        await Assert.That(missing).IsEmpty()
            .Because(
                "these three types are declared in the same file the router was deleted from. If the "
                + "reflection scan cannot see them, it is not reading that file, and a ban evaluated "
                + "over a file the scan never opened is decoration. Missing: " + string.Join(", ", missing));

        // The governed set must not contain this assembly — asserted, not assumed.
        // If the exclusion in Collect() ever stops working, the positive controls
        // leak into rule 2 and the gate is red for the wrong reason, which is at
        // least loud; this assertion names the cause instead.
        var governedAssemblies = Governed.Value
            .Select(static t => t.AssemblyName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await Assert.That(governedAssemblies.Contains(typeof(SlashCommandRouterShapeRule).Assembly.GetName().Name!)).IsFalse()
            .Because(
                "this assembly holds the positive control RouterProbeContract, which deliberately has "
                + "the banned signature. It must be excluded from the governed set, or rule 2 flags this "
                + "file's own test fixture and the gate can never pass. Excluded in Collect() by "
                + "assembly identity, not by name. Governed: " + string.Join(", ", governedAssemblies));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. <c>RouterProbeContract</c> is declared in this
    ///     file with the exact banned signature, and it carries a DIFFERENT name —
    ///     so tripping it proves the shape rule works on its own, and not because
    ///     the name rule happens to agree. Two negative controls follow: a
    ///     <c>Task&lt;bool&gt;</c> router and a typed-outcome router must both be
    ///     left alone, so the gate discriminates instead of flagging every
    ///     <c>TryHandle*</c> in the tree.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheShapeRule_FiresOnARealInterface()
    {
        var self = typeof(SlashCommandRouterShapeRule).Assembly;

        IReadOnlyList<Type> selfTypes = SlashRouterProbe.SafeGetTypes(self);
        var verdicts = new[] { typeof(RouterProbeContract), typeof(BoolReturningProbeContract), typeof(TypedOutcomeProbeContract) }
            .Where(t => selfTypes.Contains(t))
            .ToDictionary(static t => t, SlashRouterProbe.Classify);

        // -- positive control, by SHAPE (the name is deliberately different) --

        await Assert.That(verdicts.ContainsKey(typeof(RouterProbeContract))).IsTrue()
            .Because("RouterProbeContract is declared in this file; if reflection cannot see it, the "
                   + "positive control below is testing nothing");

        RouterVerdict shapeVerdict = verdicts[typeof(RouterProbeContract)];

        await Assert.That(shapeVerdict.HasBannedShape).IsTrue()
            .Because(
                "RouterProbeContract.TryHandleAsync returns Task<Result<bool>> — the exact "
                + "ISlashCommandRouter signature. This is the positive control for rule 2: if the "
                + "classifier misses it, rule 2 enforces nothing and the ban is a comment.");

        await Assert.That(shapeVerdict.HasBannedName).IsFalse()
            .Because(
                "RouterProbeContract is NOT named ISlashCommandRouter. The two rules have to be "
                + "independent — otherwise a renamed router sails past the shape rule, which is the "
                + "whole reason rule 2 exists.");

        // -- negative control 1: a plain bool is not banned ------------------

        await Assert.That(verdicts.ContainsKey(typeof(BoolReturningProbeContract))).IsTrue()
            .Because("BoolReturningProbeContract is declared in this file; the discrimination check "
                   + "below cannot run against a type the scan cannot see");

        await Assert.That(verdicts[typeof(BoolReturningProbeContract)].IsBanned).IsFalse()
            .Because(
                "Task<bool> carries no Result channel, so there is no failure for it to lose. "
                + "Banning it would make the gate cry wolf on the honest spellings and get switched off.");

        // -- negative control 2: a typed outcome is the replacement -----------

        await Assert.That(verdicts.ContainsKey(typeof(TypedOutcomeProbeContract))).IsTrue()
            .Because("TypedOutcomeProbeContract is declared in this file; the check below is the one "
                   + "that keeps the ban from outliving the thing it replaced");

        await Assert.That(verdicts[typeof(TypedOutcomeProbeContract)].IsBanned).IsFalse()
            .Because(
                "a typed outcome struct — ShouldQuit plus an exit code, which is what "
                + "SlashCommandOutcome is — expresses \"not a command\" and \"quit with N\" as states a "
                + "caller can match. That is the shape the ban steers code towards, so flagging it "
                + "would make this guard an argument against the fix.");
    }

    // =====================================================================
    // 3. The deletion was surgical, and it left a typed replacement behind.
    // =====================================================================

    /// <summary>
    ///     The router is gone; the command contracts it dispatched are not. Six
    ///     CLI commands implement them, so removing <c>ISlashCommand</c> or
    ///     <c>ICommandContext</c> would be a build break — but the intent is worth
    ///     pinning, because "the dead slash-command family was removed" is
    ///     exactly the misreading a reader takes from a bare deletion.
    /// </summary>
    [Test]
    public async Task TheDeletionWasSurgical_TheLiveCommandContractsRemain()
    {
        IReadOnlyDictionary<string, Assembly> loaded = ArchitectureTestHelpers.LoadHarborAssemblies();

        await Assert.That(loaded.ContainsKey("Harbor.Abstractions")).IsTrue()
            .Because("Harbor.Abstractions must be loaded for this to mean anything");

        var names = SlashRouterProbe.SafeGetTypes(loaded["Harbor.Abstractions"])
            .Select(static t => t.FullName ?? t.Name)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(names.Contains("Harbor.Abstractions.Tui.ISlashCommand")).IsTrue()
            .Because("ISlashCommand is LIVE — PermissionsCommand, ModelCommand, ConfigCommand, "
                   + "AuthCommand, SetupCommand and AgentCommand all implement it. Only the router was "
                   + "dead (issue #565)");

        await Assert.That(names.Contains("Harbor.Abstractions.Tui.ICommandContext")).IsTrue()
            .Because("ICommandContext is LIVE — it is the execution context every one of those six "
                   + "commands takes, and SimpleCommandContext implements it in the CLI composition root");
    }

    /// <summary>
    ///     The router's CONCEPT survived as a type. <c>SlashCommandOutcome</c> —
    ///     added by #603 — is what the REPL dispatches into: a typed
    ///     <c>ShouldQuit</c>/<c>ExitCode</c> pair where the old interface had a
    ///     bool that could not carry an exit code. Asserted from SOURCE, because
    ///     it is <c>internal</c> to <c>Harbor.App.Cli</c> and this test project
    ///     references no app (apps/ are composition roots, unrestricted by
    ///     design), so reflection cannot reach it.
    /// </summary>
    [Test]
    public async Task TheTypedOutcomeThatReplacedTheRouter_IsPresent()
    {
        string root = RepoRootOrFail();
        string relative = Path.Combine("apps", "Harbor.App.Cli", "Repl", "SlashCommandOutcome.cs");
        string full = Path.Combine(root, relative);

        await Assert.That(File.Exists(full)).IsTrue()
            .Because(
                "SlashCommandOutcome is the typed answer to the question the deleted router's "
                + "Result<bool> could not answer (#603: ShouldQuit + ExitCode instead of a bool that is "
                + "really a routing decision). If it is gone, the router was not replaced — it was "
                + "removed with nothing in its place, and this guard is defending an empty seat");

        // Declaration shape, not just the file name: a file that kept the path
        // but lost the members would satisfy an existence check.
        var declared = new Regex(
            @"SlashCommandOutcome\s*\(\s*bool\s+ShouldQuit\s*,\s*int\s+ExitCode\s*\)",
            RegexOptions.Compiled);

        await Assert.That(declared.IsMatch(File.ReadAllText(full))).IsTrue()
            .Because("SlashCommandOutcome must still carry BOTH members: `ShouldQuit` for the /exit "
                   + "and /quit commands, and `ExitCode` for the process exit code that the old "
                   + "Task<int?> and the deleted Result<bool> each handled differently");
    }

    // =====================================================================
    // 4. Plumbing.
    // =====================================================================

    /// <summary>
    ///     The repository root, as a hard failure rather than a silent skip. The
    ///     source-scanning tests in this project would otherwise return early on
    ///     a trimmed or published host and report success having checked nothing;
    ///     a ban that reports success when it did not run is worse than no ban.
    /// </summary>
    private static string RepoRootOrFail()
        => RepoPaths.RepoRoot
           ?? throw new InvalidOperationException(
               "RepoPaths.RepoRoot is null: no Harbor.slnx above " + AppContext.BaseDirectory
               + ". The source-scanning rules in this file cannot run, and returning early would make "
               + "them report success having scanned nothing.");

    /// <summary>
    ///     Every <c>.cs</c> and <c>.md</c> file under <see cref="ScannedTrees" />,
    ///     minus build output and minus this file. Repo-relative, forward-slashed.
    /// </summary>
    private static IEnumerable<string> EnumerateScannableFiles(string root)
    {
        foreach (string tree in ScannedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!string.Equals(ext, ".cs", StringComparison.Ordinal)
                    && !string.Equals(ext, ".md", StringComparison.Ordinal))
                {
                    continue;
                }

                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (string.Equals(relative, SelfRelativePath, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return relative;
            }
        }
    }

    /// <summary>
    ///     Whether the text names the banned identifier as a WHOLE WORD.
    ///     Whole-word because a substring match would fire on any comment that
    ///     merely mentions routers, and a gate that cries wolf gets switched off.
    /// </summary>
    private static bool MentionsBannedName(string text)
        => BannedNamePattern().IsMatch(text);

    /// <summary>
    ///     Compiled once — the scan runs over every source file in the tree, and
    ///     recompiling the pattern per file is the difference between a fast gate
    ///     and a slow one nobody runs.
    /// </summary>
    private static readonly Lazy<Regex> BannedNamePatternCache = new(
        () => new Regex($@"\b{Regex.Escape(SlashRouterProbe.BannedTypeName)}\b", RegexOptions.Compiled));

    private static Regex BannedNamePattern() => BannedNamePatternCache.Value;

    /// <summary>Every type in every loaded Harbor assembly but this one, with its verdict.</summary>
    private static IReadOnlyList<GovernedType> Collect()
    {
        Assembly self = typeof(SlashCommandRouterShapeRule).Assembly;
        var found = new List<GovernedType>();

        foreach ((string name, Assembly asm) in ArchitectureTestHelpers.LoadHarborAssemblies())
        {
            // Reference identity, not a name: this project's own assembly must be
            // excluded and there is nothing here a rename could break.
            if (ReferenceEquals(asm, self))
            {
                continue;
            }

            foreach (Type type in SlashRouterProbe.SafeGetTypes(asm))
            {
                found.Add(new GovernedType(name, type.FullName ?? type.Name, SlashRouterProbe.Classify(type)));
            }
        }

        return found;
    }

    // =====================================================================
    // 5. The positive controls. Declared here so their IL is in this assembly.
    //    Never implemented, never registered.
    // =====================================================================

    /// <summary>
    ///     The exact shape issue #565 deleted, under a name the name-rule does
    ///     NOT ban — so <see cref="NonVacuity_TheShapeRule_FiresOnARealInterface" />
    ///     measures the shape rule alone.
    /// </summary>
    private interface RouterProbeContract
    {
        /// <summary>The banned signature: a bool in a <c>Result</c>, standing in for a routing decision.</summary>
        Task<Result<bool>> TryHandleAsync(string input, ICommandContextLike context, CancellationToken ct = default);
    }

    /// <summary>
    ///     Negative control: a plain <c>Task&lt;bool&gt;</c>. No <c>Result</c>
    ///     channel, therefore no failure for it to lose.
    /// </summary>
    private interface BoolReturningProbeContract
    {
        /// <summary>The honest spelling of "did this input match a command".</summary>
        Task<bool> TryHandleAsync(string input, ICommandContextLike context, CancellationToken ct = default);
    }

    /// <summary>
    ///     Negative control, and the shape the ban steers code towards: a typed
    ///     outcome carrying the quit decision and the exit code.
    /// </summary>
    private interface TypedOutcomeProbeContract
    {
        /// <summary>What the REPL actually dispatches into today.</summary>
        Task<ProbeOutcome> TryHandleAsync(string input, ICommandContextLike context, CancellationToken ct = default);
    }

    /// <summary>Stands in for the CLI's <c>SlashCommandOutcome</c>, which is internal to an unreferenced app.</summary>
    private readonly record struct ProbeOutcome(bool ShouldQuit, int ExitCode);

    /// <summary>Stands in for the CLI's command context; rule 2 looks only at the return type.</summary>
    private interface ICommandContextLike
    {
    }
}
