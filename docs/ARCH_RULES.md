# Architecture rules — the classified set

> Status: normative for the CLASSIFICATION below. The census is
> measured against the tree this document was written on; the machine-
> readable form is
> [`tests/Harbor.Architecture.Tests/rule-tiers.json`]
> (../tests/Harbor.Architecture.Tests/rule-tiers.json), and it is the file
> a future gate reads.

## 1. What this document is for

The architecture rule set grew to 160 guards. Nobody could say which of
them had ever caught anything, and nobody could say which of them fire
on a change nobody made. #1724 wants to shrink the set, and it cannot
do that safely without this census: cutting a guard that caught a real
defect is a silent hole, and cutting one that only ever fired on a
legitimate refactor is pure win.

So every guard is classified here, and the classification is written
down rather than remembered:

| tier | what it means | count |
|---|---|---:|
| **A** | catches a defect and is cheap | 76 |
| **B** | catches a defect but nags | 66 |
| **C** | catches the tree's shape | 18 |

| verdict | count |
|---|---:|
| defect | 130 |
| refactoring | 12 |
| shape | 18 |

| false-positive cost | count |
|---|---:|
| low | 94 |
| medium | 58 |
| high | 8 |

## 2. How a guard was counted, and how it was read

**A guard is a FILE under `tests/Harbor.Architecture.Tests/` that
enforces an invariant**: the 129 files named `*Rule.cs` / `*Rules.cs`
minus the shared matcher `ScanRule.cs`, the 2 ratchet files, and the
29 files named `*Tests.cs` that hold a guard rather than a control over
the guard machinery. That is **160 guard files**.
The 21 files that are not guards are listed with the reason in the JSON.

**A tier is a reading of the guard's own source**, and three facts
decide it:

1. **What the rule reads.** A rule that reads a project graph, the IL
   of a method, or the DECLARATION of a type cannot fire on a refactor
   that keeps the invariant. A rule that reads the TEXT of a file can.
   That is the whole difference between tier A and tier B.
2. **What the rule claims.** A rule that MEASURES the tree (a census, a
   ratchet, a baseline) claims no defect at all. It holds a number, and
   a number is the tree's shape. That is tier C.
3. **What the firings said.** Where
   [`tools/guard-triage-classes.json`](../tools/guard-triage-classes.json)
   has a verdict for a guard, that verdict wins over the reading, because
   it was decided FROM firings rather than from the source.

**A verdict is one of three**: `defect` (the subject is an invariant
that was broken once), `refactor` (the subject is a shape that a
legitimate change moves), `shape` (the rule measures; neither of the
two).

**The false-positive cost is the price of the mechanism**: `low` means
a refactor that keeps the invariant cannot fire the rule, `medium` means
it can, but only by moving a declaration, `high` means the rule binds
to the text of a file, so a reformat fires it.

## 3. The census

Every row: the guard, what it checks, the tier, the verdict, the cost
of a false positive, and the symbol it should anchor on instead of the
text it reads (`—` when the rule already reads a symbol).

<!-- check-doc-cites: allow-unwired TypeFilterMiddleware — the census quotes the guard's own header, and that header names a type the product deleted in #478 on purpose: TypeFilterRegistrationTests exists to keep it unregistered, so declared-and-nothing-constructs-it is the state the guard enforces. -->

