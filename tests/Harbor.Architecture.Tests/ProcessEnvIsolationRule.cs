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
        bool HasTests,
        int WriteLine);

    private static IReadOnlyList<TypeSite> DiscoverWriters() =>
        [.. DiscoverTypesInProjects(EnumerateTestProjects())
            .Where(t => t.WriteLine > 0 && t.HasTests)];

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

        string[] code = SourceScan.StripComments(string.Join('\n', lines)).Split('\n');
        if (code.Length != lines.Length)
        {
            // StripComments preserves line count by design. If that ever stops being
            // true the line numbers below point at the wrong source, and failing
            // loudly beats reporting confident nonsense.
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

            yield return new TypeSite(
                project,
                relative,
                declarations[t].Name,
                index + 1,
                ReadParallelism(code, index),
                HasTests(code, index, end),
                FirstEnvWriteBetween(code, index, end));
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
    private static bool HasTests(string[] code, int fromInclusive, int toExclusive)
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
