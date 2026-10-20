// ReflectionConventionRule.cs — issue #626, and the migration of its rule to
// the compiler (issue #1844).
//
// THE CONVENTION, WRITTEN DOWN
// ----------------------------
//   Reflection is allowed in tests. It is forbidden in shipped product code,
//   except in the `src/Harbor.Plugins.*` family, which needs it deliberately.
//
// Until this file it existed only as an emergent property of the zero-warning
// bar. The `ReflectionAnalyzers` (REFL*) diagnostics did not contribute to it:
// all 46 of their rules are member-by-member rules about reflection lookups,
// and not one of them is about dynamic code — measured in #1905 by reading the
// ids, titles and strings out of the shipped analyzer assembly. A contributor
// had no way to tell it from an accident, which means the next person to add
// `Assembly.Load` somewhere new has no reason to know it is the thing the whole
// codebase is built to avoid.
//
// WHY THESE FOUR CONSTRUCTS, AND WHY NOT THE OTHERS
// --------------------------------------------------
// The dangerous half is dynamic code: an assembly loaded or IL emitted at run
// time is invisible to NativeAOT, is not in any `<ProjectReference>`, and cannot
// be audited by reading the csproj. That is the half this file bans, and it is
// the half AGENTS.md already names ("No reflection emit, no `Assembly.Load`").
//
// The constructs are grouped as one family rather than listed one by one, because
// they are one capability: `TypeBuilder`/`DynamicMethod`/`Reflection.Emit` are all
// "make or run code the compiler never saw", and `AssemblyLoadContext` is the load
// side of the same door. A guard that named only `Assembly.Load(` would be
// satisfied by switching to `AssemblyLoadContext`.
//
// STRING-BASED MEMBER ACCESS IS NOT BANNED, AND THAT IS A DECISION
// --------------------------------------------------------------
// `GetMethod("Name")` / `GetProperty("Name")` / `GetField("Name")` looks like the
// same thing and is not, and the difference is not subtle once you look at the call
// sites. Measured over `src/` and `apps/`, the overwhelming majority of that shape
// is `JsonElement.GetProperty` / `TryGetProperty` — a JSON key, not a CLR member,
// and the string is the entire point of the call. A blanket ban would fire on every
// tool's argument reader and every provider payload builder, and the cheapest way
// to make such a ban green is to suppress it, at which point it enforces nothing.
//
// So this rule does not touch that form, and the classification that would be
// needed before it could is NOT DONE — see the report on #626. What the survey did
// establish, and what is worth writing down because it is the part that is not
// obvious:
//
//   * The dynamic-load/emit family has exactly ONE real site in `src/`, and it is
//     in the allowed family: `CollectiblePluginLoadContext` in
//     `src/Harbor.Plugins.Compilation/`, which derives from `AssemblyLoadContext`.
//     Every other occurrence of these names in `src/` is inside a `///` comment.
//   * `apps/` has ZERO. Not "none found" — zero, in both entry points.
//   * One real Type-level reflection outside the plugins does exist and is NOT
//     covered by this rule: `src/Harbor.Desktop.Shared/Locators/ViewModelLocator.cs`
//     calls `typeof(ServiceProviderServiceExtensions).GetMethods()` and
//     `MakeGenericMethod` to build a service call. That is member-by-reflection,
//     not assembly loading, so banning it here would be widening the rule on a
//     guess about intent. It is recorded as out of scope, not as approved.
//
// THE PLUGIN EXCEPTION IS THE PRODUCT
// -----------------------------------
// CS-source plugins are compiled in-memory with Roslyn and run with full trust;
// DLL plugins are loaded at run time so a plugin swap does not require a host
// restart. `Harbor.Plugins.Host` is the separate-process boundary. None of that is
// debt to be cleaned up, which is exactly why the allowance is written as a
// deliberate permission with a reason: so the next contributor reads it as a
// decision and does not "fix" it like a bug.
//
// THE RULE IS THE COMPILER'S NOW (#1844)
// ---------------------------------------
// This file used to IMPLEMENT the rule: a regex over the source text, a
// comment stripper, a prefix baseline, ten planted controls, a discovery floor.
// The compiler already knows how to say "this symbol is banned here", and says it
// better: the ban is now the file `BannedSymbols.DynamicCode.txt`, listed in the
// product projects' `<AdditionalFiles>`, and enforced by the RS0030 diagnostic of
// `Microsoft.CodeAnalysis.BannedApiAnalyzers` (already referenced
// solution-wide — this migration adds no package).
//
// What the compiler buys, and what the regex could not do at all:
//
//   * It matches the SYMBOL. `Assembly . Load ( bytes )` with spaces, a `using`
//     alias, a fully qualified name — all the same symbol to the compiler, and
//     the regex needed a planted control for each spelling.
//   * It does not read comments. The regex needed a stripper to keep prose
//     about `Assembly.Load` from being an offense; the compiler keeps comments
//     in trivia, where no analyzer looks.
//   * It understands overloads. `M:System.Reflection.Assembly.Load` with no
//     parameter list matches NOTHING (Roslyn's ID resolver requires the
//     parameter count to match exactly), so every overload is listed
//     explicitly — and the test below fails when the list stops covering the
//     framework's own public surface.
//
// THE EXCEPTION IS EXPRESSED BY THE TOOL
// --------------------------------------
// The plugin family is exempt because the ban file is NOT added to the plugin
// projects' `<AdditionalFiles>`: the compiler never sees the rule there. That
// is the absence of a file, which a tool enforces, rather than a baseline a
// scanner subtracts, which a reader has to believe.
//
// WHAT IS LEFT IN THIS FILE, AND WHY
// ----------------------------------
// The compiler cannot express the parts that are not about symbols:
//
//   * the CONVENTION, in prose, so the next contributor knows it is a decision;
//   * the ALLOWANCE TABLE and its reason (`PluginAllowance`), shared with the
//     trim-safety rules;
//   * the PROOF that the compiler-side configuration is live, which is what the
//     tests below are for;
//   * the TEXT MATCHER, which no longer enforces anything and survives only as
//     the ORACLE of the #1729 pilot.
//
// THE TEXT MATCHER IS THE PILOT'S ORACLE, NOT A RULE
// --------------------------------------------------
// `Roslyn/ArchAnalyzerPromotionTests.cs` measures a Roslyn analyzer against a text rule: a
// differential needs BOTH implementations, and a copy of the pattern would be a
// second implementation rather than a comparison. That is why `Rule` below — the
// matcher, its baseline and its controls — is still here after the enforcement
// moved to the compiler.
//
// It gates NOTHING. No test in this project runs this scan to decide whether
// the product tree is clean: the compiler does that, on every build, for every
// project, and it does it on the SYMBOL rather than on the spelling. The two
// tests at the end of this file keep the oracle honest (a matcher that stopped
// matching would make the pilot's differential vacuous), and that is the whole
// of its remaining job. When the pilot is retired, this matcher goes with it.
//
// NON-VACUITY
// -----------
// A guard that names a path nobody occupies is the NetArchTest trap in a new coat:
// `NotHaveDependencyOn("Harbor.Scripting")` is green forever because the name
// matches nothing. This file closes that in five places —
// `NonVacuity_The_Compiler_Bans_A_Synthetic_Violation` runs the build's own
// analyzer over a planted offender, `NonVacuity_The_Compiler_Stays_Silent_On_The_Near_Misses`
// runs it over the shapes that must NOT be flagged, `The_Ban_File_Covers_Every_Public_Assembly_Load_Overload`
// proves the list is complete against the framework's own surface,
// `The_Ban_File_Reaches_Every_Product_Project_Except_The_Plugin_Family` proves the
// wiring, and `The_Plugin_Family_Code_Is_Banned_Only_Because_The_Tool_Exempts_It`
// proves the exemption is what saves the plugin family — and not a stale entry.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #626: the reflection convention, enforced by the compiler's
///     banned-API analyzer since #1844. Reflection is allowed in tests and
///     forbidden in product code, with the <c>src/Harbor.Plugins.*</c> family as
///     a deliberate, explained exception.
/// </summary>
public sealed class ReflectionConventionRule
{
    /// <summary>
    ///     The ban file, as the compiler sees it. The name is part of the
    ///     contract: the analyzer only reads files named <c>BannedSymbols.txt</c>
    ///     or <c>BannedSymbols.*.txt</c>.
    /// </summary>
    private const string BanFileName = BannedApiProbe.BanFileName;

