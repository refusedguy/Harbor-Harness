// PresentationCapabilityRules.cs — the CAPABILITY half of the layer contract
// for issue 455, "ARCH-5: Presentation layer performs I/O and spawns
// processes; no capability rules".
//
// WHY THIS FILE EXISTS
// --------------------
// Every other file in this project enforces the REFERENCE half of the layering
// contract: which `<ProjectReference>` edges may exist. That is necessary but
// provably not sufficient. `System.IO.File`, `System.Diagnostics.Process` and
// `System.Net.Http` all live in the BCL — no reference matrix can see them, so
// an assembly can be perfectly layered and still fork a shell, overwrite the
// user's config or open a socket. docs/ARCHITECTURE_LAYERS.md §3 assigns
// subprocess and filesystem to Infrastructure, but nothing enforced it.
//
// This file enforces the missing half: which CAPABILITIES a Presentation
// assembly may exercise. It walks every Presentation src assembly with
// Mono.Cecil, reads the IL of every method body, and fails the build when a
// type reaches for a forbidden capability.
//
// MECHANISM — AND THE TRAP THIS FILE IS BUILT AROUND
// ---------------------------------------------------
// NetArchTest's `NotHaveDependencyOn(name)` is satisfied by a NAME THAT MATCHES
// NOTHING. A typo'd or already-deleted assembly therefore passes forever: green,
// and enforcing nothing. This file is built so that cannot happen, and proves
// it at runtime — see the "non-vacuity" tests at the bottom:
//
//   1. NonVacuity_Probe_ReadsRealIlFromThisTestAssembly — probes THIS test
//      assembly, whose source is right here and is known to call
//      `Directory.GetFiles` and `File.Exists`. A miss can only mean the probe
//      is broken.
//   2. NonVacuity_Probe_CanStillDetectForbiddenCapabilities — runs the same
//      probe, rules and matchers against a REAL positive control
//      (Harbor.Tools.Builtin: ReadTool/EditTool/GlobTool touch the filesystem,
//      BashTool/McpProcessClient fork processes) and REQUIRES hits. If the probe
//      ever degrades to "no hits anywhere", the Presentation rules go red
//      instead of vacuously green. This is the "check it FAILS when you expect
//      it to" test.
//   3. NonVacuity_GrandfatheredViolations_AreStillReal + the per-assembly
//      non-zero type count — the baseline cannot rot into a blanket permission.
//   4. PermanentCapabilities_AreAllCurrentlyRealized — same guarantee for the
//      permanent-capability table, which carries a reason instead of an issue.
//
// GRANDFATHERING: the honest part
// --------------------------------
// Presentation DOES perform I/O today. Landing these rules bare would break the
// build, and a permanently skipped test is a comment with extra steps. So the
// rules land ENFORCED, with a baseline: every violation that exists today is
// listed in `KnownViolations`, keyed by (rule id, declaring type) per assembly,
// and a rule holds only if the hits are a SUBSET of that baseline. New I/O in
// any Presentation type is red on the spot. The baseline is type-granular, NOT
// method-granular: a second `File.ReadAllText` added to an already-listed type
// will not be caught. That is a deliberate, bounded trade-off — method-granular
// keys break every time Roslyn renumbers a compiler-generated state machine,
// and a test that cries wolf gets deleted.
//
// Each baseline row carries a `TrackedBy` issue URL: the refactor is filed per
// site, not silently tolerated. The liveness test above is what stops a row
// from outliving its violation. This mirrors
// `FullLayerMatrixTests.DocumentedExceptions_AllCurrentlyRealized`.
//
// Since #626 each row ALSO carries a mandatory reason. The URL says where the
// debt is tracked; only the reason says why the violation is tolerated HERE, and
// it has to be written on the row — a comment above the row is invisible to
// every tool that reads this table, so "paste the link" used to be a complete
// entry and the argument was optional. The check is `ExemptionReason`, shared
// with three other permission tables; see the note on the declaration below.
//
// PERMANENT CAPABILITIES — the row that is never a violation (#669)
// ------------------------------------------------------------------
// A baseline row is a promise TO FIX: it names the issue that will delete it.
// Reading the console is not that. `TerminalInputStream` wraps the inherited
// fd 0 in a `FileStream` because `Console.OpenStandardInput` makes the runtime
// rewrite slave-side termios on first read, which turns Ctrl+C into SIGINT —
// see that class's doc. That capability is real, permanent and legitimate: fd 0
// is the renderer's input medium, so no refactor removes it short of deleting
// the renderer. Filed under `NoFiles` it misread as "renderers may touch the
// filesystem" — the precedent a future illegitimate `FileStream` needs — and it
// made #538's "delete the baseline rows" checkbox unreachable, since the row
// could be neither deleted nor kept without sitting in the wrong table.
//
// Such capabilities go in `PermanentCapabilities`, valued by their REASON
// rather than a tracking issue: there is no fix to schedule, and the reason is
// the entire justification. `PermanentCapabilities_AreAllCurrentlyRealized`
// gives it the baseline's anti-rot guarantee, and
// `RuleTable_And_Baseline_Are_WellFormed` rejects a blank reason and an entry
// that shadows a baseline row.
//
// DELIBERATELY NOT RULES (and why — do not "helpfully" add them)
// --------------------------------------------------------------
//   * `System.IO.Path` — pure string manipulation (Combine/GetFileName/…),
//     no syscall. Presentation legitimately formats paths for display.
//   * `System.Environment.GetEnvironmentVariable` / `CurrentDirectory` /
//     `GetFolderPath` — real smells (configuration belongs in the Application
//     layer) but used pervasively by renderers. Forbidding them today would
//     fail on ~20 sites and land zero enforced rules. Tracked in #518, NOT
//     baselined: a half-true rule table is worse than a short one.
//   * P/Invoke (`[DllImport]`) — Harbor.Tui.CellForge.Engine P/Invokes
//     kernel32/libc for VT-mode handling, which IS renderer work. A rule here
//     would be a rule with no principled exceptions left.
//   * `Console.*` — writing to the terminal is the renderer's entire job.
//
// WHAT IS *NOT* BASELINED
// -----------------------
// `PRESENTATION-MUST-NOT-USE-THE-NETWORK` and
// `PRESENTATION-MUST-NOT-LOAD-ASSEMBLIES-OR-EMIT-IL` have ZERO known violations
// and therefore run fully green with an EMPTY baseline: real, unbaselined,
// red-on-first-touch rules. That is the point of landing them here rather than
// deferring them along with the rest.

using Mono.Cecil;
using Mono.Cecil.Cil;
// `System.Reflection` is a global using in this project (needed for Assembly), so
// Cecil's MethodBody would be an ambiguous reference without this alias.
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Harbor.Architecture.Tests;

/// <summary>
///     A named Presentation capability rule: a set of BCL type-name prefixes
///     that no Presentation type may reach, plus the reason it is forbidden.
/// </summary>
/// <param name="Id">
///     Stable machine-readable id. Printed verbatim in CI failure output and
///     referenced from the `KnownViolations` baseline in
///     <c>PresentationCapabilityRules</c> and docs/ARCHITECTURE_LAYERS.md §6 —
///     renaming one is a breaking change.
/// </param>
/// <param name="Forbids">One-line statement of what the rule forbids.</param>
/// <param name="Why">The architectural reason it is forbidden.</param>
/// <param name="TypeNamePrefixes">
///     Matched with <c>string.StartsWith</c> (ordinal) against the FULL CLR name
///     of every BCL type the probe finds in an IL operand or in a type/member
///     signature. Prefix semantics are deliberate: one entry covers a whole
///     family (<c>System.IO.File</c> also covers <c>FileStream</c>,
///     <c>FileInfo</c>, <c>FileAccess</c>), so a new member of an existing
///     family cannot slip past a narrow entry.
/// </param>
internal sealed record CapabilityRule(
    string Id,
    string Forbids,
    string Why,
    string[] TypeNamePrefixes);

