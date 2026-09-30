// SessionForkPortSeamRules.cs — guard for #882.
//
// WHAT #882 ASKED, AND WHAT THIS FILE ANSWERS
// -------------------------------------------
// #874 (#670's fix) put `ISessionForker` in Domain and made it a REQUIRED
// parameter of `SessionFactory`'s constructor, so a host that wires nothing
// gets an error instead of a second, silent fork. #882 is the follow-up
// question, and it is a good one: the port is bound in exactly ONE place —
// `apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs:176` — and nothing
// says the next host has to bind it too. Only the compiler stands there, and
// the compiler is standing in the wrong place (see "WHAT THE REQUIRED
// PARAMETER DOES AND DOES NOT BUY" below).
//
// Three questions, three answers, all measured on the tree this guard ships
// against.
//
// 1. IS THE REQUIRED PARAMETER ENOUGH?  No, and the reason is specific.
//
//    A required constructor parameter is checked at `new SessionFactory(…)`
//    call sites. It is NOT checked at `services.AddSingleton<SessionFactory>()`,
//    which is a DI registration, not a construction — and that is the only form
//    the shipped host uses. So what actually holds the registration honest today
//    is neither the compiler nor a container-validation pass: `ValidateOnBuild`
//    and `ValidateScopes` appear NOWHERE in this repository, and the one test
//    that would notice is `AppHostDiTests.BuildAsync_Registers_SessionManager`,
//    which reaches `SessionFactory` only TRANSITIVELY, through
//    `SessionManager → SessionLifecycleService → SessionFactory`. Delete
//    `SessionManager` from that list and the fork port becomes entirely
//    unverified, with nothing red.
//
//    Worse, a required parameter does not stop the failure #874 was written to
//    prevent. `services.AddSingleton<ISessionForker, SomeStub>()` compiles, wires,
//    and hands a class that does not fork. So does a hand-written second
//    implementer of the port. Both are green builds, and both are the exact
//    regression #670 was filed for. The gap is not "the port is required" — it
//    is that NOTHING COUNTS THE IMPLEMENTERS OF THE PORT.
//
// 2. HOW MANY HOSTS NEED A FORK AT ALL?  One of the two that exist.
//
//      apps/Harbor.App.Avalonia  registers `SessionFactory`, and binds the port.
//      apps/Harbor.App.Cli       is a composition root that does NOT fork.
//
//    The CLI half is not hypothetical, and #882's own text ("a WPF/Maui/Blazor
//    revival … or a third-party embedder") understates it: the non-fork host is
//    ALREADY SHIPPED. `Harbor.App.Cli.csproj` takes a ProjectReference on
//    `Harbor.Ui.Framework.Abstractions` (where the port lives) and NOT on
//    `Harbor.Ui.Framework.Sessions` (where the consumer lives), so it cannot name
//    `SessionFactory` at all; it calls the core `SessionForkService` directly at
//    `apps/Harbor.App.Cli/Commands/SessionForkRunner.cs:34`, which is ALLOWED —
//    the CLI may reference `Harbor.Application`, which Presentation may not.
//
//    That is why a rule of the shape "every composition root registers
//    `ISessionForker`" would be WRONG: it would demand a fork from a host that
//    forks nothing, and the honest repair would be a stub — manufacturing the
//    very defect the port exists to prevent. The rule has to be conditional, and
//    the CLI is the live counterexample that makes the condition mean something.
//    `AHostThatDoesNotFork_IsNeverAskedForThePort` pins that, so the condition
//    cannot quietly become unconditional.
//
// 3. IS THERE ALREADY A MECHANISM?  Yes — two, both merged, both reused here.
//
//    * `CommonConfigContractRules` rule 5 (#453) counts a port's production
//      implementers BY SOURCE, and its header names the reason verbatim: "the
//      one implementer lives in apps/Harbor.App.Avalonia — a composition root
//      this test project does not reference, so a reflection sweep counts zero
//      and the rule could only ever be satisfied by an unwired seam." The fork
//      port is in that position exactly, and `SessionForkerAdapter`'s own
//      summary calls the seam "the same bridge shape as CommonConfigReaderAdapter,
//      for the same reason (#453, ADR-009)". This file is a SECOND INSTANCE of
//      rule 5, for the third port of that family. It reuses rule 5's
//      implementer-vs-caller matcher, its wrapped-base-list joining, and its
//      bound statement.
//    * `ThemeStoreSeamRules` rule 2 (#668) is the "EXACTLY ONE production type
//      implements it" invariant, including the exclusion this file needs: "Test
//      assemblies are excluded — a fake proves the port is injectable … and a
//      rule that fails on the right kind of code is a rule that gets deleted."
//
//    So this is NOT a new axis and NOT a new kind of rule (#555). The two facts
//    counted here — how many production types implement a Domain port, and
//    whether the composition root binds it — are the same two facts #453 already
//    counts for `ICommonConfigModelRefReader`. `SessionFactory`/`SessionForker`
//    simply was never added to that family when #874 landed.
//
//    `PipelineBehaviorCompositionTests` (#761) is the "declared minus registered,
//    measured on the REAL composition root" shape. It is NOT reused here and the
//    reason is worth stating rather than leaving implied: it measures `AddHarbor`,
//    which is the CLI's root, and the CLI has no `SessionFactory`. Pointing #761's
//    comparison at the fork port from `Harbor.Hosting.Tests` would compare a set
//    that is empty on both sides — green by measuring nothing.
//
// WHAT IS DIFFERENT ABOUT THE FORK PORT — THE POLARITY INVERSION
// --------------------------------------------------------------
// The file-tree ports (`IDirectoryLister`, `IFileTreePolicy`) are ones the shell
// must NOT implement: `AvaloniaFileTreeWalkRules` fails the build "if it ever
// does", because their implementations live in `Harbor.Application`. The fork
// port is the opposite case. Its implementation ALSO lives in
// `Harbor.Application` (`SessionForkService`), which the Presentation framework
// may not reference — so the composition root is REQUIRED to declare a bridge.
// "Exactly one implementer" therefore has a different answer on each side of the
// matrix, and the rule that carries the invariant is correspondingly inverted:
// the count is one, and it lives in `apps/`, never in `src/`.
//
// BOUND, STATED RATHER THAN IMPLIED
// ---------------------------------
// The product sweep is `SourceScan.EnumerateProductCsFiles()` — `src/` + `apps/`
// minus `obj/ bin/ tests/ contrib/ .worktrees/`, i.e. the helper #893 measured
// and whose blind spots that issue DECLARED rather than fixed. Two of them bear
// on this file and neither is pretended away:
//
//   * `contrib/` is invisible. Accepted, not overlooked: AGENTS.md records that
//     no CI workflow builds it, so a second fork there is not a shipped second
//     fork. A `contrib/` revival must add `src`+`apps` coverage or this count is
//     measuring less than it appears to.
//   * `tests/` is invisible, which is what makes rule 3's exclusion automatic
//     rather than a special case. `CoreSessionForker` (tests/
//     Harbor.Architecture.Tests) is a real projection over the real
//     `SessionForkService`, not a stub, and it is out of scope for a production
//     count for ThemeStoreSeamRules' reason.
//
// The implementer matcher is TEXTUAL, not a parse. A base list broken by an
// intervening comment, or a type aliased onto the port, is missed. That is the
// same bound `CommonConfigContractRules` states for its own copy of this
// matcher, and the same reason rule 1 — which reads the consumer's actual
// signature — is what actually carries the invariant.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #882): the session fork has ONE implementation, behind ONE
///     Domain port, bound by the ONE composition root that registers the consumer — and the
///     consumer still holds the port requiredly. See the file header for the three measured
///     answers this replaces guesswork with.
/// </summary>
public sealed class SessionForkPortSeamRules
{
    /// <summary>The #670/#874 fork port, declared in Domain beside the config ports.</summary>
    private const string PortName = "ISessionForker";

