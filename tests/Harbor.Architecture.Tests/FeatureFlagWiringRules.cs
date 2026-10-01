// FeatureFlagWiringRules.cs — GUARD for issue #828.
//
// WHAT #828 ASKED, AND WHAT IT DID NOT DECIDE
// -------------------------------------------
// `apps/Harbor.App.Cli/Harbor.App.Cli.csproj` offers two ways to turn NativeAOT
// on and they are not equivalent. `-p:HarborWithAot=true` sets PublishAot (and
// with it PublishTrimmed) plus TrimMode=full and StripSymbols — so it does trim,
// and the issue title's "strips nothing" is loose: what it does not strip is
// whole *features*, because the four granular flags are conditioned on
// $(HARBOR_MINIMAL) alone. #829 replaced the comment that used to claim
// otherwise. Whether recipe 2 *should* strip the features is a design call that
// #829 explicitly deferred to #413/#48.
//
// This file does not decide it either, and asserts nothing about which recipe is
// correct. Adding a workflow that publishes AOT is out of scope twice over: a
// different issue owns it (#413), and a new CI gate is not what the feature
// freeze (#555) wants arriving here.
//
// WHAT THIS FILE DOES INSTEAD
// ---------------------------
// #747 was "a comment named a cleanliness the XML had configured away". The shape
// of that defect is a build switch that exists, is advertised in a header, and
// has no body — and nothing in the repo could tell, because MSBuild evaluates it
// and carries on. Reading the XML is enough to tell. Two invariants:
//
//   1. A `HarborWith*` flag the CLI declares must CONDITION something: an
//      ItemGroup or PropertyGroup whose Condition tests `$(Flag)`. Without one,
//      setting the flag excludes no ProjectReference and sets no property, while
//      the csproj header advertises it as removing a category of them.
//
//   2. A symbol a feature flag DEFINES (a DefineConstants line conditioned on
//      that flag) must be READ by an `#if`/`#elif` in product code. Otherwise the
//      flag looks wired at the C# level while branching on nothing.
//
// Both are permissive by design: a flag or symbol that is genuinely not wired
// carries a row in a table stating why, checked through `ExemptionReason`. That
// is the shape `AotBlockDemotionRules` uses for the demotion list, and it is the
// only shape that lets a guard land on a repo with real unfixed holes instead of
// failing until somebody happens to notice them.
//
// THE ROWS THAT EXIST ARE REAL HOLES, NOT EXCUSES
// ------------------------------------------------
//   * `HarborWithAllTools` — the csproj header calls it "(future) keeps only 6
//     core tools", and no ItemGroup anywhere in the build is conditioned on it.
//     It excludes no ProjectReference today. Whether to wire one is a feature
//     decision, not a defect in the flag, so the row says so rather than
//     pretending the wiring exists.
//
//   * `HARBOR_WITH_AOT` — defined by the HarborWithAot flag, read by no `#if` in
//     the repository.
//
//   * `HARBOR_MINIMAL` — defined by the four flags (CLI and Harbor.Hosting both)
//     and read by no `#if` anywhere. This is the row worth the file: both csproj
//     comments say it preserves "the existing `#if HARBOR_MINIMAL` code path in
//     HostBuilder.cs" / "the existing `#if HARBOR_MINIMAL` semantics", and
//     HostBuilder.cs has no such directive. Its only occurrences repo-wide are
//     prose, a doc comment, a code comment, and two user-facing string literals.
//     That comment is the #747 shape again — a claim about code the code does not
//     carry — and it is what the second invariant makes checkable.
//
// WHY THERE IS NO RULE ON WHAT A COMMENT MAY SAY
// ----------------------------------------------
// Deliberately none, and `AotBlockDemotionRules`' header records the experiment
// that decided it: a rule forbidding a phrase while a list is non-empty failed
// against the honest replacement text, whose own first sentence contained the
// banned phrase. A check that a truthful sentence fails and a false one passes
// by rewording enforces wording, not state. The two invariants above read the
// XML and the preprocessor, and neither can be satisfied by editing a comment —
// which is also why they outlive the wording they were written next to.
//
// NON-VACUITY
// -----------
// A guard whose subject is empty is green for no reason. Closed in four places:
//   * NonVacuity_The_Real_Project_Files_And_Flags_Are_Found — the CLI csproj is
//     found, parses, and still declares at least one flag; the build-file walk
//     returns something. Without this a renamed project degrades every rule to
//     "nothing to check".
//   * The_Rows_Are_Not_Stale — a row whose flag or symbol HAS since been wired is
//     a permission for a problem that no longer exists, and has to be deleted.
//   * NonVacuity_The_Flag_Reader_Reports_A_Flag_Nobody_Conditions — synthetic
//     csproj: a flag conditioned by nobody is reported, and the real
//     HARBOR_MINIMAL-conditioned shape stays silent. The control matters most,
//     because a reader that treated a flag's own declaration as its body would
//     report every flag as wired and this file would be green on precisely the
//     shape it exists to be red about.
//   * NonVacuity_The_Symbol_Reader_Ignores_Prose_And_String_Literals — the trap
//     this file walks into if the reader is a substring search. `HARBOR_MINIMAL`
//     appears twice in string literals a user can see
//     (SlashCommandDispatcher, PluginsPanelCommand) and in several comments; a
//     naive reader would call it read and the row would be unfalsifiable in the
//     other direction.
//
// SCOPE
// -----
// The build files CI compiles — `src/**`, `apps/**` and the root
// `Directory.Build.props`. `contrib/` is excluded on purpose and not by
// oversight: it is unmaintained and not built by CI (AGENTS.md), so a flag that
// could only be switched on through a contrib ItemGroup would be a flag that does
// not work in any build anyone runs. `SourceScan.IsBuildOutput` already filters
// obj/, bin/, tests/ and .worktrees/ out of the walk.