/// <summary>
///     One forbidden capability use, attributed to the human-authored type that
///     performed it (compiler-generated state machines and closures are
///     attributed to their declaring type — see <see cref="IlCapabilityProbe" />).
/// </summary>
/// <param name="RuleId">The <see cref="CapabilityRule.Id" /> that was violated.</param>
/// <param name="Assembly">Simple assembly name.</param>
/// <param name="DeclaringType">Full CLR type name, generic arity suffix included.</param>
/// <param name="Member">One representative member of that type that trips the rule.</param>
internal sealed record CapabilityHit(
    string RuleId,
    string Assembly,
    string DeclaringType,
    string Member);

/// <summary>The result of probing one assembly.</summary>
internal sealed class AssemblyScan
{
    /// <summary>Simple assembly name, e.g. <c>Harbor.Tui.CellForge</c>.</summary>
    public required string AssemblyName { get; init; }

    /// <summary>Top-level type count in the main module.</summary>
    public required int TopLevelTypeCount { get; init; }

    /// <summary>The forbidden-capability hits the probe found.</summary>
    public required IReadOnlyList<CapabilityHit> Hits { get; init; }
}

/// <summary>
///     IL-level capability probe. Reads an assembly's metadata with Mono.Cecil
///     and collects every BCL type its members and method bodies reference, then
///     matches that set against <see cref="CapabilityRule.TypeNamePrefixes" />.
/// </summary>
/// <remarks>
/// <para>
///     <b>Package provenance:</b> this is the <c>Mono.Cecil</c> that
///     <c>NetArchTest.Rules 1.3.2</c> already pulls in transitively
///     (<c>exclude="Build,Analyzers"</c> in its nuspec). No new
///     <c>PackageReference</c>, no new restore entry — if NetArchTest is ever
///     dropped, this file fails to compile loudly rather than silently
///     degrading to a no-op.
/// </para>
/// <para>
///     <b>Why Cecil and not <c>MethodBody.GetILAsByteArray()</c>:</b> decoding
///     raw IL and resolving metadata tokens by hand is ~200 lines of token and
///     arithmetic edge cases. Cecil gives the same information deterministically
///     and is already proven to read these assemblies — NetArchTest (which wraps
///     Cecil) runs over every one of them in <c>NetArchLayerRules.cs</c> on
///     every CI run.
/// </para>
/// <para>
///     <b>Attribution:</b> an <c>async</c> method's real IL lives in a
///     compiler-generated <c>&lt;Foo&gt;d__7</c> nested type, and a lambda's in
///     an <c>&lt;&gt;c__DisplayClass0_0</c>. Those names embed a compiler
///     sequence number that changes on any edit, which would make the failure
///     message useless and the baseline table rot on every rebuild. The probe
///     therefore attributes every hit to the nearest enclosing type a human
///     actually wrote.
/// </para>
/// </remarks>
internal static class IlCapabilityProbe
{
    /// <summary>Walks <paramref name="asm" /> and returns the capability hits against <paramref name="rules" />.</summary>
    /// <exception cref="InvalidOperationException">
    ///     The assembly has no simple name, or its file cannot be found. Raised
    ///     rather than swallowed: a probe that cannot open its input must not
    ///     report "no violations".
    /// </exception>
    public static AssemblyScan Scan(Assembly asm, IReadOnlyList<CapabilityRule> rules)
    {
        string? simpleName = asm.GetName().Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            throw new InvalidOperationException("[capability-probe] assembly has no simple name.");
        }

        string path = ResolvePath(asm);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"[capability-probe] cannot find the assembly file for '{simpleName}' at '{path}'. " +
                "The rule would be vacuous if the probe reported 'no violations' here.");
        }

        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path);

        // One representative member per (rule, type): a 200-call
        // File.ReadAllText loop must not produce 200 identical report rows.
        var members = new Dictionary<(string RuleId, string DeclaringType), SortedSet<string>>();

        int typeCount = 0;
        foreach (TypeDefinition type in definition.MainModule.Types)
        {
            WalkType(type, rules, members);
            typeCount++;
        }

        var hits = new List<CapabilityHit>(members.Count);
        foreach (KeyValuePair<(string RuleId, string DeclaringType), SortedSet<string>> entry in members)
        {
            foreach (string member in entry.Value)
            {
                hits.Add(new CapabilityHit(entry.Key.RuleId, simpleName, entry.Key.DeclaringType, member));
            }
        }

        return new AssemblyScan
        {
            AssemblyName = simpleName,
            TopLevelTypeCount = typeCount,
            Hits = hits,
        };
    }

    /// <remarks>
    ///     Deliberately does NOT use <c>Assembly.Location</c>. Under
    ///     <c>Microsoft.Testing.Platform</c> the entry assembly is testhost, and
    ///     under a single-file publish <c>Location</c> is an empty string for
    ///     every embedded assembly (analyzer IL3000). Every reference this probe
    ///     needs is copied next to the test host, so
    ///     <see cref="AppContext.BaseDirectory" /> is both correct and warning-free.
    ///     If the file is not there, <see cref="Scan" /> throws rather than
    ///     reporting a clean result.
    /// </remarks>
    private static string ResolvePath(Assembly asm)
    {
        string name = asm.GetName().Name
            ?? throw new InvalidOperationException("[capability-probe] assembly has no simple name.");
        return Path.Combine(AppContext.BaseDirectory, name + ".dll");
    }

    private static void WalkType(
        TypeDefinition type,
        IReadOnlyList<CapabilityRule> rules,
        Dictionary<(string, string), SortedSet<string>> members)
    {
        // 1. Type-level references: base class, interfaces, field types and the
        //    types in every method signature. A capability reached only through
        //    a field or a return type is still a capability.
        Inspect(type.BaseType, type, "<base type>", rules, members);
        foreach (InterfaceImplementation itf in type.Interfaces)
        {
            Inspect(itf.InterfaceType, type, "<interface>", rules, members);
        }
        foreach (FieldDefinition field in type.Fields)
        {
            Inspect(field.FieldType, type, "field " + field.Name, rules, members);
        }

        // 2. Method-level references: return type, parameters, locals, and — the
        //    part that actually matters — every call/newobj operand in the
        //    method body. This is what a pure reference matrix cannot do for
        //    the BCL.
        foreach (MethodDefinition method in type.Methods)
        {
            string member = "method " + method.Name;
            Inspect(method.ReturnType, type, member, rules, members);
            foreach (ParameterDefinition parameter in method.Parameters)
            {
                Inspect(parameter.ParameterType, type, member, rules, members);
            }

            if (!method.HasBody)
            {
                continue;
            }

            MethodBody body = method.Body;
            foreach (VariableDefinition variable in body.Variables)
            {
                Inspect(variable.VariableType, type, member, rules, members);
            }
            foreach (Instruction instruction in body.Instructions)
            {
                // A `call`/`callvirt`/`newobj`/`calli` operand is always a
                // MemberReference: a MethodReference, a TypeReference (a TypeRef
                // IS a MemberReference in Cecil), or a CallSite. All three carry
                // the declaring type we need to match against.
                if (instruction.Operand is MemberReference memberRef)
                {
                    Inspect(
                        memberRef as TypeReference ?? memberRef.DeclaringType,
                        type,
                        member,
                        rules,
                        members);
                }
            }
        }

        foreach (TypeDefinition nested in type.NestedTypes)
        {
            WalkType(nested, rules, members);
        }
    }

    private static void Inspect(
        TypeReference? typeRef,
        TypeDefinition owner,
        string member,
        IReadOnlyList<CapabilityRule> rules,
        Dictionary<(string, string), SortedSet<string>> members)
    {
        if (typeRef is null)
        {
            return;
        }

        string fullName = typeRef.FullName;
        string declaringType = AttributedTypeName(owner);
        foreach (CapabilityRule rule in rules)
        {
            foreach (string prefix in rule.TypeNamePrefixes)
            {
                if (!fullName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!members.TryGetValue((rule.Id, declaringType), out SortedSet<string>? known))
                {
                    known = new SortedSet<string>(StringComparer.Ordinal);
                    members[(rule.Id, declaringType)] = known;
                }
                known.Add(member);
                break;
            }
        }
    }

    /// <summary>
    ///     The full CLR name of <paramref name="type" />, or of the nearest
    ///     enclosing type a human actually wrote. Roslyn emits async state
    ///     machines (<c>&lt;Foo&gt;d__7</c>), closures
    ///     (<c>&lt;&gt;c__DisplayClass0_0</c>) and local functions
    ///     (<c>&lt;Bar&gt;g__Local|3_0</c>) as nested types delimited by angle
    ///     brackets; collapsing them keeps baseline keys stable across edits.
    /// </summary>
    internal static string AttributedTypeName(TypeDefinition type)
    {
        TypeDefinition current = type;
        while (current.DeclaringType is { } declaring && IsCompilerGenerated(current.Name))
        {
            current = declaring;
        }

        return current.FullName;
    }

    /// <remarks>
    ///     Leading <c>&lt;</c> is the whole test, and it must be the whole test.
    ///     The shapes differ in their tail: async state machines end
    ///     <c>d__7</c>, closures end <c>DisplayClass0_0</c>, local functions end
    ///     <c>g__Local|3_0</c> — only some end <c>&gt;</c>. Testing
    ///     <c>EndsWith('&gt;')</c> as well silently lets every <c>async</c> method
    ///     through, which is the common case. C# forbids a user-declared type name
    ///     starting with <c>&lt;</c>, so the prefix cannot false-positive.
    /// </remarks>
    private static bool IsCompilerGenerated(string typeName)
        => typeName.StartsWith('<');
}