    /// <summary>Repo-relative path of the port's own declaration — not an implementer of itself.</summary>
    private const string PortPath = "src/Harbor.Ui.Framework.Abstractions/Forking/ISessionForker.cs";

    /// <summary>The Presentation consumer #670 gave a second, drifted copy of the fork.</summary>
    private const string ConsumerTypeName = "SessionFactory";

    /// <summary>Repo-relative path of that consumer.</summary>
    private const string ConsumerPath = "src/Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs";

    /// <summary>
    ///     The one implementer. Pinned by NAME, as <c>ThemeStoreSeamRules</c> pins
    ///     <c>ThemeStore</c>: the name is what identifies the implementation, so a second
    ///     bridge shows up here as a changed row rather than as an anonymous second
    ///     implementer that looks perfectly correct on its own.
    /// </summary>
    private const string SoleBridgeName = "SessionForkerAdapter";

    /// <summary>
    ///     The composition root that registers the consumer. It is expected to be the same
    ///     file that binds the port — a host that registers the consumer in one file and the
    ///     port in another is a two-file edit nobody reviews as one.
    /// </summary>
    private const string RegistrationPath = "apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs";

    /// <summary>
    ///     The host that is NOT a fork host, named so the conditional in
    ///     <see cref="EveryCompositionRoot_ThatRegistersTheConsumer_AlsoRegistersThePort" />
    ///     has a counterexample that a run can check rather than a claim in a comment.
    /// </summary>
    private const string NonForkingHostPath = "apps/Harbor.App.Cli";

    /// <summary>
    ///     A DI registration of the consumer. Matched as <c>Add*&lt;SessionFactory</c> followed
    ///     by a separator, so the registration form is what is graded and not the spelling of
    ///     the service-collection method.
    /// </summary>
    private static readonly Regex RegistersConsumer = new(
        @"\bAdd(?:Singleton|Scoped|Transient)<\s*" + ConsumerTypeName + @"\s*[,>]",
        RegexOptions.Compiled);

    /// <summary>DI registration of the port, same shape.</summary>
    private static readonly Regex RegistersPort = new(
        @"\bAdd(?:Singleton|Scoped|Transient)<\s*" + PortName + @"\s*[,>]",
        RegexOptions.Compiled);

