# Changelog

All notable changes to Harbor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]
### Sprint multi-agent — 29.08.2026

**Multi-Agent.**

- Включи TaskTool execution.
- Убери UiStore.Transition escape hatch.
- Сделай EventBroadcaster session-scoped.
- Добавь AgentLoop pipeline behaviors (§3.5).

**Commits since previous sprint tag** (64):
- 26eb64f docs(sprint): multi-agent re-verified at head 5cac99a — build 0 errors + 10-suite run 876/0/6 all rc=0, stray duplicate report removed
- 5cac99a docs(sprint): multi-agent re-verified at head dc9fae3 — build 0 err/0 warn, 10 suites 876/0/6 all rc=0, stray duplicate report removed
- dc9fae3 docs(sprint): multi-agent re-verified at head bef04d6 — build 0 errors + 10-suite run 876/0/6 (all rc=0)
- bef04d6 docs(sprint): multi-agent re-verified at head 7b3db0a — build 0 errors + 10-suite run 876/0/6 (all rc=0)
- 7b3db0a docs(sprint): multi-agent re-verified at head df3890d — build 0 errors + 10-suite run 882/0/6
- df3890d docs(sprint): multi-agent re-verified at head 3db6942 — build 0 errors, 10-suite run 882/0/6 (all rc=0) + live E2E run task agent=explore proven
- 3db6942 docs(sprint): multi-agent re-verified at head f83bf53 — build 0 errors + 5 sprint-critical suites 576/0/6
- f83bf53 docs(sprint): multi-agent re-verified at head 9da02ca — cold build 0 errors (src/ clean) + 10-suite run 882/0/6
- 9da02ca fix(chain): restore valid JSON in multi-agent status.json
- 96005bd docs(sprint): multi-agent re-verified at head 4376cf9 — fresh build 0/0 + 10-suite run 882/0/6
- 4376cf9 docs(sprint): multi-agent re-verified at head 10c5b7a — fresh build 0 errors + 10-suite run 882/0/6
- 10c5b7a chore(sprint): multi-agent heartbeat timestamp refresh (live hermes watcher)
- 35d8664 chore(sprint): multi-agent re-verified at head b43ad82 — fresh build 0 errors + sprint-critical suites 484/0/6 green
- b43ad82 docs(sprint): multi-agent re-verified at head d61c5f0 — fresh build 0 errors + 10-suite run 882/0/6
- d61c5f0 chore(sprint): multi-agent re-verified at head eaba85e — fresh build 0 errors + 10-suite run 882/0/6
- eaba85e chore(sprint): multi-agent re-verified at head 12da510 — fresh build 0 errors + 10-suite run 876/0/6
- b476921 chore(sprint): design-system-product status heartbeat — pack consume 8/8 refreshed at head 12da510 (leftover from prior session)
- 12da510 chore(sprint): design-system-product verification refresh — head cd4a8b7 re-proven: 49+47+669 suites green, docgen zero-drift, nupkg fresh-consume 8/8
- cd4a8b7 chore(sprint): design-system-product verification refresh — comment-tolerance fix consumed 7/7, suites 49+669+47 green at head 7846615
- 7846615 fix(designsystem): tolerate JSON comments in theme files
- 50a9134 chore(sprint): multi-agent heartbeat timestamp refresh (leftover from prior session)
- 650eb5b chore(sprint): design-system-product verification refresh — pack consume 5/5, docgen zero drift re-proven at head 38f3d37
- 38f3d37 chore(sprint): design-system-product closed — 3 commits re-verified per-commit + pack dry-run consume green
- 257493b chore(sprint): multi-agent heartbeat — fresh re-verification 618/0/6 across 6 suites at head c8ffd6c
- 05762c2 chore(sprint): multi-agent full 10-suite re-verified 876/0/6 at head c8ffd6c
- c8ffd6c chore(sprint): multi-agent status heartbeat (sprint-critical suites re-run at head cae081d)
- f1f2f36 chore(sprint): multi-agent status heartbeat (sprint-critical suites re-run at head cae081d)
- cae081d chore(sprint): multi-agent status heartbeat (876/0/6 verified at head b985328)
- 9316939 docs(sprint): multi-agent verified at head b985328 — full 10-suite re-run 876/0/6 + live E2E sub-agent run
- b985328 docs(sprint): multi-agent independent re-verification at head 823194a — 5 sprint-critical suites fresh green
- 823194a docs(sprint): multi-agent re-verified at final head 689789c — fresh 876/0/6 across 10 suites
- 4c98413 docs(sprint): multi-agent final re-verification at head 689789c (5 sprint-critical suites fresh)
- 689789c chore(sprint): multi-agent status heartbeat (re-verified 876/0/6 at head)
- 9307a5e docs(sprint): multi-agent re-verification at final head 405db82
- 405db82 docs(designsystem): theme guide, generated API reference, example themes
- 88a923e feat(designsystem): theme marketplace — JSON format, validation, store, live reload
- 7100f90 feat(designsystem): extract Harbor.DesignSystem as standalone zero-dependency package
- 8296524 chore(sprint): sweep stale ui-v2 queue entries + multi-agent heartbeat
- 9d1c9aa docs(sprint): multi-agent verification record (E2E run-task smoke + fresh test counts)
- ba1fdf0 docs(sprint): multi-agent status.json + report.html

