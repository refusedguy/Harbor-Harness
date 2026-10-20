# Guard firings, classified: what the architecture guards caught and what they only nagged about

> Status (2026-10-21): a measurement, on `dev` at `086ef777`, over the CI runs of
> 2026-09-26 .. 2026-10-20. A dated record, not a normative claim about the
> current tree. Every number below was produced by `tools/guard-triage.py` over
> the CI job logs of that window; the classification of each rule is in
> `tools/guard-triage-classes.json`, next to the firings it was decided from.

Issue #1726 asks one question that cannot be answered by reading the guards:
**which guard caught something real, and which one only fired because the code
changed shape?** Without the answer, shrinking the guard set is a guess: cut the
wrong rule and a hole opens silently; cut the right one and nothing happens at
all.

This page is that answer, measured over the recent CI history, plus the tool
that keeps measuring it.

## The short answer

| | firings | share |
|---|---:|---:|
| **defect** (the rule caught something broken) | 28 | 53.8% |
| **style** (the rule is bound to the form of the tree) | 8 | 15.4% |
| **noise** (the rule fired on its own allowed code or its own table) | 16 | 30.8% |
| **unknown** (not classified) | 0 | 0% |
| **total** | **52** | |

52 firings, 35 rules, 43 CI runs, 22 days. Ten of the 35 rules fired **only**
noise. Those ten are the Tier C candidate list for #1727, and each one has a
named repair in the *Ratchet* section below.

The ratio is the headline, and it is not the ratio the epic guessed. More than
half of what the architecture guards caught in this window was a real defect,
which is the argument for keeping the set. A third of it was noise, and every
piece of that third is a rule with a **hard-coded number or a table of its
own** firing because a legitimate change moved that number.

## The corpus, and where it came from

The population is the CI runs of the `ci` workflow, 2026-09-26 to 2026-10-20.

| | |
|---|---:|
| `ci` runs in the window | 2027 |
| runs that failed | 490 |
| failed `build` and `test (core)` jobs read | 317 |
| runs with at least one architecture-guard firing | 43 |
| distinct firings (one run, one rule, one assertion) | 52 |
| raw observations before deduplication | 219 |

Two things about the shape of this corpus matter for reading the numbers.