/// <summary>
///     Capability rules for the Presentation layer — issue #455.
/// </summary>
/// <remarks>
///     Each rule is named for what it forbids and why, so a red build states the
///     architectural argument without this file being open.
/// </remarks>
public sealed class PresentationCapabilityRules
{
    private const string NoSubprocess = "PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES";
    private const string NoFiles = "PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-FILES";
    private const string NoDirectories = "PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-DIRECTORIES";
    private const string NoNetwork = "PRESENTATION-MUST-NOT-USE-THE-NETWORK";
    private const string NoRuntimeCodegen = "PRESENTATION-MUST-NOT-LOAD-ASSEMBLIES-OR-EMIT-IL";

    // ---------------------------------------------------------------------
    // The rule table.
    // ---------------------------------------------------------------------

    private static readonly CapabilityRule SubprocessRule = new(
        NoSubprocess,
        "forking a process from a Presentation assembly (System.Diagnostics.Process*)",
        "docs/ARCHITECTURE_LAYERS.md §3 assigns subprocess to Infrastructure. A UI that shells "
        + "out on its own bypasses the ITool seam entirely, so the call never reaches "
        + "PermissionRuleset.Evaluate, never appears in the tool-call transcript, and cannot be "
        + "cancelled by the agent loop or audited by the user. Route it through an ITool.",
        ["System.Diagnostics.Process"]);

    private static readonly CapabilityRule FileRule = new(
        NoFiles,
        "reading or writing files from a Presentation assembly (System.IO.File*)",
        "Persistence is Infrastructure behind ISessionStore / ICommonConfigStore. A view or "
        + "renderer that touches the filesystem itself is untestable, uninjectable, and its "
        + "atomicity and serialisation guarantees are invisible to the rest of the system.",
        ["System.IO.File"]);

    private static readonly CapabilityRule DirectoryRule = new(
        NoDirectories,
        "enumerating or creating directories from a Presentation assembly (System.IO.Directory*)",
        "Same reason as " + NoFiles + ", plus an unbounded-work hazard: a directory walk is a "
        + "blocking syscall sequence that can stall the render loop with no seam to cancel it or "
        + "bound it.",
        ["System.IO.Directory"]);

    private static readonly CapabilityRule NetworkRule = new(
        NoNetwork,
        "talking to the network from a Presentation assembly (HttpClient / sockets / WebRequest)",
        "All outbound HTTP belongs to Infrastructure behind ILlmClient / ITransportRemote, where "
        + "it gets a timeout, a cancellation token, retry policy and usage accounting. A socket "
        + "opened from a renderer gets none of those. NOTE: empty baseline — Presentation is clean "
        + "here today, so this rule is fully armed from day one.",
        [
            "System.Net.Http",
            "System.Net.Sockets",
            "System.Net.WebClient",
            "System.Net.WebRequest",
            "System.Net.WebSockets",
            "System.Net.Mail",
            "System.Net.Dns",
            "System.Net.Ping",
            "System.Net.NetworkInformation",
            "System.Net.Security",
        ]);

    private static readonly CapabilityRule RuntimeCodegenRule = new(
        NoRuntimeCodegen,
        "loading assemblies or emitting IL at runtime (System.Reflection.Emit / AssemblyLoadContext)",
        "AGENTS.md §Architecture decisions require NativeAOT-readiness: no reflection emit and no "
        + "Assembly.Load in shipped code. Plugins load out-of-process and CS-source plugins are "
        + "explicitly non-AOT. NOTE: empty baseline — Presentation is clean here today, so this "
        + "rule is fully armed from day one.",
        [
            "System.Reflection.Emit",
            "System.Runtime.Loader.AssemblyLoadContext",
        ]);

    /// <summary>All Presentation capability rules, in report order.</summary>
    private static readonly CapabilityRule[] AllRules =
    [
        SubprocessRule,
        FileRule,
        DirectoryRule,
        NetworkRule,
        RuntimeCodegenRule,
    ];

    // ---------------------------------------------------------------------
    // The baseline: every capability use that exists in Presentation TODAY.
    //
    // Per assembly, keyed by "<ruleId> <fullTypeName>". Adding a row is a claim
    // that the violation is real;
    // NonVacuity_GrandfatheredViolations_AreStillReal verifies the claim and
    // fails the build when the row goes stale.
    // ---------------------------------------------------------------------