    /// <summary>
    ///     The property in Directory.Build.props that holds the project-name
    ///     prefix the plugin family is exempt under. Read by the test below so
    ///     the wiring and the test cannot drift apart.
    /// </summary>
    private const string ExemptPrefixPropertyName = "HarborDynamicCodeBanExemptProjectNamePrefix";

    /// <summary>
    ///     The ban list, pinned. Every entry is a documentation-comment ID the
    ///     analyzer resolves against the compilation; a change here is a change
    ///     to the convention, and it shows up in the diff as one.
    /// </summary>
    /// <remarks>
    ///     The seven <c>Assembly</c> entries are the seven public
    ///     <c>Load</c>/<c>LoadFrom</c>/<c>LoadFile</c> overloads of .NET 10. The
    ///     namespace entry covers <c>TypeBuilder</c> and <c>DynamicMethod</c>,
    ///     which are the two the convention names, and every other type in
    ///     <c>System.Reflection.Emit</c> besides — the capability is one, and a
    ///     guard that named only <c>TypeBuilder</c> would be satisfied by
    ///     switching to <c>AssemblyBuilder</c>.
    ///     <para>
    ///         <c>AppDomain.DefineDynamicAssembly</c> is deliberately NOT here:
    ///         it is not public API in .NET 10, so the compiler rejects the call
    ///         before any analyzer sees it, and an entry for an API that does
    ///         not exist resolves to no symbol at all. Its live spelling,
    ///         <c>System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly</c>,
    ///         is in the banned namespace.
    ///     </para>
    /// </remarks>
    private static readonly string[] DeclaredBanIds =
    [
        "M:System.Reflection.Assembly.Load(System.Byte[])",
        "M:System.Reflection.Assembly.Load(System.Byte[],System.Byte[])",
        "M:System.Reflection.Assembly.Load(System.Reflection.AssemblyName)",
        "M:System.Reflection.Assembly.Load(System.String)",
        "M:System.Reflection.Assembly.LoadFrom(System.String)",
        "M:System.Reflection.Assembly.LoadFrom(System.String,System.Byte[],System.Configuration.Assemblies.AssemblyHashAlgorithm)",
        "M:System.Reflection.Assembly.LoadFile(System.String)",
        "T:System.Runtime.Loader.AssemblyLoadContext",
        "N:System.Reflection.Emit",
        "T:System.Reflection.Emit.TypeBuilder",
        "T:System.Reflection.Emit.DynamicMethod",
    ];

