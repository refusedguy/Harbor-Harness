// UiConfigDefaultsRule.cs — the guard for issue #677.
//
// THE DEFECT THIS GUARDS
// ----------------------
// Two desktop view-models hand-picked a DEFAULT for a persisted config field
// that the core already gives a default to, and the two answers disagreed with
// the core:
//
//   SettingsViewModel._defaultProvider   = "ollama"
//   SettingsViewModel._storageBackend    = "jsonl"
//   SettingsViewModel._defaultProvider   ? "ollama" : …
//   SettingsViewModel._fontFamily        ? "Inter"  : …
//   SettingsViewModel._storageBackend    ? "jsonl"   : …
//   SettingsViewModel._logLevel          ? "info"    : …
//   ConfigStoreOnboardingPersister.PersistAsync
//                                                               ? "jsonl"   : …
//
// while the records that own those fields say:
//   CommonConfig.DefaultProvider = "anthropic"
//   CommonConfig.StorageBackend  = ""           ("not chosen";
//                                                 the composition preset decides:
//                                                 CLI jsonl, desktop memory)
//   CommonConfig.LogLevel       = "info"
//   AvaloniaConfig.FontFamily   = "Inter"      (AvaloniaConfig.cs)
//
// and the desktop composition root says:
//   AppHost.DesktopOptions                       DefaultStorageBackend = "memory"
//
// Three sources, one setting, no two in agreement. The empty-unset contract in
// ADR-008 is only meaningful if the empty value survives the round trip — and
// the onboarding wizard wrote "jsonl" into the shared config, which is exactly
// the value the desktop preset ("memory") was supposed to be able to choose. The
// user could not unset the field from the UI: the wizard put it straight back.
//
// WORSE, AND SEPARATELY A DEFECT: `SettingsViewModel.SaveAsync` called
// `Environment.SetEnvironmentVariable` for HARBOR_MODEL / HARBOR_STORAGE /
// HARBOR_LOGLEVEL / OLLAMA_HOST. That is process state, not session state, and
// nothing reads it after startup: StorageModule resolves the backend during
// `AddHarbor`, ConfigurationModule resolves the model there, ProviderFactories
// snapshots OLLAMA_HOST there. The writes could not change a single thing; they
// only leaked the user's choice into every other component in the process and
// into every test that runs after it.
//
// THE RULE, IN FULL
// -----------------
// Inside a UI ViewModels directory, on a code line:
//
//   R1  a config-default field (DefaultProvider / DefaultModel / DefaultAgent /
//       StorageBackend / LogLevel / FontFamily) is never assigned a string
//       literal. The view-model shows what the config record holds — empty
//       included — and the composition preset picks the rest.
//   R2  `Environment.SetEnvironmentVariable(` is never CALLED. Reading the
//       environment is fine; writing it from a view-model is not.
//
// WHY A SOURCE-TEXT RULE AND NOT AN ANALYZER
// ------------------------------------------
// `Harbor.Architecture.Tests` must not reference `apps/Harbor.App.Avalonia` —
// it is an app, a composition root (the same reason AvaloniaFireAndForgetRules
// scans that app rather than referencing it). A source scan needs no reference
// edge.
//
// R1 could not be an analyzer either: the offending shape is a CONDITIONAL
// whose empty branch is a literal (`x = IsNullOrEmpty(y) ? "jsonl" : y`), and
// deciding that a string sits on the "default" side of a ternary needs to know
// which side that is — no shipped Roslyn rule for this repo does that.
//
// WHY THE RULE STRIPS LITERALS AND COMMENTS FIRST
// -----------------------------------------------
// A naive grep for `LogLevel` + `=` cannot tell an assignment from a log
// MESSAGE ("saved: log={LogLevel}"), and cannot tell a value from prose after
// a `//`. Both are everywhere in this codebase and both would make the rule
// cry wolf within a week — a rule that cries wolf gets deleted, and a deleted
// rule protects nothing. So each line is lexed into "code with literals blanked,
// trailing comment dropped" before the field name is looked for, and the
// literal test then runs on the ORIGINAL text at the offset the assignment
// starts at. Four shapes are pinned as lookalikes that must stay green; see
// `HardcodedConfigDefaultDetector_StaysQuietOnPassThrough_AndOnLookalikes`.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard. Four
// tests below defend against it: the discovery step must find the real file set
// AND both files the issue named; R1's detector must fire on all six original
// lines and stay quiet on the conforming pass-through and on four lookalikes;
// R2's detector must fire on the four original env writes and stay quiet on
// reads and on a message that merely names the API.
//
// KNOWN GAP — stated, not hidden
// -----------------------------
// `Theme` is deliberately NOT in R1's field list. Two lines outside this
// issue's file scope have the same shape and are owned by another wave:
//   ThemeSettingsViewModel._theme
// The third site was the abstract base `ThemeSettingsViewModelBase` in this
// project's ViewModels folder — the file is gone: #1835 removed the base
// because nothing derived from it.
// The next site, SettingsViewModel._theme, was fixed in the same commit that
// added this file — but a guard that named `Theme` would be red on day one, and
// a red-on-arrival guard does not land. Widening the list is the next wave's
// call to make, with the file above in the same commit.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The lexer is LINE-level, not a C# parser. A literal that opens on one line and
// closes on the next (a multi-line verbatim or raw string) can leave the
// line's literal/comment balance misread, and a call split across lines
// (`Environment\n    .SetEnvironmentVariable(`) is not matched. The limitation is
// bounded by construction: the defect is a default VALUE, values live in
// single-line literals on code lines, and a multi-line literal is not where a
// config default is spelled.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #677: a UI view-model never chooses a config default, and never
///     writes process-wide environment state.
/// </summary>
public class UiConfigDefaultsRule
{
    /// <summary>Product trees walked for <c>ViewModels/</c> directories.</summary>
    private static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>
    ///     The directory name that marks a file as belonging to the view-model
    ///     layer. Derived, not a hand-typed file list: a view-model added under
    ///     any UI project is covered the day it lands, and the composition roots
    ///     (<c>AppHost.cs</c>, <c>HostBuilder.cs</c>) — which legitimately DO
    ///     declare presets — sit outside a <c>ViewModels/</c> directory and stay
    ///     outside the rule.
    /// </summary>
    private const string ViewModelDirectory = "ViewModels";

