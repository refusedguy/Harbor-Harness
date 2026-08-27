# Modern Terminal/Desktop/Web Testing Strategies: Analysis for Harbor

## Executive Summary

Harbor is a .NET 10 AI coding agent harness with multiple TUI renderers, an Avalonia desktop app, and performance-first engineering goals. The testing landscape across modern terminal (Ratatui/Textual/Bubble Tea), desktop (Avalonia), and web (Blazor/E2E) ecosystems offers several patterns Harbor should adopt. **Harbor should prioritize**: TUnit-based unit tests for core logic, `BenchmarkDotNet` for hot-path performance regression, Avalonia headless tests + `Verify.Avalonia` for the desktop app, golden-file snapshot testing for TUI renderers, and Playwright + `@axe-core/playwright` for web-based accessibility validation of any web UI surfaces.

---

## 1. Golden / Screenshot Testing Approaches

### Ratatui (Rust)
- **Tool**: `insta` + `TestBackend`
- **Pattern**: Render to an in-memory `TestBackend`, then `assert_snapshot!(terminal.backend())`
- **Workflow**: First run creates `.snap` files; subsequent runs diff against them. Update with `cargo insta review`
- **Strengths**: Deterministic, text-based snapshots are easy to review in PRs; no real terminal needed

### Textual (Python)
- **Tool**: `pytest-textual-snapshot`
- **Pattern**: `snap_compare` fixture runs app headlessly, captures SVG screenshots, and diffs against stored baselines
- **Features**: Supports `press` (key simulation), `terminal_size`, `run_before` hooks; generates HTML diff reports
- **Strengths**: SVG diffs render directly in GitHub PRs; fast (20s vs 3.5min with xdist); catches visual regressions automatically

### Bubble Tea (Go)
- **Tool**: `github.com/charmbracelet/x/exp/golden`
- **Pattern**: Golden-file comparisons of ANSI renderer output against `testdata/` fixtures; deterministic "frozen" environments (strip ANSI colors, freeze time)
- **Workflow**: Tests render `View()` output, normalize (strip ANSI, trim whitespace), compare to `.txt` goldens; update with `-update-goldens`
- **Strengths**: Cheap, language-agnostic, excellent for CLI/TUI regression baselines

### Avalonia (.NET)
- **Tool**: `Avalonia.Headless` + `Verify.Avalonia`
- **Pattern**: `[AvaloniaFact]` runs on headless Skia; `CaptureRenderedFrame()` produces `WriteableBitmap`; `Verify.Avalonia` stores `.verified.png` baselines and diffs on regression
- **Alternative**: Pixel-by-pixel comparison with configurable tolerance (e.g., 0.02)
- **Strengths**: Catches theme/layout regressions; works in CI without a display server

### Key Takeaways for Harbor
- **Textual's SVG approach** is the most web-friendly (PR-native diffs)
- **Bubble Tea's golden-file discipline** is ideal for Harbor's `AnsiTuiRenderer` / `PlainTuiRenderer`
- **Verify.Avalonia** fits Harbor's existing Avalonia desktop app perfectly
- Harbor should avoid image-based snapshotting for TUIs unless pixel-perfect themes require it; prefer text/ANSI golden files for terminal renderers

---

## 2. Unit Testing Patterns for UI State

### Ratatui
- No prescribed architecture; state is user-managed
- **Pattern**: Test state transitions in isolation, then test rendering via `TestBackend`
- **Example**: Unit-test the model/state machine, then snapshot-test the draw call

### Textual
- **Tool**: `App.run_test()` async context manager returning a `Pilot`
- **Pattern**:
  ```python
  async with app.run_test() as pilot:
      await pilot.press("r")
      assert app.screen.styles.background == Color.parse("red")
  ```
- State assertions are direct property checks on the DOM/widget tree after simulated input

### Bubble Tea
- **Pattern**: Pure function testing of `Model.Update(msg) -> (Model, Cmd)` and `Model.View() -> string`
- No framework magic needed; standard Go table-driven tests
- Example: feed a `tea.KeyMsg`, assert the returned model's cursor position changed

### Avalonia / Blazor
- **Avalonia**: ViewModel unit tests are plain C# (no Avalonia dependency); test `INotifyPropertyChanged`, commands, converters with xUnit/NUnit
- **Blazor (bUnit)**: Render component, find DOM nodes, trigger events, assert markup/state
  ```csharp
  var cut = RenderComponent<Counter>();
  cut.Find("button").Click();
  cut.Find("p").MarkupMatches("<p>Current count: 1</p>");
  ```
