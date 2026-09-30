// ProcessEnvIsolationRule.cs — the guard for issue #823.
//
// THE DEFECT THIS GUARDS
// ----------------------
// Nine test classes call `Environment.SetEnvironmentVariable`, and not one of
// them carried a `[NotInParallel]` form of any kind. On Unix that call is
// PROCESS state, and the runtime source says so unambiguously
// (`Environment.Variables.Unix.cs`): `SetEnvironmentVariableCore` takes
// `lock (s_environment!)` on a `static Dictionary<string, string>?`. Not
// thread-local, not per-class, not per-AppDomain.
//
// CI runs one `dotnet exec` per test project, so the contention unit is the
// TEST PROJECT: two classes in one project share one environment block; two
// classes in different projects share nothing. That is why a per-class reading
// of the issue's list ("nine classes, therefore one problem") is wrong, and why
// this rule is written per project rather than per class.
//
// WHY A NAMED KEY AND NOT THE BARE FORM
// -------------------------------------
// Read from TUnit 1.61.0's scheduler, not from the XML docs.
// `TestScheduler.ExecuteAllPhasesAsync` (TestScheduler.cs:194-219) starts the
// unconstrained `Parallel` bucket and the `KeyedNotInParallel` bucket and joins
// them with `RunPhasesConcurrentlyAsync` — the two buckets run CONCURRENTLY.
// Bare `[NotInParallel]` is the only form that takes the `NotInParallelLock`
// writer gate, i.e. the only form that means "completely alone". A named key is
// a mutex over NAMED PEERS: `ConstraintKeyScheduler` admits a test when no held
// key intersects its own.
//
// So a named key is right and keyless is not, for a price that is concrete:
// keyless on `AppHostDiTests` means that class holds
// `Harbor.App.Avalonia.Tests` alone for the whole of every one of its test
// methods, forever. #700 priced exactly that and refused it, and it was right.
// Its conclusion was "prefer not writing process state at all", which is not
// available for this subject: `AppHost.ResolveHarborDir` (AppHost.cs:121-126)
// resolves the home from `GetFolderPath(SpecialFolder.UserProfile)` with a
// `$HOME` fallback and takes no parameter.
//
// THE RULE
// ---------
// (1) WRITER, derived. Every class under `tests/**` that both contains a
//     `[Test]` method and CALLS `Environment.SetEnvironmentVariable(` must carry
//     a `[NotInParallel]` form. This is #823's defect exactly — "no attribute at
//     all" — and it is derived from the tree, so a new writer fails here instead
//     of being silently unprotected.
//
//     Classes with no `[Test]` are out of scope, and deliberately so: the
//     attribute means nothing on a fixture, and the classes that USE such a
//     fixture already carry it. `Harbor.E2E.App.Avalonia` is the worked example
//     — `HeadlessAvaloniaDriver` writes `HOME` and `MockServerFixture` writes
//     `HOME`/`USERPROFILE`, neither has a test, and every consumer is already
//     tagged `e2e-framework` (`AvaloniaUiTests.cs:54` plus eight more).
//
//     Writers that already declare a key are left alone. The four
//     `Harbor.Hosting.Tests` classes that pin `HARBOR_MODE` / `HARBOR_STORAGE`
//     sentinels all carry `hosting`; demanding a SECOND key from them would be
//     this guard crying wolf, and a guard that cries wolf gets deleted.
//
// (2) READER, declared. Where one class writes a variable and another class in
//     the SAME test project reaches a product read of it, both must carry a key
//     AND their key sets must INTERSECT. A key on one side only is a label with
//     no effect, which is the exact mistake #704 was opened for — so that is a
//     violation here, not a style note.
//
//     This half cannot be derived. The reads do not name the variable: they go
//     through `Environment.GetFolderPath(SpecialFolder.UserProfile)` or through
//     a product helper, so a source scan sees nothing. It is therefore
//     hand-maintained, and closed in BOTH directions — an entry whose class no
//     longer resolves fails here, and so does a project with no writer. A stale
//     reader table is the failure mode this half exists to prevent.
//
// (3) RESTORE, derived. #847: a class that writes a variable must put back the
//     value it read, not a value it typed. Both halves above are about what
//     happens WHILE tests run; this one is about what is still true AFTER one
//     finishes, and #846 could not see it because a `[NotInParallel]` key orders
//     writers against each other and says nothing about the state the last one
//     leaves behind. See THE TEMPORAL AXIS below — this is a third half on the
//     existing axis, not a new one (#555).
//
// THE TEMPORAL AXIS — what (1) and (2) cannot see, and why (3) is not a new axis
// ---------------------------------------------------------------------------
// The blind spot is not that #846's rule is wrong; it is that "racing" and
// "leaking" are two different questions and only the first was asked. A key
// makes two writers take turns. It cannot make a writer give back what it took,
// because by the time the restore runs the other test has usually not started —
// there is nothing to race against, and the loss is invisible from inside the
// class that caused it. `ReplRunnerConfigLoadTests` sets `OLLAMA_API_KEY` to
// null and its `finally` sets it to null again, which reads as a tidy
// set/restore pair and is in fact a claim it can no longer honour.
//
// That `OLLAMA_API_KEY` is not a test-only name is what makes this cost more
// than a flaky suite. `AuthStore.FromConventionalEnv` (AuthStore.cs:65-74)
// derives `<PROVIDER>_API_KEY` from the provider id and reads the process
// environment for it, and the onboarding wizard reaches that through
// `_authStore.GetApiKeyAsync` (OnboardingWizard.cs:245). The variable is read by
// PRODUCT code, in the same process, after the test that destroyed it has
// returned and passed. The failure therefore belongs to whoever runs next, which
// is the worst attribution there is.
//
// So this stays on the axis it was born on: the subject is still the process
// environment, the unit of contention is still the test project, and the writer
// set is still the one (1) derives. What changes is that the axis gets its third
// dimension — before/after, not only alongside — which is completion, not
// extension. #555 freezes the extension points (the plugin and TUI seams);
// nothing here adds one.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that
// is broken, and a broken guard is worse than no guard because it is believed.
// Four tests below close that: discovery must find a non-trivial file set and
// must contain every file the writer list names; the SAME matcher must fire on
// a planted positive control while staying silent on a read and on comment
// prose; the rule must not report its own file, whose constant and positive
// controls are all string literals; and the reader table must be non-empty,
// fully resolvable, and pointed only at projects the writer scan found a writer
// in.
//
// The restore half gets two of its own, and they exist for a reason specific to
// it: "no class restores" and "every class restores" are both satisfiable by a
// scan that simply never matches, so the planted controls below assert the
// DETECTOR fires on a literal restore and stays quiet on a saved one. The
// process-lifetime table gets the same treatment as the rival table — closed in
// both directions — because an exemption nobody re-checks is an exemption that
// silently grows.
//
// KNOWN LIMITATION — stated, not hidden
// -------------------------------------
// * The scan is line-level over comment-stripped, literal-stripped source, not a
//   C# parser. A write whose call is split so that
//   `Environment.SetEnvironmentVariable(` never appears whole on one line is
//   missed, and so is one written inside an interpolation hole.
// * A type's extent ends at the next type declared at the same or shallower
//   indentation. That is the ordinary C# convention and it is what makes the
//   attribution correct in both directions: `OnboardingWizardTests` keeps the 32
//   writes that follow its own declaration even though four helper types are
//   declared between its test methods, and `ProviderConfigTests` does not steal
//   the writes that belong to `EnvVarAuthResolverTests` below it. A type whose
//   members are laid out at a shallower indentation than its own declaration is
//   not modelled, and a file that uses a nonstandard layout is a file this rule
//   reads wrong — it is a stated limitation, not a silent one.
// * Rule (2) is a declared list. It proves the named readers hold a matching
//   key; it does NOT prove the list is COMPLETE, because completeness is not a
//   decidable property of a source tree. A reader of a written variable that is
//   not listed here is a hole this rule cannot see. Rule (1) is derived and has
//   no such hole on the writer side.
// * Rule (3) proves that SOME write in the class passes an identifier that a
//   `GetEnvironmentVariable` call assigned. It cannot prove that identifier
//   belongs to the variable being written, that the restoring write is the one
//   on the failure path, or that it runs at all — a `finally` that is never
//   entered, a `Dispose` that never fires and a scope that outlives the test are
//   all invisible to a line scan. Matching a saved identifier to the variable it
//   was read for would need real name resolution, and the name is an identifier
//   in four of the eleven writers, so a name-matching rule would report four
//   classes that are correct. This is a proxy for "this class knows what the
//   ambient value was", not a proof of symmetric teardown, and the honest
//   consequence is stated rather than papered over: the rule fails on a class
//   that never reads, which is the #847 shape, and stays quiet on a class that
//   reads once and restores badly.
// * The process-lifetime table exempts a (class, VARIABLE) PAIR, not a class, so
//   a second, bounded write of a different variable inside an exempted class is
//   still reported. A second bounded write of the SAME variable would be masked;
//   that is the price of an exemption that cannot be derived.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #823: a test class that writes the process environment must declare
///     the keyed group it serializes in, and the classes that read those
///     variables in the same test process must hold a key that intersects it.
/// </summary>
public class ProcessEnvIsolationRule
{
    /// <summary>
    ///     The call itself, anchored on the opening paren. Matched against
    ///     comment- and literal-stripped source, so prose that NAMES the API — in
    ///     a log message, a doc comment or this file's own constant — is not a
    ///     call.
    /// </summary>
    private const string EnvWriteCall = "Environment.SetEnvironmentVariable(";