### Sprint performance — 29.08.2026

**Performance.**

- WireCodec: ArrayPool + PipeReader.
- AppReducer: streaming concat zero-alloc.
- JsonlSessionStore: Utf8JsonReader streaming parse.
- DifferentialRenderer для ConsoleEx.

**Commits since previous sprint tag** (20):
- 7e03c4e docs(sprint): performance re-verified at head f78b7ec on re-dispatch №9 — suites 36/76/81+4s/669/118 green, alloc numbers byte-identical
- f78b7ec docs(sprint): performance status.json heartbeat corrected to actual commit time
- ad80362 docs(sprint): performance re-verified at head 2079bc0 on re-dispatch №8 — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- 2079bc0 docs(sprint): performance re-verified at head 09254c5 on re-dispatch №7 — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- 09254c5 docs(sprint): performance re-verified at head 5b0f492 on re-dispatch — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- 5b0f492 docs(sprint): performance re-verified at head beda182 on re-dispatch — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- beda182 docs(sprint): performance re-verified at head ab5952f on re-dispatch — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- ab5952f docs(sprint): performance re-verified at head c581949 on re-dispatch — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- c581949 docs(sprint): performance re-verified at head 6018eaa on re-dispatch — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- 6018eaa docs(sprint): performance re-verified at head 6b3f5ec on re-dispatch — suites 36/76/81+4s/669 green, alloc numbers byte-identical
- 6b3f5ec docs(sprint): performance re-verified at head ec82f52 — 4 benchmarks re-run, alloc numbers byte-identical, suites 862/0/4-skip
- ec82f52 docs(sprint): performance status → done, 4/4 tasks with verdicts
- 7be1390 docs(sprint): performance — benchmark report before/after (WireCodec 0B/frame, AppReducer 34x, JSONL 0B machinery, DiffEngine 7KB/s) + HTML report
- 0cd7f8f bench(tui): streaming-delta ANSI-bytes acceptance for DiffEngine (§3.10)
- 66b6338 bench(ui): AppReducer streaming-delta cost — the P0 O(n^2) baseline scenario
- caec831 bench(storage): JSONL parse micro + 10k-message cold-parse acceptance benchmarks
- c546b16 perf(storage): zero-intermediate JSONL line parse — raw UTF-8 spans, no JsonDocument round-trip (PERF-005)
- 9f39718 perf(ipc): persistent PipeReader per connection in MessagePackRpcClient
- 255134b perf(ipc): WireCodec zero-alloc framing — pooled single-write + PipeReader read path
- af32355 docs(sprint): multi-agent re-verified at head 26eb64f — build 0 errors + 10-suite run 876/0/6 all rc=0, stray duplicate report removed

### Sprint security — 30.08.2026

**Security & Sandboxing.**

- CollectiblePluginLoadContext + deny-list.
- Capability manifest + trust.json v2.
- Plugin execution timeout + memory guard.
- Plugin audit log.