- **Key insight**: Separate pure logic (unit-testable) from side effects (integration/E2E)

### Key Takeaways for Harbor
- Harbor already uses **TUnit** with source-generated discovery — lean into it
- For TUIs: test state transitions (model → view) as pure functions, then snapshot the render output
- For Avalonia: use `Avalonia.Headless` only when controls/layout are involved; ViewModels should remain plain unit tests
- Adopt **table-driven tests** for permission rules, identifier validation, and provider selection (Harbor already does this well)

---

## 3. Integration Testing for Terminal I/O

### Patterns Across Ecosystems

| Ecosystem | Approach | Tooling |
|-----------|----------|---------|
| **Ratatui** | `TestBackend` (in-memory) + PTY-based `ratatui_testlib` for real escape-sequence testing | `ratatui_testlib`, `crossterm` |
| **Textual** | `run_test()` replaces `run()`; Pilot drives keyboard/mouse/resize in-process | Built-in Pilot API |
| **Bubble Tea** | `tea.WithOutput(&buf)` + `tea.WithInput(&buf)` for deterministic in-memory I/O; golden-file renderer tests | Standard `go test` |
| **General CLI** | PTY allocation (`expect`-style, `pyte` for Python, `portable-pty` for Rust) | `pexpect`, `expect`, `teatest` |

### Terminal I/O Testing Specifics
- **PTY-based testing** is the gold standard for "real" terminal behavior (Sixel, cursor movement, bracketed paste)
- **In-memory backends** are faster but miss escape-sequence edge cases
- **Determinism tricks**:
  - Freeze time (Bubble Tea sets `snapshotFixedNow`)
  - Strip ANSI colors (`NO_COLOR=""`, `KATA_COLOR_MODE=none`)
  - Pin `TERM=xterm-256color`

### Key Takeaways for Harbor
- Harbor's `ConsoleEx` renderer has a **raw-mode input pipeline** (kitty keyboard, SGR mouse, bracketed paste) — this **needs PTY-level integration tests**
- Harbor should adopt a **two-tier TUI testing strategy**:
  1. **Fast**: In-memory render tests (render frame → golden string comparison) for CI
  2. **Slow**: PTY-based integration tests (`ratatui_testlib` paradigm or .NET equivalent) for keyboard protocols and escape sequences
- For `AnsiTuiRenderer` / `PlainTuiRenderer`: capture stdout to a buffer and diff against golden files
- For IPC/headless mode: test the JSON-RPC transport with `dotnet test` + in-memory channels

---

## 4. Accessibility Testing Automation

### Web (Blazor / Playwright)
- **Tool**: `@axe-core/playwright`
- **Pattern**: Inject axe-core, run `AxeBuilder.analyze()`, assert `violations == []`
- **WCAG scoping**: `.withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])`
- **CI**: Dedicated Playwright project (`a11y-chromium`), HTML report artifacts, keyboard-navigation companion tests
- **Limitation**: Automated axe checks catch ~30% of WCAG issues; keyboard + manual audits still required

### Desktop (Avalonia / Appium)
- **Tool**: **Appium** (drives via platform accessibility tree)
- **Pattern**: Launch compiled app in real window, find elements by `AccessibilityId`, simulate clicks/typing, assert states
- **Avalonia's approach**: Tests Windows/macOS accessibility; uses Appium internally for framework CI
- **Alternative**: `AutomationPeer` inspection in headless tests (Invoke patterns, Value patterns)

### Terminal (Emerging)
- **Reality**: Terminal UIs have **no standardized accessibility tree**
- **Current practice**: Plain-text output is inherently more accessible than canvas TUIs; screen readers read streamed text
- **Emerging**: Windows Terminal's UI Automation notifications (`UIA` text-change events); VT Screen Reader Control sequences
- **Practical approach for Harbor**:
  - Ensure TUIs degrade gracefully to plain text (Harbor already has `PlainTuiRenderer`)
  - Test that critical content is present in rendered output (golden files catch missing text)
  - For Avalonia desktop app: use Appium + axe-core equivalent for Windows/macOS accessibility tree validation

### Key Takeaways for Harbor
- **For Avalonia app**: Add Appium tests for critical flows (file open, settings, chat input) — validates native accessibility
- **For any web UI** (e.g., remote mode web dashboard): Use Playwright + `@axe-core/playwright` with WCAG 2.2 AA tags
- **For TUIs**: No automated a11y tooling exists yet; focus on semantic text output and keyboard-only operability tests
- **Budget**: Start with Avalonia Appium tests (high value, Avalonia already supports it); defer web a11y until a web surface exists