    // THE ROW CARRIES A REASON, NOT JUST A LINK (#626)
    // -----------------------------------------------
    // A row here is a permission that says "this Presentation type may touch the
    // filesystem until <issue> fixes it". Until #626 it was valued by the issue
    // URL alone, and the argument for tolerating it lived in a `//` comment above
    // the row — which nothing can read. That is the form an exception takes when
    // nobody has to justify it: paste the URL, move on. Six months later the row
    // is stale, the liveness test fires, and the cheap repair is to re-add it,
    // because nothing ON the row says what it was for.
    //
    // So a row is now `ExemptionReason.Row`: `Why` (mandatory — the argument) and
    // `TrackedBy` (the issue that will delete it). The link says where the debt
    // is; only the reason says why it is tolerated HERE, and
    // RuleTable_And_Baseline_Are_WellFormed rejects a row carrying only the
    // former. The check itself is deliberately NOT re-written here: it is
    // `ExemptionReason.RowsWithoutAReason`, shared with the plugin allowance in
    // ReflectionConventionRule and the matrix exceptions in EnforcerIntegrityTests.
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, Dictionary<string, ExemptionReason.Row>> KnownViolations
        = new(StringComparer.Ordinal)
    {
        // #536 RESOLVED: the four Harbor.DesignSystem rows are GONE — not
        // re-baselined, not narrowed, and the `["Harbor.DesignSystem"]` entry is
        // gone with them, because an entry with no rows is a claim that the
        // assembly is still dirty. The leaf is CLEAN and the rules above are now
        // genuinely enforced over it.
        //
        // The rows were a permission, not a fix, and they were held in place by
        // nothing: `ThemeStore` and `ThemeDirectoryWatcher` were the only two
        // types in Harbor.DesignSystem that read a disk, and the assembly is the
        // HDS v1 package — IsPackable, `PackageId: Harbor.DesignSystem`, an EMPTY
        // allowed-reference set, no PackageReference. It is the one assembly a
        // consumer can take without pulling Harbor in, so "where the user's themes
        // live" is not something it can know. #668 had already said this about the
        // CellForge rows it deleted; #536 is the leaf's own half.
        //
        // What moved is the persistence: both types now live in
        // `Harbor.Hosting.Themes` (Harbor.Hosting, CompositionRoot — the only
        // layer that can reach Presentation from outside, since Infrastructure may
        // not). What stayed is the PORT — `IThemeStore` is still declared in the
        // leaf, still implemented by `ThemeStore`, still read by the CellForge
        // widgets — and every token. The contract belongs to the catalog; the bytes
        // belong to an outer layer.
        //
        // The two `Environment.Get*` reads (#536's "also reads HARBOR_THEMES_DIR
        // and Environment.GetFolderPath(UserProfile)") went with the types. No
        // rule here would have caught them — they are not File.*/Directory.* — so
        // `DesignSystemLeafTakesNoIoRules` is what keeps the leaf from regrowing a
        // configuration surface behind a green build.
        //
        // #537 RESOLVED: the two Harbor.Ui.Framework.Services rows are GONE — not
        // re-baselined, not widened. GitService no longer forks `git` and no longer
        // calls Directory.Exists: it maps the Domain `IGitQuery` contract onto
        // GitSessionInfo, and `ProcessGitQuery` (Harbor.Application) does the spawn
        // beside BashTool / WorkspaceInspector.
        // NonVacuity_GrandfatheredViolations_AreStillReal is what FORCES the
        // deletion: had the rows stayed while the code was fixed, that test would
        // now fail with "baseline row is stale" — the liveness check working.
        //
        // What remains is a decision, not an oversight: the UI-chrome git read is
        // deliberately NOT routed through PermissionRuleset (no model input,
        // read-only, a directory the user opened), while the AGENT's git access IS
        // gated, through the `bash` tool. See IGitQuery's remarks and
        // GitServicePermissionGatingTests.
        // #665 IN FLIGHT: the three Harbor.Tui.Notifications rows are GONE — not
        // re-baselined, not re-pointed at issue 538. The defect was never a
        // mispositioned grandf: the seam was ABSENT. `INotificationBackend` was
        // already in that file and already abstracted the three platforms by API
        // SHAPE, while each implementation still owned its own child process —
        // an abstraction over shape with none over execution. So the fix is not
        // "move these three types", it is "give them something to call": a
        // Domain `INotificationProcessRunner`, implemented by `ProcessNotificationRunner`
        // in Harbor.Application, beside `ProcessGitQuery` / `WorkspaceInspector`
        // (#537's shape, applied to this file).
        //
        // Deleting the rows is what ARMS the rule here, and it is why this
        // commit is red on its own: `Presentation_MustNot_SpawnSubprocesses`
        // now fails, naming the three backends, with "NO TRACKING ISSUE — this
        // is NEW I/O in the Presentation layer". That failure is the proof the
        // violation was real; the next commit makes it green.
        //
        // Do not re-add these rows when that happens. docs/ARCHITECTURE_LAYERS.md
        // §6's ARCH-5 template is explicit: the move out of Presentation is the
        // only exit from a baseline row, and the liveness test
        // (NonVacuity_GrandfatheredViolations_AreStillReal) exists so a row that
        // outlives its violation is a build failure rather than a habit.
        ["Harbor.Tui.CellForge"] = new(StringComparer.Ordinal)
        {
            // Chat/Panels/CellForgeJumpPalettePanel.cs:330,:343 — ProcessStartInfo
            // / Process.Start. The spawn is `git worktree list --porcelain` in
            // ReadWorktreePorcelain, redirected, with a 3s timeout.
            [NoSubprocess + " Harbor.Tui.CellForge.Panels.CellForgeJumpPalettePanel"] = new(
                Reason:
                    "Read-only UI chrome over a directory the user already opened: `git worktree "
                    + "list --porcelain`, stdout/stderr redirected and capped at 3s, with no model "
                    + "input anywhere in the path — so it never reaches PermissionRuleset and is not "
                    + "a capability the agent can be talked into using. The AGENT's git access is the "
                    + "opposite case and IS gated, through the `bash` tool. Same judgement as the "
                    + "GitService rows #537 deleted; the open question is the PLACEMENT, tracked in "
                    + "#538, which is why this is a tracked violation and not a permanent capability.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/538"),
            // #668 RESOLVED: the JsonThemeLoader / ThemeFileWatcher rows are GONE
            // — not re-baselined. Those two were a second implementation of theme
            // loading sitting next to Harbor.DesignSystem's ThemeStore, and a
            // baseline row is a permission rather than a fix: it would have said
            // "this Presentation type may touch the filesystem" forever, after
            // the duplicate was gone. They read through IThemeStore now, and
            // ThemeStoreSeamRules is the port's own guard — it fails if a second
            // implementer of that port appears.
            //
            // #667 RESOLVED: the two CellForgeFileTreePanel rows are GONE too, not
            // re-baselined and not narrowed. The panel walked the working directory
            // from `Build`; it now reads `UiState.Ui.FileTrees` and asks the
            // `IFileTreeLoader` seam for a listing, with the walk itself behind the
            // Domain `IDirectoryLister` port and implemented in
            // `SystemDirectoryLister` (Harbor.Application). Both deletions were
            // FORCED: `NonVacuity_GrandfatheredViolations_AreStillReal` fails the
            // build on a stale row, and `ResolvedViolations_HaveNoHits` fails it if
            // the file-tree capability ever comes back.
            //
            // The jump palette row above is untouched — a different defect, open.
        },
        //
        // #535 RESOLVED: the two `RecentItemsService` rows are GONE — not
        // re-baselined, and the `["Harbor.Desktop.Shared"]` entry with them,
        // because an entry with no rows is still a claim that the assembly is
        // dirty. That type persisted ~/.harbor/recent.json (File.Exists /
        // ReadAllText / WriteAllText at :89,:90,:117, Directory.CreateDirectory
        // at :110) and found the path with Environment.GetFolderPath at :45.
        //
        // It was not moved the way #536 moved the theme store, and the reason
        // is a fact rather than a preference: it was CONSTRUCTED NOWHERE. The
        // only occurrences of the identifier outside its own file were this
        // baseline, a doc-comment mention in PromptHistory, and three
        // documents — one of which (docs/KILLER_FEATURES.md §6.2) said out
        // loud that it was not used by the Avalonia command palette, and whose
        // "Action" was to wire it up. So the port #535 proposed would have had
        // zero callers: a seam with no consumer substitutes nothing, and it
        // would have left the persisting code alive in a second place behind a
        // second pair of rows. Deleting it took the last five I/O sites out of
        // `Harbor.Desktop.Shared` — the assembly is now genuinely clean, not
        // clean-with-a-waiver.
        //
        // The removal is guarded in the direction a per-type row could not
        // reach: `ResolvedViolations` is unusable here, because
        // `ResolvedRows_AreWellFormed` resolves each row's type against the
        // real assembly and a row naming a DELETED type would fail the build.
        // `DesktopSharedTakesNoIoRules` rules the whole project directory
        // instead, so it holds for a re-added type, a renamed one, and the
        // next one — and it also covers the
        // `Environment.GetFolderPath(UserProfile)` read that no capability
        // rule could see.
        //
        // #534 RESOLVED: the four Harbor.Desktop.Abstractions rows are GONE — not
        // re-baselined, not narrowed, and the whole `["Harbor.Desktop.Abstractions"]`
        // entry went with them, exactly as `["Harbor.DesignSystem"]` did in #742.
        //
        // `JsonCommonConfigStore` and `JsonAppConfigStore<T>` were the last two types
        // in this project that touched a disk. They are persistence — they stat the
        // file, create ~/.harbor, and write atomically through a sibling `.tmp` — and
        // they now live in `Harbor.Hosting/Configuration`, next to the
        // `ConfigurationModule` that already constructed both of them. What stayed in
        // the leaf is the config schema: the PORTS (`ICommonConfigStore`,
        // `IAppConfigStore<T>`) and the DTOs (`CommonConfig`, `AppConfigBase`,
        // `CompositeConfig<T>`). The contract is the schema's; the bytes are an outer
        // layer's — the same split #742 made for `IThemeStore`.
        //
        // `DesktopAbstractionsLeafTakesNoIoRules` is what keeps the leaf from regrowing
        // the capability. It is a SOURCE scan rather than a `ResolvedViolations` row on
        // purpose: `ResolvedRows_AreWellFormed` resolves each row's type name against
        // the real assembly, so a row naming a type that has moved out is a build
        // failure, not a record. A moved type cannot be expressed in that list.
        //
        // Note what this does NOT settle: the `Harbor.Desktop.Abstractions ->
        // Harbor.Application` matrix exception (#188) stays, because it exists for the
        // ProviderPresets catalog in the picker/onboarding VMs, not for config
        // persistence. Retiring that edge is a different move.
    };

