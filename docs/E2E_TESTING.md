# E2E Testing

End-to-end test framework + per-app E2E test suites for Harbor. This document
describes the framework, what's covered today, how to run the tests, and what
remains on the roadmap (TUI / Avalonia E2E).

---

## 1. Why E2E tests?

Unit tests cover individual classes (`AgentLoop`, `ToolRegistry`,
`OpenAiCompatibleLlmClient`, `JsonlSessionStore`, …). They use stubs for every
external dependency. E2E tests go the other way: they spawn the real app
binary, point it at a real (mock) LLM server, drive it like a user would, and
assert on observed output. The pipeline under test is end-to-end:

```
CLI args → Program.Main → HostBuilder → AgentLoop →
  OpenAiCompatibleLlmClient → (in-process HTTP) → MockLlmServer →
    SSE stream → renderer → Console.Out → captured by CliDriver → assertion
```

A regression in any layer (DI registration, env-var binding, SSE parser,
renderer output) surfaces as an E2E failure, not a misleading unit test pass.

---

## 2. Framework layout

All E2E infrastructure lives in `tests/Harbor.E2E.Framework/`. Test projects
reference the framework + the app under test.

```
tests/Harbor.E2E.Framework/
├── Harbor.E2E.Framework.csproj     # net10.0; IsTestProject=true; no Packable
├── IE2eDriver.cs                   # Common contract: Start/Send/Read/Wait/Stop
├── CliDriver.cs                    # Process.Start-based; for one-shot CLI + Blazor
├── TuiDriver.cs                    # PTY-wrapped; for interactive TUI renderers
├── HeadlessAvaloniaDriver.cs       # Avalonia.Headless; for desktop app
├── MockLlmServer.cs                # HttpListener; OpenAI-compatible SSE mock
├── E2eTestBase.cs                  # TUnit base; per-test MockLlmServer + temp HOME
├── HarborAppLocator.cs             # Walk up to repo root; locate dotnet host
├── AnsiStripper.cs                 # Regex-based ANSI escape remover for TUI screens
└── GlobalUsings.cs                 # Framework-wide using directives
```

### 2.1 `IE2eDriver` — the common contract

Every driver (CLI, TUI, Avalonia) implements the same 7-method surface so
test code is renderer-agnostic:

| Method | Purpose |
|---|---|
| `StartAsync(args, env, ct)` | Spawn the app with given args + env vars |
| `SendInputAsync(input, ct)` | Send raw stdin (CLI REPL or TUI input field) |
| `SendKeyAsync(key, mods, ct)` | Send a logical keystroke (Enter, Esc, F12, …) |
| `ReadScreenAsync(ct)` | Read current screen as plain text (ANSI stripped) |
| `WaitForTextAsync(pattern, timeout, ct)` | Poll screen until pattern appears |
| `WaitForExitAsync(timeout, ct)` | Block until process exits; return exit code |
| `StopAsync(ct)` | Force-kill if running; idempotent |

Plus `IsRunning` and `IAsyncDisposable`.

### 2.2 `CliDriver` — one-shot CLI / Blazor HTTP

Wraps `System.Diagnostics.Process` with redirected stdin/stdout/stderr. Resolves
the built DLL (`bin/Debug/net10.0/Harbor.App.Cli.dll`) and invokes
`dotnet exec <dll> <args>`. Sets the working directory to the project root so
ASP.NET Core apps find their `wwwroot`. Async-drains stdout/stderr into a
`StringBuilder` so the buffer never deadlocks. UTF-8 on both streams so emoji
survive.

### 2.3 `MockLlmServer` — in-process OpenAI-compatible HTTP

`HttpListener`-based. Binds a random localhost port (retries on collision).
Endpoints:

- `POST /chat/completions` and `POST /v1/chat/completions` — streams the canned
  response as `text/event-stream` SSE chunks (4 chars per chunk + final
  `[DONE]`). Each chunk is a real OpenAI-shaped JSON object so the client's
  SSE parser exercises its real code path.
- `GET /models` and `GET /v1/models` — static `{ "object": "list", "data": [...] }`.

`SetResponse(model, text)` configures the canned reply per model.
`SetToolCallResponse(model, toolName, args)` configures a single-tool-call
response (for testing tool loops). `ReceivedRequests` snapshots every request
body so tests can assert on system prompt / model / tool list.