---

## 5. Performance Testing / Benchmarking

### BenchmarkDotNet (.NET ecosystem)
- **Status**: Harbor already uses BenchmarkDotNet (`tests/Harbor.Benchmarks/`)
- **Existing benchmarks**:
  - `TerminalScreenBufferBlitBenchmark` — ANSI vs plain text blit cost
  - `EventBusBenchmark`, `JsonlSessionStoreBenchmark`, `SqliteSessionStoreWalBenchmark`
  - `SelectorMemoizationBenchmark`, `StateDiffingBenchmark`
- **Pattern**: `[MemoryDiagnoser]`, `[SimpleJob]`, baseline comparison
- **Best practice**: Two-layer perf testing
  1. **BenchmarkDotNet**: Micro/macro benchmarks for hot paths (render, IPC, JSON parsing)
  2. **Allocation-budget tests**: Fast `GC.GetAllocatedBytesForCurrentThread()` checks that gate CI (see Omni.Blazor pattern)

### TUI-Specific Performance
- **ConsoleEx renderer**: Cell-grid diff renderer (`DiffEngine`) — benchmark unchanged frame vs scattered change
- **Bubble Tea**: Framerate-based renderer; examples serve as integration smoke tests
- **Ratatui**: Zero-allocation hot paths; `ArrayPool<T>`, `Span<T>` usage
- **SharpConsoleUI**: Headless `MockConsoleDriver` benchmarks showing super-linear layout scaling (depth×breadth)

### Key Takeaways for Harbor
- Harbor's **existing BenchmarkDotNet suite is strong** — expand it to cover:
  - `ConsoleEx` `DiffEngine` frame diff cost (no-change vs scattered-change)
  - `EventBroadcaster` throughput under load
  - `IpcFraming` round-trip latency
  - Plugin compilation startup time (Roslyn in-memory)
- Add **allocation-budget tests** (`GC.GetAllocatedBytesForCurrentThread`) as fast CI gates alongside BDN
- Add a **render throughput benchmark** (frames/sec equivalent for terminal blit)
- Consider a **TUnit-powered perf regression suite** that runs in milliseconds and gates PRs, while BDN runs nightly

---

## 6. What Harbor Should Adopt

### Current State (from repo inspection)
- **Test framework**: TUnit 1.61.0 (source-generated, parallel by default, modern)
- **Assertions**: TUnit.Assertions + Moq 4.20.72
- **Architecture tests**: NetArchTest.Rules
- **Benchmarks**: BenchmarkDotNet 0.15.8 (comprehensive)
- **UI stacks**: Avalonia 12.1.0 (desktop), Spectre.Tui / Terminal.Gui / Termina / RazorConsole / ConsoleEx (TUIs)
- **Gap**: No TUI-specific testing (no golden files, no Pilot-style input simulation, no screenshot regression for renderers)

### Recommended Adoption (prioritized)