    /// <summary>
    ///     A planted OFFENDER: one call of each load API the convention names.
    ///     The analyzer must report every one of them.
    /// </summary>
    private const string OffenderSource = """
        using System;
        using System.Reflection;
        using System.Reflection.Emit;
        using System.Runtime.Loader;

        namespace Probe
        {
            public sealed class Offender
            {
                public Assembly Load(byte[] bytes) => Assembly.Load(bytes);

                public Assembly LoadFrom(string path) => Assembly.LoadFrom(path);

                public Assembly LoadFile(string path) => Assembly.LoadFile(path);

                public DynamicMethod Emit() => new DynamicMethod("go", typeof(void), Type.EmptyTypes);

                public Type EmitNamespace() => typeof(OpCode);

                public Type ContextType => typeof(AssemblyLoadContext);
            }

            public sealed class Derived : AssemblyLoadContext
            {
            }
        }
        """;

    /// <summary>
    ///     The planted NEAR-MISSES: the shapes the convention deliberately does
    ///     NOT ban, and the prose about the shapes it does.
    /// </summary>
    private const string NearMissSource = """
        using System.Reflection;
        using System.Text.Json;

        namespace Probe
        {
            public sealed class Benign
            {
                /// <summary>Loads via <c>Assembly.Load(byte[])</c> — prose, not code.</summary>
                public string? Name(JsonElement element) => element.GetProperty("models").GetString();

                public string? FullName(Assembly assembly) => assembly.FullName;

                public Assembly? Current() => Assembly.GetExecutingAssembly();
            }
        }
        """;

    // =====================================================================
    // 1. The ban list: exact, complete, and free of dead entries.
    // =====================================================================

    /// <summary>
    ///     The ban file holds EXACTLY the declared list. A ban that is added
    ///     without being declared is a rule nobody reviewed; a declared ban that
    ///     is missing from the file is a rule that reads as enforced and is not.
    /// </summary>
    [Test]
    public async Task The_Ban_File_Holds_Exactly_The_Declared_List()
    {
        IReadOnlyList<string> actual = BannedApiProbe.BanFileDeclarationIds();

        await Assert.That(actual).IsEquivalentTo(DeclaredBanIds)
            .Because(
                "the ban list is the convention expressed to the compiler, so a change to it is a change "
                + "to the convention and must show up in the diff as a reviewed edit. Actual: "
                + string.Join(" | ", actual));
    }