    /// <summary>
    ///     The config fields a view-model must not give a value of its own. Every
    ///     one of them is a persisted field whose default is already declared on
    ///     the owning record (<c>CommonConfig</c> / <c>AvaloniaConfig</c>), and
    ///     that record is the only place the default may live. <c>Theme</c> is
    ///     absent on purpose — see KNOWN GAP in the header.
    /// </summary>
    private static readonly Regex ConfigDefaultFieldAssignment = new(
        @"(?<name>defaultProvider|defaultModel|defaultAgent|storageBackend|logLevel|fontFamily)\s*=(?!=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    ///     The process-wide env WRITE, anchored on the call paren. Matched against
    ///     literal-stripped code, so a log line that merely NAMES the API stays
    ///     green while the real call — whose env-var name is a literal, not code —
    ///     is caught.
    /// </summary>
    private const string ProcessEnvWriteCall = "Environment.SetEnvironmentVariable(";

    /// <summary>The two files issue #677 named; the discovery test asserts both are scanned.</summary>
    private static readonly string[] FilesTheIssueNamed =
    [
        "apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs",
        "src/Harbor.Desktop.ViewModels/ViewModels/OnboardingViewModel.cs",
    ];

    /// <summary>The hardcoded-default sub-id: one hit per view-model line that hands a config field a literal.</summary>
    private const string HardcodedDefaultSubId = "HARDCODED-CONFIG-DEFAULT";

    /// <summary>The process-env-write sub-id: one hit per view-model line that calls the env write.</summary>
    private const string ProcessEnvWriteSubId = "PROCESS-ENV-WRITE";

    // MECHANISM (#1086, step 2, conveyor batch-15)
    // --------------------------------------------
    // The ban below is a ScanRule: the two forbidden shapes as two Forbidden
    // rows, seven planted controls, the two live view-model files as the
    // MustContain floor, a discovery floor. Enumeration, matching and the
    // control/discovery/baseline verdicts are ScanRunner's; this file keeps
    // the issue prose, the probe, and the test names.
    //
    // The verdict is ParseViewModelDefaults through the Func-overload,
    // carrying the original matcher verbatim — StripLiteralsAndComment plus
    // the ConfigDefaultFieldAssignment offset literal test (R1) and the
    // ProcessEnvWrite call test (R2), with the blank / `//` / `/*` / `*`
    // line skip verbatim. The Forbidden rows document the banned spellings;
    // the runner does not execute them (a line regex over shared-stripped
    // source cannot see what the literal-blanker sees — the log-message and
    // write-in-message controls pin exactly that difference).
    //
    // The stripper is deliberately NOT the shared one:
    // SourceScan.StripComments preserves string literals while this rule's
    // lexer blanks them, so a field name inside a message ("log={LogLevel}")
    // and the write API named inside a message are code under the shared
    // stripper and prose under this one. Folding would change matcher input
    // (ScanRule.cs header: divergent contracts migrate via the Func-overload
    // with their stripper inside, never by silently adopting the shared one).
    //
    // Scope parity is exact: Trees ["src", "apps"] + InScope "/ViewModels/"
    // walks what EnumerateViewModelFiles walks — no *.cs path under src/ or
    // apps/ carries a /tests/, /contrib/ or /.worktrees/ segment inside a
    // ViewModels/ directory, and both sides drop /obj/ + /bin/.
    //
    // MinHits 20 is the floor the original discovery test pinned
    // (files.Count > 20): the walk yields ~54 files today, so a smaller
    // count means the walk broke rather than the tree is clean.

    /// <summary>
    ///     The process-wide env WRITE shape, anchored on the call paren. The
    ///     runner does not execute this row (the rule grades through
    ///     <see cref="ParseViewModelDefaults" />); it documents the shape the
    ///     rule bans and carries the failure text.
    /// </summary>
    private static readonly Regex ProcessEnvWriteShape = new(
        @"Environment\.SetEnvironmentVariable\(",
        RegexOptions.Compiled);

    /// <summary>The view-model ban as data: two documented shapes, planted controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "UiConfigDefaults",
        Trees = ["src", "apps"],
        InScope = static p => p.Contains("/ViewModels/", StringComparison.Ordinal),
        Forbidden =
        [
            new ScanForbidden(
                HardcodedDefaultSubId,
                ConfigDefaultFieldAssignment,
                "the view-model shows what the config record holds — empty included — and the "
                + "composition preset picks the rest (ADR-008 — CLI jsonl, desktop memory). Show the "
                + "stored value verbatim. See issue #677."),
            new ScanForbidden(
                ProcessEnvWriteSubId,
                ProcessEnvWriteShape,
                "persist to the config record instead: HARBOR_STORAGE is resolved by StorageModule "
                + "during AddHarbor, HARBOR_MODEL by ConfigurationModule during AddHarbor, OLLAMA_HOST "
                + "snapshotted by ProviderFactories during AddHarbor. Nothing re-reads them after "
                + "startup. See issue #677."),
        ],
        Controls =
        [
            new ScanControl(
                "Planted/HardcodedDefault.cs",
                "    private string _storageBackend = \"jsonl\";",
                HardcodedDefaultSubId),
            // The conditional shape: the empty branch is the literal.
            new ScanControl(
                "Planted/TernaryDefault.cs",
                "        StorageBackend = string.IsNullOrEmpty(cfg.StorageBackend) ? \"jsonl\" : cfg.StorageBackend",
                HardcodedDefaultSubId),
            new ScanControl(
                "Planted/ProcessEnvWrite.cs",
                "        Environment.SetEnvironmentVariable(\"HARBOR_STORAGE\", StorageBackend);",
                ProcessEnvWriteSubId),
            // The conforming shape: read the record, show it, save it back.
            new ScanControl(
                "NearMiss/PassThrough.cs",
                "        StorageBackend = _common.StorageBackend;",
                null),
            // A field name inside a string literal is a message, not a value:
            // the literal-blanker sees no field here, the shared stripper would.
            new ScanControl(
                "NearMiss/LogMessage.cs",
                "            _logger.LogInformation(\"saved: theme={Theme}, log={LogLevel}, font={Font}\");",
                null),
            // Reading the environment is allowed.
            new ScanControl(
                "NearMiss/EnvRead.cs",
                "        OllamaHost = Environment.GetEnvironmentVariable(\"OLLAMA_HOST\") ?? string.Empty;",
                null),
            // Naming the write API inside a message is not a call: the
            // literal-blanker sees no call here, the shared stripper would.
            new ScanControl(
                "NearMiss/WriteInMessage.cs",
                "        _logger.LogDebug(\"a view-model must not call Environment.SetEnvironmentVariable(\");",
                null),
        ],
        // The walk yields ~54 view-model files today, including the two files
        // the issue named; a smaller count means the walk broke rather than
        // the tree is clean.
        MinHits = 20,
        MustContain =
        [
            "apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs",
            "src/Harbor.Desktop.ViewModels/ViewModels/OnboardingViewModel.cs",
        ],
        CustomParse = ParseViewModelDefaults,
    };