| guard | what it checks | tier | verdict | FP cost | semantic anchor |
|---|---|---|---|---|---|
| `AbstractionsNamespaceOwnershipRules` | The layers are declared but the NAMES do not reflect them. | **A** | defect | low | — |
| `AbstractionsSplitLayerRules` | layer-dependency rules for the Harbor.Abstractions contract split (F1 decoupling, sprint 5v2). | **A** | defect | low | — |
| `AotBlockDemotionRules` | "the AOT block claims analysis passes cleanly while demoting IL2026 to a warning". | **A** | defect | low | — |
| `AvaloniaFileTreeWalkRules` | source-level guard for issue #492: `MainViewModel` owned a recursive filesystem scanner (`Directory.GetDirectories` / `Directory.GetFiles` / `new Dir… | **A** | defect | low | — |
| `AvaloniaTerminalPaneSpawnRules` | source-level guard for issue #672: TerminalPaneViewModel forked a real interactive shell (`$SHELL -i`, full inherited environment) straight out of a… | **A** | defect | low | — |
| `AvaloniaTextFileIoRules` | source-level guard for issue #934: `CodeEditorViewModel` read and wrote the file the user opened straight from the view-model, with no seam: `File.Ex… | **A** | defect | low | — |
| `BuildGraphCoverageRule` | #1005 reported FOUR csproj files that CI never builds, and supported that with a grep for each project's path over `*.csproj`: apps/Harbor.App.Avalon… | **A** | defect | low | — |
| `CellForgeDiffEncoderBoundaryRules` | boundary between the portable cell-diff protocol and the CellForge engine's frame (issue #1519). | **A** | defect | low | — |
| `CellForgeEngineAbsenceAxisTests` | pins WHICH of the two absence axes each public member of src/Harbor.Tui.CellForge.Engine is on. | **A** | defect | low | — |
| `CellForgeEngineAtomicityRules` | step 1; CLOSED by #436. | **A** | defect | low | — |
| `CellForgeEngineCseOwnershipTests` | the engine's CSharpFunctionalExtensions reachability has no owner yet, and no prose keeps it that way. | **A** | defect | low | — |
| `CellForgeEngineEnterPolicyParityRules` | #435 deleted the last `Harbor.Ui.Framework.State` edge out of `Harbor.Tui.CellForge.Engine` by moving the Enter-key DECISION down into the BCL-only l… | **A** | defect | low | — |
| `CellForgeEngineVocabularyParityRules` | #436 turned `Harbor.Tui.CellForge.Engine` into a standalone leaf: zero ProjectReference entries to Harbor assemblies, zero per-file imports of Harbor… | **A** | defect | low | — |
| `CellForgeGraphRules` | the CellForge project graph: which layer each CellForge project sits in | **A** | defect | low | — |
| `ChildProcessPipeDrainRules` | for #884, widened for #908. | **A** | defect | low | — |
| `ConfigSetSingleTableTests` | `set` has ONE key table. | **A** | defect | low | — |
| `ContribIsolationRules` | for the `contrib/` boundary. | **A** | defect | low | — |
| `DeadPublicInterfaceRule` | A public interface with NO implementor and NO consumer is a promise nobody keeps, and nothing in the tree said so. | **A** | defect | low | — |
| `DeadPublicMemberRule` | The member-level half of the dead-surface finding #1831 made: a public member that nothing in the tree references is a promise nobody keeps, and noth… | **A** | defect | low | — |
| `DefaultAgentSingleSourceTests` | "Which agent is the default?" was answered in three places inside src/Harbor.Ui.Framework.Sessions, and one of the three did not answer it at all: Se… | **A** | defect | low | — |
| `DefaultInterfaceMemberRule` | for docs/PATTERNS.md §"Default interface members: a default must fail towards loudly-wrong" (issue #579). | **A** | defect | low | — |
| `DefaultModelSingleSourceTests` | `IdentityConfig` declared the default model id TWICE, seven lines apart, and the two copies did not agree: public const string FallbackModel = "kiloc… | **A** | defect | low | — |
| `DesignSystemLeafTakesNoIoRules` | for #536. | **A** | defect | low | — |
| `DesktopAbstractionsLeafTakesNoIoRules` | for #534. | **A** | defect | low | — |
| `DesktopSharedTakesNoIoRules` | for #535. | **A** | defect | low | — |
| `ExhaustiveUnionSwitchRule` | for docs/PATTERNS.md §"Type unions: a switch is a compile error when the union grows" (issue #578). | **A** | defect | low | — |
| `ExtensionAxisRegistrationRule` | successor to #620's ExtensionAxisFreezeRule. | **A** | defect | low | — |
| `ExtensionAxisRouteCarriageRule` | transport half of the extension-axis contract (issue #1723). | **A** | defect | low | — |
| `FullLayerMatrixTests` | table-driven layer rules for EVERY main-solution Harbor src assembly (ROP-D Z2). | **A** | defect | low | — |
| `HandRolledResultShapeTests` | no hand-rolled Result<T> facades in shipped code. | **A** | defect | low | — |
| `HdsPaletteReachabilityRules` | behind #583: the Avalonia app SHIPS six HDS palettes, and the desktop path could reach two of them. | **A** | defect | low | — |
| `HttpClientReuseRules` | Issue #1854 collected a candidate analyzer package (IDisposableAnalyzers, rule IDISP014 "use a single instance of HttpClient") and REJECTED it: the u… | **A** | defect | low | — |
| `IdeAbandonObserveRule` | IdeAbandonObserveRule — the IDE bridge background prompt run must stay observed. | **A** | defect | low | — |
| `InnerExceptionRules` | Two sites in the product threw a new exception inside a catch and dropped the caught exception on the floor. | **A** | defect | low | — |
| `LayerDependencyTests` | the layer dependency edges, read by reflection over the loaded assemblies | **A** | defect | low | — |
| `LegacyFlatTeaBranchRule` | The `AgentEvent` → UI-state fold has exactly ONE home in this repo, and it is `Harbor.Ui.Framework.State`: `UiStore.Dispatch(AgentEvent)` reduces thr… | **A** | defect | low | — |
| `MapErrorFailureShapeTests` | for the "hand-rolled error re-wrapping → ResultExtensions.MapError" wave, plus the behaviour tests that pin the converted error text. | **A** | defect | low | — |
| `MaybeAbsenceTests` | for the "absence is Maybe<T>" wave. | **A** | defect | low | — |
| `McpHttpClientOwnershipRules` | The remote-MCP transports each assembled their own HttpClient: private HttpClient GetClient() { if (_client is { } existing) return existing; var han… | **A** | defect | low | — |
| `ModelRefSingleParserTests` | `ModelRef.TryParse` (src/Harbor.Abstractions.Contracts/Models/Identifiers/ Identifiers.cs) is, in the words of the declaration it grew out of, "the o… | **A** | defect | low | — |
| `NetArchLayerRules` | NetArchTest-based layer dependency rules. | **A** | defect | low | — |
| `OnboardingFireAndForgetRule` | OnboardingFireAndForgetRule — the onboarding wizard's background work stays observed. | **A** | defect | low | — |
| `OpenAiCompatiblePumpObserveRule` | OpenAiCompatiblePumpObserveRule — the OpenAI-compatible SSE pump stays observed. | **A** | defect | low | — |
| `PanelImplementationBoundaryRules` | #474 was filed from the 2026-09-28 audit: "`PanelContext — IServiceProvider? Services stuffed into a per-frame context bag. | **A** | defect | low | — |
| `PresentationCapabilityRules` | CAPABILITY half of the layer contract for issue 455, "ARCH-5: Presentation layer performs I/O and spawns processes; no capability rules". | **A** | defect | low | — |
| `ProviderAuthSingleAnswerRules` | for #671. | **A** | defect | low | — |
| `ProviderIdDispatchRule` | Issue #560 asked whether Harbor still edits provider-keyed tables when a new LLM provider is added. | **A** | defect | low | — |
| `ProviderPayloadSerializationRules` | "[GoF-A8] Provider payload builders: untyped Dictionary/anonymous types + reflection". | **A** | defect | low | — |
| `PublicAbstractBaseLivenessRules` | #1835 audited the base classes under `src/` and measured twelve `public abstract` types with ZERO product descendants — 788 lines of published base c… | **A** | defect | low | — |
| `ReadOnlyAgentPermissionRule` | #798 is a careful, well-evidenced issue. | **A** | defect | low | — |
| `ReadOnlyAgentShellAllowRule` | for the hole #798 did not report. | **A** | defect | low | — |
| `ReflectionConventionRule` | and the migration of its rule to the compiler (issue #1844). | **A** | defect | low | — |
| `ReleaseBuildConfigurationRule` | CI run 39389070999 built `Harbor.slnx` in Release, and the project-completion timeline showed THREE of the 112 compiled projects producing `bin/Debug… | **A** | defect | low | — |
| `RemoteTransportResultRules` | `IMcpRemoteTransport` used to declare exactly one member: Task<JsonDocument?> RoundTripAsync(...) A nullable document has two meanings and no way to… | **A** | defect | low | — |
| `RendererEventSeamRule` | for docs/PATTERNS.md §"Observer → the event seam" (issue #575). | **A** | defect | low | — |
| `ResultConversionBehaviourTests` | behaviour proof for the ConvertFailure wave. | **A** | defect | low | — |
| `ResultFailureConversionTests` | for the `ConvertFailure` wave. | **A** | defect | low | — |
| `ResultTryAdoptionTests` | ROP wave: `try/catch -> Result` must ride the library, not be hand-rolled. | **A** | defect | low | — |
| `SeamTypeLeakRules` | two seams named a CONCRETE implementation type where the consumer therefore learned the implementation instead of the contract. | **A** | defect | low | — |
| `ServiceLocatorBoundaryRules` | Issue #470 was "[DIP] Two service locators in hot paths": `ToolContext.Services` — declared `IServiceProvider`, passed as `null!` by BOTH production… | **A** | defect | low | — |
| `SessionAgentResolutionTests` | The issue was filed against a tree where "resolve an AgentDefinition, falling back to a default" appeared to be hand-rolled eight times: five inside… | **A** | defect | low | — |
| `SharedSourceLinkRules` | Two `src/` folders hold shared source and produce no assembly: their files are `<Compile Include>`-linked into consumer projects instead of being ref… | **A** | defect | low | — |
| `SlashCommandContextScopeTests` | for the slash-command context bag (issue #483). | **A** | defect | low | — |
| `SlashCommandRouterShapeRule` | the contracts layer must not declare a slash-command ROUTER again, and must not declare the `Result<bool>` shape whose negative case is a routing dec… | **A** | defect | low | — |
| `TelemetryOtlpBoundaryRules` | OTLP exporter stays OUT of the product graph, and the OpenTelemetry SDK stays out of everything except that exporter (#1799). | **A** | defect | low | — |
| `TestKitCompositionRules` | `Harbor.TestKit` was ONE project holding ONE file that needs the UI stack (`UiTestDoubles.cs` — the dispatcher double and the counting view-model stu… | **A** | defect | low | — |
| `TestProjectReferenceRules` | edges BETWEEN test projects, and the two costs a wrong edge carries. | **A** | defect | low | — |
| `ThemeAxisStaysDataRules` | behind #622, which was the *exception* clause to the #555 feature freeze. | **A** | defect | low | — |
| `ThemeStoreSeamRules` | for #668, and for the duplicate half of #479-A6. | **A** | defect | low | — |
| `ToastKindTableRule` | the kind-to-presentation mapping is ONE table, and no product file may write a second one. | **A** | defect | low | — |
| `ToolDispatcherSyncResultRule` | ToolDispatcherSyncResultRule — the abandon paths stay async. | **A** | defect | low | — |
| `TransportRetryOwnershipRules` | "Should this failure be retried, and how long should we wait?" was supposed to have one owner: `Harbor.Application.Resilience.RetryPolicy`. | **A** | defect | low | — |
| `TrimUnsafeReflectionRules` | ("48/S3"): trim-safety of the hot paths, and why "zero allocations" is not the claim. | **A** | defect | low | — |
| `TypeFilterRegistrationTests` | `TypeFilterMiddleware` had a two-state constructor: new TypeFilterMiddleware(logger) -> _allowAll = true new TypeFilterMiddleware(logger, typeof(Foo)… | **A** | defect | low | — |
| `UiSingletonThreadSafetyRules` | for #605 (GoF-A19). | **A** | defect | low | — |
| `UnguardedResultReadRules` | `IConfigStore.LoadAsync` really does fail: `JsonConfigStore.LoadCore` ends in `Result.Try(..., ResultErrors.Message)` and returns `Failure("config.js… | **A** | defect | low | — |
| `AgentStateContractRules` | `IAgent.State` was declared `AgentState State { get; }` — non-nullable — while `DefaultAgent` initialised the backing field with `null!` and first as… | **B** | defect | medium | the IAgent.State declaration, not the text that names it |
| `AnalyzerSeverityScopeRules` | the DI severity model has a per-PATH layer, and nothing owned the gap between that layer and the document which claims to summarise it. | **B** | defect | medium | the severity entry's own path, resolved against the project graph |
| `ApprovalGateWaiterRaceRules` | source-level guard for the #765/#797 flake class: a test that asserts a terminal approval disposition while a WaitForDecisionAsync waiter is parked o… | **B** | refactoring | high | the awaited terminal state, read from the syntax tree |
| `AvaloniaConverterInstanceRules` | source-level guard for issue #1539: a view that declares its own converter instance allocates one per inflation. | **B** | defect | medium | the converter type the view declares |
| `AvaloniaDispatchAsyncVoidRule` | Avalonia headless suite must not write `session.Dispatch(async () => …)`, because that call does not await what it appears to await (#766, generalisi… | **B** | defect | medium | the Dispatch overload the call resolves to |
| `AvaloniaHeadlessSessionHelperRule` | the Avalonia headless suite opens ONE session, through ONE helper. | **B** | refactoring | medium | the helper's declaration and its callers |
| `AvaloniaInitializeComponentShadowRules` | source-level guard against a hand-written `InitializeComponent` shadowing the one Avalonia's source generator emits (issue #973). | **B** | refactoring | high | the InitializeComponent declaration, not a call-shaped string |
| `BackgroundTaskSyncResultRule` | BackgroundTaskSyncResultRule — the background-task drain path stays async. | **B** | defect | medium | the await the drain path does or does not have |
| `BuiltinAgentPickerProjectionRules` | #582 measured "adding a builtin agent = 3 structural edits, one a hardcoded onboarding menu". | **B** | defect | medium | the registry the picker projects from |
| `CellForgeWidgetAxisRules` | "Is the CellForge widget set closed at compile time, and can a plugin add a widget?" #564 measured it and reported the cost of an in-tree widget (4-5… | **B** | defect | medium | the axis the widget declares, not the string that spells it |
| `CellForgeWidgetStateRules` | for the widget-state half of #592. | **B** | defect | medium | the state record the widget owns |
| `CommonConfigContractRules` | for #453. | **B** | defect | medium | the config section declaration |
| `ConfigHalfPairWriteRules` | for #894. | **B** | defect | medium | the write pair, resolved from the declarations |
| `ConfigSectionDocRules` | the guard for the documentation half of the same issue. | **B** | refactoring | high | the section's own declaration, matched by symbol |
| `ContractsArtifactCapabilityRule` | for epic #41 slice B3.1 (#405). | **B** | defect | medium | the capability's own declaration |
| `CostPricedInCoreRules` | "the cost of a session is computed by the headless core; the presentation layer only displays it". | **B** | defect | medium | the pricing call the core makes |
| `CostUnpricedBitRules` | for #942: the core's "I could not price this session" bit must REACH the surface that paints the money. | **B** | defect | medium | the unpriced bit's carrier |
| `DebouncedSettleShapeRule` | settle that waits out the debounce must not BE the debounce, multiplied (#757). | **B** | defect | medium | the settle call's shape |
| `DefaultModelDocClaimTests` | the documentation named the free model as `kilocode/kilo-auto/free` in five places while providers/kilocode.json has declared `tencent/hy3:free` all… | **B** | refactoring | medium | the model id the doc names, matched against the catalog |
| `DiSeverityDemotionRules` | #865: the Avalonia block in `.editorconfig` demoted four DI rules under one sentence that named three of them. | **B** | defect | medium | the severity entry the demotion sets |
| `DiagnosticsClassificationRule` | Deciding "is this line a diagnostic, and how bad is it" is COUNTING, not DRAWING. | **B** | defect | medium | the detector's own declaration list, read by symbol |
| `DiffSurfaceNameCollisionRule` | The line diff has exactly ONE implementation in this repo: `src/Harbor.Ui.Framework.Rendering/Widgets/LineDiff.cs` (Myers O(ND), added by #694). | **B** | defect | medium | the two surfaces' declarations |
| `ExtensionAxisDoorHonestyRule` | door-honesty half of the successor to #620's ExtensionAxisFreezeRule (issue #1719), and the guard #1758 asks for. | **B** | defect | medium | the door's own registration |
| `FeatureFlagWiringRules` | `apps/Harbor.App.Cli/Harbor.App.Cli.csproj` offers two ways to turn NativeAOT on and they are not equivalent. | **B** | defect | medium | the flag's declaration |
| `FileLogSinkOwnershipRule` | A `Microsoft.Extensions.Logging.ILoggerProvider` implementation is a LOGGING PROVIDER: the thing an app registers to get its log lines somewhere. | **B** | defect | medium | the sink type the host registers |
| `GateGlowConsumerReachabilityRule` | The gate-glow pipeline has a producer with a test and a consumer with none, and the missing half is the half the user can see. | **B** | defect | medium | the consumer's declaration |
| `HostingCompositionNullabilityRules` | for the `null!` wave in the composition root (src/Harbor.Hosting), issue #562. | **B** | refactoring | high | the nullable annotation on the declaration |
| `KeyGateFamilyRules` | for #833. | **B** | defect | medium | the key gate's declaration |
| `LayerClaimMatchesMatrixRules` | for the layer-CLAIM class (#895). | **B** | defect | medium | the matrix row the claim names |
| `LayerDocAgreementRule` | `docs/ARCHITECTURE_LAYERS.md` §1 assigned all four IPC assemblies to APPLICATION. | **B** | refactoring | high | the layer graph, compared to the doc by name instead of by line |
| `LayerTableDocAgreementRules` | for #922. | **B** | refactoring | high | the layer table's own rows |
| `LogLevelMnemonicRule` | The 4-character LogLevel mnemonic (TRAC / DBUG / INFO / WARN / ERRO / CRIT / NONE) is a shared rendered string, and a switch that maps `LogLevel` to… | **B** | defect | medium | the mnemonic table's declaration |
| `McpOAuthTokenShapeRules` | `McpOAuthHandler` surfaced ONE lookup — "give me an access token for this server" — through THREE conventions at once: 1. | **B** | defect | medium | the token record's declaration |
| `MegaConstructorDoneStateTests` | done-state guard for issue #486 findings 3–4. | **B** | defect | medium | the constructor's parameter list |
| `ModelRateLabelRules` | for the pricing half of issue #686. | **B** | defect | medium | the rate table's declaration |
| `MoneyCellSingleHomeRules` | for #682: a session's MONEY CELL has ONE shape, and only the two rendering conventions are allowed to write it. | **B** | defect | medium | the cell writer's declaration |
| `OverlayKeyRouteClaimRule` | five doc comments (and four more this file found) describe an input path that no product host uses. | **B** | defect | medium | the route the doc names |
| `PanelKeyRouteReachabilityRule` | (and its parent #812): a key hand-off must be reachable from product code, and the tests that drive an unreachable hand-off are not evidence of anyth… | **B** | defect | medium | the route's declaration |
| `ProcessEnvIsolationRule` | Nine test classes call `Environment.SetEnvironmentVariable`, and not one of them carried a `[NotInParallel]` form of any kind. | **B** | defect | medium | the process-environment write |
| `PromptCacheKeyCoverageRules` | `CachingSystemPromptBuilder` is a memoizing decorator: it answers a repeat context from a hash instead of calling the inner builder, and the only thi… | **B** | defect | medium | the cache-key builder's declaration |
| `PromptClockPurityRule` | . (#814) THE CONVENTION BEING ENFORCED An ISystemPromptBuilder implementation reads NO clock. The prompt is a pure function of its SystemPromptContex… | **B** | defect | medium | the clock call's receiver |
| `PromptSectionPolicyRule` | . THE CONVENTION BEING ENFORCED A prompt-assembly method (one that appends `## ` Markdown section headers into a StringBuilder) must not carry a bare… | **B** | defect | medium | the section's declaration |
| `ProviderCatalogueBudgetRules` | for #685. | **B** | defect | medium | the catalogue's declaration |
| `ProviderModelAbsenceRules` | `SessionFactory.ResolveProviderModelFromConfigAsync` returned `Task<(string? ProviderId, string? ModelId)>`. | **B** | defect | medium | the model's declaration |
| `ReadmeCoverageTests` | per-project README coverage gate. | **B** | refactoring | medium | the project list the README coverage is measured against |
| `RenderInvalidationCoalescingTests` | contract for RENDER invalidation coalescing on the CellForge REPL frame loop (issue #396, epic #47/S5). | **B** | defect | medium | the coalescer's declaration |
| `ReplConstructorCompositionTests` | for the REPL's own wiring (issue #486). | **B** | defect | medium | the constructor's parameter list |
| `ResultMaybeSignatureTests` | for the ROP wave that replaced every `Result<T?>` in the non-test tree with `Result<Maybe<T>>`. | **B** | refactoring | high | the Result<T?> declaration, matched by symbol |
| `SessionForkPortSeamRules` | for #882. | **B** | defect | medium | the port's declaration |
| `SessionStatusFrameReachabilityTests` | tests for #861. | **B** | defect | medium | the projection's declaration |
| `SessionStatusReader` | #687 removed the transcript heuristic from `ChatStreamingPresenter.DeriveStatus` and made the reducer the single source of a session's status. | **B** | defect | medium | the projection's declaration |
| `SessionStatusSourceRule` | `SessionStatus` (idle / working / done / error / aborted) is a DOMAIN FACT decided once, by the core, around the run that establishes it. | **B** | defect | medium | the status source's declaration |
| `SessionStatusTableRule` | `SessionStatus` (idle / working / done / error / aborted) has exactly one rendered form per concern, and both live in ONE file: src/Harbor.Ui.Framewo… | **B** | defect | medium | the label table's declaration |
| `SessionStoreFailureTextParityRules` | #764's claim: "Harbor.Storage.Memory hand-writes the session-store failure texts nine times, and `SessionStoreErrors` is supposed to be their single… | **B** | defect | medium | the failure text each store builds |
| `SlashResultChannelTests` | for the slash-command `Result` channel (issue #603). | **B** | defect | medium | the channel's declaration |
| `TestDiagnosticsOutputRule` | for #1856: a test's diagnostics belong to that test. | **B** | defect | medium | the diagnostic sink's declaration |
| `TestKitLocalDoubleRules` | `Harbor.TestKit` is the one place a shared test double is supposed to live, and it was not: five test projects each kept their OWN set of interface d… | **B** | refactoring | medium | the double's declaration |
| `TestKitSharedCopyReasonRules` | second half. | **B** | defect | medium | the shared double's declaration |
| `TokenCellSingleHomeRules` | for #788: a session's TOKEN CELL is written by more than one surface, and the set of writers is frozen here WITHOUT choosing which notation is canoni… | **B** | defect | medium | the cell writer's declaration |
| `ToolGlyphTableRule` | Issue #680 reported that `ChatViewModelBase` holds a hand-written glyph catalogue for ten builtin tools and rebuilds a structured tool call by re-par… | **B** | defect | medium | the glyph table's declaration |
| `ToolNameListRule` | The issue says "eight hand-maintained lists of tool names, two of them already wrong". | **B** | defect | medium | the catalog declaration the list is compared against |
| `TuiReadLineContractRules` | `ITuiRenderer.ReadLineAsync` used to return `Task<Result<string>>`, and EVERY implementation answered end-of-input with `Result.Success(line ?? strin… | **B** | defect | medium | the read-line contract's declaration |
| `UiConfigDefaultsRule` | Two desktop view-models hand-picked a DEFAULT for a persisted config field that the core already gives a default to, and the two answers disagreed wi… | **B** | defect | medium | the default's declaration |
| `UiFrameworkNullabilityRules` | for the `null!` wave in src/Harbor.Ui.Framework*. | **B** | refactoring | high | the nullable annotation on the declaration |
| `VendoredHtmlSurfaceRules` | source-level guard for issue #878. | **B** | defect | medium | the surface type's declaration |
| `WellKnownToolsRule` | #1564 moved the builtin tool names out of two hundred and ninety-two string literals and into one catalog, `WellKnownTools`. | **B** | defect | medium | the catalog declaration the names are compared against |
| `AcceptedStopCensusRule` | AcceptedStopCensusRule — ratchet for the number 401 remainder, the B2 core. | **C** | shape | low | — |
| `AvaloniaFireAndForgetRules` | RETIRED pattern matcher, kept as a NON-GATING ORACLE. | **C** | shape | low | — |
| `CellForgeMascotMoveOutRules` | Issue #1619 named the mascot as the "trial balloon" for the move-out mechanics: a decoration cluster (MascotDirector + MascotPanel + AmbientMascot, ~… | **C** | shape | low | — |
| `CfeControlPositiveControlTests` | proves the CFE0001 analyzer is not vacuous. | **C** | shape | low | — |
| `CfeValueBaselineTests` | ROP-001 backstop, and the tests that prove the backstop is real. | **C** | shape | low | — |
| `ContribBoundaryNameRule` | a MEASUREMENT, not a prohibition: how many simple type names are declared on BOTH sides of the boundary CI does not build. | **C** | shape | low | — |
| `DemoGifTriggerSurfaceTests` | the demo GIFs are regenerated by a workflow that fires on `push` PATHS, so the A/B question "did anything that can move a pixel change?" is answered… | **C** | shape | low | — |
| `DialogKeyDispatchCensusRule` | + ratchet for #472 item 2 and #473. | **C** | shape | low | — |
| `E2eAvaloniaShardCoverageRules` | arch-rule half of the headless Avalonia E2E process split. | **C** | shape | low | — |
| `ExistenceCheckLivenessRule` | a MEASUREMENT: how many of this project's "this name must exist" checks can actually fail. | **C** | shape | low | — |
| `FatInterfaceShapeRatchet` | for #471, slice 2: the three interfaces the issue names and the fourth it under-counts. | **C** | shape | low | — |
| `OverlayKeyPlaneCensusRule` | + ratchet for issue #812. | **C** | shape | low | — |
| `ProjectKeyedScanRule` | a MEASUREMENT, not a fix: which directories under `src/` hold compiled product code and are not a project, because they have no `.csproj` to be found… | **C** | shape | low | — |
| `ScanUniverseRule` | a MEASUREMENT, not a fix: what is in the reflection scan universe of Harbor.Architecture.Tests, and — the part that matters more — what is NOT in it. | **C** | shape | low | — |
| `ScanVisibilityRule` | a MEASUREMENT, not a fix: which top-level repository trees the shared scan helper can read, and which it silently returns nothing for. | **C** | shape | low | — |
| `SharedSourceTreeSnapshotTests` | for the shared source snapshot (issue #1791). | **C** | shape | low | — |
| `TestShardZeroTestGuardRules` | the `test` job's shard matrix states a zero-test guard per shard, and one of the four entries stated the OPPOSITE with nothing recorded next to it. | **C** | shape | low | — |
| `TokenTrackingRatchet` | for #471, slice 1: ITokenTracker. | **C** | shape | low | — |

## 4. The rules worth moving to a semantic check

66 of the 160 guards bind to the TEXT of a file, to a file name, or to a
line number, and each of them names the symbol it should read instead.
That is the work list for #1724, and it is the list with the best
ratio: the rule keeps its subject, and the false positive goes away.

| guard | tier | FP cost | anchor on |
|---|---|---|---|
| `ApprovalGateWaiterRaceRules` | **B** | high | the awaited terminal state, read from the syntax tree |
| `AvaloniaInitializeComponentShadowRules` | **B** | high | the InitializeComponent declaration, not a call-shaped string |
| `ConfigSectionDocRules` | **B** | high | the section's own declaration, matched by symbol |
| `HostingCompositionNullabilityRules` | **B** | high | the nullable annotation on the declaration |
| `LayerDocAgreementRule` | **B** | high | the layer graph, compared to the doc by name instead of by line |
| `LayerTableDocAgreementRules` | **B** | high | the layer table's own rows |
| `ResultMaybeSignatureTests` | **B** | high | the Result<T?> declaration, matched by symbol |
| `UiFrameworkNullabilityRules` | **B** | high | the nullable annotation on the declaration |
| `AgentStateContractRules` | **B** | medium | the IAgent.State declaration, not the text that names it |
| `AnalyzerSeverityScopeRules` | **B** | medium | the severity entry's own path, resolved against the project graph |
| `AvaloniaConverterInstanceRules` | **B** | medium | the converter type the view declares |
| `AvaloniaDispatchAsyncVoidRule` | **B** | medium | the Dispatch overload the call resolves to |
| `AvaloniaHeadlessSessionHelperRule` | **B** | medium | the helper's declaration and its callers |
| `BackgroundTaskSyncResultRule` | **B** | medium | the await the drain path does or does not have |
| `BuiltinAgentPickerProjectionRules` | **B** | medium | the registry the picker projects from |
| `CellForgeWidgetAxisRules` | **B** | medium | the axis the widget declares, not the string that spells it |
| `CellForgeWidgetStateRules` | **B** | medium | the state record the widget owns |
| `CommonConfigContractRules` | **B** | medium | the config section declaration |
| `ConfigHalfPairWriteRules` | **B** | medium | the write pair, resolved from the declarations |
| `ContractsArtifactCapabilityRule` | **B** | medium | the capability's own declaration |
| `CostPricedInCoreRules` | **B** | medium | the pricing call the core makes |
| `CostUnpricedBitRules` | **B** | medium | the unpriced bit's carrier |
| `DebouncedSettleShapeRule` | **B** | medium | the settle call's shape |
| `DefaultModelDocClaimTests` | **B** | medium | the model id the doc names, matched against the catalog |
| `DiSeverityDemotionRules` | **B** | medium | the severity entry the demotion sets |
| `DiagnosticsClassificationRule` | **B** | medium | the detector's own declaration list, read by symbol |
| `DiffSurfaceNameCollisionRule` | **B** | medium | the two surfaces' declarations |
| `ExtensionAxisDoorHonestyRule` | **B** | medium | the door's own registration |
| `FeatureFlagWiringRules` | **B** | medium | the flag's declaration |
| `FileLogSinkOwnershipRule` | **B** | medium | the sink type the host registers |
| `GateGlowConsumerReachabilityRule` | **B** | medium | the consumer's declaration |
| `KeyGateFamilyRules` | **B** | medium | the key gate's declaration |
| `LayerClaimMatchesMatrixRules` | **B** | medium | the matrix row the claim names |
| `LogLevelMnemonicRule` | **B** | medium | the mnemonic table's declaration |
| `McpOAuthTokenShapeRules` | **B** | medium | the token record's declaration |
| `MegaConstructorDoneStateTests` | **B** | medium | the constructor's parameter list |
| `ModelRateLabelRules` | **B** | medium | the rate table's declaration |
| `MoneyCellSingleHomeRules` | **B** | medium | the cell writer's declaration |
| `OverlayKeyRouteClaimRule` | **B** | medium | the route the doc names |
| `PanelKeyRouteReachabilityRule` | **B** | medium | the route's declaration |
| `ProcessEnvIsolationRule` | **B** | medium | the process-environment write |
| `PromptCacheKeyCoverageRules` | **B** | medium | the cache-key builder's declaration |
| `PromptClockPurityRule` | **B** | medium | the clock call's receiver |
| `PromptSectionPolicyRule` | **B** | medium | the section's declaration |
| `ProviderCatalogueBudgetRules` | **B** | medium | the catalogue's declaration |
| `ProviderModelAbsenceRules` | **B** | medium | the model's declaration |
| `ReadmeCoverageTests` | **B** | medium | the project list the README coverage is measured against |
| `RenderInvalidationCoalescingTests` | **B** | medium | the coalescer's declaration |
| `ReplConstructorCompositionTests` | **B** | medium | the constructor's parameter list |
| `SessionForkPortSeamRules` | **B** | medium | the port's declaration |
| `SessionStatusFrameReachabilityTests` | **B** | medium | the projection's declaration |
| `SessionStatusReader` | **B** | medium | the projection's declaration |
| `SessionStatusSourceRule` | **B** | medium | the status source's declaration |
| `SessionStatusTableRule` | **B** | medium | the label table's declaration |
| `SessionStoreFailureTextParityRules` | **B** | medium | the failure text each store builds |
| `SlashResultChannelTests` | **B** | medium | the channel's declaration |
| `TestDiagnosticsOutputRule` | **B** | medium | the diagnostic sink's declaration |
| `TestKitLocalDoubleRules` | **B** | medium | the double's declaration |
| `TestKitSharedCopyReasonRules` | **B** | medium | the shared double's declaration |
| `TokenCellSingleHomeRules` | **B** | medium | the cell writer's declaration |
| `ToolGlyphTableRule` | **B** | medium | the glyph table's declaration |
| `ToolNameListRule` | **B** | medium | the catalog declaration the list is compared against |
| `TuiReadLineContractRules` | **B** | medium | the read-line contract's declaration |
| `UiConfigDefaultsRule` | **B** | medium | the default's declaration |
| `VendoredHtmlSurfaceRules` | **B** | medium | the surface type's declaration |
| `WellKnownToolsRule` | **B** | medium | the catalog declaration the names are compared against |

The 8 guards with a `high` false-positive cost are the sharp end of
that list: a reformat of a product file fires them, and a reformat is
not a defect.

## 5. What this document does NOT know

**138 of the 160 guards have never fired.** The triage registry has a
verdict for 22 of them, each decided from named firings. The other 138
have no firing evidence, and their tier is a READING OF THEIR SOURCE,
not a measurement. A guard that has never fired may still be tier A
(the defect it exists for has not happened yet) and it may equally be
tier C (it measures a shape nobody cared about). The JSON says which
is which: a guard with an empty `evidence` array is unmeasured.

**The verdict of a guard is not the verdict of its firings.** The
triage registry decides per FIRING, because one rule can mean
different things in different situations. This document decides per
GUARD, which is coarser on purpose: the question here is which guard
to keep, and the answer has to be one thing per guard.

**Nothing here is enforced yet.** The file a future gate reads is
`tests/Harbor.Architecture.Tests/rule-tiers.json`, and this document is
its prose. A gate that reads the JSON and refuses a guard that has no
tier, or a tier-C guard that fires, is the next step, and it is not
this change.

**The count is measured on one tree.** The census was taken on
`origin/dev` at the commit this document was written on. A guard added
afterwards is not in the census, and the JSON is the list a gate reads,
so a new guard shows up there as a gap rather than as a silent
omission.
