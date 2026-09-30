// BuiltinAgentPickerProjectionRules.cs — the guard for issue #582.
//
// THE DEFECT
// ----------
// #582 measured "adding a builtin agent = 3 structural edits, one a hardcoded
// onboarding menu". Re-measured on dev after 23 merges, the count is HIGHER, and
// the extra one is invisible to the issue's own method — it walked AGENTS.md's
// decision tree, not the CLI:
//
//   1. src/Harbor.Abstractions/Agents/AgentDefinition.cs              declaration
//   2. src/Harbor.Hosting/Modules/ToolsCatalog.cs:27-29                registration
//   3. src/Harbor.Application/Onboarding/OnboardingWizard.cs:376-390   the menu
//   4. apps/Harbor.App.Cli/Commands/AgentCommand.cs:25                 /agent's own text
//   5. src/Harbor.Application/Configuration/HarborConfig.cs:108        doc comment
//   6. src/Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs  doc comment
//   7. src/Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs:79
//   +. AGENTS.md, CLAUDE.md, docs/ARCHITECTURE.md
//
// Edits 1 and 2 are the axis itself: a definition and a registration. Edits 3
// and 4 are the DEFECT, and they share one shape — each is a hand-written copy
// of the set of names the agent registry already holds, written down where a
// user can see it. #724 is the precedent for the opposite treatment: a guard
// that derives its subjects from the declaration re-points when the declaration
// grows, and a hand-written list does not.
//
// SO: THREE PLACES, OR ONE REGISTRY ACTING LIKE THREE?
// -------------------------------------------------
// One registry. `IAgentRegistry` is populated by the real `AddHarbor` from
// `ToolsCatalog.CreateAgentRegistry`, and FIVE product consumers already project
// from it — `TaskTool` (the `task` tool's available-agent hint), both
// `/agent` command implementations, `TaskRunRunner`, `ReplRunner`. Two do not:
// the onboarding wizard's numbered menu and the `/agent` command's one-line
// description. Those two are the whole defect, and they are the two a user
// reads.
//
// That is the asymmetry #595 found on the tool axis (four derivable lists
// re-stating a fact the tool declares) and the same move fixes it: make the
// consumer READ the declaration. A fourth builtin agent then needs edits 1 and
// 2 only, and neither of them is a list that can rot.
//
// THE RULES, AND WHY EACH IS RED ON ITS OWN TREE
// ----------------------------------------------
//   R1  The onboarding wizard's agent step holds NO string literal naming a
//       registered agent. RED: it spells `code`, `plan` and `explore` seven
//       times across a three-line menu and a six-arm switch.
//   R2  The `/agent` command's description holds no string literal naming a
//       registered agent. RED: "Switch agent (mode): code, plan, explore" is
//       what `/help` prints.
//   R3  `OnboardingWizard` can be GIVEN an `IAgentRegistry` — by type, read off
//       the constructor. RED: no constructor accepts one, so no registry can
//       reach the picker at all. R1 alone could be satisfied by deleting the
//       menu; R3 says where the names have to come from instead, and it is the
//       seam R4 exercises.
//   R4  The menu is a PROJECTION: an agent that exists in the registry and
//       appears in no hand-written list is offered by the wizard and accepted
//       when typed. RED: the wizard cannot see a registry, so the planted agent
//       is neither listed nor selectable.
//
// R4 is the rule that would have caught the fourth agent. R1 and R2 are the
// rules that make the shape impossible to write again.
//
// WHY THE SUBJECTS ARE DERIVED, NEVER WRITTEN
// --------------------------------------------
// Every rule above grades against the names the REAL composition root
// registered — `AddHarbor` → `IAgentRegistry`. Not one agent name is spelled in
// this file. A guard that listed the three builtin names would pass on exactly
// the day it matters (a fourth agent added) only if someone remembered to add
// it to the list too, and would then be a ninth list of the kind this issue is
// about. `ProviderPresetsTests`' two-way shape is the model: the subjects come
// from the declaration, so a new agent re-points the guard.
//
// WHY THE SCOPE IS TWO NAMED FILES AND NOT THE WHOLE PRODUCT
// ----------------------------------------------------------
// A repository-wide "no string literal contains a registered agent name" rule
// cannot work, and the reason is worth recording: `code` is an ordinary English
// word and an OAuth parameter name. A sweep for it flags `McpOAuthHandler`'s
// `ParseQuery(request, "code", …)`, `GrepTool`'s "find code or text", and
// `DefaultFileTreePolicy.CodeIcon = "file-code"` — all noise, and noise is how a
// rule gets deleted rather than obeyed. So the rule is scoped to the two files
// that present the agent SET to a user, which is the same trade
// `DefaultAgentSingleSourceTests` makes for the same reason. The two are named
// PATHS, not agent names: adding an agent changes nothing here.
//
// The doc comments that enumerate the agents are prose and are stripped before
// matching (see `SourceScan.StripComments`), so they are not this file's job
// either. `CommonConfig.cs`'s `DefaultAgent = "code"` is a different axis —
// "which agent is the DEFAULT", owned by #683/#716 — and is filed separately
// rather than smuggled in here.
//
// NON-VACUITY
// -----------
// A guard that matches nothing is indistinguishable from a guard that is broken.
// Four controls, and they close the two ways this file could be green for the
// wrong reason: a vocabulary that is empty (every rule then accepts anything) and
// a planted agent that a hand-written list already happens to contain (R4 then
// proves nothing).
//
// THREE DEFECTS THIS GUARD HAD ON ITS FIRST CI RUN
// ------------------------------------------------
// Both were caught on the guard alone, before the product fix, and both are the
// kind that survives review because the code reads correctly. Recorded because
// the lesson is the file's, not the moment's.
//
//   1. The name matcher used `Contains`, and `kilocode` contains `code`. The red
//      run therefore reported the WIZARD'S PROVIDER MENU — "Pick a provider
//      (recommended: kilocode — has FREE models):" and the id `kilocode` itself —
//      as two sites spelling the `code` agent. Two of the fourteen reported hits
//      were a provider id. A guard whose findings include noise the author has to
//      discount by hand is a guard that gets turned off rather than obeyed, and
//      `ToolNameListRule`'s whole density threshold exists because noise is that
//      guard's failure mode, not a style nit. Names are matched on identifier
//      boundaries now, and the control that pins it is planted with the wizard's
//      own provider line.
//   2. The `MenuRow` non-vacuity control passed the `writer("  [1] code …")`
//      SOURCE line. `MenuRow` reads what the wizard PRINTED, and that source line
//      does not begin with `[`, so the control went red while the rule it guards
//      was working. A non-vacuity control has to be pinned to the value the
//      predicate actually receives; pinning it to the call site tests the wrong
//      string and manufactures a red that reads as a product defect.
//   3. R4 asserted SET EQUALITY between the rows it read and the registry, and
//      the same run disproved the premise underneath it: the provider menu's
//      marker is a glyph for a no-auth provider and TWO SPACES for one that needs
//      a key, so `    [1] Anthropic` is shaped exactly like an agent row. The
//      offered set came back as `Anthropic, Cerebras, …, code, explore, plan, xAI`
//      and the rule was red for a reason that had nothing to do with the
//      projection. Scoping the parse to the agent section would have meant keying
//      on a header string — a quieter coupling, not the removal of one. The rule is
//      CONTAINMENT now: every registered agent is offered, and rows belonging to a
//      different menu are not a defect. "The menu lists an unregistered agent" is
//      unreachable when the menu is built FROM the registry, so it was a claim the
//      predicate could not support and did not need to.
//
// The one that did NOT go wrong is worth naming too: R4 is written so it can run
// on an unfixed tree — it composes the wizard through DI rather than naming a
// constructor parameter that does not exist yet, and that is why R3 (the seam has
// to exist) is a separate rule rather than a comment.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #582: the two places that show a user which agents exist read a
///     hand-written list instead of the agent registry, so a new builtin agent
///     is registered, usable, and invisible in both of them.
/// </summary>
public sealed class BuiltinAgentPickerProjectionRules
{
    /// <summary>
    ///     The product files that present the agent SET to a user. Named by path,
    ///     never by the agent names they are not allowed to contain.
    /// </summary>
    private static readonly string[] PickerFiles =
    [
        "src/Harbor.Application/Onboarding/OnboardingWizard.cs",
        "apps/Harbor.App.Cli/Commands/AgentCommand.cs",
    ];