    /// <summary>
    ///     The matching READ, anchored on the opening paren and matched the same
    ///     way. A write that overwrites a variable the class never read cannot be
    ///     undone, so this is what rule (3) looks for alongside the write.
    /// </summary>
    private const string EnvReadCall = "Environment.GetEnvironmentVariable(";

    /// <summary>
    ///     <see cref="EnvReadCall" /> and the OTHER way a test legitimately learns
    ///     what $HOME was. This is not a theoretical second spelling —
    ///     <c>ViewInflationTests</c> saves with
    ///     <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c> at its line
    ///     205 and restores <c>HOME</c> from it at 240, which is the correct shape.
    ///     A rule that knew only about <c>GetEnvironmentVariable</c> would report
    ///     that class as restoring nothing, and the table in this file already
    ///     names it as a writer <c>#846</c> fixed. The two are equivalent for this
    ///     purpose: both answer "what was the ambient value".
    /// </summary>
    private const string FolderReadCall = "Environment.GetFolderPath(";

    /// <summary>Either read that yields an ambient value worth restoring.</summary>
    private static readonly Regex AnyAmbientRead = new(
        @"(?<![.\w])(?:" + Regex.Escape(EnvReadCall) + "|" + Regex.Escape(FolderReadCall) + ")",
        RegexOptions.Compiled);

    /// <summary>
    ///     An identifier assigned from an ambient READ — the only thing a restore
    ///     may pass back. Note what this is for: <c>AppHostDiTests</c> writes
    ///     <c>SetEnvironmentVariable("HOME", tempHome)</c>, and <c>tempHome</c> is a
    ///     bare identifier too. Shape alone therefore accepts the very first write of
    ///     a one-shot swap as a restore of itself, which would make the rule report
    ///     that class as correct and exempt it by accident. Requiring the identifier
    ///     to have been ASSIGNED FROM A READ is what separates "I kept what was
    ///     there" from "I am handing back a value I just made up".
    /// </summary>
    private static readonly Regex SavedEnvIdentifier = new(
        @"(?<id>[A-Za-z_]\w*)\s*=\s*[^;]*?(?<![.\w])(?:"
        + Regex.Escape(EnvReadCall) + "|"
        + Regex.Escape(FolderReadCall)
        + ")",
        RegexOptions.Compiled);

    /// <summary>
    ///     The keywords and constants that lex exactly like a bare identifier and
    ///     so pass a shape test, but which can never hold a value that was read.
    ///     <c>null</c> is the one that matters and it is the entire bug: it is spelled
    ///     the same as the locals this rule accepts, so a matcher that looked only
    ///     at shape would wave #847's own restore straight through.
    /// </summary>
    private static readonly HashSet<string> NeverARestoredValue = new(StringComparer.Ordinal)
    {
        "null", "true", "false", "default",
    };

    /// <summary>How far above a type declaration the attribute run may climb.</summary>
    private const int AttributeLookbackLimit = 60;