### 2.4 `E2eTestBase` — TUnit base class

`[Before(HookType.Test)]` spins up a fresh `MockLlmServer`, allocates a fresh
temp `$HOME`, and drops a `~/.harbor/providers/mock.json` provider config that
points at the mock server. Also writes `~/.harbor/config.json` with
`onboarded: true` so the CLI doesn't launch the onboarding wizard.

`[After(HookType.Test)]` stops the server and deletes the temp home.

`GetEnv()` returns the standard env-var dict: `HARBOR_MODEL=mock/test-model`,
`MOCK_API_KEY=test-key`, `HARBOR_LOGLEVEL=Warning`, `HARBOR_SKIP_ONBOARDING=1`,
`HOME=<temp>`.

---

## 3. What's covered

### 3.1 `tests/Harbor.E2E.Cli/` (6 tests, all passing)

| Test | Asserts |
|---|---|
| `VersionCommand_PrintsVersion` | `harbor --version` exits 0; stdout contains "Harbor" + ".NET" |
| `HelpCommand_ListsCommands` | `harbor help` exits 0; stdout contains `ask`, `providers`, `sessions` |
| `TuiCommand_ListsAllRenderers` | `harbor tui` lists `spectre-tui`, `termina`, `terminal-gui`, `razor` |
| `StorageCommand_ListsBackends` | `harbor storage` lists `jsonl`, `memory`, `sqlite` |
| `ProvidersCommand_ListsAllRegisteredProviders` | `harbor providers` lists `ollama` (always-registered) |
| `AskCommand_WithMockServer_ReturnsResponse` | `harbor ask "..."` with mock server returns the canned text; `Server.ReceivedRequests.Count > 0` |

### 3.2 `tests/Harbor.E2E.App.Blazor/` (3 tests, all passing)

| Test | Asserts |
|---|---|
| `HomePage_Returns200AndContainsHarbor` | Kestrel starts, `GET /` returns 200, body contains "Harbor" |
| `StaticAssets_CssIsServed` | `GET /css/site.css` returns 200 (UseStaticFiles wired) |
| `SessionsRoute_Returns200` | `GET /sessions` returns 200 (Blazor fallback routing) |