    /// <summary>
    ///     Every entry RESOLVES to a real symbol. An entry that resolves to
    ///     nothing is inert configuration that reads like live policy: the
    ///     analyzer skips it without a word, and the ban it claims to express is
    ///     not enforced.
    /// </summary>
    [Test]
    public async Task Every_Ban_Entry_Resolves_To_A_Real_Symbol()
    {
        var dead = new List<string>();

        foreach (string id in BannedApiProbe.BanFileDeclarationIds())
        {
            if (BannedApiProbe.Resolve(id).Length == 0)
            {
                dead.Add(id);
            }
        }

        await Assert.That(dead).IsEmpty()
            .Because(
                "a banned-API entry that resolves to no symbol enforces nothing: the analyzer resolves "
                + "each entry against the compilation and skips the ones it cannot find. That is how "
                + "'AppDomain.DefineDynamicAssembly' — not public API in .NET 10 — would have become a "
                + "dead line that reads like a live ban. Dead entries: " + string.Join(" | ", dead));
    }

    /// <summary>
    ///     The list COVERS the framework: every public <c>Assembly</c> member
    ///     that loads an assembly is in it, with its exact parameter list.
    /// </summary>
    /// <remarks>
    ///     The completeness check exists because of the trap the ban file's own
    ///     header documents: an ID with no parameter list matches only the
    ///     parameterless overload, so <c>M:System.Reflection.Assembly.Load</c>
    ///     alone bans nothing at all. A list that is missing one overload is a
    ///     list with a hole in it, and nothing else in this file would notice.
    /// </remarks>
    [Test]
    public async Task The_Ban_File_Covers_Every_Public_Assembly_Load_Overload()
    {
        IReadOnlyList<string> publicOverloads = BannedApiProbe.PublicMemberIds(
            typeof(System.Reflection.Assembly),
            "Load",
            "LoadFrom",
            "LoadFile");

        await Assert.That(publicOverloads).IsNotEmpty()
            .Because(
                "the framework's own surface is what the ban list has to cover, and an empty reading of it "
                + "would make the comparison below vacuous: a list with no framework to compare against "
                + "passes every completeness check");

        IReadOnlyList<string> declared = BannedApiProbe.BanFileDeclarationIds();
        var missing = publicOverloads.Where(id => !declared.Contains(id, StringComparer.Ordinal)).ToList();

        await Assert.That(missing).IsEmpty()
            .Because(
                "every public overload that loads an assembly has to be named, with its parameter list, "
                + "or the ban has a hole in it that a caller can walk through. Missing: "
                + string.Join(" | ", missing));
    }

    // =====================================================================
    // 2. Non-vacuity: the compiler's own analyzer, on planted source.
    // =====================================================================

    /// <summary>
    ///     THE NEGATIVE CONTROL. The build's own RS0030 analyzer, run over a
    ///     planted offender, reports every capability the convention names.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Compiler_Bans_A_Synthetic_Violation()
    {
        IReadOnlyList<string> reported = await BannedApiProbe.ReportAsync(OffenderSource, withBanFile: true);

        await Assert.That(reported).IsNotEmpty()
            .Because(
                "the ban is enforced by the compiler, so the only evidence it works is the analyzer "
                + "reporting a planted violation. An empty report means the ban file is not reaching the "
                + "compiler, the file name is wrong, or the list is empty — all three are the same bug "
                + "seen from three sides");

        foreach (string capability in new[]
                 {
                     "Assembly.Load(byte[])",
                     "Assembly.LoadFrom(string)",
                     "Assembly.LoadFile(string)",
                     "DynamicMethod",
                     "AssemblyLoadContext",
                     "System.Reflection.Emit",
                 })
        {
            await Assert.That(string.Join(" | ", reported)).Contains(capability)
                .Because(
                    "the convention names this capability, so the analyzer must report it. The planted "
                    + "source contains one of each; missing: " + capability);
        }
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The same analyzer, over the shapes the
    ///     convention deliberately does not ban, says nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Compiler_Stays_Silent_On_The_Near_Misses()
    {
        IReadOnlyList<string> reported = await BannedApiProbe.ReportAsync(NearMissSource, withBanFile: true);

        await Assert.That(reported).IsEmpty()
            .Because(
                "a JSON key is not a CLR member, an ordinary Assembly member is not a load, and prose "
                + "about a banned API is not a call. A ban that fires on any of those gets deleted by the "
                + "first person it annoys, and a deleted ban enforces nothing. Reported: "
                + string.Join(" | ", reported));
    }

    // =====================================================================
    // 3. The wiring: the ban reaches the product tree, and not the plugin family.
    // =====================================================================

    /// <summary>
    ///     The ban file is wired into every product project and into none of the
    ///     plugin family, and the plugin family is exactly the projects under
    ///     <c>src/Harbor.Plugins.</c>.
    /// </summary>
    [Test]
    public async Task The_Ban_File_Reaches_Every_Product_Project_Except_The_Plugin_Family()
    {
        string? repoRoot = RepoPaths.RepoRoot;
        await Assert.That(repoRoot).IsNotNull()
            .Because("the wiring lives in the checkout, and a run that cannot find the checkout cannot "
                   + "prove anything about it");
        string root = repoRoot!;

        XDocument props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));

