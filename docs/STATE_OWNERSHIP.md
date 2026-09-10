# State ownership + #27 acceptance evidence (#45)

Reviewer-facing tables, produced from code (not opinions). Format per spec 17 §11:
item → done/partial/no → proof (test / file:line) → dependency.
Scope key: application / window / session / run / turn / request.

## A. Ownership table (extended format)

| Fact | Scope | Authoritative owner | Mutation commands | Derived copies (read-only) | Recovery | Lifetime / disposal |
|---|---|---|---|---|---|---|
| Steering queue content | run | `DefaultAgent._steeringQueue` — `Channel<AgentMessage>` (`SingleReader=true`), `src/Harbor.Application/Agents/DefaultAgent.cs:27`, created `:87-91` | Submit/Accept: `Steer()` → `TryWrite` (`DefaultAgent.cs:360`, never throws); busy `PromptAsync` steers instead of failing (`:217-221`, `:266-275`); Consume: `SteeringDrainBehavior.DrainAsync` (`.../Pipeline/SteeringDrainBehavior.cs:17-36`) at mid-turn after tool results persisted (`AgentLoop.cs:310`) + boundary after `TurnEndEvent` (`AgentLoop.cs:319`) | `DefaultSessionContext` pass-through, same channel (`DefaultAgent.cs:460,490-510`); contract `ISessionContext.SteeringQueue` (`ISessionStore.cs:150`); `TracingAgentProxy` passthrough (`TracingAgentProxy.cs:34`) | Session-switch `Initialize()` drains+discards (`DefaultAgent.cs:404-409`, warns `:413`); foreign-session G2 filter drops (`SteeringDrainBehavior.cs:25-30`); cancelled run leaves residue, dropped on rebind (`AgentLoop.cs:335-345`, `DefaultAgent.cs:329-334`) | Per `DefaultAgent` instance (session); same-session rebind keeps queue |
| Retry countdown (`RetryId`/`Attempt`/`ScheduledAt`) | request | **VACANT** — `ToolRetryDecider` (#43) not implemented; `RetryPolicy.ExecuteAsync` keeps loop-local `attempt` only (`src/Harbor.Application/Resilience/RetryPolicy.cs:49-82`); `RetryOptions` is config (`.../Resilience/RetryOptions.cs:3`); wired for LLM-stream only (`AgentLoop.cs:49,232-246`, `onRetry` logs) | None persistent | UI render-only, cannot trigger: `RetryCountdown.Line/Segments/BackoffSeconds` (`.../CellForge/Chat/Widgets/RetryCountdown.cs:8-9,17-18,67-77`); `StatusViewModel.Retry` slot (`.../Widgets/StatusViewModel.cs:24-28,103-106`); `SetProjectedRetry` defined, **zero callers** (`ChatScreenLayout.cs:339`) — slot unfed | n/a (no state to recover) | Ephemeral per `ExecuteAsync` call |
| Token usage per step | request | Producers: `OpenAiWire.ReadUsage` (`.../Providers.Shared/OpenAiWire.cs:102-110`); `AnthropicEventMapper` (`.../Anthropic/AnthropicEventMapper.cs:113-122`); `OllamaLlmClient` (`.../Ollama/OllamaLlmClient.cs:322-325`); `OpenAiResponsesMapper` (`.../OpenAI/:104`) | `StepFinishEvent.Usage` carries a **DELTA** (evidence: every consumer `+=` — `UiReducer.cs:194-208`, `AppReducer.cs:165-176`, `TokenTracker.cs:33-40`, `ChatScreenBridge.cs:161-164`, `InstrumentedLlmClient.cs:120-124`; wire emits usage once per step) | — | — | Per step event |
| Token session aggregate | session | `SessionMetadata.AddUsage` (`.../Models/Session.cs:116-127`); `DefaultAgent.UpdateStatsAsync` (`.../Agents/DefaultAgent.cs:530-543`) fed by `AgentLoop` `RecordTurnUsage` + `UpdateStatsAsync` (`AgentLoop.cs:266-270`) | `+=` accumulation per turn | JSONL store re-derives by re-summing (`JsonlSessionStore.cs:546-590`, `UpdateStatsAsync` no-op) | Re-derivable from history | Per session |
| Token local estimate | turn | `HeuristicTokenEstimator` (chars/4, CJK/2, +100/msg) (`.../Sessions/ICompactionService.cs:107-125,128-159`); `TokenTracker` running cache + `ShouldCompact` (`.../Sessions/TokenTracker.cs:22,56-76`) | `RecordTurnUsage`, `RecordAppendedMessage` | `ShouldCompact` pre-turn check (`estimated > ContextWindow - Reserve(16384)`) | Resync paths (`TokenTrackerTests:45,70`) | Per session tracker |
| ctx% | window | **NO CANONICAL FORMULA — three divergent**: `TuiViewModels.ContextPct = Min(100, ContextTokens*100/ContextWindow)` where `ContextTokens` = **last-step input only** while `TokensIn/Out` accumulate (`.../ViewModels/TuiViewModels.cs:50,79-82`); `SideBarView pct = (TokensIn+TokensOut)*100/ContextWindow` (`...:138-141`); `StatusViewModel` ratio + 0.50/0.85 bar (`...:108-115,130-131`) | Step updates (`TuiViewModels.cs:79-82`) | Render: `Formatted` (`:58-60`), `SetContext/SetUsage` (`StatusViewModel.cs:50-82`), `SideBarView :125-153`, bridge `SessionStatsEvent→SetUsage` (`ChatScreenBridge.cs:196-198`) | — | — |
| Next-input projection | turn | **DOES NOT EXIST** (closest: `ShouldCompact` pre-turn estimate) | — | — | — | — |
| Transcript / scroll lines | window | `UiState` (`ScrollOffset/ViewportLines/TotalLines`, `AddLine/SetLine` — `.../State/UiState.cs:122-130,171-176`; `TerminalUiState.cs:21-29`; `AppState.cs:31,82-88`) | Input/scroll via `UiMsg` (`VirtualizedChatTimeline.cs:257-288`: `PageUp/DownMsg…→KeyInput/ScrollResetToTail`; measure `:342-348`); panels dispatch `TogglePanel` / `KeyInput(Submit)`; renderer `RenderAsync→Dispatch` (`CellForgeTuiRenderer.cs:166`); `LeaderKeyRouter` translate-only (`LeaderKeyRouter.cs:6`) | `ApplyStoreState` declares store source of truth (`VirtualizedChatTimeline.cs:243-250,303-330`) | Session-switch/disposal seams thin (`SessionsSnapshot`/`ActiveSessionIdSnapshot` only) | Per window/store |
| Approval decision | turn | `ApprovalGateView` one-shot gate (`ApprovalGateRouter.cs`; `RequestApprovalGate` — `ChatScreenBridge.cs:672-673`) | `TryDecide` one-shot, rejects second (`ApprovalGateViewTests:173-189`); `DecisionRecorded` fires once (`:103-116`); keys ignored after decision (`:58-71`) | No `UiMsg.Approval` — decision travels outside the store | **NO cancel-race/preemption test** (gap) | Per gate instance |
| Permission verdict | turn | `PermissionService` fail-closed Deny without asker (`IntelligenceModule.cs:22-26`); CellForge interactive override gated by `IsApprovalPromptAvailable` (`CellForgeModule.cs`, #52) | `CheckAsync` → Allow/Deny/Ask→Deny | Persisted "always" rules merged (`PermissionService.cs:179-194`) | Asker throws → Deny (`:144-161`) | Singleton |
| Session persist fidelity | session | JSONL codec (`JsonlMessageCodec.cs`, `JsonlLineParser.cs`, `JsonlSessionStore.cs`) | Append/update per message | Stats re-derived on read | Malformed lines warn+skip (`:737-741`) | Per file |

### Ownership verdicts (decisions required, not code)

1. **Steering — DONE, single owner** (`DefaultAgent` channel). One flag: `DummySessionContext.SteeringQueue`
   (`apps/Harbor.App.Cli/Hosting/Adapters.cs:47`, own A4 comment `:44-46`) is a separate per-context
   channel for the REPL/slash path, never drained by `AgentLoop` — second source of truth by construction.
   Proofs: `SteeringDeliveryTests:103,149,232`; `PipelineBehaviorTests:271,286`;
   `SteeringCrossSessionTests:37,79,129`; `AgentLoopLifecycleTests:149`; `DummySessionContextTests:19,30,50`.
2. **Retry countdown — VACANT.** #43 must define: scheduler owns `RetryId`/`Attempt`/`ScheduledAt`,
   UI renders remaining only, never triggers. Today's `RetryCountdown` widget + `StatusViewModel.Retry` slot
   are dead UI (formatter tests only: `RetryCountdownTests:8-70`; policy tests: `RetryPolicyTests:140,160,177`).
3. **ctx% — DIVERGENT, needs one formula.** Propose: `ctx% = (accumulated input + output) / ContextWindow`,
   single helper, three call sites converge; `ContextTokens`-as-last-step is the bug-shaped one
   (status line shows totals but percent of last step). Tests pinning segments:
   `StatusProjectorTests:66,75`; `TokenTrackerTests:29,45,70,105`; `DecoratorTelemetryTests:145-146`.
4. **Transcript — PARTIAL.** Store owns lines, but side buffers remain (`ScrollY`/`TotalHeight`/`_cache`,
   `TimelineRing`; write paths `ChatScreenBridge.cs:271-304`, `InlineAgentStreamBridge.cs:57,127,144-145`,
   `StreamBlock.cs:39-56`) with no single-write-path test and thin session-switch/disposal evidence.
5. **Approval — decision one-shot DONE, cancel-race NO.** Blocks #27 approval criterion.

## B. #27 acceptance table

| Item | Verdict | Proof | Dependency / note |
|---|---|---|---|
| 7 builtin panels registered in CellForge registry, rendered from `UiState` | PARTIAL (6 DONE + file-tree PARTIAL) | `CellForgeTuiRenderer.cs:109-119` registers 8 (7 + `session-sidebar`); `CellForgePanelRegistry.cs:55-79` `EnsureSeeded` via `UiMsg.SeedPanels`; behavior (not golden-only): `PanelWiringTests` (`Registry_Has_Seven_Builtins_In_Spectre_Slot_Order`, `InitializeAsync_Seeds_Panels_Hidden_With_Default_Sizes`, `AttachPanels_*`, `UpdatePanels_Refreshes_Leaf_Without_Tree_Surgery`); `CellForgeBuiltinPanelsTests` (build empty/populated/clipped, `?`/`F12`) | file-tree cursor + dir cache are provider-local behind a lock, not in `UiState` — explicit `TODO(principles)[FP-005, TEA]` (`CellForgeBuiltinPanels.cs:332-344`); moving cursor into store is the remaining work |
| Input/scroll/focus/resize only via `UiMsg`/`UiReducer`; `ShouldRenderPlacement` stub removed | PARTIAL + DONE | Write path via `UiMsg` (see transcript row above); **no `ShouldRenderPlacement` override — intentionally** (`CellForgeTuiRenderer.cs:136-139`) → stub criterion DONE | Direct-mutation survivors: `VirtualizedChatTimeline.cs:26-36,88,201-241` (`ScrollY/ScrollBy/ScrollToEnd/SnapScroll`); file-tree cursor (above); `JsonThemeLoader.cs:41-46` `Apply*/Toggle/SetThemeVariant` throw `NotImplemented`; `LeaderKeyRouter.cs:5` `TODO(TEA,DIP)` |
| Scroll/transcript on store | PARTIAL | `UiState` owns lines (row above); read-path test `PanelWiringTests:AttachPanels_RightDock` (`UiState.Lines` → rows); `RendererWiringTests.cs:130-136` session projection | Missing per proof rules: single write-path test, session-switch behavior, disposal of subscriptions |
| Approval duplicate-event + cancel-race | PARTIAL (dup DONE, race NO) | Dup/one-shot: `ApprovalGateViewTests:103-116,173-189,58-71` | No cancel-token/preemption test; no `UiMsg.Approval`; goldens/click-zones don't prove races |
| Legacy-adapter removal (zero production callers) | NO | Adapter seam `LegacySlashRunner.cs:12-21`; **5 live production callers**: `CellForgeReplRunner.cs:99,148,151,1105,1170` + `PromptPipeline.cs:28` | File-tree/legacy slash still on old dispatcher — removal criterion blocked |
| Perf defined workload | DONE (workload) / PARTIAL (docs) | `RendererBenchmarkSuite.cs:30-33` (`EventCount=1000/Warmup=100/80×24`), `:150-168` (`MessageStart` + 19× `TextDelta` per backend); gates `RendererBenchmarkTests.cs:28-51,54-64,67-77`; `baseline.csv` + `HARBOR_PERF_REPORT` | `docs/BENCHMARKS.md:253-258` still cites older `RendererMoatPerfTests` probes, not the Phase-6.1 contract — doc update owed |
| E2E Kilocode-free `agent_start→…→agent_end` unbroken | NO (in-repo) | In-repo: mock-pipeline only (`CliE2ETests:128-145,186-233`, `HARBOR_TUI=plain`); no `KILO_API_KEY`/live test in `tests/`; live signal lives out-of-tree (`docs/EVALS.md`, `tools/Harbor.Evals`, weekly + dispatch, explicitly not a gate per spec 17 §6) | Blocked on baseline dispatch (see #40: workflow must reach default branch); #51/#52 fixes land first so measurements are honest |

## C. Follow-ups filed from this pass

- ctx% convergence (single formula) — needs an issue or a line item under #37/#39.
- `BENCHMARKS.md` Phase-6.1 contract reference — doc touch-up under #39/#22.
- file-tree cursor → store; transcript single write path + session-switch/disposal tests; approval cancel-race test;
  legacy-adapter removal — all already tracked under #27; this table is the proof baseline.