**Commits since previous sprint tag** (54):
- 5320515 docs(sprint): security report — independent final re-verification at 50b1a5b, build 0/0 strict, plugins 64/64, arch 47/47
- 50b1a5b docs(sprint): security report — final verification at e5959d0, build 0/0, plugins 64/64 x7, arch 47/47
- e5959d0 chore(sprint): security status head → 8a8e1e8
- 8a8e1e8 test(plugins): drain late creation echo before asserting save-burst collapse
- de84a31 docs(sprint): security report — delete/modify FSW flake fixed in component, 64/64 x6 at 41f1531
- 41f1531 fix(plugins): report Removed when a watched file vanished by fire time
- fe2fabc test(plugins): migrate remaining HasCount assertions in TrustLayerTests
- 12985a4 test(plugins): migrate deprecated TUnit HasCount assertions to Count().IsEqualTo
- f8e4b30 docs(sprint): security report — re-verified at eedb970 (build 0/0, plugins 64/64, arch 47/47)
- eedb970 docs(sprint): security report — rename flake fixed at root cause, suite 64/64 x3 at 54c8497
- 54c8497 test(plugins): fix rename race — await both rename signals, not a bare event count
- 33cc5fc chore(sprint): security status head → c928b4c
- c928b4c docs(sprint): security report updated — trust seam finalization, 64/64 suite
- 45b7fbf test(plugins): trust-seam capability narrowing + invalid-manifest rejection
- 0e0f1bb feat(plugins): interactive per-capability approval at startup
- a636a68 fix(plugins): fail closed on invalid capability manifest in the trust gate
- 6d7be18 feat(plugins): narrow plugin capability set to approved grants at the trust seam
- b2ce47a feat(plugins): promote GetGrantedCapabilities to IPluginTrustPolicy contract
- 66da1b1 docs(sprint): security report — 60/60 suite incl. trust v2 capability tests
- 767c37b test(plugins): trust.json v2 per-capability approval contract tests
- 839f194 docs(sprint): security & sandboxing report and sprint status
- fb60d3a fix(plugins): register PluginBlockedEvent as AgentEvent JsonDerivedType
- 883c64f fix(ide): restore missing usings in IdeBridgeRunner so the solution builds
- 0515cfb fix(plugins): allow-path audits only evidenced capabilities; JSONL append-only audit tests
- 10160ff test(plugins): execution sandbox — timeout kill, memory guard, block event, audit trail
- 7b007d3 fix(plugins): capability directive accepts optional colon before the list
- 703efcb test(plugins): sandbox ALC contract — capability deny-list, shared types, leak-free unload
- 87bb0b9 test(plugins): RecordingEventBus test double and Contracts project reference for runtime tests
- ffa0192 fix(plugins): collectible sandbox ALC implements IDisposable via cooperative Unload
- 3fc4b10 feat(plugins): wire the audit log into the runtime composition root
- 9bedb19 feat(plugins): registrar feeds declared capabilities and audit sink into the tool sandbox
- 3954d8b feat(plugins): audit capability use per tool call in the execution sandbox
- 7dd1aac feat(plugins): audit read_files on plugin source files at trust-gate load
- 44d4c69 refactor(plugins): move audit contract to abstractions; Storage keeps JSONL sink
- 43a63df feat(plugins): IPluginAuditLog contract in the abstractions layer
- e27bbc0 feat(plugins): thread declared capabilities through CompiledPluginAssembly and LoadedPlugin
- 552e8cf feat(plugins): instantiator preserves declared capabilities on LoadedPlugin
- 9328f7b feat(plugins): compilers attach the capability manifest to compiled assemblies
- 2cdd365 feat(plugins): thread declared capabilities through CompiledPluginAssembly and LoadedPlugin
- bacf3d4 feat(plugins): route every plugin-contributed tool through the execution sandbox

### Sprint release-engineering — 30.08.2026

**Release Engineering.**

- Полная автоматизация sprint-chain.sh.
- Zero-warning arch-test gate в build.
- Pre-commit hook (git alias `harbor-check`).
- Автоматические release notes.

**Commits since previous sprint tag** (7):
- 4924202 feat(release-notes): automatic sprint release notes — status.json merge + CHANGELOG entry + sprint tags
- dccdc01 feat(git): install pre-commit hook (.githooks) + git alias harbor-check
- 9c4c62c feat(hooks): harbor-check — fast pre-commit verification gate
- 6c6a32c feat(build): release arch-test gate — dotnet build fails when arch tests regress
- e11a6e0 feat(cli): harbor ide — NDJSON JSON-RPC stdio bridge verb (attach mode, silent stdout)
- 6873d5a chore(sprint): queue names → slug form (status paths match sprint dirs), model kilo-auto/free
- 23070e8 feat(sprint-chain): fully automated dispatcher — branch, dispatch, status.json, fail-safe


### Sprint 2 — contrib migration + documentation sweep

- Moved optional components out of the main solution: TUI renderers (Spectre,
  Spectre.Fullscreen, SpectreTui, TerminalGui, Termina, RazorConsole, Sixel) to
  `contrib/tui/`, Wpf/Maui/Blazor apps + tests to `contrib/apps/` and
  `contrib/tests/`, the Scripting stack (SharpTS/Jint) to `contrib/scripting/`.
  Main `Harbor.slnx`: 82 → 66 projects; new `contrib/Contrib.slnx` builds
  separately. Architecture layering rules now scope the main solution only.
- Known issues: ViewInflationTests.MainWindow_Inflates pre-existing red (NRE,
  ViewInflationTests.cs:211); LocatorConventionTests.TryGet_ReturnsNullForUnregistered
  broken by sprint (passes isolated — state pollution, needs fix in sprint 3).

### Changed — R28-R31: UI component decomposition + business logic extraction