    /// <summary>
    ///     The agent planted into the registry for
    ///     <see cref="Onboarding_Offers_An_Agent_That_Exists_In_No_Hand_Written_List" />.
    ///     It is a fourth builtin agent, invented here exactly as a real one would
    ///     be, and it appears in no list anywhere in the repository.
    /// </summary>
    private const string PlantedAgentName = "review";

    /// <summary>One quoted C# <c>string</c> literal on a line.</summary>
    private static readonly Regex QuotedLiteral = new(
        @"""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     An agent name as a WHOLE WORD inside a literal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Word boundaries, and that is not a nicety — it is a correction CI made
    ///         on this rule's first run. Substring matching reported
    ///         <c>"Pick a provider (recommended: kilocode — has FREE models):"</c> and
    ///         <c>"kilocode"</c> as spelling the <c>code</c> AGENT, on the strength of
    ///         the four letters inside a provider id. Two of the fourteen reported
    ///         sites in the red run were that, and a rule which fires on a provider
    ///         name is a rule whose failure modes cannot be told apart from "the rule is
    ///         broken" — the exact condition every non-vacuity control here exists to
    ///         prevent, reached from the other direction.
    ///     </para>
    ///     <para>
    ///         A boundary on the identifier characters only, not on \w, so a name is
    ///         still found next to punctuation: <c>"Switch agent (mode): code, plan,
    ///         explore"</c> matches all three, and <c>"kilocode"</c> matches none.
    ///     </para>
    /// </remarks>
    private static Regex AgentNameWordMatcher(IReadOnlyList<string> names) => new(
        @"(?<![A-Za-z0-9_])(?:" + string.Join("|", names.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     A wizard menu row: the index in brackets, then the agent name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This reads rows from EVERY numbered menu the wizard prints, and the
    ///         first CI run made that concrete: R4's set-equality reported
    ///         <c>Anthropic, Cerebras, DeepSeek, …</c> as offered agents. The provider
    ///         menu prefixes a marker that is a glyph for a no-auth provider and TWO
    ///         SPACES for one that needs a key, so half its rows are shaped exactly
    ///         like an agent row. Nothing here pretends otherwise — the rule that
    ///         consumes it is CONTAINMENT ("is every registered agent offered?"), not
    ///         set equality, which would be a claim about menu boundaries this
    ///         predicate cannot make.
    ///     </para>
    ///     <para>
    ///         Anchored at the line start so a row that a provider menu has prefixed
    ///         with a glyph is not read; a glyph-prefixed row is not a menu entry a
    ///         user can select by number in the same way.
    ///     </para>
    /// </remarks>
    private static readonly Regex MenuRow = new(
        @"^\s*\[(?<index>\d+)\]\s+(?<name>\S+)",
        RegexOptions.Compiled);

    // ── R1 / R2: no hand-written copy of the agent set ───────────────────────

    /// <summary>
    ///     R1. The onboarding wizard's agent step must not spell a registered
    ///     agent's name. Today it writes three menu lines and a six-arm switch
    ///     over <c>code</c>, <c>plan</c> and <c>explore</c>.
    /// </summary>
    [Test]
    [NotInParallel("hosting")]
    public async Task Onboarding_AgentPicker_Spells_No_Registered_Agent_Name()
    {
        await AssertNoAgentNames(PickerFiles[0]);
    }

    /// <summary>
    ///     R2. The <c>/agent</c> command's one-line description is printed by
    ///     <c>/help</c> and enumerates the same three names. Same rule, second
    ///     file, because it is a second hand-written copy of one set.
    /// </summary>
    [Test]
    [NotInParallel("hosting")]
    public async Task AgentCommand_Description_Spells_No_Registered_Agent_Name()
    {
        await AssertNoAgentNames(PickerFiles[1]);
    }

    // ── R3: the seam has to exist ────────────────────────────────────────────

    /// <summary>
    ///     R3. The wizard must be constructible with an <see cref="IAgentRegistry" />.
    ///     Checked by TYPE on the constructor, never by the name of a parameter
    ///     (#626/#735) and never by a source grep — a wizard that mentions
    ///     <c>IAgentRegistry</c> in a comment has not been given one.
    /// </summary>
    [Test]
    public async Task OnboardingWizard_Can_Be_Given_The_Agent_Registry()
    {
        bool takes = typeof(OnboardingWizard)
            .GetConstructors()
            .Any(ctor => ctor.GetParameters().Any(p => p.ParameterType == typeof(IAgentRegistry)));

        await Assert.That(takes).IsTrue().Because(
            "OnboardingWizard.PickAgentAsync builds a numbered menu out of three string "
            + "literals, and nothing can tell it otherwise: no constructor accepts an "
            + "IAgentRegistry. Every other agent-set consumer in the product projects from "
            + "the registry (TaskTool's available-sub-agent hint, both /agent commands, "
            + "TaskRunRunner, ReplRunner), so the registry is the established source and "
            + "the picker is the outlier. Add an optional IAgentRegistry constructor "
            + "parameter, exactly as the IProviderRegistry one already there drives "
            + "PickProviderAsync. Without this seam the two rules above could be "
            + "satisfied by deleting the menu, which is not a fix. See issue #582.");
    }

    // ── R4: the menu is a projection, not a copy ─────────────────────────────

    /// <summary>
    ///     R4. A registry holding an agent that appears in no hand-written list
    ///     must produce a menu that offers it and accepts it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The wizard is resolved through <see cref="ServiceCollection" /> with
    ///         <c>AddSingleton&lt;OnboardingWizard&gt;()</c> — byte-for-byte what
    ///         <c>CoreModule</c> does — rather than through a hand-written call, so
    ///         the wiring under test is the product's wiring and not the test's. That
    ///         is also what lets this rule run on the unfixed tree: a test cannot name
    ///         a constructor parameter that does not exist yet, and it must not need to.
    ///     </para>
    ///     <para>
    ///         The wizard is driven with no <c>IProviderRegistry</c> and no
    ///         <c>IProviderHealthCheck</c>, which is the offline path: the provider list
    ///         comes from <c>ProviderPresets.All</c>, the model falls back to the preset
    ///         default and the connection probe is skipped. So the reader is consumed
    ///         exactly three times and no test reaches the network.
    ///     </para>
    /// </remarks>
    [Test]
    [NotInParallel("hosting")]
    public async Task Onboarding_Offers_An_Agent_That_Exists_In_No_Hand_Written_List()
    {
        IReadOnlyList<AgentDefinition> planted =
        [
            AgentDefinition.CodeDefault("model", "provider"),
            AgentDefinition.PlanDefault("model", "provider"),
            AgentDefinition.ExploreDefault("model", "provider"),
            NewPlantedAgent(),
        ];

        (string output, string chosen) = await RunWizardAsync(planted, PlantedAgentName);

        // Every `[n] name` row the wizard printed, whatever menu it came from.
        //
        // SET EQUALITY WAS HERE AND IT WAS WRONG, which the first CI run proved by
        // reporting `Anthropic, Cerebras, DeepSeek, …` as offered agents. The
        // provider menu prefixes a marker that is TWO SPACES for a provider needing
        // a key and a glyph for one that does not, so half the provider rows are
        // byte-identical in shape to an agent row and `MenuRow` reads them. Scoping
        // the parse to the agent section would mean keying on a header string, i.e.
        // replacing one coupling with a quieter one.
        //
        // Containment is the contract that actually matters and it needs no
        // boundary: every agent the registry holds is offered, and extra rows from
        // a different menu are not a defect. "Lists an agent that is not
        // registered" is not reachable when the list is built FROM the registry.
        var offered = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            Match match = MenuRow.Match(line);
            if (match.Success)
            {
                offered.Add(match.Groups["name"].Value);
            }
        }

        string[] missing =
        [
            .. planted.Select(a => a.Name.Value)
                .Where(n => !offered.Contains(n, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        await Assert.That(missing).IsEquivalentTo(Array.Empty<string>()).Because(
            "the wizard is the first-run screen where a user learns the agent names exist, so a "
            + "new builtin agent has to appear in it without anyone editing C#. PickAgentAsync "
            + "builds the menu from three literal lines and resolves the answer through a "
            + "six-arm switch on \"1\"/\"2\"/\"3\", so it holds its own copy of the agent set "
            + "instead of reading the registry every other consumer reads. Not offered: "
            + string.Join(", ", missing) + ". The registry holds: "
            + string.Join(", ", planted.Select(a => a.Name.Value).Order(StringComparer.Ordinal))
            + ". The whole menu was:\n" + output);

        await Assert.That(chosen).IsEqualTo(PlantedAgentName).Because(
            "listing an agent and accepting it are two different halves, and the second half "
            + "is what actually writes it into config.json. A menu projected from the registry "
            + "with a switch that is not has to reject the name and fall back to "
            + AgentName.Fallback + ", which is the defect a user cannot see: the agent is in "
            + "the registry, in the list on screen, and unreachable by typing it. Chosen: " + chosen + ".");
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────

    /// <summary>
    ///     The vocabulary every rule above grades against is the REAL composition
    ///     root's registry. If it is empty the rules accept anything; if it lost the
    ///     fallback they would be grading the wrong names.
    /// </summary>
    [Test]
    [NotInParallel("hosting")]
    public async Task NonVacuity_The_Real_Registry_Is_NonEmpty_And_Holds_The_Fallback()
    {
        IReadOnlyList<AgentDefinition> registered = RealCompositionRootAgents();
        string[] names = [.. registered.Select(a => a.Name.Value)];

        await Assert.That(registered.Count).IsGreaterThan(1).Because(
            "AddHarbor registers the agent registry from ToolsCatalog.CreateAgentRegistry, and "
            + "the count is read off it rather than written here. An empty or single-entry "
            + "answer means the probe stopped working, and R1/R2 then pass because they have "
            + "nothing to match — the NetArchTest trap in behaviour form.");

        await Assert.That(names.Contains(AgentName.Fallback)).IsTrue().Because(
            "R1 and R2 grade string literals against the registered names. If the registry did "
            + "not hold " + AgentName.Fallback + ", the rule would be grading a vocabulary the "
            + "product does not use, and a wizard that spelled it would pass. Read: "
            + string.Join(", ", names) + ".");
    }

    /// <summary>
    ///     The planted agent is planted only in the registry: it is in no scanned
    ///     file, so R4 is grading a genuine miss and not a name some list already
    ///     happens to carry.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Planted_Agent_Is_In_No_Scanned_Picker_File()
    {
        IReadOnlyList<string> hits = FindAgentNameMentions([PlantedAgentName], PickerFiles);

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "Onboarding_Offers_An_Agent_That_Exists_In_No_Hand_Written_List only proves the menu "
            + "is a projection if the agent it plants is one the hand-written lists could not "
            + "have contained anyway. If this control ever goes red, a name list has grown a row "
            + "for " + PlantedAgentName + " and the projection rule has stopped testing the "
            + "projection. Mentioned in: " + Describe(hits) + ".");
    }

    /// <summary>
    ///     The line matcher behind R1/R2 must fire on a planted menu line and on a
    ///     planted switch arm, and stay silent on prose and on a line that names no
    ///     agent. Without this, "no violations" is indistinguishable from "the regex
    ///     reads nothing".
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Scanner_Fires_On_A_Planted_Menu_Line_Only()
    {
        const string plantedMenu = """
                writer("  [1] code    — Default. Can read/write/edit files and run commands.");
            """;

        const string plantedSwitch = """
                "" or "1" => "code",
            """;

        const string prose = """
                // The default agent is "code" in prose, and this line is not code.
            """;

        const string unrelated = """
                writer("Enter number, or type an agent name (default: 1): ");
            """;

        // The provider step's own two lines, verbatim from the wizard. They contain
        // the letters of an agent name inside a PROVIDER id, which is the false
        // positive this control exists to pin shut.
        const string plantedProviderRow = """
                writer("Pick a provider (recommended: kilocode — has FREE models):");
            """;

        // What the wizard PRINTS, which is what MenuRow is pointed at. Not the
        // `writer("…")` source line: that one does not begin with `[`.
        const string printedAgentRow = "  [1] code    — Default. Can read/write/edit files and run commands.";

        await Assert.That(MatchingLines(plantedMenu, ["code"])).IsGreaterThan(0).Because(
            "a planted copy of the wizard's own menu line must be detected, or the rule is blind "
            + "to the exact shape it was written for");

        await Assert.That(MatchingLines(plantedSwitch, ["code"])).IsGreaterThan(0).Because(
            "the switch arm is the second copy of the same name and is the one a source grep for "
            + "menu lines would miss");

        await Assert.That(MatchingLines(prose, ["code"])).IsEqualTo(0).Because(
            "comment lines are prose, not a second source of truth — SourceScan.StripComments "
            + "blanks them, and a rule that read them would break the day someone documented the fix");

        await Assert.That(MatchingLines(unrelated, ["code"])).IsEqualTo(0).Because(
            "a line that names no agent must not be reported, or the rule is noise");

        // THE CONTROL THAT FIRED ON THIS FILE'S FIRST CI RUN, and it was mine.
        //
        // `kilocode` contains `code`, so substring matching reported the PROVIDER
        // menu — "Pick a provider (recommended: kilocode — has FREE models):" and
        // the id itself — as two sites spelling the `code` AGENT. Two of the
        // fourteen the red run reported were that. A rule whose hits include a
        // provider id is a rule the next reader has to re-audit by hand, which is
        // how a guard gets deleted rather than obeyed.
        await Assert.That(MatchingLines(plantedProviderRow, ["code"])).IsEqualTo(0).Because(
            "\"kilocode\" is a provider id, not the code agent. A substring match reports it, and a "
            + "reported site the author has to mentally discount is a guard people stop reading. "
            + "Agent names are matched on identifier boundaries");

        // `MenuRow` reads what the wizard PRINTED, not the C# line that printed it.
        // The first version of this control passed the `writer("  [1] code …")` SOURCE
        // line, which does not start with `[`, so the control went red while the rule
        // it guards was working. A non-vacuity control has to be pinned to the value
        // the predicate actually receives; pinning it to the call site tests the
        // wrong string and manufactures a red that reads as a product defect.
        await Assert.That(MenuRow.Match(printedAgentRow).Success).IsTrue().Because(
            "the agent menu row is `  [1] code    — …` and that is the shape R4 reads. If the "
            + "wizard's row format moves, this control goes red before R4 does, and the message "
            + "says the READER moved rather than the projection broke");
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     The agents the REAL composition root registers. Resolved from the
    ///     container <c>AddHarbor</c> returns, so it is what the CLI, the desktop
    ///     apps and the embedders get — not what a test decided.
    /// </summary>
    private static IReadOnlyList<AgentDefinition> RealCompositionRootAgents() => RealRegistry.Value;

    /// <summary>
    ///     A host over a caller-supplied agent registry, with the wizard resolved
    ///     the way <c>CoreModule</c> resolves it and nothing else added.
    /// </summary>
    private static ServiceProvider ComposeHostWithAgents(IReadOnlyList<AgentDefinition> agents)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRegistry>(new FixedRegistry(agents));
        services.AddSingleton<IConfigStore>(_ => new JsonConfigStore(
            Path.Combine(TempDir(), "config.json")));
        services.AddSingleton<AuthStore>();
        services.AddSingleton<OnboardingWizard>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The REAL composition root's agents, composed once per test process.
    /// </summary>
    /// <remarks>
    ///     Three rules read this vocabulary and <c>AddHarbor</c> builds the whole
    ///     registry set, so composing it per rule would triple the work for no added
    ///     information — and would leave R1 and R2 grading two different snapshots.
    ///     The container is disposed as soon as the snapshot is taken:
    ///     <c>GetAllAgents()</c> returns a fresh array, so nothing is left reading a
    ///     disposed graph.
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<AgentDefinition>> RealRegistry = new(() =>
    {
        using ServiceProvider sp = ComposeRealHost();
        return sp.GetRequiredService<IAgentRegistry>().GetAllAgents();
    });

    /// <summary>The real <c>AddHarbor</c> composition, as the shipped hosts call it.</summary>
    private static ServiceProvider ComposeRealHost()
    {
        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = TempDir(),
            DefaultStorageBackend = "memory",
        });
        return services.BuildServiceProvider();
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-arch-582", Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     A fourth builtin agent, declared the way a real one is: by handing
    ///     <see cref="AgentDefinition" /> its name, display name and description.
    ///     Nothing else in the repository mentions it.
    /// </summary>
    private static AgentDefinition NewPlantedAgent() => new(
        AgentName.Create(PlantedAgentName),
        "Review",
        "Planted by the #582 guard; registered in no hand-written list.",
        "model",
        "provider",
        PermissionRuleset.Default);

    /// <summary>
    ///     Drive <see cref="OnboardingWizard" /> to completion and report the printed
    ///     output and the agent it persisted.
    /// </summary>
    private static async Task<(string Output, string Chosen)> RunWizardAsync(
        IReadOnlyList<AgentDefinition> agents,
        string agentInput)
    {
        using ServiceProvider sp = ComposeHostWithAgents(agents);
        OnboardingWizard wizard = sp.GetRequiredService<OnboardingWizard>();
        IConfigStore store = sp.GetRequiredService<IConfigStore>();

        var printed = new List<string>();
        // provider (no key required) → preset default model → the agent answer.
        var answers = new Queue<string>(["ollama", "", agentInput]);
        Func<string, Task<string>> reader = _ => Task.FromResult(answers.Dequeue());

        Result wizardResult = await wizard.RunAsync(reader, printed.Add, CancellationToken.None);
        Result<HarborConfig> saved = await store.LoadAsync(CancellationToken.None);

        string chosen = "(the wizard did not complete)";
        if (wizardResult.IsSuccess && saved.IsSuccess)
        {
            chosen = saved.Value.Agent;
        }

        return (string.Join("\n", printed), chosen);
    }

    private static async Task AssertNoAgentNames(string relativePath)
    {
        string[] names =
        [
            .. RealCompositionRootAgents()
                .Select(a => a.Name.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal),
        ];

        IReadOnlyList<string> hits = FindAgentNameMentions(names, [relativePath]);

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            relativePath + " holds a hand-written copy of the agent set: a string literal naming "
            + "an agent the registry already holds. The registry is the declaration — every other "
            + "agent-set consumer in the product projects from it, and this file projects from "
            + "nothing. It is the shape #724 removed from the tool axis, and it rots silently: "
            + "the names agree today only because nobody has added a fourth builtin agent yet, "
            + "and the day they do, a registered agent is invisible here with nothing failing. "
            + "Read the registry, or stop enumerating — a description does not have to name the "
            + "set it describes. Spelled: " + Describe(hits) + ".");
    }

    /// <summary>
    ///     <c>path:line  literal</c> for every code line in the named files whose
    ///     string literal mentions one of <paramref name="names" />. Comments are
    ///     stripped first, so prose that names an agent is not a violation.
    /// </summary>
    private static IReadOnlyList<string> FindAgentNameMentions(
        IReadOnlyList<string> names,
        IReadOnlyList<string> relativePaths)
    {
        if (RepoPaths.RepoRoot is not { } root || names.Count == 0)
        {
            return [];
        }

        var hits = new List<string>();
        Regex namesInLiteral = AgentNameWordMatcher(names);
        foreach (string relative in relativePaths)
        {
            string path = Path.Combine(root, relative);
            string? source = SourceScan.TryReadAllText(path);
            if (source is null)
            {
                hits.Add(relative + "  (unreadable — the guard cannot grade a file it cannot read)");
                continue;
            }

            string[] lines = SourceScan.StripComments(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (string quoted in QuotedLiterals(lines[i]))
                {
                    Match named = namesInLiteral.Match(quoted);
                    if (named.Success)
                    {
                        hits.Add($"{relative}:{i + 1}  …{quoted.Trim()}…  (names \"{named.Value}\")");
                    }
                }
            }
        }

        return hits;
    }

    private static IEnumerable<string> QuotedLiterals(string line)
    {
        foreach (Match match in QuotedLiteral.Matches(line))
        {
            yield return match.Groups["value"].Value;
        }
    }

    /// <summary>
    ///     How many lines of <paramref name="source" /> carry a string literal naming
    ///     one of <paramref name="names" />. The predicate R1/R2 use, in the shape
    ///     the non-vacuity control drives it.
    /// </summary>
    private static int MatchingLines(string source, IReadOnlyList<string> names)
    {
        int hits = 0;
        Regex namesInLiteral = AgentNameWordMatcher(names);
        string[] stripped = SourceScan.StripComments(source).Split('\n');
        foreach (string line in stripped)
        {
            if (QuotedLiterals(line).Any(q => namesInLiteral.IsMatch(q)))
            {
                hits++;
            }
        }

        return hits;
    }

    private static string Describe(IReadOnlyList<string> hits)
        => hits.Count == 0 ? "(none)" : string.Join(" | ", hits);
}

/// <summary>
///     An <see cref="IAgentRegistry" /> over a fixed set. The production registry is
///     backed by a <c>ConcurrentDictionary</c> whose enumeration order is
///     unspecified, so the wizard's own ordering is graded by a set comparison in
///     <see cref="BuiltinAgentPickerProjectionRules" /> rather than by a position.
/// </summary>
internal sealed class FixedRegistry(IReadOnlyList<AgentDefinition> agents) : IAgentRegistry
{
    public IReadOnlyList<AgentDefinition> GetAllAgents() => agents;

    public Result<AgentDefinition> GetAgent(AgentName name)
    {
        AgentDefinition? found = agents.FirstOrDefault(a => a.Name.Value == name.Value);
        return found is null
            ? Result.Failure<AgentDefinition>($"Agent '{name}' is not registered.")
            : Result.Success(found);
    }

    public Result Register(AgentDefinition agent) => Result.Success();

    public Result Unregister(AgentName name) => Result.Success();
}
