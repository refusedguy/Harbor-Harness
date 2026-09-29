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

    private static readonly Dictionary<string, Dictionary<string, string>> KnownViolations = new(StringComparer.Ordinal)
    {
        ["Harbor.Ui.Framework.Services"] = new(StringComparer.Ordinal)
        {
            // Services/GitService.cs — ProcessStartInfo/Process.Start at :52,:64
            // (git rev-parse, git status) and Directory.Exists at :21. GitService
            // is NOT an ITool and never reaches the permission seam.
            [NoSubprocess + " Harbor.Ui.Framework.Services.GitService"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/537",
            [NoDirectories + " Harbor.Ui.Framework.Services.GitService"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/537",
        },
        ["Harbor.Tui.Notifications"] = new(StringComparer.Ordinal)
        {
            // NotificationTuiRenderer.cs — three OS notification backends, each
            // shelling out to the platform notifier.
            [NoSubprocess + " Harbor.Tui.Notifications.LinuxNotifySendBackend"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
            [NoSubprocess + " Harbor.Tui.Notifications.MacOsascriptBackend"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
            [NoSubprocess + " Harbor.Tui.Notifications.WindowsToastBackend"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
        },
        ["Harbor.Tui.CellForge"] = new(StringComparer.Ordinal)
        {
            // Chat/Panels/CellForgeJumpPalettePanel.cs:330,:343 — ProcessStartInfo
            // / Process.Start to run the jump-to-definition search.
            [NoSubprocess + " Harbor.Tui.CellForge.Panels.CellForgeJumpPalettePanel"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
            // Chat/Widgets/JsonThemeLoader.cs:57,:62 and ThemeFileWatcher.cs:35,:42,:47.
            [NoFiles + " Harbor.Tui.CellForge.Widgets.JsonThemeLoader"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
            [NoFiles + " Harbor.Tui.CellForge.Widgets.ThemeFileWatcher"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
            // Chat/Panels/CellForgeFileTreePanel.cs:153,:230,:232,:240 for
            // Directory, and :237,:242,:247 for FileInfo/FileAttributes — the
            // NoFiles prefix matches those too, so this panel needs BOTH rows.
            [NoFiles + " Harbor.Tui.CellForge.Panels.CellForgeFileTreePanel"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
            [NoDirectories + " Harbor.Tui.CellForge.Panels.CellForgeFileTreePanel"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
        },
        ["Harbor.Tui.CellForge.Engine"] = new(StringComparer.Ordinal)
        {
            // Input/TerminalInputStream.cs:26 — FileStream over the inherited fd 0.
            // It is not a file, it is a terminal; filed anyway so the judgement
            // call is explicit rather than accidental.
            [NoFiles + " Harbor.Tui.CellForge.Input.TerminalInputStream"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/538",
        },
        ["Harbor.DesignSystem"] = new(StringComparer.Ordinal)
        {
            // ThemeStore.cs (File :117,:122,:176; Directory :69,:71,:151,:152) and
            // ThemeDirectoryWatcher.cs (File :64,:91; Directory :45,:46).
            [NoFiles + " Harbor.DesignSystem.ThemeStore"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/536",
            [NoFiles + " Harbor.DesignSystem.ThemeDirectoryWatcher"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/536",
            [NoDirectories + " Harbor.DesignSystem.ThemeStore"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/536",
            [NoDirectories + " Harbor.DesignSystem.ThemeDirectoryWatcher"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/536",
        },
        ["Harbor.Desktop.Abstractions"] = new(StringComparer.Ordinal)
        {
            // Configuration/JsonAppConfigStore.cs:117,:124,:173-:178 and
            // JsonCommonConfigStore.cs:83,:89,:182-:223. These two are the ONLY
            // reason Harbor.Desktop.Abstractions sits in Presentation rather than
            // Domain — see the layer-matrix exception on its Harbor.Application
            // edge (#188) and docs/ARCHITECTURE_LAYERS.md §1's
            // ICommonConfigReader cycle note.
            [NoFiles + " Harbor.Desktop.Abstractions.Configuration.JsonAppConfigStore`1"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/534",
            [NoFiles + " Harbor.Desktop.Abstractions.Configuration.JsonCommonConfigStore"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/534",
            [NoDirectories + " Harbor.Desktop.Abstractions.Configuration.JsonAppConfigStore`1"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/534",
            [NoDirectories + " Harbor.Desktop.Abstractions.Configuration.JsonCommonConfigStore"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/534",
        },
        ["Harbor.Desktop.Shared"] = new(StringComparer.Ordinal)
        {
            // Services/RecentItemsService.cs:89,:90,:117 (File) and :110 (Directory).
            [NoFiles + " Harbor.Desktop.Shared.Services.RecentItemsService"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/535",
            [NoDirectories + " Harbor.Desktop.Shared.Services.RecentItemsService"] =
                "https://github.com/refusedguy/Harbor-Harness/issues/535",
        },
    };

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

            foreach (var (key, trackedBy) in byKey)
            {
                int sep = key.IndexOf(' ');
                if (sep <= 0)
                {
                    failures.Add($"{assemblyName}: malformed baseline key '{key}' — expected "
                        + $"'<ruleId> <typeName>'; tracked by {trackedBy}");
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
                    + $"Tracked by {trackedBy}.");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("A baseline row that no longer matches reality is a lie: it lets a type be "
                   + "re-added under a renamed key with nobody noticing. "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     Rule-table and baseline integrity: rule ids unique and non-blank, every
    ///     rule states what it forbids and why, every rule and every baseline row
    ///     names a rule that exists, every baseline row points at a tracking
    ///     issue, and no baseline row names an assembly the layer matrix does not
    ///     classify as Presentation (a typo there would grandf nothing).
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

            foreach (var (key, trackedBy) in byKey)
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

                if (!trackedBy.Contains("https://github.com/", StringComparison.Ordinal))
                {
                    failures.Add($"baseline row '{key}' has no tracking issue URL (got '{trackedBy}')");
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

            var baseline = KnownViolations.TryGetValue(assemblyName, out Dictionary<string, string>? rows)
                ? rows
                : new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (IGrouping<string, CapabilityHit> byType in scan.Hits
                .Where(hit => hit.RuleId == rule.Id)
                .GroupBy(static hit => hit.DeclaringType))
            {
                if (baseline.ContainsKey(rule.Id + " " + byType.Key))
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
                    + "issue URL. Do not widen an existing row to cover it.");
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