using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #828: a feature switch the CLI advertises has to have a body, and a
///     symbol a feature switch defines has to be read.
/// </summary>
public sealed class FeatureFlagWiringRules
{
    /// <summary>Repo-relative path of the project that declares the feature flags.</summary>
    internal const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";

    /// <summary>
    ///     The root props file. The only build input outside <c>src/</c>/<c>apps/</c> that
    ///     participates in every project's evaluation, and it carries a
    ///     <c>HarborWithPlugins</c> default.
    /// </summary>
    internal const string RootPropsFileName = "Directory.Build.props";

    /// <summary>Repo-relative trees walked for the build files that can carry a flag's body.</summary>
    private static readonly string[] BuildFileTrees = ["src", "apps"];

    /// <summary>Prefix of an MSBuild feature-switch property.</summary>
    private const string FlagPrefix = "HarborWith";

    /// <summary>Prefix of a compile symbol a feature switch defines.</summary>
    private const string SymbolPrefix = "HARBOR_";

    /// <summary>
    ///     Feature flags that condition nothing. Each row says why the flag legitimately
    ///     has no body yet; see the header for what each one actually does today.
    /// </summary>
    internal static readonly Dictionary<string, ExemptionReason.Row> UnwiredFlags = new(StringComparer.Ordinal)
        {
            ["HarborWithAllTools"] = new(
                "No ItemGroup or PropertyGroup anywhere in the build is conditioned on this flag, so "
                + "HarborWithAllTools=false excludes no ProjectReference and sets no property. The csproj "
                + "header labels it \"(future) keeps only 6 core tools\", which is honest: the flag exists "
                + "to be wired, not to be used. Wiring one is a feature decision rather than a defect in "
                + "the switch, and this row must not be read as the wiring existing. #828 records the "
                + "question; deleting this row means an ItemGroup now conditions on the flag.",
                "#828"),
        };