Each Blazor test writes a fresh `~/.harbor/blazor.json` with a random
`listenPort`, launches the app via `CliDriver`, waits for the
`Harbor Blazor listening on http://localhost:PORT` banner (printed after
Kestrel's `ApplicationStarted` lifetime event fires), then issues real HTTP
requests with `HttpClient`.

---

## 4. How to run

### 4.1 Build everything first

E2E tests spawn real `dotnet exec <dll>` subprocesses. The DLLs must exist:

```bash
dotnet build tests/Harbor.E2E.Cli/Harbor.E2E.Cli.csproj
dotnet build tests/Harbor.E2E.App.Blazor/Harbor.E2E.App.Blazor.csproj
```

Both build commands pull in the framework + app under test as project refs.

### 4.2 Run the CLI E2E suite

```bash
dotnet test tests/Harbor.E2E.Cli --no-build
```

Expected: `Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6`.

### 4.3 Run the Blazor E2E suite

```bash
dotnet test tests/Harbor.E2E.App.Blazor --no-build
```

Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

### 4.4 Filter by Category

All E2E tests are tagged `[Category("E2E")]`. Run only E2E tests across the
whole solution:

```bash
dotnet test --filter "Category=E2E"
```

Useful in CI to keep E2E (slow, ~5s total) separate from the fast unit suite
(~3s for 400+ tests).

### 4.5 Detailed per-test output

```bash
dotnet test tests/Harbor.E2E.Cli --no-build -- --output detailed
```

---

## 5. How it works — wiring the mock into the CLI

The Harbor CLI resolves providers from `~/.harbor/providers/*.json`. The
`E2eTestBase.SetupAsync` hook writes a `mock.json` provider config that points
at the in-process `MockLlmServer.BaseUri`:

```json
{
  "id": "mock",
  "displayName": "Mock LLM (E2E)",
  "baseUrl": "http://localhost:54321",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "MOCK_API_KEY",
  "models": [{ "id": "test-model", "displayName": "Mock Test Model", ... }]
}
```

Combined with `HARBOR_MODEL=mock/test-model` + `MOCK_API_KEY=test-key` env
vars, the CLI's `HostBuilder` registers the mock provider and the
`OpenAiCompatibleLlmClient` hits the in-process server. The full pipeline is
exercised with zero external dependencies.

---

## 6. Roadmap — TUI + Avalonia E2E (TODO)

These are scoped but not delivered in the current sprint due to environment
constraints (PTY allocation in CI sandboxes, headless Avalonia on Linux).

### 6.1 TUI E2E (SpectreTui / Termina / Terminal.Gui / RazorConsole)

**Status:** Test projects exist at `tests/Harbor.E2E.Tui.{SpectreTui,Termina,TerminalGui,RazorConsole}/`
with placeholder tests. They will not pass in the current sandbox because:

- TUI renderers call `Console.ReadKey(true)` in raw mode, which requires a
  real PTY. The sandbox blocks `forkpty`/`openpty` via seccomp.
- `TuiDriver` wraps `script -qfc` on Linux to allocate a PTY, but `script(1)`
  is killed before it can exec the app.
- `E2eTestBase.EnsurePtyAvailable()` guards every TUI test — it logs a `[SKIP]`
  message and the test passes as a no-op when PTY is unavailable.

**To finish:**
1. Run in a sandbox with PTY allocation (or use ConPTY on Windows).
2. Replace placeholder tests with real driver scenarios: type a prompt, wait
   for the agent's response to render, assert on the visible screen.
3. Wire tool-call canned responses to exercise the tool-call card UI.

### 6.2 Avalonia desktop E2E — DELIVERED

**Status:** ✅ `tests/Harbor.E2E.App.Avalonia/` is implemented and passing.
7 E2E tests run against the real `Harbor.App.Avalonia` app via
`Avalonia.Headless`'s in-process off-screen software renderer. No `xvfb` or
real X display required — the headless platform renders to an in-memory
bitmap.

**Architecture:**

```
Test thread (TUnit)                 UI thread (dedicated)
─────────────────                   ──────────────────────
[Before(HookType.Test)]
  └─ new HeadlessAvaloniaDriver      static ctor starts UI thread
     └─ InitializeAsync()              └─ UIThreadMain():
         ├─ AppHost.BuildAsync()  ──┐     1. Dispatcher.UIThread  (binds)
         │   (on test thread,        │     2. DispatcherReady.SetResult()
         │    ConfigureAwait(false)) │     3. Dispatcher.UIThread.MainLoop(ct)
         │                           │        └─ pumps jobs forever
         └─ Dispatcher.UIThread  ◀───┘
            .InvokeAsync(setup
              Avalonia app)
                                          
[Test body]
  └─ Driver.ScreenshotAsync("01-...")
       └─ Dispatcher.UIThread.InvokeAsync(() => {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Dispatcher.UIThread.RunJobs();
            var bitmap = window.CaptureRenderedFrame();
            bitmap.Save(fs);  // → ~/.harbor/test-screenshots/01-...png
          }).GetAwaiter().GetResult();
```

**Key design decisions:**

1. **Dedicated UI pump thread.** TUnit runs each test method on a fresh
   threadpool thread, but Avalonia's `Dispatcher` + `Application` are bound
   to ONE thread for the process lifetime. The driver's static constructor
   starts a dedicated background thread that binds the dispatcher to itself
   and enters `Dispatcher.UIThread.MainLoop(ct)`. Test threads then marshal
   all UI access via `Dispatcher.UIThread.InvokeAsync(...)` — the job lands
   in the queue, the UI thread's MainLoop picks it up, runs it, and the
   Task returned by `InvokeAsync` completes. No manual `RunJobs` pumping
   anywhere in test code.

2. **`InvokeAsync` + `GetAwaiter().GetResult()`** for synchronous-style
   access (`FindControlByName`, `FindButtonByText`, `OnUIThread`).
   `DispatcherOperation` doesn't have `ConfigureAwait`, so we block via
   `GetAwaiter().GetResult()`. Safe because the UI thread's MainLoop is
   independent — no deadlock.

3. **`ForceRenderTimerTick`** drives a fresh render pass. The headless
   render timer doesn't fire automatically (no 60Hz vsync), so
   `CaptureRenderedFrame()` returns the LAST rendered bitmap. We tick the
   timer explicitly + call `Dispatcher.UIThread.RunJobs()` to drain the
   queued render job before capturing.

4. **Visual-tree-walking `FindByName`** instead of
   `ControlExtensions.FindControl<T>`. The latter only searches the
   immediate `NameScope` of the control it's called on — it doesn't
   recurse into child `UserControl`s. Since `InputBox` lives inside
   `ChatView.axaml` (a `UserControl` embedded in `MainWindow`), we walk
   the visual tree depth-first and match by `StyledElement.Name`.

5. **`OnUIThread<T>(Func<T>)` helper** for reading dispatcher-affine
   properties (`Button.IsEnabled`, `TextBox.Text`) from the test thread.
   Without this, tests throw `"calling thread cannot access this object
   because a different thread owns it"`.

**Tests (7, all passing):**

| Test | Asserts | Screenshot |
|---|---|---|
| `MainWindow_OpensWithoutCrash` | App boots; `MainWindow` non-null; PNG > 5KB | `01-main-window.png` |
| `Sidebar_ShowsHarborBrand` | "Harbor" appears in the rendered visual tree | `02-brand.png` |
| `ChatInput_AcceptsText` | `InputBox` TextBox accepts typed text; `Text` reflects it | `03-input-typed.png` |
| `SendButton_ExistsAndIsEnabled` | "Send ▶" button found by text; `IsEnabled` true when input non-empty | `04-send-button.png` |
| `SessionSidebar_ShowsSearchBox` | "Search sessions" watermark visible | `05-session-sidebar.png` |
| `StatusBar_ShowsProviderAndModel` | Status bar shows "ollama" (default provider) | `06-status-bar.png` |
| `OnboardingWindow_RendersWelcomeScreen` | Onboarding window renders; "Harbor" in brand header | `07-onboarding.png` |

**How to run:**

```bash
dotnet build tests/Harbor.E2E.App.Avalonia/Harbor.E2E.App.Avalonia.csproj
dotnet test tests/Harbor.E2E.App.Avalonia --no-build
ls -la ~/.harbor/test-screenshots/
```

Expected: `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7`.
Screenshots at `~/.harbor/test-screenshots/01-main-window.png` through
`07-onboarding.png` — open them in any image viewer to SEE what the UI
looks like without running the app.

**Limitations:**

- The Avalonia.Headless software renderer doesn't always pick up dynamic
  text changes (typed text in a TextBox) in the captured bitmap — the
  `CaptureRenderedFrame()` API returns the last rendered frame, and
  `ForceRenderTimerTick` may not fully flush the text formatter's cached
  text runs. The tests assert on the in-memory `TextBox.Text` property
  (which IS updated correctly) rather than the rendered bitmap pixels.
  Visual review of the PNGs shows the static window chrome (title bar,
  sidebar, status bar) but may not show dynamically typed text.
- Tests are tagged `[NotInParallel]` because the driver mutates `$HOME`
  (process-wide env var) and shares the process-wide Avalonia
  `Application` singleton.
- Each test class pays a one-time ~500ms init cost (builds the DI host +
  Avalonia app). Subsequent tests in the same class reuse the singleton.
- **TwoWay binding race**: the `TextBox.Text ↔ ChatViewModel.InputText`
  binding is eventually-consistent across dispatcher cycles. Tests that
  set `TextBox.Text` and then read it back in SEPARATE `InvokeAsync`
  calls can see the value revert to a previous test's text (a deferred
  `VM→TextBox` binding update lands between the set and the read). The
  fix is to set AND read in the SAME `OnUIThread` call — see
  `ChatInput_AcceptsText` for the pattern.

### 6.3 WPF / MAUI E2E

Out of scope for Linux CI. WPF needs the Windows Desktop runtime; MAUI needs
the maui-tizen workload. Both are skipped at the slnx level on Linux.

---

## 7. Conventions

- **`sealed` classes** for all drivers and the mock server.
- **`ConfigureAwait(false)`** on every await in the framework + tests.
- **XML docs** on every public member of the framework.
- **Deterministic waits**: every "wait for X" uses `WaitForTextAsync` with an
  explicit `TimeSpan` timeout. No `Thread.Sleep` in test bodies.
- **Temp HOME isolation**: each test gets a fresh `~/.harbor/` so concurrent
  test classes don't collide on config files or the mock server port.
- **TUnit `[Category("E2E")]`** on every test class + method for filtering.
- **No network**: every external call (LLM, model list) hits the in-process
  mock. Tests pass offline.