    /// <summary>
    ///     The custom parser: the original line scan over one file's raw
    ///     source — the blank / comment-line skip verbatim, then the R1 field
    ///     test and the R2 write test verbatim, one report per offending line.
    /// </summary>
    private static IEnumerable<ScanHit> ParseViewModelDefaults(string displayPath, string rawSource)
    {
        string[] lines = rawSource.Split('\n').Select(static l => l.TrimEnd('\r')).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            string trimmed = text.TrimStart();
            if (trimmed.Length == 0
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            if (HardcodedConfigDefault(text) is not null)
            {
                yield return new ScanHit(HardcodedDefaultSubId, displayPath, i + 1, text.Trim());
            }

            if (ProcessEnvWrite(text))
            {
                yield return new ScanHit(ProcessEnvWriteSubId, displayPath, i + 1, text.Trim());
            }
        }
    }

    // ── the rules ─────────────────────────────────────────────────────────

    [Test]
    public async Task NoViewModel_ChoosesAConfigDefault_OrWritesTheProcessEnvironment()
    {
        var hardcodedDefaults = new List<string>();
        var processEnvWrites = new List<string>();

        foreach ((string file, int line, string text) in ScanViewModelLines())
        {
            if (ProcessEnvWrite(text))
            {
                processEnvWrites.Add(
                    $"{file}:{line} — a view-model calls Environment.SetEnvironmentVariable. That is PROCESS state, "
                    + "not session state: HARBOR_STORAGE is resolved by StorageModule during AddHarbor, HARBOR_MODEL by "
                    + "ConfigurationModule during AddHarbor, OLLAMA_HOST snapshotted by ProviderFactories during AddHarbor. "
                    + "Nothing re-reads any of them after startup, so the write cannot change the running app — it only "
                    + "leaks the user's choice into every other component, and into every test that runs after it. Persist "
                    + "to the config record instead. Offending line: " + text.Trim());
            }

            if (HardcodedConfigDefault(text) is { } field)
            {
                hardcodedDefaults.Add(
                    $"{file}:{line} — the view-model assigns a literal to `{field}`, a persisted config field whose "
                    + "default is already declared on the owning record. The UI is not allowed to pick: an empty value "
                    + "means \"not chosen\" and the composition preset resolves it (ADR-008 — CLI jsonl, desktop memory). "
                    + "A hardcoded value here makes that preset unreachable AND disagrees with the record, which is how "
                    + "the UI came to say \"ollama\" where the core says \"anthropic\". Show the stored value verbatim, "
                    + "empty included. Offending line: " + text.Trim());
            }
        }

        await Assert.That(hardcodedDefaults).IsEmpty()
            .Because(
                "Issue #677. Every one of these is the UI choosing a value the core already owns. The empty-unset "
                + "contract only holds if the empty value survives: a wizard that writes \"jsonl\" into the shared config "
                + "makes the desktop preset (\"memory\") unreachable and leaves the user unable to unset the field.");

        await Assert.That(processEnvWrites).IsEmpty()
            .Because(
                "Issue #677. Environment.SetEnvironmentVariable from a view-model mutates process state that no "
                + "component re-reads after startup, and that outlives the session the user was editing. It is the "
                + "fourth source of truth for a setting the composition root already resolved.");

        // The ScanRule half: the same two verdicts over ScanRunner's
        // collection (per-line hits through ParseViewModelDefaults, carrying
        // the literal-blanking lexer verbatim). No baseline rows and no file
        // hatch — zero hits is the whole rule (see the MECHANISM note above).
        List<string> scanFailures = ScanRunner.Evaluate(Rule);

        await Assert.That(scanFailures).IsEmpty()
            .Because(
                "Issue #677 via ScanRule. " + string.Join("\n", scanFailures));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Discovery_FindsTheViewModelLayer_AndBothFilesTheIssueNamed()
    {
        // Without a repository root every rule in this file passes vacuously.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory the scan yields "
                + "nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        IReadOnlyList<string> files = EnumerateViewModelFiles(root);

        await Assert.That(files.Count).IsGreaterThan(20)
            .Because(
                $"The four ViewModels/ directories under src/ + apps/ hold far more than 20 source files; found "
                + $"{files.Count}. A near-zero count means the directory name is stale and the rule guards nothing.");

        foreach (string named in FilesTheIssueNamed)
        {
            await Assert.That(files.Contains(named)).IsTrue()
                .Because(named + " must be inside the scanned set — it is the file #677 is about.");
        }

        // The composition roots legitimately DO declare presets. They sit outside
        // a ViewModels/ directory and must stay outside the scan; if a future
        // refactor moves one, the rule starts failing on correct code.
        await Assert.That(files.Any(f => f.EndsWith("/AppHost.cs", StringComparison.Ordinal))).IsFalse()
            .Because("A composition root declares presets by design; policing it would be a guard crying wolf.");

        // Comment lines never reach the rule, so prose describing a hardcoded
        // default cannot fail the build.
        int commentHits = files
            .SelectMany(f => File.ReadAllLines(Path.Combine(root, f)))
            .Count(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && HardcodedConfigDefault(l) is not null);

        await Assert.That(commentHits).IsEqualTo(0)
            .Because("A comment line that shows the old code must not be a violation — the scan drops it, and this test "
                + "fails if that ever stops being true.");
    }

    [Test]
    public async Task HardcodedConfigDefaultDetector_FiresOnEveryLineTheIssueNamed()
    {
        // The exact six lines #677 reported, verbatim. If the rule cannot
        // recognise the defect it was written for, it recognises nothing.
        string[] mustFail =
        [
            "    private string _defaultProvider = \"ollama\";",
            "    private string _fontFamily = \"Inter\";",
            "    private string _logLevel = \"info\";",
            "    private string _storageBackend = \"jsonl\";",
            "        DefaultProvider = string.IsNullOrEmpty(_common.DefaultProvider) ? \"ollama\" : _common.DefaultProvider;",
            "        StorageBackend = string.IsNullOrEmpty(cfg.StorageBackend) ? \"jsonl\" : cfg.StorageBackend",
        ];

        foreach (string bad in mustFail)
        {
            await Assert.That(HardcodedConfigDefault(bad)).IsNotNull()
                .Because($"This is one of the lines issue #677 reported; the rule must recognise it: {bad.Trim()}");
        }
    }

    [Test]
    public async Task HardcodedConfigDefaultDetector_StaysQuietOnPassThrough_AndOnLookalikes()
    {
        // (a) The conforming shape: read the record, show it, save it back.
        string[] mustPass =
        [
            "        DefaultProvider = _common.DefaultProvider;",
            "        DefaultModel = _common.DefaultModel ?? string.Empty;",
            "        StorageBackend = _common.StorageBackend;",
            "        LogLevel = _common.LogLevel;",
            "        FontFamily = _app.FontFamily;",
            "            DefaultProvider = DefaultProvider,",
            "            StorageBackend = StorageBackend,",
            // An initialiser with no literal is not a value.
            "    private string _defaultProvider = string.Empty;",
        ];

        foreach (string good in mustPass)
        {
            await Assert.That(HardcodedConfigDefault(good)).IsNull()
                .Because(
                    "This is the shape the fix produces — the view-model shows what the record holds. Flagging it "
                    + $"would make the rule cry wolf: {good.Trim()}");
        }

        // (b) Lookalikes: each carries a config field name AND an `=`, and none
        //     of them assigns the UI-chosen value.
        string[] lookalikes =
        [
            "            _logger.LogInformation(\"saved: theme={Theme}, log={LogLevel}, font={Font}\");",
            "        public string CurrentModelLabel => $\"{result.Value.DefaultProvider}/{result.Value.DefaultModel}\";",
            "        await Assert.That(cfg.LogLevel).IsEqualTo(\"info\");",
            "            // the old line was: StorageBackend = IsNullOrEmpty(x) ? \"jsonl\" : x;",
            "        _common = _common with { DefaultModel = model.Trim() };",
            "        DefaultProvider = _common.DefaultProvider; // no longer falls back to \"ollama\"",
        ];

        foreach (string lookalike in lookalikes)
        {
            await Assert.That(HardcodedConfigDefault(lookalike)).IsNull()
                .Because(
                    "A field name in a message, in a comparison, in a comment, or in a prose note about a value that is "
                    + "no longer used is not the UI choosing a default. A rule that reds these gets deleted: "
                    + lookalike.Trim());
        }
    }

    [Test]
    public async Task ProcessEnvWriteDetector_FiresOnTheFourWrites_AndIgnoresReads()
    {
        string[] mustFail =
        [
            "        Environment.SetEnvironmentVariable(\"HARBOR_MODEL\", $\"{DefaultProvider}/{DefaultModel}\");",
            "        Environment.SetEnvironmentVariable(\"HARBOR_STORAGE\", StorageBackend);",
            "        Environment.SetEnvironmentVariable(\"HARBOR_LOGLEVEL\", LogLevel);",
            "        Environment.SetEnvironmentVariable(\"OLLAMA_HOST\", OllamaHost);",
        ];

        foreach (string bad in mustFail)
        {
            await Assert.That(ProcessEnvWrite(bad)).IsTrue()
                .Because($"This is one of the four process-wide writes #677 reported: {bad.Trim()}");
        }

        string[] mustPass =
        [
            "        OllamaHost = Environment.GetEnvironmentVariable(\"OLLAMA_HOST\") ?? string.Empty;",
            "        string? env = Environment.GetEnvironmentVariable(\"HARBOR_STORAGE\");",
            "        _logger.LogDebug(\"a view-model must not call Environment.SetEnvironmentVariable(\");",
        ];

        foreach (string good in mustPass)
        {
            await Assert.That(ProcessEnvWrite(good)).IsFalse()
                .Because($"Reading the environment is allowed; naming the write API inside a message is not a call: {good.Trim()}");
        }
    }

    /// <summary>
    ///     The planted controls drive the REAL parser: the field and write
    ///     shapes reported under their sub-ids (including the ternary), the
    ///     pass-through and the literal-naming lookalikes silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the seven planted controls must agree with the matcher cases above through "
                + "the rule's own parser — otherwise the rule guards nothing, or forbids "
                + "the fix. " + string.Join("; ", failures));
    }

    /// <summary>
    ///     Non-vacuity: the walk must find the layer it claims to govern. A scan
    ///     rooted at a renamed directory finds zero files and then passes
    ///     everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheScopeItClaimsToGovern()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/ + apps/ hold far more than 20 view-model files, including SettingsViewModel.cs and "
                + "OnboardingViewModel.cs; a smaller count — or a scope that stopped "
                + "seeing those files — means the directory was renamed or the walk stopped "
                + "matching, and NoViewModel_ChoosesAConfigDefault_OrWritesTheProcessEnvironment becomes "
                + "vacuous. " + string.Join("; ", discovery));
    }

    /// <summary>
    ///     The equivalence proof: the ScanRule parser IS the probe's line scan —
    ///     the same literal-blanking lexer, the same field and write tests, the
    ///     same line skip — over every live file in scope. If this fails, the
    ///     Func path drifted from the oracle.
    /// </summary>
    [Test]
    public async Task Parser_MatchesTheProbeOnEveryLiveFile()
    {
        var mismatches = new List<string>();
        foreach ((string displayPath, string source) in ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)))
        {
            string[] lines = source.Split('\n').Select(static l => l.TrimEnd('\r')).ToArray();
            var expected = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i];
                string trimmed = text.TrimStart();
                if (trimmed.Length == 0
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (HardcodedConfigDefault(text) is not null)
                {
                    expected.Add($"{HardcodedDefaultSubId}@{i + 1}");
                }

                if (ProcessEnvWrite(text))
                {
                    expected.Add($"{ProcessEnvWriteSubId}@{i + 1}");
                }
            }

            List<ScanHit> actual = [.. Rule.CustomParse!(displayPath, source)];

            string expectedKey = string.Join(
                " | ",
                expected.OrderBy(static s => s, StringComparer.Ordinal));
            string actualKey = string.Join(
                " | ",
                actual
                    .OrderBy(static h => h.Line)
                    .ThenBy(static h => h.RuleId, StringComparer.Ordinal)
                    .Select(static h => $"{h.RuleId}@{h.Line}"));

            if (!string.Equals(expectedKey, actualKey, StringComparison.Ordinal))
            {
                mismatches.Add($"{displayPath} probe=[{expectedKey}] rule=[{actualKey}]");
            }
        }

        await Assert.That(mismatches).IsEmpty()
            .Because(
                "the ScanRule parser must agree with HardcodedConfigDefault + ProcessEnvWrite on every "
                + "live file in scope — it carries those methods verbatim, so a disagreement means "
                + "the Func path stopped calling them. " + string.Join("; ", mismatches));
    }

    /// <summary>
    ///     The rule carries no tolerated rows: every hardcoded default and every
    ///     process-env write is the defect #677 reported, so there is nothing
    ///     to excuse.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     No baseline row is stale: a permission for a problem that no longer
    ///     exists is a blanket permission.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a stale baseline row excuses nothing and teaches the next reader "
                   + "that permissions accumulate. " + string.Join("; ", stale));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    ///     The config field this line hands a literal of its own, or
    ///     <c>null</c>. The field name is matched against literal-stripped,
    ///     comment-stripped code; the literal test then runs on the ORIGINAL
    ///     text at the offset where the assignment starts, so a literal in a
    ///     message or in a trailing comment is not mistaken for the value.
    /// </summary>
    private static string? HardcodedConfigDefault(string line)
    {
        string code = StripLiteralsAndComment(line);
        Match match = ConfigDefaultFieldAssignment.Match(code);
        if (!match.Success)
        {
            return null;
        }

        string withoutComment = StripComment(line);
        int valueStart = match.Index + match.Length;
        return valueStart < withoutComment.Length && withoutComment[valueStart..].Contains('"')
            ? match.Groups["name"].Value
            : null;
    }

    /// <summary>
    ///     Whether this line CALLS <c>Environment.SetEnvironmentVariable</c>.
    ///     Matched against literal-stripped code, so a message that names the
    ///     API is not a call while the real call is.
    /// </summary>
    private static bool ProcessEnvWrite(string line)
        => StripLiteralsAndComment(line).Contains(ProcessEnvWriteCall, StringComparison.Ordinal);

    /// <summary>Every non-comment, non-blank source line of every UI view-model.</summary>
    private static IEnumerable<(string File, int Line, string Text)> ScanViewModelLines()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            yield break;
        }

        foreach (string file in EnumerateViewModelFiles(root))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(root, file));
            }
            catch (IOException)
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i];
                string trimmed = text.TrimStart();
                if (trimmed.Length == 0
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                yield return (file, i + 1, text);
            }
        }
    }

    /// <summary>Repo-relative, forward-slashed paths of every view-model source file.</summary>
    private static IReadOnlyList<string> EnumerateViewModelFiles(string? root)
    {
        if (root is null)
        {
            return [];
        }

        var files = new List<string>();
        foreach (string tree in ProductTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string viewModelDir in Directory.GetDirectories(dir, ViewModelDirectory, SearchOption.AllDirectories))
            {
                if (IsBuildOutput(viewModelDir))
                {
                    continue;
                }

                files.AddRange(Directory.GetFiles(viewModelDir, "*.cs", SearchOption.AllDirectories)
                    .Where(p => !IsBuildOutput(p))
                    .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal)
               || normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    // ── the line lexer ────────────────────────────────────────────────────

    /// <summary>
    ///     <paramref name="line" /> with every string / char literal replaced by
    ///     a space and everything from the first unquoted <c>//</c> dropped, so
    ///     only CODE is left. A LINE-level lexer, not a C# parser — see the KNOWN
    ///     LIMITATION in the file header.
    /// </summary>
    private static string StripLiteralsAndComment(string line)
    {
        var code = new StringBuilder(line.Length);

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = ConsumeVerbatim(line, i + 1, code);
                continue;
            }

            if (c == '$' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = ConsumeInterpolated(line, i + 1, code);
                continue;
            }

            if (c == '"')
            {
                i = ConsumeQuoted(line, i, code);
                continue;
            }

            if (c == '\'')
            {
                i = ConsumeChar(line, i, code);
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            code.Append(c);
        }

        return code.ToString();
    }

    /// <summary><paramref name="line" /> up to the first unquoted <c>//</c>, literals intact.</summary>
    private static string StripComment(string line)
    {
        var code = new StringBuilder(line.Length);

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = ConsumeVerbatim(line, i + 1, null);
                code.Append(line, i, 1);
                continue;
            }

            if (c == '$' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = ConsumeInterpolated(line, i + 1, null);
                code.Append(line, i, 1);
                continue;
            }

            if (c == '"')
            {
                i = ConsumeQuoted(line, i, null);
                code.Append(line, i, 1);
                continue;
            }

            if (c == '\'')
            {
                i = ConsumeChar(line, i, null);
                code.Append(line, i, 1);
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            code.Append(c);
        }

        return code.ToString();
    }

    /// <summary>Consumes a <c>@"…"</c> body; returns the index of its closing quote.</summary>
    private static int ConsumeVerbatim(string line, int openQuote, StringBuilder? code)
    {
        for (int i = openQuote + 1; i < line.Length; i++)
        {
            code?.Append(' ');

            if (line[i] != '"')
            {
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == '"')
            {
                code?.Append(' ');
                i++;
                continue;
            }

            return i;
        }

        return line.Length - 1;
    }

    /// <summary>
    ///     Consumes a <c>$"…{expr}…"</c> body. The interpolated holes are
    ///     recursed into, because the hole is code — and <c>$"{a.DefaultProvider = "x"}"</c>
    ///     is a value the UI chose.
    /// </summary>
    private static int ConsumeInterpolated(string line, int openQuote, StringBuilder? code)
    {
        for (int i = openQuote + 1; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                return i;
            }

            if (line[i] != '{')
            {
                code?.Append(' ');
                continue;
            }

            int close = line.IndexOf('}', i);
            if (close < 0)
            {
                code?.Append(' ');
                return line.Length - 1;
            }

            if (code is not null)
            {
                code.Append(' ').Append(StripLiteralsAndComment(line[(i + 1)..close])).Append(' ');
            }

            i = close;
        }

        return line.Length - 1;
    }

    /// <summary>Consumes a <c>"…"</c> body, honouring backslash escapes.</summary>
    private static int ConsumeQuoted(string line, int openQuote, StringBuilder? code)
    {
        for (int i = openQuote + 1; i < line.Length; i++)
        {
            code?.Append(' ');

            if (line[i] == '\\')
            {
                code?.Append(' ');
                i++;
                continue;
            }

            if (line[i] == '"')
            {
                return i;
            }
        }

        return line.Length - 1;
    }

    /// <summary>Consumes a <c>'c'</c> char literal.</summary>
    private static int ConsumeChar(string line, int openQuote, StringBuilder? code)
    {
        for (int i = openQuote + 1; i < line.Length; i++)
        {
            code?.Append(' ');

            if (line[i] == '\\')
            {
                code?.Append(' ');
                i++;
                continue;
            }

            if (line[i] == '\'')
            {
                return i;
            }
        }

        return line.Length - 1;
    }
}