    /// <summary>
    ///     Compile symbols a feature flag defines that no preprocessor block reads. Each row says
    ///     why the symbol can stay unread.
    /// </summary>
    internal static readonly Dictionary<string, ExemptionReason.Row> UnreadSymbols = new(StringComparer.Ordinal)
        {
            ["HARBOR_WITH_AOT"] = new(
                "Defined by the HarborWithAot flag and read by no #if or #elif in src/ or apps/, so no "
                + "code branches on whether the build is AOT. That is a fact about the switch, not a "
                + "defect in it: PublishAot itself is honoured by the SDK and needs no C# guard. The row "
                + "exists because an unconsumed symbol is indistinguishable from a wired one when read "
                + "from the csproj alone. #48 and #413 own whether one is wanted.",
                "#48"),
            ["HARBOR_MINIMAL"] = new(
                "Defined by the four granular flags in apps/Harbor.App.Cli/Harbor.App.Cli.csproj and "
                + "again in src/Harbor.Hosting/Harbor.Hosting.csproj, and read by no #if or #elif in the "
                + "repository, so the symbol is dead. Its only occurrences are prose, a doc comment, a "
                + "code comment, and two user-facing string literals. The csproj comments used to say "
                + "it preserved \"the existing #if HARBOR_MINIMAL code path in HostBuilder.cs\" — that is "
                + "the #747 shape, a claim about code the code does not carry, and HostBuilder.cs names "
                + "no such directive; the text now says so and points here. Whether to drop the two "
                + "DefineConstants lines or restore a reader changes how the minimal profile is "
                + "detected, which is not this guard's to make. #828 owns the question.",
                "#828"),
        };

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     Every feature flag the CLI declares either conditions something or carries a row
    ///     saying why not. This is the rule that makes the csproj header checkable: a sixth
    ///     <c>HarborWith…=false</c> switch added next to the five, advertising itself as removing
    ///     a category of ProjectReferences while excluding none, fails here unless someone says
    ///     out loud that it is a placeholder.
    /// </summary>
    [Test]
    public async Task Every_Advertised_Feature_Flag_Conditions_Something_Or_Carries_A_Reason()
    {
        IReadOnlyList<string> flags = FeatureFlags(CliProject());
        IReadOnlyList<XDocument> buildFiles = BuildFiles();

        var unexplained = new List<string>();
        foreach (string flag in flags)
        {
            if (!HasConditionedBody(flag, buildFiles) && !UnwiredFlags.ContainsKey(flag))
            {
                unexplained.Add(flag);
            }
        }

        await Assert.That(unexplained).IsEmpty()
            .Because("these feature flags condition no ItemGroup and no PropertyGroup, so setting "
                   + "them to false excludes no ProjectReference and sets no property: "
                   + string.Join(", ", unexplained) + ". The csproj header advertises each of them as "
                   + "removing a category of references. Either give the flag a body, or add a row to "
                   + "UnwiredFlags saying why it legitimately has none — a row is a permission, and an "
                   + "unexplained one is indistinguishable from a switch that was wired and then lost.");
    }

    /// <summary>
    ///     Every compile symbol a feature flag defines is read by a preprocessor block, or
    ///     carries a row saying why not. Defines the other half of the #747 shape: a symbol that
    ///     reads as a switch in the csproj and branches on nothing in the code.
    /// </summary>
    [Test]
    public async Task Every_Symbol_A_Feature_Flag_Defines_Is_Read_Or_Carries_A_Reason()
    {
        IReadOnlyList<string> flags = FeatureFlags(CliProject());
        IReadOnlyList<string> symbols = FlagDefinedSymbols(CliProject(), flags);
        IReadOnlyList<string> sources = ProductSources();

        var unexplained = new List<string>();
        foreach (string symbol in symbols)
        {
            if (!IsSymbolRead(symbol, sources) && !UnreadSymbols.ContainsKey(symbol))
            {
                unexplained.Add(symbol);
            }
        }

        await Assert.That(unexplained).IsEmpty()
            .Because("these symbols are defined by a feature flag in the CLI csproj and read by no "
                   + "#if or #elif in src/ or apps/: " + string.Join(", ", unexplained) + ". A symbol "
                   + "nobody reads makes the flag look wired at the C# level while it branches on "
                   + "nothing, and the comment above the definition cannot be checked against prose — "
                   + "only against the preprocessor. Add a row to UnreadSymbols, or wire the symbol up.");
    }

    // =====================================================================
    // 2. The tables are well formed and not stale.
    // =====================================================================

    /// <summary>
    ///     Every row states a reason, in the reason slot rather than in a comment above it.
    ///     <see cref="ExemptionReason" /> is the one place in this project that decides what
    ///     counts as a reason, so this rule asks it rather than re-asking the question here.
    /// </summary>
    [Test]
    public async Task The_Rows_Are_WellFormed()
    {
        IEnumerable<(string Key, ExemptionReason.Row Row)> rows =
            UnwiredFlags.Select(pair => (Key: pair.Key, Row: pair.Row))
                .Concat(UnreadSymbols.Select(pair => (Key: $"symbol {pair.Key}", Row: pair.Row)));

        IReadOnlyList<string> failures = ExemptionReason.RowsWithoutAReason(
            $"{nameof(FeatureFlagWiringRules)}.{nameof(UnwiredFlags)}/{nameof(UnreadSymbols)}",
            rows);

        await Assert.That(failures).IsEmpty()
            .Because("a row that grants a permission and does not say why is indistinguishable from "
                   + "an accident: " + string.Join(" | ", failures));
    }

