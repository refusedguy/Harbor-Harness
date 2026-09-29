// ReflectionConventionRule.cs — issue #626.
//
// THE CONVENTION, WRITTEN DOWN
// ----------------------------
//   Reflection is allowed in tests. It is forbidden in shipped product code,
//   except in the `src/Harbor.Plugins.*` family, which needs it deliberately.
//
// Until this file it existed only as an emergent property of the `ReflectionAnalyzers`
// (REFL*) diagnostics plus the zero-warning bar. A contributor had no way to tell it
// from an accident, which means the next person to add `Assembly.Load` somewhere new
// has no reason to know it is the thing the whole codebase is built to avoid.
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
// NON-VACUITY
// -----------
// A guard that names a path nobody occupies is the NetArchTest trap in a new coat:
// `NotHaveDependencyOn("Harbor.Scripting")` is green forever because the name
// matches nothing. This file closes that in three places —
// `The_Plugin_Allowance_Is_Not_A_Dead_Prefix` requires the allowance to be
// OCCUPIED by a real forbidden construct, `NonVacuity_The_Forbidden_Construct_Matcher_Fires_On_A_Planted_Offender_Only`
// runs the matcher over synthetic source, and
// `NonVacuity_Discovery_Sees_The_Product_Tree_And_Excludes_Tests_On_Purpose` proves
// the `tests/` exclusion is load-bearing rather than incidental (see there).

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #626: the reflection convention, enforced. Reflection is allowed in
///     tests and forbidden in product code, with the <c>src/Harbor.Plugins.*</c>
///     family as a deliberate, explained exception.
/// </summary>
public sealed class ReflectionConventionRule
{
    /// <summary>
    ///     The project-directory prefix the convention exempts, and why. One row
    ///     because the exemption is one architectural fact — plugins run code the
    ///     host did not compile — not one per project. The rule therefore cannot
    ///     be widened by appending a row: a new prefix is a new row, and a new row
    ///     has to be justified in review (see
    ///     <see cref="The_Plugin_Allowance_Is_NonEmpty_Scoped_And_Explained" />).
    /// </summary>
    private static readonly (string Prefix, ExemptionReason.Row Allowance)[] AllowedProjectPrefixes =
    [
        (
            "src/Harbor.Plugins.",
            new ExemptionReason.Row(
                "Loading code at run time IS the plugin product: CS-source plugins are compiled "
                + "in-memory by Roslyn and DLL plugins are loaded so a swap needs no host restart, "
                + "which is why Harbor.Plugins.Host exists as a separate process. Not debt — the one "
                + "place the capability is wanted. Docs: docs/ARCHITECTURE_LAYERS.md §5.8.",
                TrackedBy: null)),
    ];

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

    /// <summary>
    ///     Repo-relative project directory of a repo-relative file path, e.g.
    ///     <c>src/Harbor.Plugins.Compilation</c>. Only ever called on the OUTPUT of
    ///     <see cref="SourceScan.Relative" />: re-relativising a relative path
    ///     resolves it against the test bin directory, which is how a rule ends up
    ///     comparing "src/..." against "../../bin/Debug/net10.0/src/..." and matching
    ///     nothing.
    /// </summary>
    private static string ProjectDirOf(string repoRelativeFile) =>
        Path.GetDirectoryName(repoRelativeFile)?.Replace('\\', '/') ?? string.Empty;

    /// <summary>Whether <paramref name="projectDir" /> sits under one of the allowed prefixes.</summary>
    private static bool IsAllowed(string projectDir) =>
        AllowedProjectPrefixes.Any(a => projectDir.StartsWith(a.Prefix, StringComparison.Ordinal));

    /// <summary>
    ///     Every forbidden construct in the named files. A file that cannot be read
    ///     is skipped, not reported: that is a discovery problem, and the discovery
    ///     self-check below is what catches it.
    /// </summary>
    private static List<Hit> Scan(IEnumerable<string> files)
    {
        var sources = new List<(string DisplayPath, string Source)>();
        foreach (string file in files)
        {
            if (SourceScan.TryReadAllText(file) is { } text)
            {
                sources.Add((SourceScan.Relative(file), text));
            }
        }

        return ScanSource(sources);
    }