**R28 — Platform-agnostic ToolCallViewModel + reusable components (Avalonia):**
- Moved `ToolCallViewModel` from `Harbor.App.Avalonia.ViewModels` to `Harbor.Ui.Framework.ViewModels`.
  Replaced `IBrush StatusBackgroundBrush` with `string StatusBrushKey` so the VM no longer
  depends on `Avalonia.Media`. Same VM is now reusable by WPF/MAUI/Blazor.
- Created `Harbor.Ui.Framework.Converters.StatusMappers` — platform-agnostic static helpers
  for status → brush-key / status → label / duration / time-ago / token-compact / cost formatting.
- Added 8 new Avalonia `IValueConverter` wrappers in `Views/Converters.cs`:
  `StatusTextToBrushConverter`, `ToolCallStatusToBrushConverter`, `SessionStatusToTextConverter`,
  `SessionStatusToBrushConverter`, `TimeAgoConverter`, `TokensToCompactConverter`,
  `CostToUsdConverter`, `InverseBoolConverter`, `StringNullOrEmptyToBoolConverter`.
- Created 3 reusable React-style `UserControl`s in `apps/Harbor.App.Avalonia/Views/Components/`:
  - `StatusBadge` — colored dot + label pill
  - `ChatBubble` — role pill + message body + optional timestamp
  - `SessionRow` — sidebar list row (title + subtitle + status dot + dirty indicator)
- Extracted `StatusBarViewModel` from `MainViewModel` (now independently testable).
- Added 33 `StatusMappersTests` + 20 `ComponentTests` in `Harbor.App.Avalonia.Tests`.

**R29 — Blazor + WPF ports:**
- Created Blazor equivalents: `Components/Shared/StatusBadge.razor`, `ChatBubble.razor`, `SessionRow.razor`.
- Updated `StatusBar.razor` + `Sessions.razor` to use `StatusMappers` helpers (replaced hard-coded "● running"/"● idle" strings).
- Renamed `MessageBubble.razor` → `ChatBubble.razor` for consistency.
- Created WPF equivalents: `Controls/ChatBubble.xaml`, `SessionRow.xaml`, `StatusBadge.xaml`.
- Created `apps/Harbor.App.Wpf/Converters/Converters.cs` mirroring the Avalonia wrappers
  (`BrushKeyConverter`, `NullToCollapsedConverter`, `StatusTextToBrushConverter`,
  `TimeAgoConverter`, `TokensToCompactConverter`, `CostToUsdConverter`).
- Extended `ChatLineViewModel` with `TimestampUtc` + `TimestampText` + `Preview` (80-char truncation).
- Added `RoleBrushKey` (renamed from `BrushKey`, kept alias for back-compat) and expanded
  `RoleLabel` switch to all 7 `ChatRole` values.
- Added 21 `ChatLineViewModelTests`.

**R30 — Plugin system bug fix + business-logic extraction:**
- **BUG FIX**: Plugin compilation tests were failing (3/24) because `Harbor.Abstractions.Models.*`
  types physically live in `Harbor.Domain.dll`, not `Harbor.Abstractions.dll`. Roslyn couldn't
  resolve the namespace without an explicit reference. Fixed by adding
  `typeof(Harbor.Abstractions.Models.Session).Assembly` to `PluginAssemblyReferences.BuildReferences()`
  and a direct `ProjectReference` to `Harbor.Domain` in `Harbor.Plugins.Compilation.csproj`.
  All 24 plugin tests now pass.
- Moved 6 platform-agnostic files from `apps/Harbor.App.Avalonia/Services/` to `Harbor.Ui.Framework/`:
  - `ChatMessageRenderer` → `Rendering/`
  - `ChatStreamingPresenter` → `Rendering/`
  - `SessionContext` → `Sessions/`
  - `SessionFactory` → `Sessions/`
  - `SessionSwitcher` → `Sessions/`
  - `SessionGitTracker` → `Sessions/`
- Created `ICommonConfigReader` interface in `Harbor.Ui.Framework/Configuration/` to break the
  circular dependency: `Ui.Framework → Desktop.Abstractions → Terminal.Abstractions → Ui.Framework`.
  Each platform app implements it as an adapter over its own `ICommonConfigStore`
  (e.g. `CommonConfigReaderAdapter` in Avalonia).

**R31 — God-object decomposition:**
- **MarkdownRenderer**: 487 → 110 lines (control) + 4 specialized classes:
  - `Markdown/MarkdownBlockRenderer.cs` (202) — block-level rendering
  - `Markdown/MarkdownInlineRenderer.cs` (173) — inline emission
  - `Markdown/MarkdownTextExtractor.cs` (96) — pure text extraction
  - `Markdown/MarkdownResourceResolver.cs` (57) — brush/font lookup