    /// <summary>
    ///     A row whose flag or symbol HAS since been wired is a permission for a problem that no
    ///     longer exists, and the cheap repair is to re-add it. Copied from
    ///     <c>ProviderPayloadSerializationRules.Table_Rows_Are_Not_Stale</c> for the same reason.
    /// </summary>
    [Test]
    public async Task The_Rows_Are_Not_Stale()
    {
        IReadOnlyList<string> flags = FeatureFlags(CliProject());
        IReadOnlyList<XDocument> buildFiles = BuildFiles();
        IReadOnlyList<string> sources = ProductSources();
        IReadOnlyList<string> symbols = FlagDefinedSymbols(CliProject(), flags);

        var stale = new List<string>();
        foreach (string flag in UnwiredFlags.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (HasConditionedBody(flag, buildFiles))
            {
                stale.Add($"UnwiredFlags['{flag}'] — the flag now conditions something");
            }
        }

        foreach (string symbol in UnreadSymbols.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!symbols.Contains(symbol, StringComparer.Ordinal))
            {
                stale.Add($"UnreadSymbols['{symbol}'] — no feature flag defines it any more");
            }
            else if (IsSymbolRead(symbol, sources))
            {
                stale.Add($"UnreadSymbols['{symbol}'] — a #if or #elif reads it now");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because("these rows tolerate something that is no longer true, so they have become "
                   + "permissions for defects nobody is looking for: " + string.Join("; ", stale)
                   + ". Delete the row in the same change that wires the flag or adds the reader.");
    }

    // =====================================================================
    // 3. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The subject exists: the CLI csproj is found, parses, still declares feature flags,
    ///     and the build-file walk returns something to judge them against. Without this a
    ///     renamed or restructured project degrades both rules to "nothing to check" and they
    ///     pass for no reason.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Real_Project_Files_And_Flags_Are_Found()
    {
        XDocument? project = CliProject();

        await Assert.That(project).IsNotNull()
            .Because($"{CliProjectPath} is missing or does not parse, so every rule in this file "
                   + "would pass on an empty subject");

        IReadOnlyList<string> flags = FeatureFlags(project);

        await Assert.That(flags).IsNotEmpty()
            .Because($"no property named {FlagPrefix}* is declared in {CliProjectPath}. The flags were "
                   + "renamed or moved, and this guard has to be pointed at the new shape rather than "
                   + "deleted — a guard over zero flags is green forever.");

        await Assert.That(flags.Contains("HarborWithAot", StringComparer.Ordinal)).IsTrue()
            .Because($"{CliProjectPath} no longer declares HarborWithAot. #828's subject is the AOT "
                   + "switch, so losing it means this file is guarding the wrong property.");

        await Assert.That(BuildFiles()).IsNotEmpty()
            .Because("the walk over src/ and apps/ found no build file, so no flag could be seen to "
                   + "condition anything and every flag would need a row for the wrong reason");
    }

    /// <summary>
    ///     Sensitivity and specificity of the flag reader over synthetic csproj text. It must
    ///     report a flag that conditions nothing, and it must NOT count a flag's own declaration
    ///     as its body — the control is the point of this test, because a reader that did count
    ///     declarations would report all five real flags as wired and be green on exactly the
    ///     shape it exists to catch.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Flag_Reader_Reports_A_Flag_Nobody_Conditions()
    {
        const string NothingConditionsIt = """
            <Project>
              <PropertyGroup>
                <HarborWithPlugins Condition="'$(HARBOR_MINIMAL)' == 'true'">false</HarborWithPlugins>
                <HarborWithPlugins Condition="'$(HarborWithPlugins)' == ''">true</HarborWithPlugins>
                <HarborWithEverything Condition="'$(HarborWithEverything)' == ''">true</HarborWithEverything>
              </PropertyGroup>
            </Project>
            """;

        const string SomethingConditionsIt = """
            <Project>
              <PropertyGroup>
                <HarborWithPlugins Condition="'$(HARBOR_MINIMAL)' == 'true'">false</HarborWithPlugins>
                <HarborWithPlugins Condition="'$(HarborWithPlugins)' == ''">true</HarborWithPlugins>
              </PropertyGroup>
              <ItemGroup Condition="'$(HarborWithPlugins)' == 'true'">
                <ProjectReference Include="..\Harbor.Plugins.Hosting\Harbor.Plugins.Hosting.csproj"/>
              </ItemGroup>
            </Project>
            """;

        IReadOnlyList<XDocument> unwiredDocuments = [XDocument.Parse(NothingConditionsIt)];
        IReadOnlyList<XDocument> wiredDocuments = [XDocument.Parse(SomethingConditionsIt)];

        var unwired = new List<string>();
        foreach (string flag in FeatureFlags(unwiredDocuments[0]))
        {
            if (!HasConditionedBody(flag, unwiredDocuments))
            {
                unwired.Add(flag);
            }
        }

        await Assert.That(unwired.Contains("HarborWithEverything", StringComparer.Ordinal)).IsTrue()
            .Because("the reader must report a flag that no ItemGroup or PropertyGroup conditions, "
                   + "or it can never fail on the real project");

        await Assert.That(unwired.Contains("HarborWithPlugins", StringComparer.Ordinal)).IsFalse()
            .Because("HarborWithPlugins declares itself twice in this synthetic project and nothing "
                   + "conditions it, so it belongs in the unwired set — a reader that reported the "
                   + "empty set here would be counting a declaration as a body");

        foreach (string flag in FeatureFlags(wiredDocuments[0]))
        {
            await Assert.That(HasConditionedBody(flag, wiredDocuments)).IsTrue()
                .Because($"an ItemGroup is conditioned on {flag} in this synthetic project, so the "
                       + "reader has to see it — otherwise every real flag reads as unwired and the "
                       + "rule is red for the wrong reason");
        }
    }

    /// <summary>
    ///     Specificity of the symbol reader against the trap that would make the rule
    ///     unfalsifiable in the other direction. A substring search finds <c>HARBOR_MINIMAL</c> in
    ///     two user-facing strings and several comments, calls it read, and the row for it can
    ///     never fire. Only a preprocessor directive counts.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Symbol_Reader_Ignores_Prose_And_String_Literals()
    {
        const string OnlyProse = """
            namespace Harbor.Cli;

            /// HARBOR_MINIMAL: the plugin loader is skipped.
            internal sealed class Prose
            {
                // A code comment naming HARBOR_MINIMAL is not a reader.
                public void Write() => Console.WriteLine("not available in this build (HARBOR_MINIMAL).");
            }
            """;

        const string ARealReader = """
            namespace Harbor.Cli;

            internal static class Guarded
            {
            #if HARBOR_MINIMAL
                public const bool Skipped = true;
            #endif
            }
            """;

        await Assert.That(IsSymbolRead("HARBOR_MINIMAL", [OnlyProse])).IsFalse()
            .Because("HARBOR_MINIMAL appears in this source only in a doc comment, a code comment and "
                   + "a string literal, none of which is a preprocessor directive. A reader that "
                   + "accepts any of them makes the UnreadSymbols row for it unfalsifiable.");

        await Assert.That(IsSymbolRead("HARBOR_MINIMAL", [ARealReader])).IsTrue()
            .Because("this source branches on HARBOR_MINIMAL in a real #if, so the reader has to see "
                   + "it — a reader that ignored directives would report every symbol as unread");

        // Substring safety: a longer symbol containing this one must not be mistaken for it.
        await Assert.That(IsSymbolRead("HARBOR_MINIMAL", ["""
            namespace Harbor.Cli;
            internal static class Guarded
            {
            #if HARBOR_MINIMAL_V2
                public const bool On = true;
            #endif
            }
            """])).IsFalse()
            .Because("HARBOR_MINIMAL_V2 is a different symbol, and a reader that matched the shorter "
                   + "name inside it would call HARBOR_MINIMAL read off a directive that does not "
                   + "mention it");
    }

    // =====================================================================
    // 4. Helpers.
    // =====================================================================

    /// <summary>
    ///     The CLI csproj parsed, or <see langword="null" /> when it is missing or malformed.
    /// </summary>
    /// <returns>The parsed project, or <see langword="null" /> outside a usable checkout.</returns>
    private static XDocument? CliProject() =>
        RepoPaths.RepoRoot is { } root ? LoadProject(Path.Combine(root, CliProjectPath)) : null;

    /// <summary>
    ///     Every build file CI compiles, parsed. Cached: both rules and both non-vacuity
    ///     controls ask for the same walk, and re-parsing every csproj in <c>src/</c> for each
    ///     question made the architecture gate measurably slower for no new information.
    /// </summary>
    private static IReadOnlyList<XDocument> BuildFiles() => CachedBuildFiles.Value;

    private static readonly Lazy<IReadOnlyList<XDocument>> CachedBuildFiles = new(DiscoverBuildFiles);

    private static IReadOnlyList<XDocument> DiscoverBuildFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var documents = new List<XDocument>();

        foreach (string tree in BuildFileTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                // IsBuildOutput is the filter, not the predicate: obj/, bin/, tests/,
                // contrib/ and .worktrees/ are skipped.
                if (!SourceScan.IsBuildOutput(path) && LoadProject(path) is { } document)
                {
                    documents.Add(document);
                }
            }
        }