        XElement? banItem = props
            .Descendants("AdditionalFiles")
            .FirstOrDefault(static e =>
                (e.Attribute("Include")?.Value ?? string.Empty)
                .EndsWith(BanFileName, StringComparison.Ordinal));

        await Assert.That(banItem).IsNotNull()
            .Because($"the ban file must be listed as an AdditionalFiles item, or the compiler never reads "
                   + $"it and the whole convention is decoration. Expected an AdditionalFiles item ending "
                   + $"in '{BanFileName}'.");

        string? condition = banItem!.Attribute("Condition")?.Value;
        await Assert.That(condition).IsNotNull()
            .Because("the item must carry the exemption condition, or the ban reaches the plugin family "
                   + "as well and the plugin family stops building");

        await Assert.That(condition!).Contains("$(MSBuildProjectName.StartsWith(")
            .Because("the exemption is a PROJECT-NAME predicate, so it does not depend on how the "
                   + "checkout spells a path separator. Actual condition: " + condition);

        XElement? prefixElement = props
            .Descendants(ExemptPrefixPropertyName)
            .FirstOrDefault();

        await Assert.That(prefixElement).IsNotNull()
            .Because($"the exempt prefix must be a named property ({ExemptPrefixPropertyName}), so the "
                   + "wiring and this test read ONE fact from ONE place");

        string prefix = prefixElement!.Value.Trim();

        await Assert.That(condition!).Contains($"'$({ExemptPrefixPropertyName})'")
            .Because("the condition must consult the declared prefix property rather than repeat the "
                   + "prefix inline, or there are two spellings of one fact. Actual condition: "
                   + condition);

        IReadOnlyList<string> productProjects = ProductProjectNames(root);
        await Assert.That(productProjects).IsNotEmpty()
            .Because("the set of product projects is what the wiring is asserted against, and an empty "
                   + "reading of it would make the assertion vacuous");