    /// <summary>Normalises line endings and splits, so a CRLF checkout scans the same.</summary>
    private static string[] StrippedLines(string source) =>
        SourceCommentStripper.StripAll(source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

    // =====================================================================
    // Rule 1 — the port exists, in Domain, and the consumer holds it REQUIREDLY.
    // =====================================================================

    /// <summary>
    ///     §ARCH (#882). The consumer's constructor takes the port as a non-nullable,
    ///     non-optional parameter, and the port is declared in the Domain assembly the
    ///     composition root adapts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is #874's central design decision, and until now it was only ever a doc
    ///         comment — three separate comments, in the port, the consumer and the test
    ///         fixture, all asserting that "a host that wires nothing fails to compile". That
    ///         sentence is the claim #882 questions and it is <b>wrong as written for a DI
    ///         host</b>: a registration is not a construction, so no compiler sees it. What is
    ///         true is narrower — a host that constructs the consumer by hand cannot omit the
    ///         argument. So the property is asserted here against the real signature, where it
    ///         is both true and checkable, instead of being left to prose.
    ///     </para>
    ///     <para>
    ///         The nullable and optional arms are the ones worth guarding: both compile, both
    ///         wire, and both hand `CreateBranchAsync` a null `_forker` — a
    ///         <see cref="NullReferenceException" /> on the user's first branch gesture, which
    ///         is the one gesture the port exists to make correct.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task TheConsumer_HoldsTheForkPort_Requiredly()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard reads the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the sweep yields nothing and every rule below reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        var failures = new List<string>();

        if (!File.Exists(Path.Combine(root, PortPath.Replace('/', Path.DirectorySeparatorChar))))
        {
            failures.Add(
                $"{PortPath} does not exist. The fork port belongs in Domain, beside the other narrow "
                + "seams a composition root adapts (#453, ADR-009): the UI framework has to fork "
                + "sessions and the layer matrix draws Presentation → Application as a violation "
                + "(`FullLayerMatrixTests.Matrix_AllowedEntries_RespectLayerRules`), so the port is how "
                + "the desktop asks the core. #670.");
        }

        string consumer = Path.Combine(root, ConsumerPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(consumer))
        {
            failures.Add(
                $"{ConsumerPath} does not exist, so there is no consumer left to hold the port requiredly. "
                + "#670 removed its hand-written fork; if the type moved, this rule is no longer talking "
                + "about the code it was written for, which is a fact to report rather than a smaller "
                + "table to shrink.");
        }
        else
        {
            string parameters = ConstructorParameters(SourceScan.TryReadAllText(consumer) ?? string.Empty,
                ConsumerTypeName);
            if (parameters.Length == 0)
            {
                failures.Add(
                    $"{ConsumerPath} declares no readable `public {ConsumerTypeName}(…)` constructor. "
                    + "Rule 1 grades the port parameter's own nullability, so a constructor it cannot "
                    + "read is a question this guard cannot answer — and 'cannot answer' must not read "
                    + "as 'passed'.");
            }
            else
            {
                string? requiredness = PortParameterRequiredness(parameters);
                switch (requiredness)
                {
                    case "required":
                        break;
                    case "nullable":
                        failures.Add(
                            $"{ConsumerPath}: the {PortName} constructor parameter is NULLABLE. A host "
                            + "may then wire nothing and the consumer compiles, wires, and throws a "
                            + "NullReferenceException on the first branch gesture — the one gesture this "
                            + "port exists to make correct. #670 deleted a second fork that failed exactly "
                            + "this way; the required parameter is what stops it coming back silently.");
                        break;
                    case "optional":
                        failures.Add(
                            $"{ConsumerPath}: the {PortName} constructor parameter is OPTIONAL. "
                            + "`ISessionForker? forker = null` is the shape a future host reaches for when "
                            + "it is not a fork host and does not want a compile error — and it is a stub "
                            + "by another name. The CLI shows the honest alternative: do not reference the "
                            + "consumer's assembly at all, and call the core fork directly.");
                        break;
                    default:
                        failures.Add(
                            $"{ConsumerPath}: `public {ConsumerTypeName}(…)` does not take a {PortName} at "
                            + "all. #874 made the port the consumer's only way to fork; without the "
                            + "parameter the consumer either stopped forking or grew a private copy, and "
                            + "both are the duplicate #670 was filed for. Parameters seen: " + parameters);
                        break;
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(
            "§ARCH (#882). #874's fix rests on the port being REQUIRED, and until this guard that rested "
            + "on three doc comments asserting something the compiler does not check for a DI host. The "
            + "property is real — assert it where it is true. " + string.Join("\n", failures));
    }

    // =====================================================================
    // Rule 2 — exactly ONE production implementer.
    // =====================================================================

    /// <summary>
    ///     §ARCH (#882). Exactly one file under <c>src/</c> + <c>apps/</c> declares a type that
    ///     implements <see cref="PortName" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the invariant #882 is actually about, and the one the required parameter
    ///         does not buy. A second implementer compiles, wires, and is invisible in review —
    ///         each one looks correct alone. #670 was filed on precisely that shape: the UI
    ///         framework's hand-written fork had drifted far enough that a UI branch was not
    ///         recognisable as a branch, and the reason it could drift is that there was nothing
    ///         counting forks.
    ///     </para>
    ///     <para>
    ///         Counted BY SOURCE, after <c>CommonConfigContractRules</c> rule 5, and for the
    ///         reason that file's header gives: the one implementer lives in
    ///         <c>apps/Harbor.App.Avalonia</c>, a composition root this test project does not
    ///         reference. A reflection sweep here counts zero and the rule could then only ever
    ///         be satisfied by an UNWIRED seam — a guard that rewards deleting the bridge. That
    ///         is not hypothetical; it is what rule 5's first CI run measured.
    ///     </para>
    ///     <para>
    ///         <b>Test assemblies are excluded, by the sweep's own bounds</b> — not by a
    ///         special case. <c>SourceScan.ProductTrees</c> is <c>["src","apps"]</c> and
    ///         <c>IsBuildOutput</c> drops <c>/tests/</c>, so
    ///         <c>CoreSessionForker</c> is out of scope here for
    ///         <c>ThemeStoreSeamRules</c>'s stated reason: a test fixture that implements the
    ///         port proves the port is injectable, and a rule that fails on the right kind of
    ///         code is a rule that gets deleted.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task ExactlyOneProductionType_ImplementsTheForkPort()
    {
        IReadOnlyList<string> implementers = ProductionImplementers();

        await Assert.That(implementers.Count).IsEqualTo(1).Because(
            "§ARCH (#882). " + SoleBridgeName + " is the only production implementer of " + PortName + ", "
            + "and it is the only place a second fork can be born: the required constructor parameter "
            + "stops a host omitting the port, and nothing at all stops one ADDING to it. A stub that "
            + "does not fork, or a hand-written copy beside the consumer, both compile and both wire. "
            + "Found " + implementers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ": " + (implementers.Count == 0
                ? "(none — the seam is unwired, which this file cannot distinguish from a broken "
                  + "matcher; see NonVacuity_TheSweepFindsTheRealBridge)"
                : string.Join(", ", implementers)));
    }

    // =====================================================================
    // Rule 3 — the sole implementer is the composition-root bridge.
    // =====================================================================

    /// <summary>
    ///     §ARCH (#882). The one implementer is <see cref="SoleBridgeName" />, declared under
    ///     <c>apps/</c> — never under <c>src/</c>, never beside the consumer it serves.
    /// </summary>
    /// <remarks>
    ///     This is the polarity inverse of <c>AvaloniaFileTreeWalkRules</c> rule 2, and the
    ///     difference is the whole reason the count above is not enough. For
    ///     <c>IDirectoryLister</c>/<c>IFileTreePolicy</c> the shell must not implement anything —
    ///     the implementations sit in <c>Harbor.Application</c>, which it may reference. For
    ///     <c>ISessionForker</c> the implementation is in <c>Harbor.Application</c> too and
    ///     Presentation may NOT reference it, so a bridge in a composition root is not an
    ///     intrusion but the only legal wiring. What is forbidden is the bridge's opposite: an
    ///     implementer in <c>src/</c>, which is a fork living next to the class that is supposed to
    ///     reach the core for it — i.e. #670's deleted copy, back under a name that reads well.
    /// </remarks>
    [Test]
    public async Task TheSoleImplementer_IsTheCompositionRootBridge()
    {
        IReadOnlyList<string> implementers = ProductionImplementers();

        if (implementers.Count == 0)
        {
            // Rule 2 owns the empty case; an empty set has no implementer to place.
            return;
        }

        // Graded over the WHOLE set, not over implementers[0]. When a second implementer
        // is what the guard is catching, "the one implementer is the bridge" is not the
        // useful sentence — the useful sentence is WHICH file is the impostor and WHY it
        // is one, and grading the set is what produces that. Bailing out when the count
        // is wrong would hand the count to rule 2 and throw away the diagnosis.
        var offenders = new List<string>();
        foreach (string implementer in implementers)
        {
            if (!Path.GetFileNameWithoutExtension(implementer).Equals(
                    SoleBridgeName, StringComparison.Ordinal))
            {
                offenders.Add(
                    $"{implementer} implements " + PortName + " but is not " + SoleBridgeName + ". The one "
                    + "implementation is the bridge that forwards to Harbor.Application's SessionForkService. "
                    + "A different name is a rename, or a replacement that does the fork itself — and a "
                    + "replacement is invisible in review, because on its own it reads correctly. The name is "
                    + "what identifies the implementation rather than the assembly it sits in, which is why "
                    + "ThemeStoreSeamRules pins `ThemeStore`.");
            }

            if (!implementer.StartsWith("apps/", StringComparison.Ordinal))
            {
                offenders.Add(
                    $"{implementer} implements " + PortName + " from src/. The bridge belongs in a "
                    + "composition root, and the only one in-tree is apps/Harbor.App.Avalonia. An "
                    + "implementer under src/ is a fork living BESIDE " + ConsumerTypeName + " — which is "
                    + "exactly where #670 found the copy that had drifted until a UI branch set no "
                    + "ParentSessionId, applied a title it never persisted, and regenerated every copied "
                    + "message id. Putting it in src/ is not a different way to wire the port; it is the "
                    + "defect the port was introduced to remove, relocated. If this host genuinely cannot "
                    + "reference Harbor.Application, that is what the port and the adapter are for.");
            }
        }

        await Assert.That(offenders).IsEmpty().Because(
            "§ARCH (#882). " + SoleBridgeName + " is the bridge, and a composition root is where it lives. "
            + "This is the polarity inverse of AvaloniaFileTreeWalkRules rule 2: for "
            + "IDirectoryLister/IFileTreePolicy the shell must not implement anything, because their "
            + "implementations sit in Harbor.Application which it may reference — but for " + PortName
            + " that same implementation is out of Presentation's reach, so the bridge is required rather "
            + "than forbidden. What is forbidden is the bridge's opposite. " + string.Join("\n", offenders));
    }

    // =====================================================================
    // Rule 4 — the binding half: the root that registers the consumer binds the port.
    // =====================================================================

    /// <summary>
    ///     §ARCH (#882). Every composition root that registers <see cref="ConsumerTypeName" />
    ///     registers <see cref="PortName" /> in the same file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is #882's literal question, and it is deliberately CONDITIONAL. The obvious
    ///         rule — every composition root registers the port — would be wrong, and measurably
    ///         so: <c>apps/Harbor.App.Cli</c> is a shipped composition root that does not fork, and
    ///         the only way to satisfy an unconditional rule there would be to register a stub,
    ///         manufacturing the exact defect the port exists to prevent. The next host that is
    ///         not a fork host is therefore not a problem to be solved by a placeholder; it is a
    ///         host that simply does not reference <c>Harbor.Ui.Framework.Sessions</c>.
    ///     </para>
    ///     <para>
    ///         Same file, not same tree: a host that registers the consumer on one line and the
    ///         port in another file has split one decision across two, and neither file's diff
    ///         shows the other half. This is the seam #453 already spells as a three-file
    ///         <c>SeamFiles</c> list.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task EveryCompositionRoot_ThatRegistersTheConsumer_AlsoRegistersThePort()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("This rule reads the working tree; without a checkout it enforces nothing.");

        if (root is null)
        {
            return;
        }

        var failures = new List<string>();
        var registrars = new List<string>();

        // `EnumerateCsFiles("apps")` and not `EnumerateProductCsFiles()`: the product helper
        // is hard-wired to ["src","apps"], and a composition root is an apps/ file by
        // definition. Asking for apps/ alone also keeps the registration scan away from src/,
        // where a DI registration would be #470's service locator rather than composition.
        foreach (string path in SourceScan.EnumerateCsFiles("apps"))
        {
            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            // Comments stripped: ServiceRegistration.cs:173-175 is a four-line comment
            // explaining WHY the adapter exists, and it names both types. A registration scan
            // that reads prose grades the explanation as the registration.
            string clean = string.Join('\n', StrippedLines(text));

            if (!RegistersConsumer.IsMatch(clean))
            {
                continue;
            }

            string relative = SourceScan.Relative(path);
            registrars.Add(relative);

            if (!RegistersPort.IsMatch(clean))
            {
                failures.Add(
                    $"{relative} registers {ConsumerTypeName} but not {PortName}. {ConsumerTypeName}'s "
                    + "constructor requires the port (#670/#874), so this container cannot build it: "
                    + "nothing here is validated at build time — `ValidateOnBuild` and `ValidateScopes` "
                    + "appear nowhere in this repository — and the failure surfaces as an "
                    + "InvalidOperationException the first time the session graph is resolved, or not "
                    + "at all if the graph is never resolved. Add "
                    + "`services.AddSingleton<" + PortName + ", " + SoleBridgeName + ">();` beside the "
                    + "registration, or do not register " + ConsumerTypeName + " here at all if this host "
                    + "does not fork. Do NOT reach for a stub: a port bound to something that does not "
                    + "fork is the regression #882 was filed about.");
            }
        }

        await Assert.That(registrars).IsNotEmpty().Because(
            "No file under apps/ registers " + ConsumerTypeName + ", so the comparison this rule is made of "
            + "has an empty left-hand side and the rule would pass over any tree at all. The one "
            + "registration in-tree is " + RegistrationPath + "; if it moved, that is a fact to report, "
            + "not a smaller table to shrink.");

        await Assert.That(failures).IsEmpty().Because(
            "§ARCH (#882). The fork port is bound in exactly one place today "
            + "(" + RegistrationPath + ":176), and #882's question is what happens in the next host. "
            + "The required constructor parameter does not answer it: a DI registration is not a "
            + "construction, so no compiler sees it. " + string.Join("\n", failures));
    }

    // =====================================================================
    // Rule 5 — the condition stays conditional.
    // =====================================================================

    /// <summary>
    ///     §ARCH (#882). <c>apps/Harbor.App.Cli</c> registers no <see cref="ConsumerTypeName" />,
    ///     and so is never asked for a fork.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the non-vacuity control for rule 4's CONDITION, and it is the direct
    ///         answer to #882's unstated third possibility: that the next host is not a fork host
    ///         at all. That is not a future host. It is the CLI, shipped, and the reason is
    ///         structural rather than deliberate: <c>Harbor.App.Cli.csproj</c> references
    ///         <c>Harbor.Ui.Framework.Abstractions</c> (the port) and not
    ///         <c>Harbor.Ui.Framework.Sessions</c> (the consumer), so it cannot name the consumer.
    ///         It calls the core <c>SessionForkService</c> directly at
    ///         <c>Commands/SessionForkRunner.cs:34</c>, which is permitted — the CLI is a host that
    ///         may reference <c>Harbor.Application</c>.
    ///     </para>
    ///     <para>
    ///         So "one host out of two needs a fork" is a measured fact, not an estimate, and the
    ///         rule that encodes it must keep asking the question <em>conditionally</em>. If this
    ///         test ever goes red, a host that does not fork has started referencing the consumer —
    ///         and the right answer is to wire the port there, not to relax rule 4 into the
    ///         unconditional form that would have demanded a stub.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task AHostThatDoesNotFork_IsNeverAskedForThePort()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("This rule reads the working tree; without a checkout it enforces nothing.");

        if (root is null)
        {
            return;
        }

        // The whole host tree, not one file: a claim about "this host never registers X"
        // cannot be made from a file list that might be missing the file that does.
        var consumers = new List<string>();
        var ports = new List<string>();
        foreach (string path in SourceScan.EnumerateCsFiles(NonForkingHostPath))
        {
            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            string clean = string.Join('\n', StrippedLines(text));
            if (RegistersConsumer.IsMatch(clean))
            {
                consumers.Add(SourceScan.Relative(path));
            }

            if (RegistersPort.IsMatch(clean))
            {
                ports.Add(SourceScan.Relative(path));
            }
        }

        await Assert.That(consumers).IsEmpty().Because(
            NonForkingHostPath + " now registers " + ConsumerTypeName + " (" + string.Join(", ", consumers)
            + "). That is allowed and welcome — a CLI that forks through the UI framework needs the "
            + "port — but it makes this host a FORK host, so the non-forking half of the measured "
            + "answer (\"one host of two needs a fork\") no longer holds and this control has to be "
            + "re-derived rather than deleted. Rule 4 is already asking the right question of the new "
            + "registration; this test is what stops the condition from quietly becoming unconditional, "
            + "which is the form that would have demanded a stub from a host that forks nothing.");

        await Assert.That(ports).IsEmpty().Because(
            NonForkingHostPath + " registers " + PortName + " (" + string.Join(", ", ports)
            + ") without registering the consumer. A port bound in a host that never constructs "
            + ConsumerTypeName + " is the shape of the stub #882 is about, arrived at from the other "
            + "direction: nothing reads it, so a future author can reasonably believe the host forks, "
            + "and a reader of the DI file cannot tell that it does not.");
    }

    // =====================================================================
    // Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The sweep must be reading the real tree: the bridge #874 shipped has to be in the
    ///     answer. A sweep that found nothing would report "no second implementer" on every run —
    ///     green by finding nothing, which is the shape of a guard that enforces nothing, and the
    ///     exact vacuity <c>CommonConfigContractRules</c>'s first CI run measured on its own
    ///     reflection probe.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheSweepFindsTheRealBridge()
    {
        IReadOnlyList<string> implementers = ProductionImplementers();

        // `.Count`, not `IsNotEmpty()`: TUnit routes IsEmpty/IsNotEmpty by the STATIC
        // collection type and IReadOnlyList is not one of the shapes it names —
        // the trap ThemeStoreSeamRules documents in its own remarks.
        await Assert.That(implementers.Count).IsGreaterThan(0).Because(
            "the source sweep over " + string.Join(" + ", SourceScan.ProductTrees) + " found no file that "
            + "declares an implementer of " + PortName + ". " + SoleBridgeName
            + " is declared at apps/Harbor.App.Avalonia/Services/" + SoleBridgeName + ".cs as "
            + "`public sealed class " + SoleBridgeName + " : " + PortName + "`. If the bridge is gone the "
            + "seam is unwired and rule 2 is passing on an empty set; if it was renamed, the matcher is "
            + "reading a spelling that no longer exists. Either way this is a fact to report, not a "
            + "smaller table to shrink.");
    }

    /// <summary>
    ///     The implementer matcher must tell an IMPLEMENTER from a CALLER and from a REGISTRAR —
    ///     and the port's own declaration from all three.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Borrowed from <c>CommonConfigContractRules</c>, whose first CI run returned THREE
    ///         files for a plain "does the file mention the name" scan: the adapter, the file that
    ///         registers it, and the consumer that holds it as a parameter. A guard that cannot
    ///         separate those is a guard satisfied by deleting the only real implementer.
    ///     </para>
    ///     <para>
    ///         The base-list spellings are driven too — the bare list, a list wrapped across
    ///         lines, and a generic constraint — because #670's copy and a hypothetical second
    ///         one can each be written in any of the three, and a matcher that only catches the
    ///         first is a matcher that catches the shape the author happened to pick today.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task NonVacuity_TheImplementerMatcher_TellsAnImplementerFromACallerAndARegistrar()
    {
        // A caller: the port named as a constructor parameter. This is the consumer's own
        // signature, and counting it would report a second fork on the file that removed one.
        const string caller = """
            public SessionFactory(
                IAgentRegistry agents,
                ISessionForker forker,
                ILogger<SessionFactory> logger)
            {
                _forker = forker;
            }
            """;

        // A registrar: the composition root binding the port. Counting this would make the
        // rule demand that the wiring be deleted.
        const string registrar = """
            services.AddSingleton<ISessionForker, SessionForkerAdapter>();
            services.AddSingleton<SessionFactory>();
            """;

        // The port's own declaration, which implements nothing.
        const string portItself = """
            public interface ISessionForker
            {
                Task<Result<SessionForked>> ForkAsync(string sessionId);
            }
            """;

        // The field the parameter is stored into.
        const string field = "    private readonly ISessionForker _forker;\n";

        // Prose: the port's own doc comment names the port and the consumer.
        const string prose = """
            // #670: this used to be a second fork. SessionFactory now reaches
            // SessionForkService through the ISessionForker port instead.
            /// <inheritdoc cref="ISessionForker" />
            public Task ForkAsync() => Task.CompletedTask;
            """;

        foreach (string required in new[]
                 {
                     "public sealed class SessionForkerAdapter : ISessionForker\n",      // the bridge
                     "internal sealed class CoreSessionForker(ISessionStore s) : ISessionForker\n",
                     "public sealed class Local :\n    ISessionForker\n",                 // wrapped
                     "internal static class W<T> where T : ISessionForker\n",             // constraint
                 })
        {
            await Assert.That(DeclaresImplementer(StrippedLines(required))).IsTrue().Because(
                "the matcher must report a REAL implementer. This is the declaration #882 is about: a "
                + "second type satisfying the port is the whole failure, and one written on a different "
                + "line, wrapped, or as a constraint is still it. Not detected: " + required);
        }

        foreach (string innocent in new[] { caller, registrar, portItself, field, prose })
        {
            await Assert.That(DeclaresImplementer(StrippedLines(innocent))).IsFalse().Because(
                "a caller, a registrar, the port's own declaration, a field and a doc comment are five "
                + "things the rule is supposed to ALLOW — #882's guard would otherwise fail on the code "
                + "that is correct, and a permanently-red rule is one that gets deleted. Wrongly "
                + "detected: " + innocent);
        }
    }

    /// <summary>
    ///     The requiredness probe must accept a required parameter and reject both ways of
    ///     quietly making it optional — the two spellings a future host reaches for when it is
    ///     not a fork host and would rather not have the compiler object.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheRequirednessProbe_RejectsANullableAndAnOptionalPort()
    {
        string required = ConstructorParameters(
            "public SessionFactory(IAgentRegistry a, ISessionForker forker, ILogger<SessionFactory> l) { }",
            ConsumerTypeName);
        string nullable = ConstructorParameters(
            "public SessionFactory(IAgentRegistry a, ISessionForker? forker, ILogger<SessionFactory> l) { }",
            ConsumerTypeName);
        string optional = ConstructorParameters(
            "public SessionFactory(IAgentRegistry a, ISessionForker? forker = null, ILogger<SessionFactory> l) { }",
            ConsumerTypeName);
        // A DIFFERENT type whose name merely starts with the port's. Counting it would make
        // rule 1 pass on a port that is not there.
        string lookalike = ConstructorParameters(
            "public SessionFactory(IAgentRegistry a, ISessionForkerFactory f, ILogger<SessionFactory> l) { }",
            ConsumerTypeName);
        // The same hole with no `?` to notice, which is why the default is tested first.
        string defaulted = ConstructorParameters(
            "public SessionFactory(IAgentRegistry a, ISessionForker forker = null, ILogger<SessionFactory> l) { }",
            ConsumerTypeName);

        await Assert.That(PortParameterRequiredness(required)).IsEqualTo("required").Because(
            "the shape #874 shipped — a plain, non-nullable, non-defaulted port parameter — has to be "
            + "the one the probe calls required, or rule 1 fails on correct code and gets deleted");

        await Assert.That(PortParameterRequiredness(nullable)).IsEqualTo("nullable").Because(
            "a nullable port parameter is the shape that lets a host wire nothing and throw on the "
            + "user's first branch gesture. The probe has to see the `?`, or rule 1 is decorative");

        await Assert.That(PortParameterRequiredness(optional)).IsEqualTo("optional").Because(
            "`= null` is how the required parameter gets talked out of being required, and it is the "
            + "shape most likely to arrive as a well-meaning 'let hosts that do not fork opt out'");

        await Assert.That(PortParameterRequiredness(defaulted)).IsEqualTo("optional").Because(
            "`ISessionForker forker = null` carries no `?` at all, so a probe that looked only for the "
            + "nullable annotation would call it required and pass a signature that omits the port "
            + "whenever the argument is left out. The default is the load-bearing token, not the "
            + "annotation");

        await Assert.That(PortParameterRequiredness(lookalike)).IsNull().Because(
            "ISessionForkerFactory is a different type whose name merely starts with the port's. A "
            + "prefix match would let rule 1 report 'required' on a port that is not in the signature at "
            + "all, and the green would be about nothing");
    }

    // =====================================================================
    // Machinery — the three predicates, kept separate so each can be driven.
    // =====================================================================

    /// <summary>
    ///     Every <c>src/</c>+<c>apps/</c> file that DECLARES an implementer of the port, as
    ///     repo-relative paths.
    /// </summary>
    private static IReadOnlyList<string> ProductionImplementers()
    {
        var found = new List<string>();
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            string relative = SourceScan.Relative(path);
            if (string.Equals(relative, PortPath, StringComparison.Ordinal))
            {
                continue;
            }

            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            if (DeclaresImplementer(StrippedLines(text)))
            {
                found.Add(relative);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     Whether a file DECLARES a type implementing the port, as opposed to naming it.
    /// </summary>
    /// <remarks>
    ///     The distinction is the base list, and the method is
    ///     <c>CommonConfigContractRules</c>'s matcher rather than a fourth copy of the idea —
    ///     two rules, one matcher, so a defect in it shows up in both rather than as a quiet
    ///     disagreement. A line ending in a separator is joined with the next before matching,
    ///     because base lists wrap. Comments are already blanked by
    ///     <see cref="StrippedLines" />, so the port's own documentation does not read as code.
    /// </remarks>
    private static bool DeclaresImplementer(string[] cleanLines)
    {
        for (int i = 0; i < cleanLines.Length; i++)
        {
            // Try this line on its own FIRST. Joining before searching would let a line
            // ending in ':' — a ternary arm, a switch label, a `case` — pull the next line
            // into a match it never really had. A base list that wraps is only worth
            // chasing once the single-line reading has failed.
            if (IsBaseListEntry(cleanLines[i]))
            {
                return true;
            }

            if (!ContinuesBaseList(cleanLines[i]) || i + 1 >= cleanLines.Length)
            {
                continue;
            }

            // The list wraps. Accumulate forward while the tail keeps asking for more,
            // bounded at four joins — a real base list is short, and an unbounded walk
            // over a file would eventually pair two unrelated lines.
            string accumulated = cleanLines[i].Trim();
            for (int j = i + 1, guard = 0; j < cleanLines.Length && guard < 4; j++, guard++)
            {
                accumulated = accumulated + " " + cleanLines[j].Trim();
                if (IsBaseListEntry(accumulated))
                {
                    return true;
                }

                if (!ContinuesBaseList(cleanLines[j]))
                {
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>Whether one line is a base-list entry naming the port, or a constraint's tail.</summary>
    private static bool IsBaseListEntry(string line)
    {
        int at = line.IndexOf(PortName, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        string before = line[..at].TrimEnd();
        string after = line[(at + PortName.Length)..].TrimStart();

        // A base-list entry (": ISessionForker", ", ISessionForker") or the tail of a
        // generic constraint ("where T : ISessionForker"). The tail test is what keeps a
        // constructor PARAMETER out: in `ISessionForker forker,` the text before the name
        // is only whitespace, so there is no base list to be in.
        bool inBaseList = before.EndsWith(":", StringComparison.Ordinal)
                          || before.EndsWith(",", StringComparison.Ordinal);
        bool isConstraintTail = after.Length == 0
                                || after.StartsWith(")", StringComparison.Ordinal)
                                || after.StartsWith(",", StringComparison.Ordinal);

        return inBaseList && isConstraintTail;
    }

    /// <summary>
    ///     Whether a line ends in a token a base list can continue from. <c>:</c> is here as
    ///     well as <c>,</c> and <c>|</c> — a declaration broken as <c>class X :</c> then
    ///     <c>ISessionForker</c> on the next line is the same implementer, and
    ///     <c>CommonConfigContractRules</c>'s copy of this matcher omits it, which is why its
    ///     control set drives only the comma form.
    /// </summary>
    private static bool ContinuesBaseList(string line)
    {
        string trimmed = line.TrimEnd();
        return trimmed.EndsWith(":", StringComparison.Ordinal)
               || trimmed.EndsWith(",", StringComparison.Ordinal)
               || trimmed.EndsWith("|", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The text inside <c>public <paramref name="typeName" />( … )</c>, with comments
    ///     stripped, or empty when no such constructor is readable.
    /// </summary>
    /// <remarks>
    ///     Parens are matched by depth rather than by splitting on <c>)</c>, so a parameter with
    ///     a default of <c>default</c> or a generic default does not truncate the list — a
    ///     truncated list is one where the port parameter can be missing without the probe
    ///     noticing, which is rule 1 failing open.
    /// </remarks>
    private static string ConstructorParameters(string source, string typeName)
    {
        string text = string.Join('\n', StrippedLines(source));
        string head = "public " + typeName + "(";
        int at = text.IndexOf(head, StringComparison.Ordinal);
        if (at < 0)
        {
            return string.Empty;
        }

        int open = at + head.Length - 1;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(open + 1)..i];
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    ///     <c>"required"</c>, <c>"nullable"</c>, <c>"optional"</c> for the port parameter,
    ///     or <see langword="null" /> when the constructor does not take one.
    /// </summary>
    private static string? PortParameterRequiredness(string parameters)
    {
        foreach (string raw in SplitTopLevelCommas(parameters))
        {
            string parameter = raw.Trim();
            if (!parameter.StartsWith(PortName, StringComparison.Ordinal))
            {
                continue;
            }

            // Reject a longer identifier that merely starts with the port's name:
            // ISessionForkerFactory is not the port, and a prefix match would let rule 1
            // report "required" on a signature the port is absent from.
            string rest = parameter[PortName.Length..];
            if (rest.Length > 0 && (char.IsLetterOrDigit(rest[0]) || rest[0] == '_'))
            {
                continue;
            }

            rest = rest.Trim();

            // A default is tested BEFORE the `?`, because a parameter can be both and the
            // more specific claim is the more actionable one: `ISessionForker? forker = null`
            // is not a host that forgot the annotation, it is a host that declared the
            // parameter optional on purpose. It also catches the non-nullable spelling
            // `ISessionForker forker = someStub`, which is the same hole with no `?` to
            // notice.
            if (rest.IndexOf('=', StringComparison.Ordinal) >= 0)
            {
                return "optional";
            }

            return rest.StartsWith("?", StringComparison.Ordinal) ? "nullable" : "required";
        }

        return null;
    }

    /// <summary>Splits a parameter list on commas that are not inside brackets.</summary>
    private static IReadOnlyList<string> SplitTopLevelCommas(string parameters)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < parameters.Length; i++)
        {
            char c = parameters[i];
            if (c is '(' or '[' or '<')
            {
                depth++;
            }
            else if (c is ')' or ']' or '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(parameters[start..i]);
                start = i + 1;
            }
        }

        parts.Add(parameters[start..]);
        return [.. parts];
    }
}