        // The one build input outside those trees that every project evaluates.
        if (LoadProject(Path.Combine(root, RootPropsFileName)) is { } props)
        {
            documents.Add(props);
        }

        return documents;
    }

    /// <summary>
    ///     Product C# with comments blanked out, so prose about a switch cannot stand in for a
    ///     reader of it. Cached for the same reason as <see cref="BuildFiles" />.
    /// </summary>
    private static IReadOnlyList<string> ProductSources() => CachedProductSources.Value;

    private static readonly Lazy<IReadOnlyList<string>> CachedProductSources = new(DiscoverProductSources);

    private static IReadOnlyList<string> DiscoverProductSources()
    {
        var sources = new List<string>();
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is { } text)
            {
                sources.Add(SourceScan.StripComments(text));
            }
        }

        return sources;
    }

    /// <summary>
    ///     Every <c>HarborWith…</c> property the project declares, sorted. Read from the file
    ///     rather than hardcoded, so a sixth flag added later joins the subject automatically —
    ///     a hardcoded inventory would go stale quietly, which is the failure
    ///     <c>RepoPaths.ReadProjectReferences</c> documents at length.
    /// </summary>
    /// <param name="project">The parsed project, or <see langword="null" />.</param>
    /// <returns>The flag names, deduplicated and sorted.</returns>
    private static IReadOnlyList<string> FeatureFlags(XDocument? project)
    {
        if (project is null)
        {
            return [];
        }

        return
        [
            .. project.Descendants()
                .Select(e => e.Name.LocalName)
                .Where(name => name.StartsWith(FlagPrefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The compile symbols a feature flag defines: the <c>HARBOR_…</c> tokens in every
    ///     <c>DefineConstants</c> line whose <c>Condition</c> tests one of
    ///     <paramref name="flags" />. Scoping to flag-conditioned lines is what keeps IPC and
    ///     mode symbols out of the subject — they are wired by a different switch and are not
    ///     what this file is about.
    /// </summary>
    /// <param name="project">The parsed project, or <see langword="null" />.</param>
    /// <param name="flags">The feature flag names to treat as the subject.</param>
    /// <returns>The symbols, deduplicated and sorted.</returns>
    private static IReadOnlyList<string> FlagDefinedSymbols(XDocument? project, IReadOnlyList<string> flags)
    {
        if (project is null || flags.Count == 0)
        {
            return [];
        }

        var symbols = new List<string>();

        foreach (XElement element in project.Descendants().Where(e => e.Name.LocalName == "DefineConstants"))
        {
            string? condition = element.Attribute("Condition")?.Value;
            if (condition is null)
            {
                continue;
            }

            bool conditionedOnAFlag = flags.Any(flag => condition.Contains($"$({flag})", StringComparison.Ordinal));
            if (!conditionedOnAFlag)
            {
                continue;
            }

            foreach (string token in element.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // A bare symbol. `$(DefineConstants)` is an MSBuild property reference, not a
                // symbol, and is the one token in every one of these values that is not.
                if (token.StartsWith(SymbolPrefix, StringComparison.Ordinal) && !token.StartsWith("$(", StringComparison.Ordinal))
                {
                    symbols.Add(token);
                }
            }
        }

        return
        [
            .. symbols
                .Distinct(StringComparer.Ordinal)
                .OrderBy(symbol => symbol, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Whether any build file has an <c>ItemGroup</c> or <c>PropertyGroup</c> whose
    ///     <c>Condition</c> tests <c>$(flag)</c>. A flag's own declaration is a property element
    ///     inside a group, not a group, so it can never be mistaken for a body.
    /// </summary>
    /// <param name="flag">The MSBuild property name, without <c>$(…)</c>.</param>
    /// <param name="buildFiles">The parsed build files to search.</param>
    /// <returns><see langword="true" /> when the flag conditions something.</returns>
    private static bool HasConditionedBody(string flag, IReadOnlyList<XDocument> buildFiles)
    {
        foreach (XDocument document in buildFiles)
        {
            foreach (XElement group in document.Descendants())
            {
                if (group.Name.LocalName is not ("ItemGroup" or "PropertyGroup"))
                {
                    continue;
                }

                string? condition = group.Attribute("Condition")?.Value;
                if (condition?.Contains($"$({flag})", StringComparison.Ordinal) == true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether a preprocessor directive in any of <paramref name="sources" /> branches on
    ///     <paramref name="symbol" />. Directive lines only, whole-word, and against
    ///     comment-stripped source — a substring search would call <c>HARBOR_MINIMAL</c> read off
    ///     a user-facing string, which is the specific way this rule could go green for the wrong
    ///     reason.
    /// </summary>
    /// <param name="symbol">The compile symbol, without <c>HARBOR_</c> decoration.</param>
    /// <param name="sources">Comment-stripped C# source text.</param>
    /// <returns><see langword="true" /> when an <c>#if</c>/<c>#elif</c> mentions the symbol.</returns>
    private static bool IsSymbolRead(string symbol, IEnumerable<string> sources)
    {
        foreach (string source in sources)
        {
            foreach (string line in source.Split('\n'))
            {
                ReadOnlySpan<char> directive = line.AsSpan().TrimStart();
                if (!directive.StartsWith("#if", StringComparison.Ordinal)
                    && !directive.StartsWith("#elif", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ContainsWholeWord(directive, symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether <paramref name="word" /> occurs in <paramref name="haystack" /> not as part of
    ///     a longer identifier, so <c>HARBOR_MINIMAL</c> is not found inside
    ///     <c>HARBOR_MINIMAL_V2</c>.
    /// </summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="word">The identifier to look for.</param>
    /// <returns><see langword="true" /> when a whole-word occurrence exists.</returns>
    private static bool ContainsWholeWord(ReadOnlySpan<char> haystack, string word)
    {
        int index = haystack.IndexOf(word.AsSpan(), StringComparison.Ordinal);

        while (index >= 0)
        {
            bool leftIsBoundary = index == 0 || !IsIdentifierChar(haystack[index - 1]);
            int end = index + word.Length;
            bool rightIsBoundary = end >= haystack.Length || !IsIdentifierChar(haystack[end]);

            if (leftIsBoundary && rightIsBoundary)
            {
                return true;
            }

            // Step past this occurrence's first character so overlapping hits are still found.
            int next = haystack[(index + 1)..].IndexOf(word.AsSpan(), StringComparison.Ordinal);
            index = next < 0 ? -1 : next + 1;
        }

        return false;
    }

    /// <summary>Whether a character can be part of a C# identifier.</summary>
    /// <param name="c">The character to test.</param>
    /// <returns><see langword="true" /> for letters, digits and underscore.</returns>
    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    ///     Parses a build file, returning <see langword="null" /> rather than throwing: a file
    ///     that vanished or does not parse is a discovery problem, and every rule here pairs the
    ///     value with a non-vacuity check so an empty result cannot pass quietly.
    /// </summary>
    /// <param name="path">Absolute path of the build file.</param>
    /// <returns>The parsed document, or <see langword="null" />.</returns>
    private static XDocument? LoadProject(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