    /// <summary>
    ///     Capabilities a Presentation assembly owns by construction and will
    ///     never give up, keyed exactly like <see cref="KnownViolations" /> but
    ///     valued by REASON instead of a tracking issue.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Split out from the baseline in #669. A <see cref="KnownViolations" />
    ///         row means "this is wrong, here is the issue that fixes it" — the
    ///         liveness test enforces that promise by failing when the row goes
    ///         stale. A row whose capability is permanent can keep the promise only
    ///         by never being fixed, so it breaks the promise it is making: it
    ///         reads as precedent to the next person, and it makes the owning
    ///         issue's "delete the baseline rows" checkbox unreachable, because the
    ///         row can be neither deleted (removing it breaks the build) nor kept
    ///         without sitting in the wrong table.
    ///     </para>
    ///     <para>
    ///         No tracking issue, deliberately: there is no fix to schedule. The
    ///         reason IS the entry, which is why it is mandatory —
    ///         <c>RuleTable_And_Baseline_Are_WellFormed</c> rejects a blank one
    ///         and <c>PermanentCapabilities_AreAllCurrentlyRealized</c> rejects a
    ///         stale one, so this table cannot rot into a blanket permission any
    ///         more than the baseline can.
    ///     </para>
    /// </remarks>
    private static readonly Dictionary<string, Dictionary<string, string>> PermanentCapabilities
        = new(StringComparer.Ordinal)
        {
            ["Harbor.Tui.CellForge.Engine"] = new(StringComparer.Ordinal)
            {
                // Input/TerminalInputStream.cs:26 — FileStream over the inherited
                // fd 0. Not a violation and not fixable: fd 0 is the renderer's
                // input medium and reading the console is renderer work, so the
                // only refactor that removes this deletes the renderer.
                //
                // Why a FileStream rather than Console.OpenStandardInput: the
                // runtime rewrites slave-side termios on the first read from the
                // stream it hands back, re-enabling ISIG and turning Ctrl+C into
                // SIGINT instead of the 0x03 byte the key loop expects. Reading the
                // raw fd skips that rewrite. See the class doc for the full story.
                [NoFiles + " Harbor.Tui.CellForge.Input.TerminalInputStream"] =
                    "The console device, not storage: fd 0 is the renderer's input medium.",
            },
        };

    /// <summary>Shared empty row set, so a lookup miss allocates nothing per assembly.</summary>
    private static readonly Dictionary<string, ExemptionReason.Row> EmptyBaselineRows
        = new(StringComparer.Ordinal);

    /// <summary>The same, for the permanent-capability table, whose value is a bare reason.</summary>
    private static readonly Dictionary<string, string> EmptyPermanentRows = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------------
    // The RESOLVED list — violations that were tracked and are being deleted.
    //
    // WHY THIS LIST EXISTS, AND WHY IT IS NOT THE SAME THING AS DELETING THE ROW
    // ------------------------------------------------------------------------
    // Deleting a `KnownViolations` row is the last commit of a refactor, and by
    // itself it is indistinguishable from "the author was tired". There is
    // nothing in the baseline that says "this row was SUPPOSED to disappear" —
    // only rows that say "this row is still fine". A regression that
    // re-introduces `Directory.EnumerateDirectories` in the file-tree panel would
    // therefore be re-grandfable by simply re-adding the row, and the only
    // signal that it had already been paid for is a code reviewer's memory.
    //
    // So the promise is written down as data BEFORE the fix, in a list whose
    // whole content is negative assertions. Each entry says: this (rule, type)
    // pair must have NO hits, ever again. Landing it while the I/O is still
    // there is deliberate — the test is RED BY CONSTRUCTION, and the red is the
    // proof that the probe is looking at the real type rather than at nothing.
    //
    // This is the same discipline as `NonVacuity_GrandfatheredViolations_AreStillReal`
    // applied in the other direction: that test stops a baseline row outliving
    // its violation; this one stops a removed violation coming back.
    //
    // ANTI-TYPO, because a mistyped type name is silently vacuous
    // -----------------------------------------------------------
    // A row naming a type that does not exist can never fail, so a typo would
    // turn the guard into a comment wearing a test's clothes. `ResolvedRows_AreWellFormed`
    // therefore resolves every type name against the real assembly before any
    // row is allowed to count as satisfied, and fails loudly on a miss.
    // ---------------------------------------------------------------------