- **JsonlSessionStore**: 688 → 528 lines + new `JsonlMessageCodec` (215 lines, stateless).
  Extracted 4 private static methods (`SerializeMessagePayload`, `SerializePart`,
  `DeserializeMessage`, `DeserializePart`).
- **SessionManager**: 495 → 492 lines but 2 fewer concerns via new `IChatViewBinder` interface
  in `Harbor.Ui.Framework/Sessions/`. The interface abstracts `ChatViewModel` (Avalonia-specific
  VM) + `Dispatcher.UIThread` (Avalonia static) behind a narrow seam
  (`GetRenderedLineCount()`, `Rebind(UiStore, int)`). Implementation: `AvaloniaChatViewBinder`.
  Removed `using Avalonia.Threading;` and `using Harbor.App.Avalonia.ViewModels;` from SessionManager.

### Changed — Code Principles Audit: Sprint 1 + Sprint 2 fixes

Resolved 10 of the 11 critical/high findings from `docs/CODE_PRINCIPLES_AUDIT.md`
(one — §PERF-005 — partial with documented decision). Each fixed finding now carries
a `✅ RESOLVED` block in the audit doc; acknowledged-but-deferred findings carry
`⚠️ ACKNOWLEDGED` or `⚠️ PARTIAL`.

**Critical (Sprint 1):**

- **§ROP-002** — `PermissionService.CheckAsync`/`GetRuleset` now pattern-match
  `Result<AgentName>` instead of calling `.Value` (which threw on invalid input).
  Invalid agent names return `Result.Failure<PermissionResponse>` /
  `PermissionRuleset.Empty` instead of crashing the call stack.
- **§OOP-001** — `OpenAiCompatibleLlmClient._toolCallIndexToId` field removed; the
  tool-call index→id map is now a local `Dictionary<int, string>` inside
  `StreamAsync`, threaded into `MapChunk`/`MapChunkFromDocument` via parameter.
  Concurrent `StreamAsync` calls on the same singleton client no longer race.
- **§FP-003** — `AgentLoop.ToolContext.ReportProgress` is now `async`/`await` with
  a try/catch that logs failures at Warning level, instead of
  `_ = _eventBus.PublishAsync(...)` fire-and-forget.
- **§FP-006** — `TuiEffectHost.Run` attaches `.ContinueWith(OnlyOnFaulted |
  RunSynchronously)` to each `PromptAsync`/`RunSlashAsync`/`AbortAsync` call so
  unobserved-task exceptions are logged via the newly-injected
  `ILogger<TuiEffectHost>`. The `Run` contract stays synchronous.
- **§ROP-001** — `JsonlSessionStore.DeserializeMessage` now returns
  `Result<AgentMessage>` (was `AgentMessage?`). `GetMessagesAsync` aggregates
  per-line parse errors into a `List<string>` and logs them at Warning level,
  while still returning the successfully-deserialized messages (a single corrupt
  line no longer truncates the whole transcript).
- **§OOP-003** — `DeserializeMessage` takes `string sessionId` as a parameter
  (was a `""` placeholder), so the reconstructed `AgentMessage` is always in a
  valid state.

**High-perf (Sprint 2):**

- **§PERF-007** — `UiStore.Dispatch` is now lock-free: `lock(_gate)` replaced
  with a CAS loop on `volatile UiState _state`. `Dispatch(AgentEvent)`,
  `Dispatch(UiMsg)`, and `Transition` all use the same pattern with a no-op
  short-circuit.
- **§PERF-009** — `ChatMarkdown.Cache` is now a `ConcurrentDictionary`, removing
  the per-render `lock(Cache)`. The `Cache.Count > 2048 → Clear()` thundering-herd
  eviction is removed (the cache is already bounded upstream by
  `ChatTranscriptCache._rows`).
- **§PERF-006** — `BashTool` now rents `stdout`/`stderr` from `StringBuilderPool`
  (capacities 4096/1024). Each is capped at `MaxOutputChars = 100_000`; dropped
  bytes are counted and logged at Warning level. `Append('\n')` replaces
  `AppendLine()` for platform-independent separators.
- **§ROP-004** — `OpenAiCompatibleLlmClient.MapChunk` now returns
  `new[] { new ErrorEvent($"Parse failed: {ex.Message}") }` on parse failure
  (was `Enumerable.Empty<LlmEvent>()`), so the agent loop aborts the turn loudly
  instead of stalling on a silently-dropped chunk.