    /// <summary>
    ///     The matcher itself, over (display path, source) pairs so the non-vacuity
    ///     control can hand it synthetic source without touching the disk — and so a
    ///     synthetic name comes back out verbatim instead of being relativised
    ///     against a bin directory it does not live in.
    ///     Comments are stripped first, preserving line count, so a `///` sentence
    ///     ABOUT <c>Assembly.Load</c> — of which <c>src/</c> has several, and the
    ///     plugin exception is documented in them — cannot report itself as a
    ///     violation. That is the difference between a rule and a grep: a grep
    ///     has to be re-read every time somebody writes a comment, and the first
    ///     time it cries wolf it gets deleted.
    /// </summary>
    private static List<Hit> ScanSource(
        IEnumerable<(string DisplayPath, string Source)> sources)
    {
        var hits = new List<Hit>();

        foreach ((string displayPath, string source) in sources)
        {
            string projectDir = ProjectDirOf(displayPath);
            string[] lines = SourceScan.StripComments(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = ForbiddenConstruct.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add(new Hit(displayPath, projectDir, i + 1, match.Value.Trim()));
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     One forbidden construct. <see cref="ProjectDir" /> is carried on the hit
    ///     rather than recomputed at the call site, because the two call sites hold
    ///     different things — a repo-relative path from the scan, a bare name from
    ///     the synthetic control — and a helper that cannot tell them apart is how
    ///     the allowance quietly stops matching.
    /// </summary>
    /// <param name="File">Repo-relative (or synthetic) file name.</param>
    /// <param name="ProjectDir">Repo-relative project directory.</param>
    /// <param name="Line">1-based line in the comment-stripped source.</param>
    /// <param name="Text">The matched construct, trimmed.</param>
    private readonly record struct Hit(string File, string ProjectDir, int Line, string Text);

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     THE CONVENTION. No shipped product file outside
    ///     <c>src/Harbor.Plugins.*</c> loads an assembly or emits IL. Today the
    ///     foreign count is zero and the allowed count is one, which is the fact
    ///     #626 asked to be pinned: the rule was satisfied by nobody enforcing
    ///     it, and an unenforced rule is a coincidence with an expiry date.
    /// </summary>
    [Test]
    public async Task Reflection_May_Not_Appear_In_Product_Code_Outside_The_Plugin_Family()
    {
        var offenders = new List<string>();
        foreach (Hit hit in Scan(SourceScan.EnumerateProductCsFiles()))
        {
            if (IsAllowed(hit.ProjectDir))
            {
                continue;
            }

            offenders.Add($"{hit.File}:{hit.Line}  {hit.Text}");
        }

        await Assert.That(offenders).IsEmpty()
            .Because("AGENTS.md §Architecture decisions requires NativeAOT-readiness — no "
                   + "reflection emit, no Assembly.Load — and a run-time-loaded assembly appears in no "
                   + "ProjectReference, so no reference rule can see it. The one place this capability "
                   + "is wanted is the plugin family, where loading code at run time IS the product. If "
                   + "this file genuinely needs it, say so by adding an explained row to "
                   + "AllowedProjectPrefixes, and be ready to justify it in review. Offenders: "
                   + string.Join(" | ", offenders));
    }

    // =====================================================================
    // 2. The allowance is a decision, not a widening.
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

        if (AllowedProjectPrefixes.Length == 0)
        {
            failures.Add(
                "the allowance table is empty. The plugin exception is real and deliberate, so an "
                + "empty table means the exemption was deleted and the rule now reads as \"no "
                + "reflection anywhere\", including in the code whose job is plugins.");
        }

        failures.AddRange(
            ExemptionReason.RowsWithoutAReason(
                "ReflectionConventionRule.AllowedProjectPrefixes",
                AllowedProjectPrefixes.Select(static a => (a.Prefix, a.Allowance))));

        // Every real project directory, so "this prefix matches something" can be
        // answered from the repository rather than assumed.
        var realProjectDirs = SourceScan.EnumerateProductCsFiles()
            .Select(static file => ProjectDirOf(SourceScan.Relative(file)))
            .Where(static dir => dir.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach ((string prefix, ExemptionReason.Row _) in AllowedProjectPrefixes)
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
    ///     the thing it describes is gone. Requires a real forbidden construct
    ///     under an allowed prefix, which is
    ///     <c>src/Harbor.Plugins.Compilation/CollectiblePluginLoadContext.cs</c>
    ///     today.
    /// </summary>
    [Test]
    public async Task The_Plugin_Allowance_Is_Not_A_Dead_Prefix()
    {
        var allowedHits = Scan(SourceScan.EnumerateProductCsFiles())
            .Where(hit => IsAllowed(hit.ProjectDir))
            .ToList();

        await Assert.That(allowedHits.Count).IsGreaterThan(0)
            .Because("the plugin family is allowed to load code because it does; if no file under an "
                   + "allowed prefix contains a forbidden construct any more, the exemption is stale, "
                   + "and the rule has quietly become \"no reflection anywhere\" — or the construct was "
                   + "renamed and the guard is now matching nothing at all. Either way this is the "
                   + "assertion that would have noticed.");
    }

    // =====================================================================
    // 3. Non-vacuity: the matcher and the discovery must both be able to fail.
    // =====================================================================

    /// <summary>
    ///     THE MATCHER CONTROL. Handed synthetic source, the matcher must report
    ///     the four forms that are the capability and nothing else: a bare
    ///     <c>Assembly.Load</c>, the <c>AssemblyLoadContext</c> base clause, an
    ///     emitted type, and a construct hidden behind whitespace. It must stay
    ///     silent on JSON property access, on reflection prose, and on a
    ///     non-dynamic <c>Assembly</c> member.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Forbidden_Construct_Matcher_Fires_On_A_Planted_Offender_Only()
    {
        const string loadImage = "var a = Assembly.Load(bytes);";
        const string loadFrom = "var a = Assembly.LoadFrom(path);";
        const string loadContext = "public sealed class Ctx : AssemblyLoadContext { }";
        const string emit = "var t = new TypeBuilder(\"T\", attrs);";
        const string dynamicMethod = "var m = new DynamicMethod(\"go\", typeof(void), Type.EmptyTypes);";
        const string spacedOut = "var a = Assembly . Load ( bytes );";

        const string jsonAccess = "var name = element.GetProperty(\"models\")";
        const string prose = "/// Loads via Assembly.Load(byte[]) — see IPluginCompiler.";
        const string benignAssembly = "var name = assembly.FullName;";
        const string threadOnly = "var v = field.GetValue(instance);";

        var reported = ScanSource(
        [
            ("load.cs", loadImage),
            ("loadfrom.cs", loadFrom),
            ("context.cs", loadContext),
            ("emit.cs", emit),
            ("dynamic.cs", dynamicMethod),
            ("spaced.cs", spacedOut),
            ("json.cs", jsonAccess),
            ("prose.cs", prose),
            ("benign.cs", benignAssembly),
            ("field.cs", threadOnly),
        ])
            .Select(static hit => hit.File)
            .ToList();

        await Assert.That(reported.Count).IsEqualTo(6)
            .Because("six of the ten planted snippets are the capability. If the matcher reported "
                   + "fewer the rule is vacuous; if it reported more it is flagging JSON keys and "
                   + "prose, and the first person to hit that will delete the guard instead of "
                   + "working around it. Reported: " + string.Join(", ", reported));

        foreach (string expected in new[] { "context.cs", "dynamic.cs", "emit.cs", "load.cs", "loadfrom.cs", "spaced.cs" })
        {
            await Assert.That(reported.Contains(expected)).IsTrue()
                .Because($"'{expected}' is one of the dynamic-code forms the convention names; missing it "
                       + "means the matcher has a hole, and a hole in this guard is a hole in the rule");
        }

        foreach (string quiet in new[] { "benign.cs", "field.cs", "json.cs", "prose.cs" })
        {
            await Assert.That(reported.Contains(quiet)).IsFalse()
                .Because($"'{quiet}' is not the capability — JSON property access, a doc comment, an "
                       + "ordinary Assembly member and a FieldInfo read are four different things, and "
                       + "banning them is the failure mode this matcher is tuned against");
        }
    }

    /// <summary>
    ///     THE SCOPE CONTROL, and the proof that "tests are allowed" is a
    ///     DECISION rather than an accident of what gets scanned.
    ///     <see cref="SourceScan" /> excludes <c>tests/</c> for every gate in this
    ///     project, and this convention leans on that: a literal reading of "no
    ///     reflection" would forbid this very file, which reflects over the
    ///     unions to prove exhaustiveness, and every architecture test that
    ///     loads an assembly to inspect its references.
    ///     <para>
    ///     The exclusion is load-bearing if, and only if, the excluded tree really
    ///     does contain what the rule bans. So this test looks for a forbidden
    ///     construct in <c>tests/</c> and REQUIRES one:
    ///     <c>tests/Harbor.Architecture.Tests/GlobalUsings.cs</c> calls
    ///     <c>Assembly.Load</c> in <c>ArchitectureTestHelpers.LoadHarborAssemblies</c>.
    ///     If that ever stops being true the exclusion has become indistinguishable
    ///     from an oversight and the doc comment above is lying.
    ///     </para>
    /// </summary>
    [Test]
    public async Task NonVacuity_Discovery_Sees_The_Product_Tree_And_Excludes_Tests_On_Purpose()
    {
        IReadOnlyList<string> product = SourceScan.EnumerateProductCsFiles();

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a repository root the scan finds no files and every rule in this file "
                   + "passes for the wrong reason. RepoPaths degrades to empty rather than throwing, so "
                   + "this is the one assertion standing between a green run and a blind one.");

        await Assert.That(product.Count).IsGreaterThan(100)
            .Because("the product trees are src/ and apps/ together; a count this low means discovery "
                   + "is broken rather than the code being clean");

        var scannedTests = product
            .Where(static file => SourceScan.Relative(file).StartsWith("tests/", StringComparison.Ordinal))
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

        List<Hit> reflectiveHits = reflectiveSource is null
            ? []
            : ScanSource([(KnownReflectiveTestFile, reflectiveSource)]);

        await Assert.That(reflectiveHits.Count).IsGreaterThan(0)
            .Because($"{KnownReflectiveTestFile} no longer contains a forbidden construct, so excluding "
                   + "tests/ from this rule no longer excludes anything. Either the helper was rewritten "
                   + "without Assembly.Load — in which case say so here, because the reason the convention "
                   + "carves out tests is now unproven — or the construct was renamed and this matcher is "
                   + "blind to it.");
    }
}