    private static readonly (string Assembly, string RuleId, string TypeName)[] ResolvedViolations =
    [
        // #667: CellForgeFileTreePanel listed the working directory from
        // `Build` — i.e. from inside a painted frame. Three layers were missing
        // at once, and all three had to land for the type to lose the
        // capability: the listing now lives in `UiState.Ui.FileTrees`, the walk
        // lives behind the Domain `IDirectoryLister` port, and
        // `FileTreeLoader` owns the CancellationTokenSource that can actually
        // stop one. See docs/ARCHITECTURE_LAYERS.md §3.
        ("Harbor.Tui.CellForge", NoFiles, "Harbor.Tui.CellForge.Panels.CellForgeFileTreePanel"),
        ("Harbor.Tui.CellForge", NoDirectories, "Harbor.Tui.CellForge.Panels.CellForgeFileTreePanel"),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, Assembly>> LoadedAssemblies =
        new(ArchitectureTestHelpers.LoadHarborAssemblies);

    // The probe walks IL; five rules x seventeen assemblies would otherwise walk
    // each assembly's IL five times.
    private static readonly Dictionary<string, AssemblyScan> ScanCache = new(StringComparer.Ordinal);

    // =====================================================================
    // 1. The rules. One test per named capability.
    // =====================================================================

    /// <summary>
    ///     A Presentation assembly may not fork a process. Anything that shells
    ///     out has to be an <c>ITool</c> in Harbor.Tools.Builtin so the call
    ///     passes through <c>PermissionRuleset</c> and the tool transcript.
    /// </summary>
    [Test]
    public async Task Presentation_MustNot_SpawnSubprocesses()
    {
        var failures = Evaluate(SubprocessRule);
        await Assert.That(failures).IsEmpty()
            .Because("Presentation forking a process bypasses the ITool/permission seam; "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     A Presentation assembly may not read or write files. Storage is
    ///     Infrastructure behind <c>ISessionStore</c> / <c>ICommonConfigStore</c>.
    /// </summary>
    [Test]
    public async Task Presentation_MustNot_TouchTheFilesystem_Files()
    {
        var failures = Evaluate(FileRule);
        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     A Presentation assembly may not enumerate or create directories —
    ///     unbounded blocking work on the render thread with no seam to cancel it.
    /// </summary>
    [Test]
    public async Task Presentation_MustNot_TouchTheFilesystem_Directories()
    {
        var failures = Evaluate(DirectoryRule);
        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     A Presentation assembly may not open a socket or an
    ///     <c>HttpClient</c>. Currently clean — empty baseline, so genuinely
    ///     armed from day one.
    /// </summary>
    [Test]
    public async Task Presentation_MustNot_UseTheNetwork()
    {
        var failures = Evaluate(NetworkRule);
        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     A Presentation assembly may not load assemblies or emit IL at runtime
    ///     (NativeAOT-readiness). Currently clean, empty baseline, armed from day
    ///     one.
    /// </summary>
    [Test]
    public async Task Presentation_MustNot_LoadAssembliesOrEmitIl()
    {
        var failures = Evaluate(RuntimeCodegenRule);
        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 2. Non-vacuity. A rule nobody can fail is a comment — these prove the
    //    probe reads real IL and still detects the capabilities it forbids.
    // =====================================================================

    /// <summary>
    ///     Probes THIS test assembly, whose source is right here and is known to
    ///     call <c>Directory.GetFiles</c> and <c>File.Exists</c> in
    ///     <see cref="ArchitectureTestHelpers.LoadHarborAssemblies" />. Both
    ///     filesystem rules MUST fire. Also asserts Cecil found a real file to
    ///     open, so "no violations" can never mean "no input".
    /// </summary>
    [Test]
    public async Task NonVacuity_Probe_ReadsRealIlFromThisTestAssembly()
    {
        var self = typeof(PresentationCapabilityRules).Assembly;
        string path = Path.Combine(
            AppContext.BaseDirectory,
            (self.GetName().Name ?? throw new InvalidOperationException("unnamed assembly")) + ".dll");

        await Assert.That(File.Exists(path)).IsTrue()
            .Because("the probe must open a real assembly file; reporting 'no violations' "
                   + "because it silently found no input would make every rule vacuous");

        var scan = Probe(self);
        await Assert.That(scan.TopLevelTypeCount).IsGreaterThan(0);

        var fired = scan.Hits.Select(static hit => hit.RuleId).ToHashSet(StringComparer.Ordinal);
        await Assert.That(fired.Contains(NoFiles)).IsTrue()
            .Because("the probe is known to work on this assembly, so it must detect the "
                   + "File.Exists call in ArchitectureTestHelpers.LoadHarborAssemblies");
        await Assert.That(fired.Contains(NoDirectories)).IsTrue()
            .Because("the probe is known to work on this assembly, so it must detect the "
                   + "Directory.GetFiles call in ArchitectureTestHelpers.LoadHarborAssemblies");
    }

    /// <summary>
    ///     Sensitivity control against a REAL assembly rather than synthetic
    ///     code: <c>Harbor.Tools.Builtin</c> is Infrastructure and is full of
    ///     filesystem and process use (ReadTool, EditTool, GlobTool, BashTool,
    ///     McpProcessClient). The same probe, rules and matchers must report it.
    ///     If it ever reports nothing, the Presentation rules above are green for
    ///     no reason and this test is red.
    /// </summary>
    [Test]
    public async Task NonVacuity_Probe_CanStillDetectForbiddenCapabilities()
    {
        var control = Probe(RequireLoaded("Harbor.Tools.Builtin"));
        var fired = control.Hits.Select(static hit => hit.RuleId).ToHashSet(StringComparer.Ordinal);

        await Assert.That(control.TopLevelTypeCount).IsGreaterThan(0);
        await Assert.That(fired.Contains(NoFiles)).IsTrue()
            .Because("Harbor.Tools.Builtin reads and writes files (ReadTool/EditTool/GlobTool); "
                   + "if the probe reports none, the Presentation file rules are vacuous");
        await Assert.That(fired.Contains(NoSubprocess)).IsTrue()
            .Because("Harbor.Tools.Builtin forks processes (BashTool/McpProcessClient); "
                   + "if the probe reports none, the Presentation subprocess rule is vacuous");
    }

    /// <summary>
    ///     Every row of the baseline must still correspond to a real hit. This
    ///     stops the grandf list rotting into a blanket permission, and doubles
    ///     as a second sensitivity check: it asserts the probe DOES see the
    ///     Presentation violations the baseline claims exist.
    /// </summary>
    [Test]
    public async Task NonVacuity_GrandfatheredViolations_AreStillReal()
    {
        var failures = new List<string>();

        foreach (var (assemblyName, byKey) in KnownViolations)
        {
            AssemblyScan scan = Probe(RequireLoaded(assemblyName));
            var real = scan.Hits
                .Select(static hit => (hit.RuleId, hit.DeclaringType))
                .ToHashSet();

            foreach (var (key, row) in byKey)
            {
                int sep = key.IndexOf(' ');
                if (sep <= 0)
                {
                    failures.Add($"{assemblyName}: malformed baseline key '{key}' — expected "
                        + $"'<ruleId> <typeName>'; tracked by {row.TrackedBy}");
                    continue;
                }

                (string ruleId, string typeName) = (key[..sep], key[(sep + 1)..]);
                if (real.Contains((ruleId, typeName)))
                {
                    continue;
                }

                failures.Add(
                    $"{assemblyName} / {ruleId} / {typeName}: baseline row is stale — the probe "
                    + "finds no such violation any more. Delete the row and close the issue. "
                    + $"Tracked by {row.TrackedBy}. Reason it was tolerated: {row.Reason}");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("A baseline row that no longer matches reality is a lie: it lets a type be "
                   + "re-added under a renamed key with nobody noticing. "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     Every row of the RESOLVED list must have zero hits. This is the
    ///     negative twin of <see cref="NonVacuity_GrandfatheredViolations_AreStillReal" />:
    ///     a grandf row has to keep matching reality, and a resolved row has to
    ///     keep NOT matching it.
    /// </summary>
    /// <remarks>
    ///     Landing it before the fix is the point — see
    ///     <see cref="ResolvedViolations" />. The probe's sensitivity to these
    ///     very two rules is pinned independently by
    ///     <see cref="NonVacuity_Probe_ReadsRealIlFromThisTestAssembly" />, so a
    ///     green result here means "no hits", not "no probe".
    /// </remarks>
    [Test]
    public async Task ResolvedViolations_HaveNoHits()
    {
        var failures = new List<string>();

        foreach ((string assemblyName, string ruleId, string typeName) in ResolvedViolations)
        {
            AssemblyScan scan = Probe(RequireLoaded(assemblyName));
            var real = scan.Hits
                .Where(hit => hit.RuleId == ruleId && hit.DeclaringType == typeName)
                .Select(static hit => hit.Member)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static member => member, StringComparer.Ordinal)
                .ToList();

            if (real.Count == 0)
            {
                continue;
            }

            failures.Add(
                $"{assemblyName} / {ruleId} / {typeName}: this violation was RESOLVED — the "
                + $"capability is back, in {string.Join(", ", real)}. Do not re-add a "
                + "KnownViolations row for it; fix the code, or reopen the tracking issue "
                + "and say why the resolution was wrong.");
        }

        await Assert.That(failures).IsEmpty()
            .Because("A resolved capability that silently returns is a regression nobody asked "
                   + "for, and the baseline is the only place it would hide. "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     The RESOLVED list is only as strong as its own well-formedness: a row
    ///     naming a rule that does not exist, an assembly the layer matrix does
    ///     not call Presentation, or a type name that does not exist can never
    ///     fail, and would turn <see cref="ResolvedViolations_HaveNoHits" /> into
    ///     a green comment. The type check is the important one — a renamed or
    ///     misspelled CLR name is exactly the silent failure mode here.
    /// </summary>
    [Test]
    public async Task ResolvedRows_AreWellFormed()
    {
        var failures = new List<string>();
        var ruleIds = AllRules.Select(static rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        var presentation = FullLayerMatrixTests.PresentationLayerAssemblies().ToHashSet(StringComparer.Ordinal);

        await Assert.That(ResolvedViolations.Length).IsGreaterThan(0)
            .Because("an empty RESOLVED list is indistinguishable from a guard that was never "
                   + "written; the section it guards must contain at least one row");

        foreach ((string assemblyName, string ruleId, string typeName) in ResolvedViolations)
        {
            if (!ruleIds.Contains(ruleId))
            {
                failures.Add($"resolved row '{ruleId} {typeName}' names an unknown rule id");
            }

            if (!presentation.Contains(assemblyName))
            {
                failures.Add($"resolved row names assembly '{assemblyName}', which the layer "
                    + "matrix does not classify as Presentation — the row would guard nothing");
                continue;
            }

            Assembly asm = RequireLoaded(assemblyName);
            if (asm.GetType(typeName) is null)
            {
                failures.Add($"resolved row names type '{typeName}', which does not exist in "
                    + $"'{assemblyName}'. A typo here makes the row permanently vacuous — the "
                    + "exact green-but-guarding-nothing shape this file was built to prevent.");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every permanent-capability entry must still match a real hit — the same
    ///     anti-rot guarantee <see cref="NonVacuity_GrandfatheredViolations_AreStillReal" />
    ///     gives the baseline. Without it this table grows silently, and the next
    ///     site gets filed here by reflex rather than by judgement.
    /// </summary>
    [Test]
    public async Task PermanentCapabilities_AreAllCurrentlyRealized()
    {
        var failures = new List<string>();

        foreach (var (assemblyName, byKey) in PermanentCapabilities)
        {
            AssemblyScan scan = Probe(RequireLoaded(assemblyName));
            var real = scan.Hits
                .Select(static hit => (hit.RuleId, hit.DeclaringType))
                .ToHashSet();

            foreach (var (key, reason) in byKey)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    failures.Add($"{assemblyName} / {key}: permanent capability states no reason");
                }

                int sep = key.IndexOf(' ');
                if (sep <= 0)
                {
                    failures.Add($"{assemblyName}: malformed permanent-capability key '{key}' — "
                        + "expected '<ruleId> <typeName>'");
                    continue;
                }

                if (real.Contains((key[..sep], key[(sep + 1)..])))
                {
                    continue;
                }

                failures.Add(
                    $"{assemblyName} / {key}: stale — the probe finds no such capability any more, "
                    + "so the entry excuses nothing. Delete it. " + reason);
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("A permanent-capability entry that no longer matches reality is a lie: it lets "
                   + "a type be re-added under a renamed key with nobody noticing. "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     #669 — a baseline row is a PROMISE TO FIX. Every row carries a tracking
    ///     issue that plans the refactor, and #538's checkbox counts rows deleted.
    ///     Reading the console is not that: fd 0 is the renderer's input medium,
    ///     so <c>TerminalInputStream</c> owns the capability permanently and no
    ///     refactor removes it short of deleting the renderer.
    ///     <para>
    ///     Filed under <c>PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-FILES</c> it
    ///     read as the opposite of the truth — "renderers may touch the filesystem"
    ///     — which is precisely the precedent a future, illegitimate
    ///     <c>FileStream</c> needs. It also made #538 ("seven baseline rows
    ///     deleted") unreachable: the row could be neither deleted (the capability
    ///     is real) nor kept without leaving the exception under the wrong rule.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Baseline_MustNot_FilePermanentCapabilities_AsViolations()
    {
        // Capabilities a Presentation assembly owns by construction, and the
        // architectural reason each one is not a fixable violation. Listed so the
        // next site of this shape (#538 was split into five) lands in the right
        // table from the start instead of being filed as debt and re-litigated.
        var permanent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NoFiles + " Harbor.Tui.CellForge.Input.TerminalInputStream"] =
                "fd 0 is the renderer's input medium, not storage — reading the console is "
                + "renderer work.",
        };

        var failures = new List<string>();

        foreach (var (assemblyName, byKey) in KnownViolations)
        {
            foreach (var (key, row) in byKey)
            {
                foreach (var (permanentKey, reason) in permanent)
                {
                    if (!string.Equals(key, permanentKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    failures.Add(
                        $"{assemblyName} / {key}: this is a PERMANENT capability, not a fixable "
                        + $"violation — {reason} Listed under a capability rule and tracked by "
                        + $"{row.TrackedBy}, it misfiles a permission as debt, teaches the next "
                        + "FileStream that renderers may touch the filesystem, and blocks that "
                        + "issue's 'delete the baseline row' checkbox. Record it as a permanent "
                        + "capability with the reason attached instead.");
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Rule-table and baseline integrity: rule ids unique and non-blank, every
    ///     rule states what it forbids and why, every rule and every baseline row
    ///     names a rule that exists, every baseline row points at a tracking
    ///     issue AND states a reason (#626), and no baseline row names an assembly
    ///     the layer matrix does not classify as Presentation (a typo there would
    ///     grandf nothing).
    /// </summary>
    [Test]
    public async Task RuleTable_And_Baseline_Are_WellFormed()
    {
        var failures = new List<string>();
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (CapabilityRule rule in AllRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                failures.Add("a capability rule has a blank Id");
                continue;
            }

            if (!ruleIds.Add(rule.Id))
            {
                failures.Add($"duplicate capability rule id '{rule.Id}'");
            }

            if (string.IsNullOrWhiteSpace(rule.Forbids) || string.IsNullOrWhiteSpace(rule.Why))
            {
                failures.Add($"rule '{rule.Id}' must state both what it forbids and why");
            }

            if (rule.TypeNamePrefixes.Length == 0)
            {
                failures.Add($"rule '{rule.Id}' forbids nothing — it has no type-name prefixes");
            }

            foreach (string prefix in rule.TypeNamePrefixes)
            {
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    failures.Add($"rule '{rule.Id}' has a blank type-name prefix");
                }
            }
        }

        var presentation = FullLayerMatrixTests.PresentationLayerAssemblies().ToHashSet(StringComparer.Ordinal);
        await Assert.That(presentation.Count).IsGreaterThan(0)
            .Because("the layer matrix must classify at least one src assembly as Presentation");

        foreach (var (assemblyName, byKey) in KnownViolations)
        {
            if (!presentation.Contains(assemblyName))
            {
                failures.Add($"baseline names '{assemblyName}', which the layer matrix does not "
                    + "classify as Presentation — the row would grandf nothing");
            }

            foreach (var (key, row) in byKey)
            {
                int sep = key.IndexOf(' ');
                if (sep <= 0)
                {
                    continue; // already reported by the liveness test
                }

                if (!ruleIds.Contains(key[..sep]))
                {
                    failures.Add($"baseline row '{key}' references unknown rule id '{key[..sep]}'");
                }

                if ((row.TrackedBy ?? string.Empty).Contains("https://github.com/", StringComparison.Ordinal) is false)
                {
                    failures.Add($"baseline row '{key}' has no tracking issue URL (got '{row.TrackedBy}')");
                }
            }
        }

        // #626 — THE REASON IS MANDATORY, on this table and on every other. This
        // is the check that stops an exception from being added silently: a
        // baseline row used to be valued by its issue URL alone, with the
        // argument in a comment no tool can read, so "paste the link" was a
        // complete row. The check itself lives in ExemptionReason because three
        // other permission tables now ask the identical question, and a third
        // blank-check written slightly differently is how the answer drifts.
        failures.AddRange(
            ExemptionReason.RowsWithoutAReason(
                "PresentationCapabilityRules.KnownViolations",
                KnownViolations.SelectMany(
                    static entry => entry.Value.Select(
                        kv => (Key: $"{entry.Key} / {kv.Key}", Row: kv.Value)))));

        // The permanent-capability table obeys the same integrity rules, minus the
        // tracking URL (there is no fix to schedule) and plus two of its own: a
        // blank reason, and an entry that shadows a baseline row. The second one
        // is the #669 bug in general form — the same capability counted twice,
        // once as debt and once as a permission, so the debt count never moves.
        foreach (var (assemblyName, byKey) in PermanentCapabilities)
        {
            if (!presentation.Contains(assemblyName))
            {
                failures.Add($"permanent capability names '{assemblyName}', which the layer matrix "
                    + "does not classify as Presentation — the entry would excuse nothing");
            }

            foreach (var (key, reason) in byKey)
            {
                int sep = key.IndexOf(' ');
                if (sep <= 0)
                {
                    continue; // already reported by the liveness test
                }

                if (!ruleIds.Contains(key[..sep]))
                {
                    failures.Add($"permanent capability '{key}' references unknown rule id '{key[..sep]}'");
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    failures.Add($"permanent capability '{key}' states no reason — the reason is the "
                        + "only thing separating a permission from a debt");
                }

                if (KnownViolations.TryGetValue(assemblyName, out Dictionary<string, ExemptionReason.Row>? rows)
                    && rows.ContainsKey(key))
                {
                    failures.Add($"'{key}' is listed BOTH as a baseline violation and as a permanent "
                        + $"capability in '{assemblyName}'. Pick one: a violation is tracked to a fix, "
                        + "a permanent capability carries its reason.");
                }
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 3. Evaluation plumbing.
    // =====================================================================

    /// <summary>
    ///     Runs one rule over every Presentation assembly and returns the
    ///     violations that are NOT in the baseline.
    /// </summary>
    private static List<string> Evaluate(CapabilityRule rule)
    {
        var failures = new List<string>();

        foreach (string assemblyName in FullLayerMatrixTests.PresentationLayerAssemblies())
        {
            AssemblyScan scan = Probe(RequireLoaded(assemblyName));

            if (scan.TopLevelTypeCount == 0)
            {
                // Cannot happen: RequireLoaded proves the assembly is loaded and
                // the probe throws when the file is missing. Asserted anyway,
                // because a zero-type scan is the exact shape of a vacuous pass.
                failures.Add($"{assemblyName}: probe found 0 top-level types, so {rule.Id} "
                    + "would pass vacuously");
                continue;
            }

            var baseline = KnownViolations.TryGetValue(
                assemblyName, out Dictionary<string, ExemptionReason.Row>? rows)
                ? rows
                : EmptyBaselineRows;

            Dictionary<string, string> permanent = PermanentCapabilities.TryGetValue(
                assemblyName, out Dictionary<string, string>? permanentRows)
                ? permanentRows
                : EmptyPermanentRows;

            foreach (IGrouping<string, CapabilityHit> byType in scan.Hits
                .Where(hit => hit.RuleId == rule.Id)
                .GroupBy(static hit => hit.DeclaringType))
            {
                string key = rule.Id + " " + byType.Key;
                if (baseline.ContainsKey(key) || permanent.ContainsKey(key))
                {
                    continue;
                }

                string members = string.Join(
                    ", ",
                    byType.Select(static hit => hit.Member)
                          .Distinct(StringComparer.Ordinal)
                          .OrderBy(static member => member, StringComparer.Ordinal));

                failures.Add(
                    $"[{rule.Id}] {assemblyName}: {byType.Key} uses {rule.Forbids} ({members}). "
                    + rule.Why
                    + " Tracked by: NO TRACKING ISSUE — this is NEW I/O in the Presentation layer. "
                    + "Open an issue, then either move the I/O behind a Domain contract plus an "
                    + "Infrastructure implementation, or add an explicit baseline row with that "
                    + "issue URL. Do not widen an existing row to cover it. If this assembly owns "
                    + "the capability for good (the console device, for instance), record it in "
                    + "PermanentCapabilities with its reason instead — never as a baseline row.");
            }
        }

        return failures;
    }

    private static AssemblyScan Probe(Assembly asm)
    {
        string key = asm.GetName().Name ?? asm.FullName ?? asm.ToString();
        lock (ScanCache)
        {
            if (ScanCache.TryGetValue(key, out AssemblyScan? cached))
            {
                return cached;
            }

            AssemblyScan scan = IlCapabilityProbe.Scan(asm, AllRules);
            ScanCache[key] = scan;
            return scan;
        }
    }

    private static Assembly RequireLoaded(string assemblyName)
    {
        if (LoadedAssemblies.Value.TryGetValue(assemblyName, out var asm))
        {
            return asm;
        }

        throw new InvalidOperationException(
            $"[capability-probe] '{assemblyName}' is not loaded. Every Presentation assembly the "
            + "layer matrix classifies must be loadable by this test project, otherwise its "
            + "capability rules are skipped without anyone noticing — add a <ProjectReference> "
            + "to Harbor.Architecture.Tests.csproj.");
    }
}