- **§OOP-002** — `ApplyCompatFlags` extracted to Strategy pattern:
  `IProviderCompatFlag` in `Harbor.Providers.OpenAiCompatible/Compat/`.
  `ProviderConfig.Quirks` carries the per-provider list (populated by
  `ProviderCompatFlags.For(providerId)` in registration code);
  `ApplyCompatFlags` simply iterates the list. New providers with quirks no
  longer require editing the client. Built-in implementations:
  `DeepSeekReasonerCompatFlag`, `GroqMaxTokensCompatFlag`.

**Acknowledged-but-deferred:**

- **§PERF-005** (partial) — The full `Utf8JsonReader` rewrite for
  `JsonlSessionStore.GetMessagesAsync` was judged too risky without AOT testing.
  `JsonDocument.Parse` is kept, but the ROP path is fixed (see §ROP-001).
- **§FP-007** (acknowledged) — `UiStore.Transition` left as `internal` escape
  hatch. `TuiEffectHost` legitimately needs to fold follow-up state after
  running an effect; removing it requires restructuring `TuiEffectHost` to emit
  `UiMsg` values instead of mutating state directly.
- **§FP-004** (partial) — `ChatMarkdown.Cache` is now a `ConcurrentDictionary`,
  but `Enabled` is left as a process-wide static toggle (markdown is on/off for
  the whole UI, not per-renderer). Making it per-renderer would require
  threading a flag through every call site.

**Sprint 3+ remaining:** §SOLID-001 (`AgentLoop` SRP), §SOLID-002 (`ChatScreen`
god-class), §OOP-004 (visitor for `LlmEvent`), §OOP-005 (Composite registry),
§FP-001/§FP-002/§FP-005 (mutation escapes), §ROP-003 (silent upsert),
§PERF-001/§PERF-002/§PERF-003/§PERF-004/§PERF-008 (perf), §AOT-001/§AOT-002
(JsonSerializerContext source-gen), plus all GoF / Low-level / Concurrency
duplicates.

### Added — Harbor.Scripting (in-process JavaScript / TypeScript plugins)