        var failures = new List<string>();
        foreach (string projectName in productProjects)
        {
            bool exemptByCondition = projectName.StartsWith(prefix, StringComparison.Ordinal);
            bool inPluginFamily = projectName.StartsWith("Harbor.Plugins.", StringComparison.Ordinal);

            if (exemptByCondition != inPluginFamily)
            {
                failures.Add(
                    $"project '{projectName}' is {(exemptByCondition ? "exempt" : "covered")} by the "
                    + $"condition but {(inPluginFamily ? "is" : "is not")} in the plugin family. The two "
                    + "spellings of the exemption must agree on every project that exists.");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(
                "the compiler-side exemption is a project-name prefix and the scan-side one is a "
                + "directory prefix. If they disagree, a project is either built twice over or exempt "
                + "twice. Mismatches: " + string.Join(" | ", failures));
    }

    /// <summary>
    ///     THE PLUGIN-FAMILY CONTROL. The plugin family's real code trips the
    ///     ban when the ban file is applied, and is silent when it is not — so
    ///     the exemption is the tool's doing, not a stale entry nobody reads.
    /// </summary>
    [Test]
    public async Task The_Plugin_Family_Code_Is_Banned_Only_Because_The_Tool_Exempts_It()
    {
        const string PluginSource = "src/Harbor.Plugins.Compilation/CollectiblePluginLoadContext.cs";

        string? repoRoot = RepoPaths.RepoRoot;
        await Assert.That(repoRoot).IsNotNull()
            .Because("the plugin family's own source is the evidence, and a run that cannot find the "
                   + "checkout has no evidence to look at");
        string root = repoRoot!;

        string pluginPath = Path.Combine(root, PluginSource);
        string pluginCode = File.ReadAllText(pluginPath);

        IReadOnlyList<string> withBan = await BannedApiProbe.ReportAsync(pluginCode, withBanFile: true);
        IReadOnlyList<string> withoutBan = await BannedApiProbe.ReportAsync(pluginCode, withBanFile: false);

        await Assert.That(withBan).IsNotEmpty()
            .Because(
                $"the plugin family's own code IS the capability — {PluginSource} derives from "
                + "AssemblyLoadContext — so it must trip the ban when the ban file is applied. If it "
                + "does not, the exemption is not doing any work: the file has simply stopped matching "
                + "the thing it was written for");

        await Assert.That(withoutBan).IsEmpty()
            .Because(
                "the plugin family is exempt because the ban file is not in its AdditionalFiles, so the "
                + "compiler sees no rule there. If the analyzer reports anyway, the exemption has moved "
                + "somewhere it does not apply. Reported: " + string.Join(" | ", withoutBan));
    }

    // =====================================================================
    // 4. The allowance is a decision, not a widening.
    // =====================================================================

    /// <summary>
    ///     The exception must exist, must say why, must resolve to real
    ///     directories, and must not quietly reach past the plugin family. A
    ///     prefix nobody occupies is the NetArchTest trap: it reads as "enforced
    ///     on the one allowed place" while enforcing nothing at all, which is
    ///     the exact failure the issue names.
    /// </summary>
    [Test]
    public async Task The_Plugin_Allowance_Is_NonEmpty_Scoped_And_Explained()
    {
        var failures = new List<string>();

        if (PluginAllowance.AllowedProjectPrefixes.Length == 0)
        {
            failures.Add(
                "the allowance table is empty. The plugin exception is real and deliberate, so an "
                + "empty table means the exemption was deleted and the rule now reads as \"no "
                + "reflection anywhere\", including in the code whose job is plugins.");
        }

        string? repoRoot = RepoPaths.RepoRoot;
        await Assert.That(repoRoot).IsNotNull()
            .Because("the allowance is asserted against the projects that exist, and a run that cannot "
                   + "find the checkout has no projects to assert against");
        string root = repoRoot!;

        // Every real project directory, so "this prefix matches something" can be
        // answered from the repository rather than assumed.
        var realProjectDirs = RepoPaths.EnumerateSrcProjects()
            .Select(static file => Path.GetDirectoryName(file) ?? string.Empty)
            .Where(static dir => dir.Length > 0)
            .Select(dir => Path.GetRelativePath(root, dir).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        foreach ((string prefix, ExemptionReason.Row _) in PluginAllowance.AllowedProjectPrefixes)
        {
            var matched = realProjectDirs
                .Where(dir => dir.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(static dir => dir, StringComparer.Ordinal)
                .ToList();

            if (matched.Count == 0)
            {
                failures.Add(
                    $"prefix '{prefix}' matches no real project directory. An exemption that "
                    + "matches nothing is a satisfied constraint enforcing nothing: the rule would be "
                    + "green because it is looking at no file, not because the code is clean.");
                continue;
            }

            foreach (string dir in matched.Where(dir => !dir.StartsWith("src/Harbor.Plugins.", StringComparison.Ordinal)))
            {
                failures.Add(
                    $"prefix '{prefix}' reaches '{dir}', which is not in the plugin family. The "
                    + "exemption is one fact — plugins load code the host did not compile — so a second "
                    + "one is a change to the CONVENTION, not a row to add. Change the rule text and the "
                    + "doc in the same review, so the widening is visible instead of silent.");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("an unexplained or unoccupied exemption is a permission nobody chose. "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     The allowance must be OCCUPIED. An exception that no code uses is not
    ///     an exception, it is a comment with a table around it — and if the
    ///     construct this file bans is ever renamed, the rule keeps passing while
    ///     the thing it describes is gone.
    /// </summary>
    [Test]
    public async Task The_Plugin_Allowance_Is_Not_A_Dead_Prefix()
    {
        await Assert.That(PluginAllowance.IsAllowed("src/Harbor.Plugins.Compilation")).IsTrue()
            .Because("the plugin family is allowed to load code because it does");

        await Assert.That(PluginAllowance.IsAllowed("src/Harbor.Storage.Jsonl")).IsFalse()
            .Because("a prefix that matched everything would exempt the entire product tree, which is "
                   + "the widening the allowance test above refuses");
    }

    // =====================================================================
    // 5. The pilot's oracle stays honest.
    // =====================================================================

    /// <summary>
    ///     THE MATCHER CONTROL — the oracle's liveness. Handed synthetic
    ///     source, the matcher must report the forms that are the capability and
    ///     nothing else. It must stay silent on JSON property access, on
    ///     reflection prose, and on a non-dynamic <c>Assembly</c> member.
    /// </summary>
    /// <remarks>
    ///     This does not gate the convention — the compiler does that, on every
    ///     build. It keeps the #1729 pilot's differential from going vacuous: a
    ///     matcher that stopped matching would make "every file the text rule
    ///     sees is a file the analyzer sees" true for the wrong reason.
    /// </remarks>
    [Test]
    public async Task NonVacuity_The_Forbidden_Construct_Matcher_Fires_On_A_Planted_Offender_Only()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because("six of the ten planted snippets are the capability and four are not — JSON "
                   + "property access, a doc comment, an ordinary Assembly member and a FieldInfo "
                   + "read. A matcher that reported fewer is vacuous; one that reported more flags "
                   + "JSON keys and prose, and the first person to hit that deletes the guard. "
                   + string.Join("; ", failures));
    }

    /// <summary>
    ///     THE SCOPE CONTROL, and the proof that "tests are allowed" is a
    ///     DECISION rather than an accident of what gets scanned.
    ///     <see cref="SourceScan" /> excludes <c>tests/</c> for every gate in this
    ///     project, and the pilot's oracle leans on that: a literal reading of "no
    ///     reflection" would forbid this very file, which reflects over the
    ///     unions to prove exhaustiveness, and every architecture test that
    ///     loads an assembly to inspect its references.
    ///     <para>
    ///         The exclusion is load-bearing if, and only if, the excluded tree really
    ///         does contain what the rule bans. So this test looks for a forbidden
    ///         construct in <c>tests/</c> and REQUIRES one:
    ///         <c>tests/Harbor.Architecture.Tests/GlobalUsings.cs</c> calls
    ///         <c>Assembly.Load</c> in <c>ArchitectureTestHelpers.LoadHarborAssemblies</c>.
    ///         If that ever stops being true the exclusion has become indistinguishable
    ///         from an oversight and the doc comment above is lying.
    ///     </para>
    /// </summary>
    [Test]
    public async Task NonVacuity_Discovery_Sees_The_Product_Tree_And_Excludes_Tests_On_Purpose()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);
        await Assert.That(discovery).IsEmpty()
            .Because("without a repository root the scan finds no files and every rule in this file "
                   + "passes for the wrong reason. RepoPaths degrades to empty rather than throwing, so "
                   + "this is the one assertion standing between a green run and a blind one. "
                   + string.Join("; ", discovery));

        var scannedTests = ScanRunner.ScopeFiles(Rule)
            .Where(static file => file.StartsWith("tests/", StringComparison.Ordinal))
            .ToList();

        await Assert.That(scannedTests).IsEmpty()
            .Because("the convention ALLOWS reflection in tests, so a test file must not be part of "
                   + "the product scan. If one is, either SourceScan's filter changed or a project "
                   + "moved, and the rule's own scope statement is now false: " + string.Join(", ", scannedTests));

        // The other half: the excluded tree must really hold what the rule bans,
        // or excluding it is an accident rather than a decision.
        const string KnownReflectiveTestFile = "tests/Harbor.Architecture.Tests/GlobalUsings.cs";
        string? reflectiveTestFile = Path.Combine(RepoPaths.RepoRoot ?? ".", KnownReflectiveTestFile);
        string? reflectiveSource = SourceScan.TryReadAllText(reflectiveTestFile);

        await Assert.That(reflectiveSource).IsNotNull()
            .Because($"{KnownReflectiveTestFile} is the file that makes the tests/ exclusion load-bearing: "
                   + "it calls Assembly.Load in ArchitectureTestHelpers.LoadHarborAssemblies. If it moved "
                   + "or was renamed, update this file — do not let the exclusion quietly become "
                   + "unfalsifiable");

        List<ScanHit> reflectiveHits = reflectiveSource is null
            ? []
            : ScanRunner.Collect(Rule, [(KnownReflectiveTestFile, reflectiveSource)]);

        await Assert.That(reflectiveHits.Count).IsGreaterThan(0)
            .Because($"{KnownReflectiveTestFile} no longer contains a forbidden construct, so excluding "
                   + "tests/ from this rule no longer excludes anything. Either the helper was rewritten "
                   + "without Assembly.Load — in which case say so here, because the reason the convention "
                   + "carves out tests is now unproven — or the construct was renamed and this matcher is "
                   + "blind to it.");
    }

    // =====================================================================
    // 6. The pilot's oracle: the text rule as it was.
    // =====================================================================

    /// <summary>
    ///     The dynamic-code family, as one alternation. Each branch is a construct
    ///     that loads or manufactures code at run time; whitespace is tolerated
    ///     around the dot so `Assembly . Load(` is caught too.
    /// </summary>
    private static readonly Regex ForbiddenConstruct = new(
        @"\bAssembly\s*\.\s*Load\s*\("
        + @"|\bAssembly\s*\.\s*LoadFrom\s*\("
        + @"|\bAssembly\s*\.\s*LoadFile\s*\("
        + @"|\bAssemblyLoadContext\b"
        + @"|\bSystem\s*\.\s*Reflection\s*\.\s*Emit\b"
        + @"|\bReflection\s*\.\s*Emit\b"
        + @"|\bTypeBuilder\b"
        + @"|\bDynamicMethod\b"
        + @"|\bAppDomain\s*\.\s*DefineDynamicAssembly\b",
        RegexOptions.Compiled);

    private const string RuleId = "REFLECTION-DYNAMIC-CODE";

    /// <summary>The rule as data: one shape, two prefix baselines, ten controls, a floor.</summary>
    /// <remarks>
    ///     internal since the #1729 pilot: the pilot's differential test compares THIS
    ///     rule's matcher against the Roslyn analyzer, and a differential needs the old
    ///     rule's own verdict — a copy of its pattern would be a second implementation,
    ///     not a comparison.
    ///     <para>
    ///         Since #1844 this is NOT the enforcement: the ban is
    ///         <c>BannedSymbols.DynamicCode.txt</c> and the compiler's RS0030
    ///         diagnostic, on every build, for every product project. What is left
    ///         here is the oracle the pilot measures the analyzer against, and the
    ///         two tests above keep it from rotting. When the pilot is retired, this
    ///         goes with it.
    ///     </para>
    ///     <para>
    ///         The second baseline row is #1842's: the analyzer that ENFORCES the
    ///         ban lives in the product tree, and it has to name the banned APIs to
    ///         match them. A call is the capability; a string constant is the
    ///         matcher's operand. The compiler-side rule and the analyzer both say
    ///         that file is clean, and only a scan that cannot tell a constant from
    ///         a call says otherwise.
    ///     </para>
    /// </remarks>
    internal static readonly ScanRule Rule = new()
    {
        Id = "ReflectionConvention",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                RuleId,
                ForbiddenConstruct,
                "loading code at run time is invisible to NativeAOT and to every ProjectReference rule — "
                + "unless this file genuinely needs it, say so by adding an explained row to this rule's "
                + "baseline (the plugin family's row lives in PluginAllowance.AllowedProjectPrefixes, "
                + "which the trim-safety rules read too), and be ready to justify it in review."),
        ],
        Baseline =
        [
            .. PluginAllowance.AllowedProjectPrefixes.Select(static a => new ScanBaseline(
                a.Prefix,
                IsPrefix: true,
                a.Allowance.Reason,
                a.Allowance.TrackedBy)),

            // #1842: the analyzer that enforces the ban lives in the product tree
            // and names the banned APIs as DATA — the string constants its matcher
            // compares against. A call is the capability; a constant is the
            // matcher's operand, and the compiler-side ban (RS0030) and the
            // analyzer that replaced this scan both say the file is clean.
            new ScanBaseline(
                "src/Harbor.Arch.Analyzers",
                IsPrefix: true,
                "The rule that enforces the dynamic-code ban lives here, so the banned names appear in this "
                + "project as string constants the matcher compares against — not as calls the code makes. "
                + "The compiler-side ban (RS0030) and the analyzer that replaced this scan both say the file "
                + "is clean; a text scan cannot tell a constant from a call. Not debt — the one place the "
                + "spellings must appear.",
                TrackedBy: null),
        ],
        Controls =
        [
            new ScanControl("load.cs", "var a = Assembly.Load(bytes);", RuleId),
            new ScanControl("loadfrom.cs", "var a = Assembly.LoadFrom(path);", RuleId),
            new ScanControl("context.cs", "public sealed class Ctx : AssemblyLoadContext { }", RuleId),
            new ScanControl("emit.cs", "var t = new TypeBuilder(\"T\", attrs);", RuleId),
            new ScanControl("dynamic.cs", "var m = new DynamicMethod(\"go\", typeof(void), Type.EmptyTypes);", RuleId),
            new ScanControl("spaced.cs", "var a = Assembly . Load ( bytes );", RuleId),
            new ScanControl("json.cs", "var name = element.GetProperty(\"models\")", null),
            new ScanControl("prose.cs", "/// Loads via Assembly.Load(byte[]) — see IPluginCompiler.", null),
            new ScanControl("benign.cs", "var name = assembly.FullName;", null),
            new ScanControl("field.cs", "var v = field.GetValue(instance);", null),
        ],
        MinHits = 100,
    };

    /// <summary>
    ///     The product project names the wiring is asserted against: the
    ///     <c>src/</c> projects and the <c>apps/</c> entry points.
    /// </summary>
    private static IReadOnlyList<string> ProductProjectNames(string root)
    {
        var names = new List<string>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            names.Add(Path.GetFileNameWithoutExtension(csproj));
        }

        string apps = Path.Combine(root, "apps");
        if (Directory.Exists(apps))
        {
            foreach (string csproj in Directory.GetFiles(apps, "*.csproj", SearchOption.AllDirectories))
            {
                if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(Path.GetFileNameWithoutExtension(csproj));
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