**A firing is counted once per run, not once per log.** The architecture suite
runs twice in a failed run: once in the `build` job (the in-build
`HarborArchitectureGate` target in `Directory.Build.props:563`) and once in the
`core` shard. The build job prints the suite output three times (the exec
task, the MSBuild error, the build tool's own report). 219 raw observations
collapse into 52 firings. Counting the raw ones would have multiplied every
number on this page by four.

**A green run has no firing, by definition.** Only failed runs were read. A
guard that fired in a run that later went green still counts: the firing
happened, and the fix is a separate commit.

The corpus was harvested with the GitHub API and the `gh` CLI:

```bash
# every `ci` run the API exposes for the window
gh api "repos/refusedguy/Harbor-Harness/actions/workflows/347118936/runs?per_page=100&page=N&branch=dev"
gh api "repos/refusedguy/Harbor-Harness/actions/workflows/347118936/runs?per_page=100&page=N&event=pull_request"

# the failed jobs of the failed runs
gh api "repos/refusedguy/Harbor-Harness/actions/runs/<run-id>/jobs?per_page=100"

# the log of one job
gh api "repos/refusedguy/Harbor-Harness/actions/jobs/<job-id>/logs" --allow-escape-sequences > <run>-<job>.log
```

Three queries were needed because the workflow-runs endpoint returns at most
1000 runs per query. `branch=dev` reaches back to 2026-09-26; `event=pull_request`
and `event=push` add the branch runs. The union is the 2027 runs above.

The logs are the primary source on purpose: the TRX artifacts are retained for
7 days, and the `Upload TRX test results` step only matches `TestResults/**/*.trx`
at the workspace root, while the test host has also written the TRX next to the
assembly (`tests/<project>/bin/Release/net10.0/TestResults/<shard>/`). Job logs
survive the whole window, which is what a retrospective needs.

## The tool

`tools/guard-triage.py` reads what a run already produced and writes down what
every guard firing meant.

```bash
# over a CI job log
python3 tools/guard-triage.py --log run-12345.log --repo-root . --out firings.json

# over the TRX of a shard
python3 tools/guard-triage.py --trx "TestResults/core/*.trx" --repo-root . --out firings.json

# the self-test: the tool still parses a failure it is given
python3 tools/guard-triage.py --self-test
```

It writes three things: a machine-readable JSON report (every firing, with the
rule, the guard file and line, the evidence, the class and the ratchet), a
Markdown table, and a short table for a CI job summary. The JSON carries the
schema id `harbor.guard-triage/1` and the registry carries
`harbor.guard-triage/registry/1`, so a consumer can tell which shape it is
looking at.

### The four classes

| class | the test |
|---|---|
| `defect` | the code the rule named violates an invariant the repository states, and the code was wrong |
| `style` | the rule is bound to the **form** of the tree (a count, a name, a README section, a text anchor on a declaration); the firing is a chore or a preference |
| `noise` | the rule fired on code the repository **explicitly allows**, or on the rule's own table: a stale ledger row, a stale exemption, a measured baseline that moved, a control whose hard-coded expectation no longer matches reality |
| `unknown` | not classified. This is the worklist, not a verdict |

`unknown` is a first-class answer. A report where everything is `unknown` says
nothing, and the tool prints the share instead of hiding it. On this window it
is 0, because every rule that fired has an entry in the registry.

### Where the class comes from

In this order:

1. a per-firing entry in `tools/guard-triage-classes.json` (matched by rule and
   by a substring of the assertion), for a rule whose firings mean different
   things in different situations;
2. a per-rule entry in the same file: the class of the rule, with the reason
   and the concrete firings it was decided from;
3. two mechanical detectors, and only two, because only two of these situations
   are decidable from the text alone:
   - **ledger-retirement**: the guard's own source lists the fired-on path and
     the assertion says the allowance is stale. The rule fired on its own
     allowance. That is noise by definition.
   - **allowance-listed**: the fired-on path is named in an allowance file the
     rule declares. The repository allows that code, so the rule told us
     nothing new.
4. `unknown`.

The two detectors can only ever produce `noise`, the class that means "the rule
told us nothing new". Everything else is a human decision recorded in a file,
with the firings it was derived from.

### The emission mechanism, in CI

The `test` job of `.github/workflows/ci.yml` runs the triage over the shard's
TRX after the shard, writes `guard-firings-core.json`, and uploads it as a
30-day artifact. The step is report-only: the tool exits 0 on a missing TRX
set, the way `tools/test-measurements.py` does, so a shard that found nothing
reads as "nothing fired" and not as a failure. The floor
(`--min-guard-files 100`) is the one thing that can make the step red, and it
is there so that a renamed guard project cannot make the tool report an empty
tree.

The old output of the guards is untouched: the step adds a table to the job
summary and a JSON file to the artifacts, and changes nothing a human reads
in the log.

The tool's own failure path is tested in `docs.yml`'s `selftest` job, with the
other python tools: a reporter that quietly stops reading a failure is green
on every run and reports nothing.

## The rules, one by one

`firings` counts the runs in which the rule fired. `distinct` counts the
different assertions it produced (a rule can fire with a different number in
the message each time).

| rule | guard | firings | class | ratchet |
|---|---|---:|---|---|
| `AcceptedStopCensusRule.TerminalToolEndEvents_StillRideTerminalToken` | `AcceptedStopCensusRule.cs:91` | 1 | defect | keep |
| `CellForgeEngineAbsenceAxisTests.NoMaybeMemberIsReadWithAStructUnawareForm` | `CellForgeEngineAbsenceAxisTests.cs:236` | 1 | defect | keep |
| `CellForgeWidgetAxisRules.PerimeterSettlesAgainstTheFilesThatNameTheSeam` | `CellForgeWidgetAxisRules.cs:1201` | 1 | defect | keep |
| `CellForgeWidgetAxisRules.WidgetAxis_TheShippedLayoutTreeHasNoSecondHome` | `CellForgeWidgetAxisRules.cs:752` | 1 | defect | keep |
| `CellForgeWidgetStateRules.ClassifiedProperties_StillExistAndAreStillNullableReferences` | `CellForgeWidgetStateRules.cs:179` | 1 | noise | ledger-edit |
| `CellForgeWidgetStateRules.EveryNullablePublicWidgetProperty_IsClassified` | `CellForgeWidgetStateRules.cs:149` | 1 | defect | keep |
| `ContribBoundaryNameRule.CrossBoundaryNames_MatchTheMeasuredBaseline` | `ContribBoundaryNameRule.cs:368` | 1 | noise | fix-the-rule |
| `DefaultAgentSingleSourceTests.CoreFallbackConstant_IsDerivedFromTheOneAgentNameConstant` | `DefaultAgentSingleSourceTests.cs:349` | 1 | style | fix-the-rule |
| `EnforcerIntegrityTests.DocumentedExceptions_AreScopedToNamedFiles` | `EnforcerIntegrityTests.cs:461` | 1 | defect | keep |
| `EnforcerIntegrityTests.Matrix_AllowedEntries_AreLive` | `EnforcerIntegrityTests.cs:278` | 2 | noise | ledger-edit |
| `FullLayerMatrixTests.EverySrcAssembly_ReferenceSet_MatchesMatrix` | `FullLayerMatrixTests.cs:725` | 3 | defect | keep |
| `FullLayerMatrixTests.Matrix_CoversExactlyTheSrcInventory` | `FullLayerMatrixTests.cs:856` | 1 | defect | keep |
| `KeyGateFamilyRules.TableCoversEveryRoutedSiteInTheProductTree` | `KeyGateFamilyRules.cs:728` | 1 | defect | keep |
| `MaybeAbsenceTests.GuardedProjects_DeclareNoAbsenceViaNullableOutParameter` | `MaybeAbsenceTests.cs:225` | 1 | defect | keep |
| `McpHttpClientOwnershipRules.Assert_TheClientHasExactlyOneOwnerAndItIsNotPublic` | `McpHttpClientOwnershipRules.cs:160` | 1 | defect | keep |
| `ModelRefSingleParserTests.EveryAdHocCut_IsEitherTheContractOrInventoryed` | `ModelRefSingleParserTests.cs:414` | 2 | defect | keep |
| `ModelRefSingleParserTests.EveryInventoryEntry_StillNamesAFileThatStillCuts` | `ModelRefSingleParserTests.cs:437` | 1 | noise | ledger-edit |
| `PilotRoslynAnalyzerTests.Pilot_Harch001_Reports_Every_Planted_Dynamic_Code_Capability` | `PilotRoslynAnalyzerTests.cs:184` | 1 | defect | keep |
| `ReadmeCoverageTests.Assert_NoNewProjectLacksAReadme` | `ReadmeCoverageTests.cs:114` | 3 | style | keep |
| `ReadmeCoverageTests.Assert_PackableReadmesHaveRequiredSections` | `ReadmeCoverageTests.cs:136` | 4 | style | keep |
| `ReflectionConventionRule.NonVacuity_The_Compiler_Bans_A_Synthetic_Violation` | `ReflectionConventionRule.cs:347` | 1 | defect | keep |
| `ReflectionConventionRule.The_Ban_File_Covers_Every_Public_Assembly_Load_Overload` | `ReflectionConventionRule.cs:314` | 1 | noise | fix-the-rule |
| `ReleaseBuildConfigurationRule.EveryProjectTheReleaseBuildPullsIn_IsBuiltInTheSolutionConfiguration` | `ReleaseBuildConfigurationRule.cs:528` | 1 | defect | keep |
| `ReleaseBuildConfigurationRule.NoReleaseOutput_HoldsACopyOfADebugBuild` | `ReleaseBuildConfigurationRule.cs:804` | 2 | defect | keep |
| `ResultFailureConversionTests.GuardedTrees_DoNotHandRollFailureConversion` | `ResultFailureConversionTests.cs:156` | 1 | defect | keep |
| `ResultMaybeSignatureTests.Assert_NoResultOfNullableValueTypeUnderSrc` | `ResultMaybeSignatureTests.cs:86` | 1 | defect | keep |
| `ResultTryAdoptionTests.HandRolledTryCatchToResult_UsesResultTry` | `ResultTryAdoptionTests.cs:151` | 1 | defect | keep |
| `ResultTryAdoptionTests.KnownExemptSites_AreStillAccurate` | `ResultTryAdoptionTests.cs:177` | 1 | noise | ledger-edit |
| `ScanUniverseRule.BlindProjects_MatchTheMeasuredBaseline` | `ScanUniverseRule.cs:565` | 5 | noise | fix-the-rule |
| `SharedSourceLinkRules.The_Link_Inventory_Is_Not_Empty` | `SharedSourceLinkRules.cs:207` | 2 | noise | fix-the-rule |
| `TestShardZeroTestGuardRules.TheGuardFlagIsActuallyReadByTheRunStep` | `TestShardZeroTestGuardRules.cs:170` | 3 | defect | keep |
| `TokenTrackingRatchet.Control_TheSplitLandingIsReportedAsProgressNotAsARegression` | `TokenTrackingRatchet.cs:709` | 1 | noise | fix-the-rule |
| `TuiReadLineContractRules.RendererTrees_AllExist` | `TuiReadLineContractRules.cs:267` | 1 | noise | fix-the-rule |
| `UnguardedResultReadAnalyzerTests.Analyzer_Stays_Silent_On_The_Planted_Guarded_Reads` | `UnguardedResultReadAnalyzerTests.cs:358` | 1 | defect | keep |
| `UnguardedResultReadAnalyzerTests.The_Analyzer_Sees_Every_Site_The_Text_Rule_Sees` | `UnguardedResultReadAnalyzerTests.cs:376` | 1 | defect | keep |

Every row above is backed by a classified firing in
`tools/guard-triage-classes.json`, and every entry there names the run it came
from. The review below spells out the ten noise rules and a sample of the
defects.

## The review: ten noise rules, one firing each

These are the ten rules whose firings in this window were all noise. Each entry
says what fired, where, and what the repair is.

### 1. `ScanUniverseRule.BlindProjects_MatchTheMeasuredBaseline` (5 firings)

The rule's own header says it is "a MEASUREMENT, not a fix". It pins the
measured count of non-product projects that reference `src/` as a literal
string, and compares the walk against it.

Every firing was a count that moved by one because a test project was added:

| run | branch | expected |
|---|---|---|
| 39117642149 | `feat/1616-changetracking` | `apps=2 \| contrib=14 \| samples=5 \| tests=32 \| tools=1` |
| 39365793623 | `decomp/1624-session-context` | `... \| tests=33 \| tools=1` |
| 39380697456 | `test/1772-1773-testkit-helpers` | `... \| tests=34 \| tools=1` |
| 39416159291 | `test/e2e-framework-cleanup` | `... \| tests=36 \| tools=1` |

The product graph did not change. The count of test projects did, which is the
normal cost of splitting a test tree.

**Ratchet: fix-the-rule.** Derive the string from the walk instead of spelling
it, or drop the equality and keep the measurement as a report. The literal is
the whole defect: it turns every new test project into a red build.

### 2. `SharedSourceLinkRules.The_Link_Inventory_Is_Not_Empty` (2 firings)

The rule pins the number of `<Compile Include>` link items (14, then 16) and
says in its own message that "a different count means a link was added or
removed and R2 is judging a set that is not the build's".

- run 38516394743 (`perf/1102-requestbuild-args`): "Expected to be 14"
- run 38854377103 (`nightshift/1563-utf8jsonreader-helpers`): "Expected to be 16"

Both times a shared-source file was added on purpose.

**Ratchet: fix-the-rule.** Count the `<Compile Include>` items in the build and
compare the count with the count, not with a number written down months ago.

### 3. `ModelRefSingleParserTests.EveryInventoryEntry_StillNamesAFileThatStillCuts` (1 firing)

The rule is a ledger with a liveness check: an inventory entry that no longer
describes an ad-hoc cut is stale, and the rule asks for the entry to be
deleted.

- run 39114828129 (`decomp/1615-permissions-subdomain`): "inventory entry
  `src/Harbor.Application/Configuration/OnboardingWizard.cs` no longer contains
  an ad-hoc cut, so it is no longer an exception"

The code is fine. The row is not.

**Ratchet: ledger-edit.** Delete the row, with the reason, the way
`KnownAdHocCutSites` rows carry one today.

### 4. `CellForgeWidgetStateRules.ClassifiedProperties_StillExistAndAreStillNullableReferences` (1 firing)

Same shape: an exemption table, and a row whose property was renamed or
removed.

- run 39391713493 (`perf/1140-frame-snapshot`): "stale: `[SessionTabStripPanel.Dispatch]`"

**Ratchet: ledger-edit.** Drop the row in the same commit that removes or
renames the property.

### 5. `TokenTrackingRatchet.Control_TheSplitLandingIsReportedAsProgressNotAsARegression` (1 firing)

A **control** test whose expectation is a hard-coded list of seven member
names.

- run 38695392450 (`fix/1106-tracker-staleness-invalidate`): "all seven must
  show up as improvements ... but collection has 8 items but expected 7"

The ratchet did not rot; the control's table did. The aggregate now declares
eight members, so the control's list of seven is stale.

**Ratchet: fix-the-rule.** Derive the expected list from the same source the
comparator reads, so the control cannot outlive the thing it controls.

### 6. `TuiReadLineContractRules.RendererTrees_AllExist` (1 firing)

The rule pins the number of renderer families and says in its own message that
"a new family must raise this number in the same commit that names it".

- run 39405134739 (`refactor/remove-nickconsoleex`): "Expected to be 6 ... but
  found 5", the named trees being `src/Harbor.Tui.CellForge`,
  `src/Harbor.Tui.CellForge.Engine`, `src/Harbor.Tui.AnsiPlain`,
  `src/Harbor.Tui.Notifications`

This is the NickConsoleEx renderer leaving the product (ADR-019). A legitimate
refactor, and the rule fired on the number it had written down.

**Ratchet: fix-the-rule.** Derive the count from the list of trees, the way the
rule already derives the names.

### 7. `EnforcerIntegrityTests.Matrix_AllowedEntries_AreLive` (2 firings)

The layer matrix's allowed set is a permission table. This rule is its
liveness check, and it fired on the table itself.

- run 38866609300 (`refactor/1613-hookrunner-extract`) and run 39122010120
  (`refactor/agent-runtime-1623`): "Harbor.Hooks -> Harbor.Abstractions:
  permitted by the matrix but the assembly does not reference it - stale
  allowed-set entry, remove it (or add a DocumentedException if the edge is
  real)"

**Ratchet: ledger-edit.** Remove the stale allowed edge. This is the shape
`ScanBaseline` rows already have in `tests/Harbor.Architecture.Tests/ScanRule.cs`:
the key, and the reason as a value.

### 8. `ResultTryAdoptionTests.KnownExemptSites_AreStillAccurate` (1 firing)

The exemption table of the `Result.Try` rule, and its liveness check.

- run 36536630732 (`dev`): "`src/Harbor.Lsp/LspManager.cs:240` - catch no
  longer constructs Result.Failure (catch mutates `_unavailable` under a lock;
  try assigns an outer local - needs a control-flow refactor)"

The recorded reason no longer matches the site, so the row has to go.

**Ratchet: ledger-edit.** Delete the row, or re-record the reason.

### 9. `ContribBoundaryNameRule.CrossBoundaryNames_MatchTheMeasuredBaseline` (1 firing)

"a MEASUREMENT, not a prohibition", by its own header. It records the simple
type names declared on both sides of the `contrib/` boundary, which CI does not
build.

- run 39405134739 (`refactor/remove-nickconsoleex`): the named set changed when
  the renderer moved out of the product.

**Ratchet: fix-the-rule.** Derive the set from the walk, or keep it as a
report with no equality assertion.

### 10. `ReflectionConventionRule.The_Ban_File_Covers_Every_Public_Assembly_Load_Overload` (1 firing)

The rule reads the framework's public `Assembly.Load` surface by reflection and
fails when the read is empty, because "an empty reading of it would make the
comparison below vacuous".

- run 39395794130 (`refactor/1844-banned-api-rule`): "but received empty
  collection"

The rule fired on its own instrument. The branch is the migration of the ban to
the compiler (`BannedApiAnalyzers`), and the reflection-based read is what the
migration emptied. Nothing in the product is wrong.

**Ratchet: fix-the-rule.** When the ban moves to the compiler, the reflection
probe has to go with it, or it has to fail with the reason it found nothing
rather than with "empty collection".

## The review: the defects, with the file and the line

The other side of the table. Each of these is a rule that caught something
broken, with the place it caught it.

| run | branch | rule | what the rule caught |
|---|---|---|---|
| 38860059916 | `rec1-1559-identifiers-records` | `DefaultAgentSingleSourceTests` | **not a defect.** The rule locates the declaration by a text anchor (`class AgentName`, at `DefaultAgentSingleSourceTests.cs:479` in that commit) and reads the value with a regex. The refactor that turned `AgentName` into a record moved the declaration out of the anchor's reach, so the rule reported a missing declaration that was there: `src/Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs:506` (`public sealed record AgentName`) and `:529` (`public const string Fallback = "code";`). Classified **style**, and the reason it is worth reading: the rule is right about the invariant and wrong about the way it finds it. |
| 38864383040 | `refactor-1507-tooldispatcher-retrypolicy` | `AcceptedStopCensusRule` | The dispatcher publishes terminal `ToolExecutionEndEvent`s five times where the census found seven. The floor dropped: the terminal surface of the dispatcher moved. |
| 38862719862 | `fix/1546-webfetch-ssrf-result-railway` | `ResultFailureConversionTests` | `src/Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs:334` re-types a failure by hand (`Result.Failure<K>(other.Error)`), and that member throws `InvalidOperationException` on a success. |
| 39122311873 | `fix/1536-sse-parser-result-chain` | `ResultMaybeSignatureTests` | `src/Harbor.Tools.Builtin/Tools/Mcp/McpSseParser.cs:88` declares `Result<JsonElement?>`, the banned shape that hides absence inside success. |
| 36534957325 | `dev` | `ResultTryAdoptionTests` | `src/Harbor.Lsp/LspManager.cs:307` hand-rolls `catch(Exception) -> Result.Failure`, a second implementation of `Result.Try`. |
| 39002619447 | `feat/telemetry-visible-bounded` | `ModelRefSingleParserTests` | `src/Harbor.Application/Configuration/OnboardingPicks.cs:56` cuts a slash-separated string by hand, a second parser of `provider/model`. |
| 38357908793 | `feat/compose-screen-builder-1201` | `CellForgeWidgetAxisRules` | `src/Harbor.Tui.CellForge/Chat/Rendering/ScreenBuilder.cs:102` builds a `LayoutTree` outside the recorded builder set: a second home for the chrome. |
| 38264879229 | `feat/1188-t6-bindings` | `KeyGateFamilyRules` | `src/Harbor.Tui.CellForge.Engine/Input/BindingsMap.cs` routes keys and is absent from the family table, so it never declared its family. |
| 39376347667 | `fix/1758-registration-honesty` | `EnforcerIntegrityTests` | `src/Harbor.Plugins.Registration/RecordingPluginLoadHost.cs` reaches into `Harbor.Terminal.Abstractions`, which only `PluginRegistrar.cs` may reach. |
| 39387894338 | `refactor/1835-mcp-composition` | `McpHttpClientOwnershipRules` | The remote-MCP `HttpClient` has two owners (`McpHttpClientFactory` and `McpOAuthHandler`), which is the duplication #1567 removed coming back. |
| 39407510139 | `fix/1921-debug-in-release` | `ReleaseBuildConfigurationRule` | A `bin/Release` output is byte-identical to a project's own `bin/Debug` output: the #1921 defect, measured. |
| 39436392533 | `ci/submodule-checkouts-ci-yml` | `TestShardZeroTestGuardRules` | The CI run step had lost `--zero-tests-policy strict`, so a shard in which every test was skipped would have read as green. |
| 39395794130 | `refactor/1844-banned-api-rule` | `ReflectionConventionRule` | The compiler ban did not report a planted `System.Reflection.Assembly.Load`, so the ban file has a hole the text rule cannot see. |
| 38371707686 | `stealR13-tableblock-navstate` | `CellForgeEngineAbsenceAxisTests` | `src/Harbor.Tui.CellForge/Chat/Widgets/TableBlock.cs:319` reads a `Maybe` member in a form that does not compile for a struct. |
| 38685128982 | `perf/1095-pipeline-reg` | `MaybeAbsenceTests` | `src/Harbor.Hosting/Rendering/RendererPipeline.cs:93` declares absence through a nullable out-parameter. |
| 39386000544 | `fix/1831-dead-public-surface` | `CellForgeWidgetAxisRules` | `CHANGELOG.md` names the view seam and is in neither the teaching nor the describing list, so the guard was green over a document that teaches a dead seam. |
| 39414753478 | `arch/1868-result-read-analyzer` | `UnguardedResultReadAnalyzerTests` | `src/Harbor.Tools.Builtin/Tools/Mcp/McpOAuthHandler.cs` is flagged by the analyzer and known to neither the text rule nor a documented exemption: the migration would have gone red on a site nobody reviewed. |
| 39114828129 | `decomp/1615-permissions-subdomain` | `FullLayerMatrixTests` | `Harbor.Registries` had no matrix row, so no layer rule constrained that assembly. |

## The ratchet

Every rule with a proven noise share gets one of four repairs, and the repair is
in the registry next to the class, so it cannot drift apart from it.

| repair | what it means | rules |
|---|---|---|
| `keep` | the rule is right and the code was wrong | 24 rules |
| `fix-the-rule` | the intent is right, the instrument is bound to form: derive the value, or anchor on a symbol instead of on a literal | `ScanUniverseRule`, `SharedSourceLinkRules`, `TokenTrackingRatchet`, `TuiReadLineContractRules`, `ContribBoundaryNameRule`, `ReflectionConventionRule`, `DefaultAgentSingleSourceTests` |
| `ledger-edit` | the rule IS a ledger: the row has to change, and the change carries a reason | `ModelRefSingleParserTests`, `CellForgeWidgetStateRules`, `EnforcerIntegrityTests`, `ResultTryAdoptionTests` |
| `retire-the-rule` | the rule measures and enforces nothing | (none yet; see below) |

The two `fix-the-rule` shapes are the same shape the epic calls Tier B-2, and
they are the cheapest repairs on this list:

- **a hard-coded number next to the thing it counts.** `ScanUniverseRule`,
  `SharedSourceLinkRules`, `TuiReadLineContractRules` and the
  `TokenTrackingRatchet` control all pin a count and compare it to a literal.
  The count is already derivable from the walk the rule performs. Deriving it
  removes the firing without removing the check.
- **a text anchor on a declaration.** `DefaultAgentSingleSourceTests` looks
  for the literal `class AgentName` (or `record AgentName`) and then reads a
  value with a regex. The refactor that turned the type into a record moved
  the declaration out of the anchor's reach, and the rule reported a missing
  constant that was present. The anchor has to become the symbol.

The ledger repairs use the shape the repository already has:
`tests/Harbor.Architecture.Tests/ScanRule.cs` carries every tolerated row as a
`ScanBaseline` with the reason as a value, `ExemptionReason.cs` is the one
check that a reason is a reason, and `tools/test-package-exceptions.txt` is the
same idea for a Python gate. A stale ledger row is deleted in the same commit
that made it stale, and the deletion is the record.

### The Tier C candidates

Ten rules fired only noise in this window. Two of them say so in their own
header: `ScanUniverseRule` is "a MEASUREMENT, not a fix" and
`ContribBoundaryNameRule` is "a MEASUREMENT, not a prohibition". They are the
clearest Tier C candidates (#1727), with one caveat: they carry information
nobody else carries, so retiring them means losing the measurement. The
cheaper repair is to derive the value and keep the measurement, and that is
what `fix-the-rule` says. The owner decision belongs to #1727, and this page
gives it the numbers.

The other eight are rules with a job, whose instrument is wrong. They are not
Tier C material. They are Tier B-2 material, and the epic's own words apply:
"the guarantee stops being independent of what it guarantees".

## What this does not measure

Stated plainly, because a measurement that hides its edges is a measurement
nobody can use:

- **Only the `ci` workflow.** `test-os.yml`, `docs.yml`, `beta.yml` and the
  release workflows were not harvested. The `ci` workflow is where the
  architecture suite runs, so the arch-guard population is covered, but a
  guard that only ever runs elsewhere is not in these numbers.
- **Only the architecture suite.** The tool triages any test project, and it
  was run with `--project Harbor.Architecture.Tests`. The other guards in the
  tree (the other 37 test projects) are not in the population.
- **Only failed runs.** A green run has no firing. A firing in a run that was
  later re-run green is still counted, once, in the run where it happened.
- **Only what the API exposes.** The workflow-runs endpoint returns at most
  1000 runs per query, and three queries were needed to cover the window. Runs
  older than 2026-09-26 are reachable only through the per-branch queries, and
  the per-branch history is not complete for the pull-request runs.
- **Only the assertion text.** The class of a firing is decided from the
  message the rule produced and from the rule's own source, not from the diff
  that caused it. Where the two disagree, the registry entry says so and names
  the run, so the disagreement is reviewable.
- **The corpus is 22 days.** The numbers move as the window moves. The tool is
  the durable part: it re-measures any window in one command.

## Related

- #1724, the epic this issue belongs to.
- #1725, the audit of the 110 rules into tiers A/B/C. This page is the metric
  that audit needs.
- #1727, the Tier C removal. The ten rules above are its input.
- #1086, the `ScanRule` engine. Every `fix-the-rule` repair on this list is a
  job that engine can take.
- [tools/guard-triage-classes.json](../tools/guard-triage-classes.json), the
  registry: one entry per rule, with the firings each decision was made from.