New assembly `Harbor.Scripting` adds an in-process scripting layer so plugins
can be authored in JavaScript (or TypeScript, pre-compiled via `tsc`). The
long-term direction is to swap the engine to [SharpTS](https://github.com/nickna/SharpTS)
for native TypeScript execution — the `IScriptEngine` abstraction is shaped so
that swap requires no call-site changes.

- **New project:** `src/Harbor.Scripting/`
  - `IScriptEngine` — abstraction over a JS/TS engine
  - `JintScriptEngine` — Jint-based impl (pure .NET, AOT-friendly, sandboxed)
  - `ScriptContext` — what's exposed to scripts: `IToolRegistry`,
    `IProviderRegistry`, `IAgentRegistry`, `ILogger`, `CancellationToken`,
    plus resource limits (timeout, memory, statements, recursion depth)
  - `ScriptLoader` — discovers `.js` / `.ts` files in `~/.harbor/scripts/`
  - `ScriptTool` — `ITool` adapter so script-registered tools become invocable
    by agents
  - `TypeScriptTranspiler` — shells out to `tsc` if on PATH; cached
- **New tests:** `tests/Harbor.Scripting.Tests/` — 10 tests covering
  expression evaluation, global `Harbor` object, timeout enforcement, denied
  built-ins (`process`/`require`/`print`), and tool registration via script
- **New sample:** `samples/scripts/hello.ts` (and `.js` twin) — a 10-line
  TypeScript plugin that registers a `hello` tool
- **New doc:** [docs/SCRIPTING.md](./docs/SCRIPTING.md) — full comparison of
  CS (Roslyn) / JS (Jint) / TS (SharpTS / tsc+Jint) / MCP, with performance
  table, security model, recommendation matrix, and migration paths
- **CLI:** `harbor --script <path>` runs a script file at startup, after
  plugins are loaded. Script-registered tools become invocable by agents in
  the same session

#### Security model

- `AllowClr=false`, `AllowOperatorOverloading=false` — no CLR access from scripts
- `require` / `process` / `print` not registered (undefined)
- Default timeout 5 s (configurable via `ScriptContext.Timeout`)
- Default memory cap 10 MB (`ScriptContext.MemoryLimitBytes`)
- Default statement budget 1,000,000 (`ScriptContext.MaxStatements`)
- Default recursion depth 1,000 (`ScriptContext.MaxRecursionDepth`)
- All expected failures (syntax error, runtime exception, timeout, denied
  built-in, conversion error) return `Result.Failure` — no exceptions leak
- Each `Evaluate` call uses its own `Engine` instance (Jint engines are not
  thread-safe)

#### PoC limitations (documented in SCRIPTING.md §9)

1. `execute` functions must be synchronous — Promise draining is on the roadmap
2. `Harbor.registerProvider` is a no-op in scripts (providers need `ILlmClient`)
3. TypeScript requires `tsc` on PATH (SharpTS will remove this)
4. Per-call engine cost ~1-2 ms (SharpTS expected to enable engine reuse)
5. No source-map support (TS error traces show JS line numbers)

### Code Principles Audit — 2026-07-17

Проведён детальный аудит кодовой базы по принципам OOP/SOLID/GoF/FP/ROP/perf/low-level.

- **New doc:** [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) — 41 finding (11 critical), примеры кода, рекомендации, приоритизированный план рефакторинга на 4 спринта.
- **New doc:** [docs/SPECTRE_TUI_DEEP_DIVE.md](./docs/SPECTRE_TUI_DEEP_DIVE.md) — детальный разбор SpectreTUI-рендерера: архитектура, layout tree, scroll conventions, flow данных, грабли, и рецепты для переноса фич из opencode/kilocode/pi-agent (diff-view, slash-completion, file-tree, LSP-diagnostics, token-breakdown).
- **TODO-комментарии** расставлены в коде: `// TODO(principles)[CATEGORY]: ...` с обратной ссылкой на раздел аудита. Поиск: `grep -rn "TODO(principles)" src/`.
- **Updated:** [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) — добавлена секция "Code principles" с краткой сводкой и эталонными реализациями.
- **Updated:** [docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md) — добавлен "Principles checklist" для PR (OOP/SOLID/GoF/FP/ROP/perf/low-level/concurrency/AOT).
- **Updated:** [CLAUDE.md](./CLAUDE.md) ↔ [AGENTS.md](./AGENTS.md) — двусторонние ссылки, синхронизированы разделы "principles".
- **Updated:** [README.md](./README.md) — добавлены ссылки на новые документы в секции "For developers".

#### Топ-3 критических нарушения

1. **§ROP-002** — `PermissionService.CheckAsync` бросает исключение на expected failure (invalid agent name) — краш под нагрузкой.
2. **§OOP-001** — `OpenAiCompatibleLlmClient._toolCallIndexToId` — instance-level mutable state, гонка при параллельных сессиях на одном singleton-клиенте.
3. **§PERF-005** — `JsonlSessionStore.GetMessagesAsync` — `JsonDocument.Parse` на каждой строке, ~10k аллокаций на длинную сессию.

Полный план рефакторинга — [docs/CODE_PRINCIPLES_AUDIT.md §Prioritized plan](./docs/CODE_PRINCIPLES_AUDIT.md#10-приоритизированный-план-рефакторинга).



### Added — v0.2.0-alpha

#### Native LLM providers (3 new)
- `Harbor.Providers.Anthropic` — native Anthropic Messages API
  - `cache_control` (ephemeral, 1h TTL)
  - Extended thinking with `budget_tokens`
  - Fine-grained tool streaming beta
  - Interleaved thinking beta
  - `tool_result` as content block in user message
  - System prompt as separate field (not message)
- `Harbor.Providers.OpenAI` — native OpenAI provider
  - Chat Completions API for GPT-4o, GPT-4.1
  - Responses API for o1, o3, o4-mini, GPT-5+ (auto-detected)
  - `reasoning_effort` parameter
  - `max_completion_tokens` (vs legacy `max_tokens`)
  - `reasoning_content` streaming
- `Harbor.Providers.Ollama` — native Ollama provider
  - NDJSON (not SSE) parsing
  - `/api/chat` endpoint (not `/v1/chat/completions`)
  - `keep_alive` parameter for model persistence
  - No auth required (local)
  - Auto-detects models via `/api/tags`

#### Storage backends (2 new)
- `Harbor.Storage.Memory` — in-memory session storage (for tests, ephemeral)
- `Harbor.Storage.Sqlite` — SQLite-backed storage with indexed queries
  - WAL mode for concurrent reads
  - `PRAGMA cache_size = -8000` (8 MB, not 64 MB)
  - Foreign keys enabled
  - Schema: `sessions`, `messages` tables with indexes

#### TUI renderers (2 new)
- `Harbor.Tui.Plain` — plain text renderer (no ANSI, no colors)
  - For pipes, CI logs, accessibility, file output
  - Writes to any `TextWriter`
- `Harbor.Tui.Spectre` — Spectre.Console renderer
  - Rich panels, tables, markup
  - Better visual formatting
  - Uses Spectre.Console v0.50

#### Builtin tools (1 new)
- `TaskTool` — delegates work to sub-agents
  - Validates sub-agent exists and `IsSubAgent=true`
  - Implements Command pattern
  - Foundation for multi-agent orchestration

#### Sample plugins (4 new, separate projects)
- `Harbor.Plugin.WebSearch` — DuckDuckGo web search (no API key)
  - Demonstrates HTTP-based tool
  - HTML parsing with regex
- `Harbor.Plugin.TodoWrite` — per-session todo list
  - Demonstrates stateful tool (ConcurrentDictionary by SessionId)
  - Add/update/list/complete/clear operations
- `Harbor.Plugin.GitTools` — safe git wrapper
  - Demonstrates process-wrapping tool
  - Blocks dangerous commands (`push --force`, `reset --hard`)
  - Uses `ArgumentList` for safe quoting
- `Harbor.Plugin.FileTree` — directory tree visualization
  - Demonstrates read-only filesystem tool
  - Honors common ignore patterns (node_modules, bin, obj, .git)
  - Custom output formatting

#### Performance optimizations
- `FrozenDictionary<TKey, TValue>` in `ProviderRegistry` and `ToolRegistry`
  - O(1) lookups after `Freeze()` is called
  - Lock-free reads on frozen snapshot
- `IReadOnlyCollection<T>` / `IReadOnlyList<T>` in public APIs
- `ArrayPool<T>` extensions (`RentScoped`) for zero-alloc buffers
- `StringBuilderPool` for hot-path string building
- `Channel<T>` for backpressure-aware streaming in all LLM clients
- Lazy initialization for providers (only load when first used)
- Pre-allocated arrays in hot paths (no LINQ `ToList()` in loops)

#### Infrastructure
- `Directory.Build.targets` — test project detection, AOT defaults, embedded resources
- `BannedApi.txt` — banned APIs (Newtonsoft.Json, Thread.Sleep, etc.)
- Added analyzers:
  - `Microsoft.CodeAnalysis.NetAnalyzers` — performance, async
  - `Microsoft.CodeAnalysis.BannedApiAnalyzers` — banned API enforcement
  - `AsyncFixer` — async best practices
  - `ReflectionAnalyzers` — AOT-friendliness
  - `Meziantou.Analyzer` — comprehensive code quality (180+ rules)
- `MA0046` (unsafe code) set to **error** — project rule: no unsafe ever
- Comprehensive `.editorconfig` with 200+ analyzer severity configurations
- Embedded provider JSON configs (via `<EmbedProviders>true</EmbedProviders>`)
  - Builtin providers work after `dotnet publish` without external files

#### CLI improvements
- `HARBOR_TUI` env var — choose TUI renderer (ansi/plain/spectre)
- `HARBOR_STORAGE` env var — choose storage backend (jsonl/memory/sqlite)
- `harbor tui` command — show TUI options
- `harbor storage` command — show storage options
- `/tui` and `/storage` slash-commands in REPL
- Provider configs auto-discovered from 3 locations:
  1. Embedded resources (ship with binary)
  2. `~/.harbor/providers/` (user-global)
  3. `./providers/` (project-local, dev)

#### Documentation (3 new)
- `docs/GETTING_STARTED.md` — user-facing install/configure/run guide
- `docs/BUILD.md` — build, test, publish, distribute instructions
- `docs/PLUGIN_DEVELOPMENT.md` — write your own plugins (with patterns)

### Changed
- `ProviderRegistry` — now has `Freeze()` method for fast lookups
- `ToolRegistry` — now has `Freeze()` method for fast lookups
- All LLM clients — refactored to use `Channel<T>` pattern (no yield in try/catch)
- `ToolResult` — split into `ToolResult` (from tool) and `ToolResultEntry` (with call ID, in messages)
- `BashTool` — uses `ArgumentList.Add()` instead of `Arguments` string (safe quoting)
- Version bumped to 0.2.0-alpha

### Fixed
- `BashTool` quoting bug — `Arguments = "-c echo hello"` was parsed as 3 args
- `ProviderRegistry` tuple nullable mismatch
- Sonar analyzer `MA0046` — `KeyPressEventArgs` now inherits `EventArgs`

## [0.1.0-alpha] - 2026-07-16

Initial release with core agent loop, 13 JSON providers, 7 tools, JSONL storage, and 65 tests.

### Added
- `Harbor.Abstractions` — all interfaces and models
- `Harbor.Core` — EventBus, AgentLoop, registries, compaction, permissions
- `Harbor.Storage.Jsonl` — JSONL session store
- `Harbor.Providers.OpenAiCompatible` — generic OpenAI-compat client
- `Harbor.Tools.Builtin` — 7 builtin tools (read/write/edit/bash/glob/grep/ls)
- `Harbor.Tui.Abstractions` + `Harbor.Tui.Ansi` — ANSI streaming renderer
- `Harbor.Cli` — entry point
- 13 JSON provider configs
- 65 TUnit tests
- 16 design specification documents