    /// <summary>
    ///     A type declaration plus the modifier run above it. Requires the
    ///     modifier run or line-start, so a `class` token inside an expression
    ///     does not register as a declaration.
    /// </summary>
    private static readonly Regex TypeDeclaration = new(
        @"^\s*(?:\[[^\]]*\]\s*|public\s+|internal\s+|private\s+|protected\s+|sealed\s+|abstract\s+"
        + @"|static\s+|partial\s+|file\s+|unsafe\s+)*(?:class|record|struct)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A `[Test]` attribute at the start of a line, ignoring what follows the
    ///     name so `[Test]`, `[Test(...)]` and `[Test, NotInParallel("k")]` all
    ///     match while `[TestKitSomething]` does not.
    /// </summary>
    private static readonly Regex TestAttribute = new(@"^\s*\[Test(\]|\(|\s|,)", RegexOptions.Compiled);

    /// <summary>A `NotInParallel` attribute and the keys it names; empty keys means the bare form.</summary>
    private static readonly Regex NotInParallelAttribute = new(@"NotInParallel\s*\(([^)]*)\)", RegexOptions.Compiled);

    /// <summary>A quoted string inside an attribute argument list.</summary>
    private static readonly Regex QuotedKey = new(@"""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    ///     The writer/reader pairs this rule holds closed, one per test project
    ///     that has a demonstrated product-read race. Every entry states the read,
    ///     because an unexplained entry is an entry nobody maintains.
    /// </summary>
    private static readonly (string Project, string Writer, string Reader, string Shared)[] RivalPairs =
    [
        (
            "Harbor.App.Avalonia.Tests",
            "AppHostDiTests",
            "ViewInflationTests",
            "HOME. AppHostDiTests writes it in a process-lifetime Lazy and never restores it; "
            + "ViewInflationTests writes and restores it. Both call AppHost.BuildAsync, which reaches "
            + "AppHost.ResolveHarborDir (AppHost.cs:121-126) — GetFolderPath(UserProfile) with a $HOME "
            + "fallback. ViewInflationTests already held `avalonia-headless`, which AppHostDiTests does "
            + "not, so the two sat in different scheduler buckets and overlapped."),

        (
            "Harbor.Tools.Builtin.Tests",
            "McpRemoteTransportTests",
            "McpTransportFactoryTests",
            "HARBOR_MCP_OAUTH_TOKEN. McpRemoteTransportTests pins it to null so the no-token path is "
            + "real; McpTransportFactoryTests reaches McpRegistry.GetTransport through "
            + "registry.InvokeAsync (:245,262,278,295), and that branch reads the variable "
            + "(McpRegistry.cs:575)."),

        (
            "Harbor.App.Cli.Tests",
            "HostBuilderDiTests",
            "McpLoginRunnerTests",
            "HARBOR_MCP_CONFIG and HARBOR_HOME. McpLoginRunnerTests points the first at a temp mcp.json; "
            + "HostBuilderDiTests reaches ToolsCatalog.CreateMcpRegistry through HostBuilder.Build → "
            + "Registration.AddHarbor → AddHarborRegistries (Registration.cs:25) → RegistriesModule.cs:53, "
            + "and that reads the first (ToolsCatalog.cs:72) and the second (HostBuilder.cs:160). "
            + "McpLoginRunnerTests already documented the resulting flake at its own line 61."),

        (
            "Harbor.Config.Tests",
            "AuthStoreTests",
            "OnboardingWizardTests",
            "ANTHROPIC_API_KEY, plus the whole-environment enumeration. AuthStoreTests clears it in four "
            + "places; OnboardingWizardTests clears it in eight and OLLAMA_API_KEY in twenty-four, and it "
            + "is not in #823's list. Both classes reach AuthStore.ListApiKeysAsync, which enumerates the "
            + "entire process environment (AuthStore.cs:140)."),

        (
            "Harbor.Config.Tests",
            "AuthStoreTests",
            "SetupChecklistDetectorTests",
            "The whole-environment enumeration, from the other direction. This class writes nothing, so "
            + "nothing in #823's method would have found it, but it constructs an AuthStore and "
            + "SetupChecklistDetector.DetectAsync calls ListApiKeysAsync (SetupChecklistDetector.cs:176) "
            + "— the whole process environment, same as above. Stated honestly: today's assertion cannot "
            + "be flipped by a concurrent write, because ProviderKeyStored is true whenever ANY key is "
            + "present and this test stores one in config. It is listed anyway because that is a property "
            + "of one assertion, not of the design, and the next test to assert an exact key set would be "
            + "a latent failure with no guard behind it."),
    ];

    /// <summary>
    ///     Every class #847's investigation found restoring nothing. Listed as a
    ///     REACHABILITY set, not as an expectation: the test that uses it asserts
    ///     each is a writer rule (3) can see, which is a property of the walk and
    ///     holds however many of them have since been fixed. Whether a given one is
    ///     still reported is asserted against its SHAPE (planted lines), never against
    ///     this list — a control pinned to the repository's current state stops being
    ///     a control the moment the next fix lands. Three of the four are fixed in
    ///     #870; the list stays until they are, because it is also what proves the
    ///     walk still reaches them.
    /// </summary>
    private static readonly string[] ClassesThatRestoredNothing =
    [
        "tests/Harbor.App.Cli.Tests/ReplRunnerConfigLoadTests.cs",
        "tests/Harbor.Config.Tests/OnboardingWizardTests.cs",
        "tests/Harbor.Config.Tests/AuthStoreTests.cs",
        "tests/Harbor.Providers.Tests/ProviderConfigTests.cs",
    ];

    /// <summary>
    ///     Every file #823 named, plus the one it missed. Discovery asserts each is
    ///     inside the derived writer set, so a walk that stops early fails here
    ///     instead of passing vacuously.
    /// </summary>
    private static readonly string[] WriterFilesTheIssueNamed =
    [
        "tests/Harbor.App.Avalonia.Tests/AppHostDiTests.cs",
        "tests/Harbor.App.Cli.Tests/CellForgeModuleApproverTests.cs",
        "tests/Harbor.App.Cli.Tests/HostBuilderDiTests.cs",
        "tests/Harbor.App.Cli.Tests/McpLoginRunnerTests.cs",
        "tests/Harbor.App.Cli.Tests/ReplRunnerConfigLoadTests.cs",
        "tests/Harbor.Config.Tests/AuthStoreTests.cs",
        // 24 writes of OLLAMA_API_KEY and 8 of ANTHROPIC_API_KEY, in the same
        // assembly as AuthStoreTests. Not in #823's list.
        "tests/Harbor.Config.Tests/OnboardingWizardTests.cs",
        // #823 attributes lines 193-255 of ProviderConfigTests.cs to the class
        // named in the file. Those lines belong to the resolver suite declared at
        // line 173; the class named in the file (lines 10-168) writes nothing.
        "tests/Harbor.Providers.Tests/ProviderConfigTests.cs",
        "tests/Harbor.Tools.Builtin.Tests/McpRemoteTransportTests.cs",
        "tests/Harbor.Tui.CellForge.Tests/SkillFreshnessPanelRegistrationTests.cs",
        // A writer the issue did not name, and the one that made the derived rule
        // necessary: a writer and a reader that are the SAME class.
        "tests/Harbor.App.Avalonia.Tests/ViewInflationTests.cs",
    ];

    /// <summary>
    ///     The writes that are process-lifetime BY DESIGN and therefore have
    ///     nothing to restore, one row per (class, variable) PAIR rather than per
    ///     class so that a second, bounded write of a different variable inside
    ///     an exempted class is still reported. Every entry states the claim, for
    ///     the same reason the rival table does: an unexplained exemption is an
    ///     exemption nobody re-checks.
    /// </summary>
    private static readonly (string Project, string Class, string Variable, string Why)[] ProcessLifetimeWrites =
    [
        (
            "Harbor.App.Avalonia.Tests",
            "AppHostDiTests",
            "HOME",
            "Written once, inside a static Lazy<Task<IHost>> that builds a host cached for the whole test "
            + "process. The swap is not scoped to one test because the thing it isolates is not: a "
            + "process-lifetime cached host. Restoring it would leave later methods composing against the "
            + "developer's real ~/.harbor/config.json, which is exactly the leak the swap exists to stop "
            + "(the comment at its own line 36 says so). #846 already recorded this class as a writer "
            + "that never restores; it is the same fact, stated for the lifetime rule instead of the "
            + "concurrency one."),

        (
            "Harbor.App.Cli.Tests",
            "HostBuilderDiTests",
            "HOME",
            "Written once, in the same static Lazy<IHost> shape as AppHostDiTests above, for the same "
            + "reason: the host is cached for the whole process and the swap exists so a saved config "
            + "cannot reach a pure-DI assertion."),

        (
            "Harbor.App.Cli.Tests",
            "HostBuilderDiTests",
            "USERPROFILE",
            "The Windows half of the same swap. GetFolderPath(UserProfile) resolves from the user token "
            + "on Windows and ignores a swapped process variable, so USERPROFILE has to move with HOME "
            + "or the two disagree."),

        (
            "Harbor.App.Cli.Tests",
            "HostBuilderDiTests",
            "HARBOR_HOME",
            "The third leg of the same swap, and the one HarborPaths reads directly "
            + "(HarborPaths.cs:29). It exists because the other two are not sufficient on Windows; it "
            + "is not an independent write."),
    ];

    // ── rule (1): the writer must declare a parallelism form ────────────────

    [Test]
    public async Task Every_ProcessEnv_Writing_Test_Class_Declares_A_Parallelism_Form()
    {
        var violations = new List<string>();

        foreach (TypeSite site in DiscoverWriters())
        {
            if (site.Form.IsGlobal || site.Form.Keys.Length > 0)
            {
                continue;
            }

            violations.Add(
                $"{site.File}:{site.WriteLine} — {site.Class} holds a [Test] method and calls "
                + $"{EnvWriteCall}) but carries no [NotInParallel] form. On Unix that call mutates a "
                + "static process-wide dictionary (Environment.Variables.Unix.cs), which every class in "
                + "this test project shares, so this write is serialised against nothing.");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "Issue #823. Nine test classes wrote the process environment with no concurrency "
                + "declaration at all, and two more that its author did not name — OnboardingWizardTests, "
                + "which writes the same variables as AuthStoreTests in the same assembly, and "
                + "ViewInflationTests, which is both a writer and the reader AppHostDiTests races. This "
                + "half of the rule is derived from the tree rather than from a list, so the next writer "
                + "fails here instead of being silently unprotected.");
    }

    // ── rule (2): both sides of a race must hold an intersecting key ─────────

    [Test]
    public async Task Every_Writer_And_Its_Product_Reader_Hold_An_Intersecting_Key()
    {
        var violations = new List<string>();

        foreach ((string project, string writerName, string readerName, string shared) in RivalPairs)
        {
            IReadOnlyList<TypeSite> types = DiscoverTypesInProject(project);

            if (types.Count == 0)
            {
                violations.Add(
                    $"{project} — the rival table names this project but no source in it could be "
                    + "scanned. Either it was renamed or the walk is broken; both make this rule vacuous.");
                continue;
            }

            TypeSite? writer = types.FirstOrDefault(t => t.Class == writerName);
            TypeSite? reader = types.FirstOrDefault(t => t.Class == readerName);

            if (writer is null)
            {
                violations.Add(
                    $"{project} — the rival table names {writerName} as the writer and no such type was "
                    + "found. A stale entry means the key is no longer held on the side that has to hold it.");
            }

            if (reader is null)
            {
                violations.Add(
                    $"{project} — the rival table names {readerName} as the reader and no such type was "
                    + "found. A stale entry means the key is no longer held on the side that has to hold it.");
            }

            if (writer is null || reader is null)
            {
                continue;
            }

            if (Holds(writer.Form, reader.Form))
            {
                continue;
            }

            violations.Add(
                $"{writer.File} / {reader.File} — {writerName} writes and {readerName} reads the same "
                + $"process state in {project}, and their [NotInParallel] declarations do not intersect. "
                + $"The writer holds [{Describe(writer.Form)}], the reader "
                + $"[{Describe(reader.Form)}]. A named key is a mutex over its NAMED PEERS only, so "
                + "a key on one side and not the other excludes nothing and the two still overlap — the "
                + "mistake #704 was opened for. They share " + shared);
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "This is the half the writer rule cannot express. It cannot be derived, because the reads "
                + "these classes perform do not name the variable — they go through GetFolderPath or "
                + "through a product helper — so the pairs are declared, and the assertion is that the two "
                + "sides hold a key that actually overlaps.");
    }

    // ── rule (3): a write must be undone with the value that was read ────────

    /// <summary>
    ///     The defect #847 reports, in the shape the derived rule can see: a class
    ///     that writes a variable and never reads it has no value it could put
    ///     back, so whatever its <c>finally</c> writes is a guess. Here the guess
    ///     is <c>null</c>, and the guess is indistinguishable from a restore in
    ///     the source — which is why it survived review and why a keyed lock
    ///     cannot see it.
    /// </summary>
    [Test]
    public async Task Every_Env_Writing_Class_Restores_A_Value_It_Actually_Read()
    {
        var violations = new List<string>();

        foreach (TypeSite site in DiscoverWriters())
        {
            if (IsRestored(site) || IsProcessLifetime(site))
            {
                continue;
            }

            IReadOnlyList<string> unrestored = UnrestoredVariables(site);

            violations.Add(
                $"{site.File}:{site.WriteLine} — {site.Class} writes "
                + $"{string.Join(", ", unrestored)} and never "
                + $"restores {(unrestored.Count == 1 ? "that variable" : "those variables")} "
                + "to a value it read. It pins "
                + $"{site.WrittenVariables.Length} variable(s) from line {site.WriteLine}, and the "
                + "environment is process-wide state "
                + "(Environment.Variables.Unix.cs), so on a runner that exports the variable the "
                + "teardown DESTROYS it for every test that runs after this one. Read the ambient "
                + "value first and write that identifier back in the finally.");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "Issue #847. ReplRunnerConfigLoadTests sets OLLAMA_API_KEY to null and its finally sets "
                + "it to null again — a restore that restores nothing, because nothing was read. "
                + "AuthStore.FromConventionalEnv (AuthStore.cs:65-74) derives OLLAMA_API_KEY from the "
                + "provider id and reads the process environment for it, so the product reads this "
                + "variable in the same process, AFTER this test has returned and passed. A keyed "
                + "NotInParallel cannot help: it orders writers against each other while they run and "
                + "says nothing about the state the last one leaves behind. This half is what closes "
                + "the axis.");
    }

    /// <summary>
    ///     The other direction: a class that restores correctly is reported as
    ///     clean. Without this the rule could pass by finding no writers at all,
    ///     which is the failure mode every non-vacuity test in this file exists to
    ///     prevent.
    /// </summary>
    [Test]
    public async Task A_Class_That_Restores_A_Read_Value_Is_Not_Reported()
    {
        IReadOnlyList<TypeSite> writers = DiscoverWriters();

        await Assert.That(writers.Count).IsGreaterThan(0)
            .Because("there are writers to check; an empty set would make this assertion vacuous.");

        IReadOnlyList<TypeSite> reported =
        [
            .. from site in writers
               where !IsRestored(site) && !IsProcessLifetime(site)
               select site
        ];

        // The class #847 is ABOUT, now that it is fixed. It is the strongest available
        // evidence that the rule discriminates rather than merely complaining: the
        // same detector that named it before the fix must be silent on it now, with
        // no change to the rule itself.
        await Assert.That(reported.Any(s => s.Class == "ReplRunnerConfigLoadTests")).IsFalse()
            .Because(
                "This PR changed ReplRunnerConfigLoadTests to read OLLAMA_API_KEY before pinning it, and "
                + "the rule — which was not modified to accommodate that — must no longer report it. If "
                + "it still does, the rule cannot tell a restore from a guess and every green here is "
                + "unearned.");

        await Assert.That(reported.Any(s => s.Class == "McpRemoteTransportTests")).IsFalse()
            .Because(
                "McpRemoteTransportTests is the worked example of the CORRECT shape: it reads "
                + "previous = Environment.GetEnvironmentVariable(HARBOR_MCP_OAUTH_TOKEN) and passes "
                + "that identifier back in its finally. If the rule reports it, the rule cannot tell a "
                + "restore from a guess — which would make it worse than the defect it was written for.");

        await Assert.That(reported.Any(s => s.Class == "ViewInflationTests")).IsFalse()
            .Because(
                "A second worked example, and the one with TWO keys and a bare `$HOME` swap — the "
                + "shape most likely to be misread as a literal restore. It reads originalHome and "
                + "writes it back.");

        // Every reported class must actually lack a read, or the rule is judging
        // something other than what it says it judges.
        foreach (TypeSite site in reported)
        {
            await Assert.That(site.DeclaresEnvRead).IsFalse()
                .Because(
                    $"{site.Class} is reported by rule (3), which claims it never reads the variable it "
                    + "writes. If it does read, the report is about something else and the message "
                    + "above is lying — which is the failure this assertion exists to catch.");
        }

        List<TypeSite> clean = [.. writers.Where(s => IsRestored(s) || IsProcessLifetime(s))];

        await Assert.That(clean.Count).IsGreaterThanOrEqualTo(8)
            .Because(
                "Nine classes write the environment correctly today — the four Harbor.Hosting.Tests "
                + "sentinels, McpRemoteTransportTests, McpLoginRunnerTests, ViewInflationTests, "
                + "CellForgeModuleApproverTests, SkillFreshnessPanelRegistrationTests and the "
                + "ReplRunnerConfigLoadTests this PR fixed — and two more are the declared "
                + "process-lifetime writes. Fewer than eight means the detector stopped recognising a "
                + "restore, and rule (3) would report every writer as broken. A rule that reports all of "
                + "everything is not a rule; it gets deleted.");

        await Assert.That(clean.Any(s => s.Class == "HostBuilderDiTests")).IsTrue()
            .Because(
                "It writes HOME, USERPROFILE and HARBOR_HOME and restores none of them, and is exempt "
                + "only because it sits in the declared process-lifetime table. If that table stopped "
                + "applying, this goes red and the exemption has become dead weight nobody re-checks.");

        await Assert.That(clean.Any(s => s.Class == "AppHostDiTests")).IsTrue()
            .Because("The other declared process-lifetime writer, for the same reason.");
    }

    /// <summary>
    ///     Whether rule (3) accepts this site. A class passes only if EVERY variable
    ///     it writes is handed back — one correct restore does not cover a second
    ///     variable pinned to a literal, which is why this is not
    ///     <c>Any(...Contains)</c> over the whole class.
    /// </summary>
    private static bool IsRestored(TypeSite site) =>
        site.WrittenVariables.Length > 0
        && site.WrittenVariables.All(v => site.RestoredVariables.Contains(v, StringComparer.Ordinal));

    /// <summary>
    ///     The variables this site pins without restoring. At least one when the
    ///     site is a violation, and it is what the message names.
    /// </summary>
    private static IReadOnlyList<string> UnrestoredVariables(TypeSite site) =>
        [.. site.WrittenVariables.Where(v => !site.RestoredVariables.Contains(v, StringComparer.Ordinal))];

    /// <summary>
    ///     Whether every variable this site writes that it does not restore is a
    ///     declared process-lifetime write. Partial exemption is not possible: a
    ///     class cannot be half process-lifetime, so a row naming one of two
    ///     variables leaves the other one reported.
    /// </summary>
    private static bool IsProcessLifetime(TypeSite site)
    {
        IReadOnlyList<string> unrestored = UnrestoredVariables(site);
        return unrestored.Count > 0
            && unrestored.All(v => ProcessLifetimeWrites.Any(w => w.Project == site.Project
                && w.Class == site.Class
                && w.Variable == v));
    }

    /// <summary>
    ///     The process-lifetime table, closed in BOTH directions for the same
    ///     reason the rival table is. An exemption that no test re-checks is an
    ///     exemption that silently grows: a stale row keeps protecting a class that
    ///     stopped being process-lifetime, and a row for a variable nobody writes
    ///     hides the fact that the table has drifted from the tree.
    /// </summary>
    [Test]
    public async Task The_Process_Lifetime_Table_Matches_Real_One_Shot_Writers()
    {
        await Assert.That(ProcessLifetimeWrites.Length).IsGreaterThan(0)
            .Because("an empty exemption table would make rule (3) pass vacuously on those two classes.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> stale = [];

        foreach ((string project, string className, string variable, _) in ProcessLifetimeWrites)
        {
            TypeSite? site = DiscoverTypesInProject(project).FirstOrDefault(t => t.Class == className);

            if (site is null)
            {
                stale.Add($"{project}/{className} — named in the process-lifetime table, and no such type was found.");
                continue;
            }

            if (!seen.Add($"{project}/{className}/{variable}"))
            {
                stale.Add($"{project}/{className}/{variable} — listed twice.");
            }

            // The row's premise is that this class writes the variable ONCE, from
            // a process-lifetime initializer. A second write means the class has
            // a bounded scope after all and the row is protecting the wrong thing.
            int writes = CountEnvWritesOf(site, variable);
            if (writes != 1)
            {
                stale.Add(
                    $"{project}/{className} — the table says {variable} is a one-shot process-lifetime "
                    + $"write, and it is written {writes} times. Either the class gained a bounded scope "
                    + "or the row is stale; either way the exemption no longer describes what it exempts.");
            }

            if (IsRestored(site))
            {
                stale.Add(
                    $"{project}/{className} — exempted as never restoring, and it now restores "
                    + $"{string.Join(", ", site.WrittenVariables)}. The exemption should be deleted, "
                    + "not kept for safety.");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "Every row states a claim about one (class, VARIABLE) pair, and each claim is checkable: "
                + "the type must exist, the variable must be written exactly once, and the class must "
                + "still restore nothing. A row that fails any of those is not protecting a process-lifetime "
                + "write — it is a hole with a comment on it.");

        await Assert.That(seen.Count).IsEqualTo(ProcessLifetimeWrites.Length)
            .Because("A duplicated row would make the table look fuller than it is.");
    }

    /// <summary>
    ///     The detector on planted lines. Both directions, because "reports
    ///     everything" and "reports nothing" are the same broken detector.
    /// </summary>
    [Test]
    public async Task Restore_Detector_Separates_A_Literal_Restore_From_A_Saved_One()
    {
        // #847's exact shape: pin to null, then write null back.
        (string Name, string Value) guess = ReadEnvWriteArguments(
            "        Environment.SetEnvironmentVariable(\"OLLAMA_API_KEY\", null);");
        await Assert.That(guess.Name).IsEqualTo("OLLAMA_API_KEY")
            .Because("The variable name must survive the literal-stripping that proves the line is a call.");

        // `previous` is the identifier a save produced; `null` is what #847 hands back
        // instead. Both lex as bare identifiers, and only the assignment distinguishes
        // them — which is why the rule matches the SAVE and not the shape.
        Match guessSave = SavedEnvIdentifier.Match(
            "        string? previous = Environment.GetEnvironmentVariable(\"OLLAMA_API_KEY\");");
        await Assert.That(guessSave.Success).IsTrue()
            .Because(
                "The save shape must be recognised: an identifier assigned from the read is the ONLY "
                + "thing rule (3) accepts as a restore value.");

        await Assert.That(NeverARestoredValue.Contains(guess.Value)).IsTrue()
            .Because(
                "`null` must be excluded by name, or the rule accepts the exact token #847 reports as a "
                + "restore — making the guard approve the defect it was written for.");

        (string Name, string Value) saved = ReadEnvWriteArguments(
            "        Environment.SetEnvironmentVariable(\"HARBOR_MCP_OAUTH_TOKEN\", previous);");
        await Assert.That(saved.Name).IsEqualTo("HARBOR_MCP_OAUTH_TOKEN");
        await Assert.That(saved.Value).IsEqualTo("previous")
            .Because(
                "The value must come through the argument reader as the bare identifier, because that is "
                + "what the save-set membership test then looks up.");

        // A computed value is not something that was read, so it does not count —
        // and neither does a bare identifier that was never assigned from a read,
        // which is the AppHostDiTests shape that would otherwise self-exempt.
        (string Name, string Value) computed = ReadEnvWriteArguments(
            "        Environment.SetEnvironmentVariable(\"HOME\", Path.Combine(a, b));");
        await Assert.That(SavedEnvIdentifier.IsMatch(computed.Value)).IsFalse()
            .Because(
                "Path.Combine(a, b) is a new value, not the one that was there before. Accepting it "
                + "would let a class pass by rebuilding the ambient value instead of restoring it.");

        (string Name, string Value) invented = ReadEnvWriteArguments(
            "        Environment.SetEnvironmentVariable(\"HOME\", tempHome);");
        await Assert.That(invented.Value).IsEqualTo("tempHome");
        await Assert.That(SavedEnvIdentifier.IsMatch(invented.Value)).IsFalse()
            .Because(
                "`tempHome` is a bare identifier and was NEVER read — it is what AppHostDiTests and "
                + "HostBuilderDiTests hand a one-shot swap. If the rule counted shape alone it would call "
                + "that a restore of itself, declare both classes correct, and the process-lifetime table "
                + "would be exempting classes that do not need it.");

        // Prose that NAMES the API is still not a call. ReadEnvWriteArguments is the
        // RAW argument reader and is deliberately not literal-aware — IsEnvWrite is
        // the gate that strips, and ReadEnvTraffic consults it before every read of
        // names. So the assertion is that the GATE stays shut, not that the reader
        // re-derives the stripping.
        string[] prose = ["        _logger.LogDebug(\"no Environment.SetEnvironmentVariable(\");"];
        await Assert.That(IsEnvWrite(prose[0])).IsFalse()
            .Because("A name inside a message must not become a violation; that is what the stripping is for.");
        await Assert.That(DeclaresEnvRead(prose, 0, 1)).IsFalse()
            .Because("The same holds for the read side.");

        // The read side, which is the necessary condition the rule states.
        await Assert.That(DeclaresEnvRead(
                ["        string? previous = Environment.GetEnvironmentVariable(\"HOME\");"], 0, 1))
            .IsTrue()
            .Because("A class that saves a value is one that can put it back.");

        // GetFolderPath is the OTHER legitimate save, and this control exists
        // because ViewInflationTests uses exactly that (its line 205) and restores
        // $HOME from the result at 240. A rule that knew only about
        // GetEnvironmentVariable would report a class #846 already fixed as
        // restoring nothing — the crying-wolf failure that gets a guard deleted.
        string[] folderSave =
        [
            "        var originalHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);",
        ];
        await Assert.That(DeclaresEnvRead(folderSave, 0, 1)).IsTrue()
            .Because("ViewInflationTests saves $HOME this way, so this must count as a read.");
        await Assert.That(SavedEnvIdentifier.Match(folderSave[0]).Groups["id"].Value)
            .IsEqualTo("originalHome")
            .Because(
                "And the identifier it binds is what the restore passes back at that class's line 240, so "
                + "the save-set membership test has to see it under exactly that name.");

        await Assert.That(DeclaresEnvRead(["        Environment.SetEnvironmentVariable(\"HOME\", null);"], 0, 1))
            .IsFalse()
            .Because("Writing is not reading, and treating it as both would let the defect through.");

        // Every class #847's investigation found must be REACHABLE by rule (3) — a
        // writer under rule (1), so the walk reaches it. That is a property of the
        // walk, and it holds however many of them have since been fixed.
        IReadOnlyList<TypeSite> writers = DiscoverWriters();

        IReadOnlySet<string> writerFiles =
            new HashSet<string>(writers.Select(w => w.File), StringComparer.Ordinal);

        foreach (string restoredNothing in ClassesThatRestoredNothing)
        {
            await Assert.That(writerFiles.Contains(restoredNothing)).IsTrue()
                .Because(
                    restoredNothing + " is one of the four classes #847 found restoring nothing, so it is "
                    + "a writer under rule (1) and therefore visible to rule (3). If the walk cannot reach "
                    + "it, rule (3) is not enforcing anything on a class this investigation named.");
        }

        // Planted classes, run through the WHOLE of rule (3)'s judgement —
        // ReadEnvTraffic, not the argument reader and not the save matcher. Those
        // two were shown to be separable above, but neither is the rule: a
        // detector can read `previous` off a line perfectly and still fail to
        // credit it to the variable it is written back for, and only
        // ReadEnvTraffic is where that pairing is decided.
        //
        // This replaces an assertion on the repository's current state — "rule (3)
        // still reports somebody" — which was the same mistake the list above used
        // to make. It would have gone red on the commit that fixed the last of the
        // #870 classes, because that commit empties the report, and the honest
        // reading of such a red is "the control described a moment in time" rather
        // than "the rule broke". A control that describes a moment stops being a
        // control. These describe the SHAPE, so they hold before the fixes, during
        // them and after them, and they still go red if the detector ever stops
        // pairing a save with the variable it is written back for.
        string[] plantedLeak =
        [
            "public class PlantedLeak",
            "{",
            "    public void T()",
            "    {",
            "        Environment.SetEnvironmentVariable(\"PLANTED_API_KEY\", null);",
            "    }",
            "}",
        ];
        var plantedLeakTraffic = ReadEnvTraffic(plantedLeak, 0, plantedLeak.Length);
        await Assert.That(plantedLeakTraffic.Restored.Contains("PLANTED_API_KEY")).IsFalse()
            .Because(
                "A class that pins a variable and hands back `null` restores nothing, and rule (3) must "
                + "report it. This is the #847 shape verbatim, on lines that exist in this file rather "
                + "than in the tree — so it stays a control after every class it names has been fixed, "
                + "which is exactly when the version pinned to the tree stopped being one.");

        string[] plantedRestore =
        [
            "public class PlantedRestore",
            "{",
            "    public void T()",
            "    {",
            "        string? previous = Environment.GetEnvironmentVariable(\"PLANTED_API_KEY\");",
            "        Environment.SetEnvironmentVariable(\"PLANTED_API_KEY\", null);",
            "        Environment.SetEnvironmentVariable(\"PLANTED_API_KEY\", previous);",
            "    }",
            "}",
        ];
        var plantedRestoreTraffic = ReadEnvTraffic(plantedRestore, 0, plantedRestore.Length);
        await Assert.That(plantedRestoreTraffic.Restored.Contains("PLANTED_API_KEY")).IsTrue()
            .Because(
                "The same class with a save in front of the pin and the saved identifier handed back must "
                + "be credited, or rule (3) reports every writer in the repository and a rule that reports "
                + "all of everything gets deleted.");

        // One correct restore does not cover a second variable pinned to a literal.
        // IsRestored turns on that — it is an All over the written set, not an Any
        // over the class — and a class that quietly degraded to Any would pass
        // every writer it has left, so the property is worth a control of its own.
        string[] plantedPartial =
        [
            "public class PlantedPartial",
            "{",
            "    public void T()",
            "    {",
            "        string? previous = Environment.GetEnvironmentVariable(\"PLANTED_B_API_KEY\");",
            "        Environment.SetEnvironmentVariable(\"PLANTED_A_API_KEY\", null);",
            "        Environment.SetEnvironmentVariable(\"PLANTED_B_API_KEY\", null);",
            "        Environment.SetEnvironmentVariable(\"PLANTED_B_API_KEY\", previous);",
            "    }",
            "}",
        ];
        var plantedPartialTraffic = ReadEnvTraffic(plantedPartial, 0, plantedPartial.Length);
        await Assert.That(plantedPartialTraffic.Restored.Contains("PLANTED_B_API_KEY")).IsTrue()
            .Because("The one variable that WAS saved and handed back is credited, as above.");
        await Assert.That(plantedPartialTraffic.Restored.Contains("PLANTED_A_API_KEY")).IsFalse()
            .Because(
                "PLANTED_A_API_KEY is pinned to a literal and never saved, so crediting it would let one "
                + "good restore cover a second variable the class never read. That is the All-in-"
                + "IsRestored this rule depends on, asserted on planted lines so it cannot be lost to the "
                + "next class someone fixes.");

        // The pairing a line scan genuinely CANNOT make, asserted here as a
        // documented gap rather than as a guarantee. `previous` is read for
        // PLANTED_OTHER_KEY and handed back for PLANTED_API_KEY, and the rule
        // credits it anyway — matching a saved identifier to the variable it was
        // read for needs name resolution, and the header says so. Left as a
        // control in the direction the rule actually behaves: if this ever flips
        // to false, the detector got STRICTER, and the header's limitation is
        // stale and has to be rewritten rather than the test quietly deleted.
        string[] plantedCrossed =
        [
            "public class PlantedCrossed",
            "{",
            "    public void T()",
            "    {",
            "        string? previous = Environment.GetEnvironmentVariable(\"PLANTED_OTHER_KEY\");",
            "        Environment.SetEnvironmentVariable(\"PLANTED_API_KEY\", null);",
            "        Environment.SetEnvironmentVariable(\"PLANTED_API_KEY\", previous);",
            "    }",
            "}",
        ];
        var plantedCrossedTraffic = ReadEnvTraffic(plantedCrossed, 0, plantedCrossed.Length);
        await Assert.That(plantedCrossedTraffic.Restored.Contains("PLANTED_API_KEY")).IsTrue()
            .Because(
                "Documented gap, asserted so it stays documented: the rule credits a save read for one "
                + "variable when it is handed back for another. It is a proxy for 'this class knows what the "
                + "ambient value was', not proof of symmetric teardown, and that is why the file header "
                + "lists it under KNOWN LIMITATION instead of leaving it to be discovered.");
    }

    /// <summary>How many times a site writes one named variable across its extent.</summary>
    private static int CountEnvWritesOf(TypeSite site, string variable)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return 0;
        }

        string file = Path.Combine(root, site.File);
        if (!File.Exists(file))
        {
            return 0;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            return 0;
        }

        string[] code = SourceScan.StripComments(string.Join('\n', lines)).Split('\n');
        int count = 0;

        for (int i = 0; i < code.Length; i++)
        {
            if (!IsEnvWrite(code[i]))
            {
                continue;
            }

            (string name, _) = ReadEnvWriteArguments(code[i]);
            if (string.Equals(name, variable, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    // ── non-vacuity ─────────────────────────────────────────────────────────

    [Test]
    public async Task Discovery_Finds_The_Full_Writer_Set_And_Not_Its_Own_Prose()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory the "
                + "scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        IReadOnlyList<string> files = EnumerateTestSourceFiles(root);
        await Assert.That(files.Count).IsGreaterThan(500)
            .Because(
                "tests/ holds 770 source files. A near-zero count means the tree filter is stale and the "
                + "rule guards nothing.");

        IReadOnlySet<string> writerFiles =
            new HashSet<string>(DiscoverWriters().Select(w => w.File), StringComparer.Ordinal);

        await Assert.That(writerFiles.Count).IsGreaterThanOrEqualTo(11)
            .Because(
                "Eleven classes write the process environment and hold tests. Fewer than eleven means the "
                + "walk stopped early or the [Test] / write detection regressed.");

        foreach (string named in WriterFilesTheIssueNamed)
        {
            await Assert.That(writerFiles.Contains(named)).IsTrue()
                .Because(
                    named + " writes the process environment and must be inside the derived writer set. If "
                    + "it is not, the walk stopped early or the class was renamed — and a rule that cannot "
                    + "see the defect it was written for recognises nothing.");
        }

        // This file and UiConfigDefaultsRule.cs both NAME the API — in a const, in
        // doc comments and in planted positive controls. None of that is a call,
        // and a guard that reported its own prose would be unusable the moment
        // anyone edited a comment in it.
        await Assert.That(writerFiles.Any(f => f.Contains("Harbor.Architecture.Tests", StringComparison.Ordinal)))
            .IsFalse()
            .Because(
                "Harbor.Architecture.Tests contains no Environment.SetEnvironmentVariable CALL — only the "
                + "literal this guard matches on, its documentation, and the positive controls. If this "
                + "goes red the literal-stripping regressed and every log message in the repository that "
                + "mentions the API would be reported as a write.");
    }

    [Test]
    public async Task Writer_Detector_Fires_On_A_Call_And_Ignores_A_Read_And_Prose()
    {
        string[] mustFail =
        [
            "        Environment.SetEnvironmentVariable(\"HOME\", tempHome);",
            "            Environment.SetEnvironmentVariable(envName, null);",
            "        Environment.SetEnvironmentVariable(\"HOME\", temp); // restored in finally",
        ];

        foreach (string bad in mustFail)
        {
            await Assert.That(IsEnvWrite(bad)).IsTrue()
                .Because("This is a call the rule must catch: " + bad.Trim());
        }

        string[] mustPass =
        [
            "        string? prev = Environment.GetEnvironmentVariable(\"HARBOR_NO_APPROVER\");",
            "        _logger.LogDebug(\"a test must not call Environment.SetEnvironmentVariable(\");",
            "        await Assert.That(Environment.GetEnvironmentVariable(\"HARBOR_MCP_CONFIG\")).IsNotNull();",
        ];

        foreach (string good in mustPass)
        {
            await Assert.That(IsEnvWrite(good)).IsFalse()
                .Because(
                    "Reading the environment is not a write, and naming the API inside a message is not a "
                    + "call. A detector that reds these gets deleted: " + good.Trim());
        }
    }

    [Test]
    public async Task The_Rival_Table_Is_Non_Empty_Resolvable_And_Pointed_At_Real_Writers()
    {
        await Assert.That(RivalPairs.Length).IsGreaterThan(0)
            .Because(
                "An empty rival table would make the reader rule pass while protecting nothing — the same "
                + "vacuous-green failure the discovery test above exists to prevent.");

        var projectsWithWriters = new HashSet<string>(
            DiscoverWriters().Select(w => w.Project), StringComparer.Ordinal);
        var named = new HashSet<string>(RivalPairs.Select(p => $"{p.Project}/{p.Writer}/{p.Reader}"), StringComparer.Ordinal);

        await Assert.That(named.Count).IsEqualTo(RivalPairs.Length)
            .Because("A duplicated row in the rival table would make it look fuller than it is.");

        foreach ((string project, string writerName, string readerName, _) in RivalPairs)
        {
            await Assert.That(projectsWithWriters.Contains(project)).IsTrue()
                .Because(
                    $"{project} holds a declared writer/reader pair, but the derived writer scan found no "
                    + $"writer there. Either the row is stale or the writer scan is broken — {writerName} "
                    + $"vs {readerName}.");
        }
    }

    // ── discovery ───────────────────────────────────────────────────────────

    /// <summary>The `[NotInParallel]` form declared on a type: the bare one, or a key list.</summary>
    private sealed record Parallelism(bool IsGlobal, string[] Keys)
    {
        internal static readonly Parallelism None = new(false, []);
    }

    /// <summary>One top-level type declaration and everything the rules need to judge it.</summary>
    private sealed record TypeSite(
        string Project,
        string File,
        string Class,
        int Line,
        Parallelism Form,
        bool DeclaresTests,
        int WriteLine,
        string[] WrittenVariables,
        HashSet<string> RestoredVariables,
        bool DeclaresEnvRead)
    {
        /// <summary>A site with no env write at all, for the reader half's lookups.</summary>
        internal static readonly TypeSite None = new(
            "", "", "", 0, Parallelism.None, false, 0, [],
            new HashSet<string>(StringComparer.Ordinal), false);
    };

    /// <summary>
    ///     Every variable name the type writes, paired with the ones it gives
    ///     back. A restore is only a restore if it writes a variable the type
    ///     actually wrote: a test that saves <c>ANTHROPIC_API_KEY</c> and then
    ///     restores <c>HOME</c> has restored nothing.
    /// </summary>
    private static (string[] Written, HashSet<string> Restored) ReadEnvTraffic(string[] code, int from, int to)
    {
        var written = new List<string>();
        var restored = new HashSet<string>(StringComparer.Ordinal);

        // Read the whole extent FIRST, because a save may sit after the write it
        // serves — McpLoginRunnerTests saves in a field initialiser and restores in
        // Dispose, and a single forward pass would judge the save as absent.
        var saved = new HashSet<string>(StringComparer.Ordinal);
        for (int i = from; i < to && i < code.Length; i++)
        {
            Match save = SavedEnvIdentifier.Match(code[i]);
            if (save.Success)
            {
                saved.Add(save.Groups["id"].Value);
            }
        }

        for (int i = from; i < to && i < code.Length; i++)
        {
            // `code` has had its literals blanked, so this cannot see a name. It is
            // therefore only trusted after IsEnvWrite confirms the line really is a
            // CALL; ReadEnvWriteArguments then re-reads the same line from the
            // comment-stripped source, where the names are intact.
            if (!IsEnvWrite(code[i]))
            {
                continue;
            }

            (string name, string value) = ReadEnvWriteArguments(code[i]);
            if (name.Length == 0)
            {
                continue;
            }

            if (!written.Contains(name, StringComparer.Ordinal))
            {
                written.Add(name);
            }

            // A restore hands back a value that was READ — so the value must be an
            // identifier this extent assigned from a GetEnvironmentVariable call.
            // Shape alone is not enough and the repository is why: AppHostDiTests
            // writes SetEnvironmentVariable("HOME", tempHome), and `tempHome` is a
            // bare identifier that was never read, so a shape-only rule would call
            // that one-shot swap a restore of itself and exempt the class by
            // accident. The variable must also be one this type writes: a test that
            // saves ANTHROPIC_API_KEY and restores HOME has restored nothing.
            if (saved.Contains(value)
                && !NeverARestoredValue.Contains(value)
                && written.Contains(name, StringComparer.Ordinal))
            {
                restored.Add(name);
            }
        }

        return ([.. written], restored);
    }

    /// <summary>
    ///     The variable name and the value of one env write, read from a
    ///     comment-stripped line with literals INTACT — <c>StripLiterals</c> blanks
    ///     the name, and the name is the whole point. Returns empty strings when the
    ///     line is not a single-line call, which is the stated scan limitation.
    /// </summary>
    private static (string Name, string Value) ReadEnvWriteArguments(string commentStrippedLine)
    {
        int open = commentStrippedLine.IndexOf(EnvWriteCall, StringComparison.Ordinal);
        if (open < 0)
        {
            return (string.Empty, string.Empty);
        }

        int cursor = open + EnvWriteCall.Length;
        if (!ReadArgument(commentStrippedLine, ref cursor, out string name))
        {
            return (string.Empty, string.Empty);
        }

        if (!ReadArgument(commentStrippedLine, ref cursor, out string value))
        {
            return (name, string.Empty);
        }

        return (Unquote(name.Trim()), value.Trim());
    }

    /// <summary>
    ///     One comma-separated argument from <paramref name="cursor" />, respecting
    ///     nesting so an argument that is itself a call (<c>Path.Combine(a, b)</c>)
    ///     is not split down the middle. Advances the cursor past the argument and
    ///     its comma.
    /// </summary>
    private static bool ReadArgument(string line, ref int cursor, out string argument)
    {
        int depth = 0;
        int start = cursor;

        while (cursor < line.Length)
        {
            char c = line[cursor];

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                argument = line[start..cursor];
                cursor++;
                return true;
            }

            cursor++;
        }

        if (cursor <= start)
        {
            argument = string.Empty;
            return false;
        }

        argument = line[start..cursor];
        return true;
    }

    /// <summary>Strips the quotes from a literal argument, so `"HOME"` and `HOME` compare equal.</summary>
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    /// <summary>
    ///     Whether the extent READS the environment at all. A class that never
    ///     reads has no value it could put back, so this is the necessary condition
    ///     for rule (3) and is what the positive control exercises.
    /// </summary>
    private static bool DeclaresEnvRead(string[] code, int from, int to)
    {
        for (int i = from; i < to && i < code.Length; i++)
        {
            if (AnyAmbientRead.IsMatch(StripLiterals(SourceScan.StripComments(code[i]))))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<TypeSite> DiscoverWriters() =>
        [.. DiscoverTypesInProjects(EnumerateTestProjects())
            .Where(t => t.WriteLine > 0 && t.DeclaresTests)];

    private static IReadOnlyList<TypeSite> DiscoverTypesInProject(string project) =>
        DiscoverTypesInProjects([project]);

    private static IReadOnlyList<TypeSite> DiscoverTypesInProjects(IReadOnlyList<string> projects)
    {
        var found = new List<TypeSite>();

        if (RepoPaths.RepoRoot is not { } root)
        {
            return found;
        }

        foreach (string project in projects)
        {
            string dir = Path.Combine(root, "tests", project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory
                         .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                         .Where(p => !IsExcluded(p))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                found.AddRange(ParseTypes(root, project, file));
            }
        }

        return found;
    }

    private static IEnumerable<TypeSite> ParseTypes(string root, string project, string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            yield break;
        }

        // Two views of the same lines, for two different questions.
        //
        //   * `code` has comments stripped and literals INTACT. Rule (3) needs the
        //     variable NAME, and StripLiterals — which is what makes prose in a log
        //     message stop looking like a call — necessarily destroys it. So a
        //     candidate line is confirmed through IsEnvWrite (which strips, on its
        //     own, per line) and only then re-read for its name from this view. A
        //     name inside a message still cannot become a violation: it is filtered
        //     out before the name is ever read.
        //   * Length must match, because every line number reported below indexes
        //     into it. StripComments preserves line count by design; if that ever
        //     stops being true the numbers point at the wrong source, and failing
        //     loudly beats reporting confident nonsense.
        string[] code = SourceScan.StripComments(string.Join('\n', lines)).Split('\n');
        if (code.Length != lines.Length)
        {
            yield break;
        }

        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

        // Only top-level types are sites, and each one's extent ends at the next
        // top-level one. Two earlier attempts were wrong, and the CI red run is
        // what showed it:
        //
        //   * Bounding at the next declaration of ANY indentation reported three
        //     nested helpers in OnboardingWizardTests.cs — FakeHealthCheck,
        //     FakeLiveRegistry, FakeCatalogClient — as writers in their own right,
        //     because each one's extent swallowed the test methods that follow it.
        //   * Bounding at the next declaration of the SAME OR SHALLOWER indentation
        //     removed the helpers but then let the outer class's writes be
        //     attributed to whichever nested type they happened to land after.
        //
        // Ignoring nested types outright is not a workaround, it is the correct
        // model: TUnit discovers top-level types, so a nested type is not a suite
        // and cannot be scheduled against anything. It also gets the awkward file
        // right in both directions — OnboardingWizardTests keeps all 32 of its
        // writes despite four nested helpers declared between its test methods,
        // while ProviderConfigTests does not steal the writes belonging to the
        // resolver class 160 lines below it.
        var declarations = new List<(int Index, int Column, string Name)>();
        for (int i = 0; i < code.Length; i++)
        {
            Match match = TypeDeclaration.Match(code[i]);
            if (match.Success)
            {
                declarations.Add((i, LeadingWhitespace(code[i]), match.Groups["name"].Value));
            }
        }

        for (int t = 0; t < declarations.Count; t++)
        {
            if (declarations[t].Column != 0)
            {
                continue;
            }

            int index = declarations[t].Index;
            int end = code.Length;

            for (int u = t + 1; u < declarations.Count; u++)
            {
                if (declarations[u].Column == 0)
                {
                    end = declarations[u].Index;
                    break;
                }
            }

            (string[] written, HashSet<string> restored) = ReadEnvTraffic(code, index, end);

            yield return new TypeSite(
                project,
                relative,
                declarations[t].Name,
                index + 1,
                ReadParallelism(code, index),
                DeclaresTestMethods(code, index, end),
                FirstEnvWriteBetween(code, index, end),
                written,
                restored,
                DeclaresEnvRead(code, index, end));
        }
    }

    /// <summary>How many leading spaces a line has, so a top-level type is told from a nested one.</summary>
    private static int LeadingWhitespace(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    ///     The parallelism form in the attribute run immediately above
    ///     <paramref name="declarationIndex" />. That run is what sits between the
    ///     previous member and this declaration: attributes, doc comments, blank
    ///     lines. Anything else ends it, because an attribute further up belongs
    ///     to a different member. Bracket depth is tracked so an attribute the
    ///     formatter split across lines still counts as one run.
    /// </summary>
    private static Parallelism ReadParallelism(string[] code, int declarationIndex)
    {
        int floor = Math.Max(0, declarationIndex - AttributeLookbackLimit);
        int depth = 0;
        var run = new List<string>();

        for (int i = declarationIndex - 1; i >= floor; i--)
        {
            string trimmed = code[i].Trim();
            bool insideAttribute = depth > 0;

            depth += trimmed.Count(c => c == '[') - trimmed.Count(c => c == ']');

            bool inRun = insideAttribute
                         || trimmed.Length == 0
                         || trimmed.StartsWith("///", StringComparison.Ordinal)
                         || trimmed.StartsWith("//", StringComparison.Ordinal)
                         || trimmed.StartsWith("/*", StringComparison.Ordinal)
                         || trimmed.StartsWith("*/", StringComparison.Ordinal)
                         || trimmed.StartsWith('*')
                         || trimmed.StartsWith('[');

            if (!inRun)
            {
                break;
            }

            run.Add(trimmed);
        }

        if (run.Count == 0)
        {
            return Parallelism.None;
        }

        // Reversed: the run was collected upwards, and a multi-line attribute reads
        // backwards as `")]"` then `"NotInParallel("`.
        string text = string.Join('\n', run);

        var keys = new List<string>();
        foreach (Match attribute in NotInParallelAttribute.Matches(text))
        {
            string arguments = attribute.Groups[1].Value;
            if (arguments.Trim().Length == 0)
            {
                return new Parallelism(true, []);
            }

            foreach (Match key in QuotedKey.Matches(arguments))
            {
                string value = key.Groups[1].Value;
                if (!keys.Contains(value, StringComparer.Ordinal))
                {
                    keys.Add(value);
                }
            }
        }

        // `[NotInParallel]` with no parentheses is the bare, "completely alone" form.
        return keys.Count > 0
            ? new Parallelism(false, [.. keys])
            : text.Contains("NotInParallel", StringComparison.Ordinal)
                ? new Parallelism(true, [])
                : Parallelism.None;
    }

    /// <summary>Whether the extent holds at least one <c>[Test]</c> method.</summary>
    private static bool DeclaresTestMethods(string[] code, int fromInclusive, int toExclusive)
    {
        for (int i = fromInclusive; i < toExclusive && i < code.Length; i++)
        {
            if (TestAttribute.IsMatch(code[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The 1-based line of the first env write inside
    ///     <c>[fromInclusive, toExclusive)</c> — one type's own extent — else 0.
    /// </summary>
    private static int FirstEnvWriteBetween(string[] code, int fromInclusive, int toExclusive)
    {
        for (int i = fromInclusive; i < toExclusive && i < code.Length; i++)
        {
            if (IsEnvWrite(code[i]))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    ///     Whether this line calls the env WRITE. Strips comments and string
    ///     literals itself, so no caller can get the two steps out of order and
    ///     turn a log message into a violation.
    /// </summary>
    private static bool IsEnvWrite(string rawLine) =>
        StripLiterals(SourceScan.StripComments(rawLine)).Contains(EnvWriteCall, StringComparison.Ordinal);

    /// <summary>
    ///     Replaces every character INSIDE a string, verbatim, interpolated or char
    ///     literal with a space, preserving length so offsets survive.
    ///     <c>SourceScan.StripComments</c> deliberately leaves literals alone — its
    ///     callers all match a quoted literal — which is exactly wrong here.
    /// </summary>
    /// <remarks>
    ///     A line-level lexer, not a C# parser. An interpolation hole
    ///     (<c>$"{Environment.SetEnvironmentVariable("A", v)}"</c>) is blanked along
    ///     with the surrounding text and would be missed; the same bounded limitation
    ///     <c>UiConfigDefaultsRule</c> states for its own lexer.
    /// </remarks>
    private static string StripLiterals(string line)
    {
        var code = line.ToCharArray();

        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];

            if (c is '@' or '$' && i + 1 < code.Length && code[i + 1] == '"')
            {
                i = BlanksLiteral(code, i + 1, verbatim: true) + 1;
                continue;
            }

            if (c == '"')
            {
                i = BlanksLiteral(code, i, verbatim: false) + 1;
                continue;
            }

            if (c == '\'')
            {
                i = BlanksLiteral(code, i, verbatim: false, quote: '\'') + 1;
                continue;
            }

            i++;
        }

        return new string(code);
    }

    /// <summary>
    ///     Blanks one literal from its opening quote and returns the index of its
    ///     closing quote. A verbatim literal escapes its quote by doubling it; every
    ///     other literal escapes with a backslash.
    /// </summary>
    private static int BlanksLiteral(char[] code, int open, bool verbatim, char quote = '"')
    {
        int i = open + 1;
        while (i < code.Length)
        {
            // A backslash escape covers the next character too, so `\"` must not
            // be read as the closing quote.
            if (!verbatim && code[i] == '\\' && i + 1 < code.Length)
            {
                code[i] = ' ';
                code[i + 1] = ' ';
                i += 2;
                continue;
            }

            if (code[i] != quote)
            {
                code[i] = ' ';
                i++;
                continue;
            }

            // A verbatim literal escapes its own quote by doubling it.
            if (verbatim && i + 1 < code.Length && code[i + 1] == quote)
            {
                code[i] = ' ';
                code[i + 1] = ' ';
                i += 2;
                continue;
            }

            return i;
        }

        return code.Length - 1;
    }

    /// <summary>
    ///     Whether two declarations actually exclude each other. The bare form
    ///     excludes everything, so it intersects with any key at all.
    /// </summary>
    private static bool Holds(Parallelism a, Parallelism b) =>
        a.IsGlobal || b.IsGlobal || a.Keys.Intersect(b.Keys, StringComparer.Ordinal).Any();

    /// <summary>How a declaration reads in a failure message, including the empty case.</summary>
    private static string Describe(Parallelism form) => form switch
    {
        { IsGlobal: true } => "NotInParallel (the bare form — completely alone)",
        { Keys.Length: > 0 } => $"NotInParallel({string.Join(", ", form.Keys)})",
        _ => "no [NotInParallel] at all",
    };

    private static IReadOnlyList<string> EnumerateTestProjects()
    {
        if (RepoPaths.RepoRoot is not { } root || !Directory.Exists(Path.Combine(root, "tests")))
        {
            return [];
        }

        return
        [
            .. Directory.GetDirectories(Path.Combine(root, "tests"))
                .Select(d => Path.GetFileName(d))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
        ];
    }

    private static IReadOnlyList<string> EnumerateTestSourceFiles(string root)
    {
        var files = new List<string>();

        foreach (string project in EnumerateTestProjects())
        {
            string dir = Path.Combine(root, "tests", project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            files.AddRange(Directory
                .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsExcluded(p)));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    ///     Build output, the unmaintained <c>contrib/</c> tree, and sibling
    ///     worktrees. Deliberately NOT <c>SourceScan.IsBuildOutput</c>: that one
    ///     rejects <c>/tests/</c> as well, because every other gate in this project
    ///     scans the product trees and must not police its own fixtures. This rule's
    ///     subject IS the fixtures.
    /// </summary>
    private static bool IsExcluded(string path)
    {
        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/obj/", StringComparison.Ordinal)
               || normalised.Contains("/bin/", StringComparison.Ordinal)
               || normalised.Contains("/contrib/", StringComparison.Ordinal)
               || normalised.Contains("/.worktrees/", StringComparison.Ordinal);
    }
}