| Priority | Adoption | Rationale | Effort |
|----------|----------|-----------|--------|
| **P0** | **Golden-file snapshot testing for TUI renderers** | Harbor has 3+ renderers (`AnsiTuiRenderer`, `PlainTuiRenderer`, `ConsoleEx`). Capture rendered output to strings/byte arrays and diff against `.golden` files. Update with `--update-goldens`. | Low — new test project, no new deps |
| **P0** | **Extend BenchmarkDotNet to TUI hot paths** | `ConsoleEx.DiffEngine`, `TerminalScreenBufferBlit` exist but miss the new renderer. Add frame-diff and blit benchmarks. | Low — follow existing pattern |
| **P1** | **Avalonia headless tests with Verify.Avalonia** | Harbor ships `Harbor.App.Avalonia`. Add `[AvaloniaFact]` tests for critical windows and `Verify.Avalonia` for visual regression. | Medium — requires Avalonia test host setup |
| **P1** | **PTY-based integration tests for ConsoleEx input pipeline** | Kitty keyboard, SGR mouse, bracketed paste need real terminal emulation. Use `System.Diagnostics.Process` + PTY or a Rust `ratatui_testlib`-style harness via interop. | Medium — infrastructure work |
| **P1** | **Allocation-budget CI gates** | Add `GC.GetAllocatedBytesForCurrentThread()` assertions for hot paths (render, IPC, tool dispatch). Runs in milliseconds, gates every PR. | Low — follows Omni.Blazor pattern |
| **P2** | **Appium tests for Avalonia desktop app** | Validate native accessibility tree, focus, menus on Windows/macOS. High value for Harbor's desktop GUI. | High — requires Appium infra + real display |
| **P2** | **Playwright + axe-core for web surfaces** | If Harbor's remote/daemon mode gets a web dashboard, add accessibility scans. Not urgent today. | Medium — depends on web surface |
| **P3** | **Pilot-style input simulation for ConsoleEx** | Build a `Pilot`-like harness (Textual's pattern) that sends key/mouse events to `ConsoleEx` and asserts state. | Medium — depends on renderer abstractions |

### Concrete Next Steps
1. **Create `tests/Harbor.Tui.Tests/`**:
   - Reference `Harbor.Tui.Ansi`, `Harbor.Tui.Plain`, `Harbor.Tui.ConsoleEx`
   - Add golden-file tests for each renderer: render a fixed model, snapshot the output
   - Use `TUnit` + a simple file-comparison assertion
2. **Add `Verify.Avalonia` to `tests/Harbor.Desktop.Tests/`**:
   - Capture main window frames, baseline them
3. **Add allocation-budget tests in `tests/Harbor.Core.Tests/Performance/`**:
   - `RenderFrameAllocationBenchmark` using `GC.GetAllocatedBytesForCurrentThread`
4. **Document testing conventions** in `AGENTS.md` or a new `docs/TESTING.md`:
   - How to update goldens
   - How to run benchmarks
   - When to use headless vs Appium vs unit tests

---

## 7. Framework Comparison Matrix

| Feature | Ratatui | Textual | Bubble Tea | Avalonia | Blazor / bUnit | Playwright |
|---------|---------|---------|------------|----------|----------------|------------|
| **Golden/Snapshot** | insta + TestBackend | pytest-textual-snapshot (SVG) | charmbracelet/x/golden (ANSI) | Verify.Avalonia (PNG) | MarkupMatches (HTML) | N/A |
| **Unit test style** | Pure state + render | Pilot.press + assert | Table-driven Update/View | xUnit + headless | bUnit Render + Find | N/A |
| **Terminal I/O** | TestBackend / PTY | run_test() in-process | WithOutput(&buf) / PTY | N/A | N/A | N/A |
| **Accessibility** | Limited (text-only) | Limited (text-only) | Limited (text-only) | Appium + AutomationPeer | axe-core + Playwright | @axe-core/playwright |
| **Performance** | BDN / custom | pytest-benchmark | go test + bench | BDN + RenderTimer | BDN + allocation budgets | BDN |
| **Maturity** | High | High | High | Medium-High | High | High |

---

## 8. Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| **Golden file churn** — Every TUI layout change breaks snapshots | Use text-based goldens (not images) so diffs are readable; automate updates with `--update-goldens` and require human review |
| **PTY tests are flaky across OS/terminal** | Pin `TERM=xterm-256color`, run PTY tests only on Linux CI, use in-memory fallback for cross-platform parity |
| **BenchmarkDotNet artifacts bloat repo** | Keep `BenchmarkDotNet.Artifacts/` gitignored; store only summary markdown in docs if needed |
| **Avalonia Appium is heavyweight** | Gate Appium tests to nightly CI; keep fast headless tests as the PR gate |
| **TUnit ecosystem is young** | TUnit assertions package works with xUnit/NUnit if migration is ever needed; source-gen is already in use |

---

## Conclusion

Harbor's testing stack is **above average for a .NET 10 project** (TUnit, BenchmarkDotNet, NetArchTest, Moq), but it lacks **TUI-specific testing discipline**. The biggest gap is golden-file regression coverage for the multiple terminal renderers (`AnsiTuiRenderer`, `PlainTuiRenderer`, `ConsoleEx`). Adopting Bubble Tea's golden-file pattern — text snapshots with `--update-goldens` — is the lowest-effort, highest-value addition.

For the Avalonia desktop app, `Verify.Avalonia` provides screenshot regression with minimal friction. For performance, Harbor should complement its existing BDN suite with fast allocation-budget CI gates.

**Bottom line**: Harbor should not rewrite its test stack; it should **extend** TUnit with golden-file TUI tests, Avalonia visual regression, and allocation budgets — all achievable in 1–2 sprints with low risk.
