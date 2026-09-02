# IDE Integration Patterns Analysis
## Modern Coding Tools vs. Harbor-Harness

> Analyzed: Codex /ide IPC bridge, Cursor IDE, GitHub Copilot, JetBrains AI Assistant, Continue.dev, Cline
> Harbor context: `/mnt/projects/Harbor-Harness` (branch `dev`)

---

## 1. Codex CLI — `/ide` IPC Bridge

### 1.1 How the CLI communicates with the IDE
- **Transport**: Unix domain socket at `$TMPDIR/codex-ipc/ipc-$UID.sock` (macOS/Linux); named pipes on Windows.
- **Protocol**: JSON-RPC-style message passing over the socket. The CLI TUI spawns a background task that maintains the connection.
- **Implementation**: Rust code in `codex-rs/tui/src/ide_context/ipc.rs` within the open-source Codex repo.
- **Lifecycle**: The IDE extension creates the socket when activated; the CLI connects on `/ide` slash command; editor state is pushed continuously on focus/selection changes.

### 1.2 What data flows back and forth
```json
{
  "file": "src/handlers/auth.ts",
  "selection": {
    "start": { "line": 27, "character": 0 },
    "end":   { "line": 42, "character": 0 },
    "active": { "line": 42, "character": 0 },
    "anchor": { "line": 27, "character": 0 }
  },
  "tabs": [ ... ]
}
```
- **File path** of the active editor.
- **Selection range** with `active` vs `anchor` to distinguish cursor direction (top-down vs bottom-up selection).
- **Open tabs** list.
- The CLI injects this context automatically into the agent conversation — no explicit `/mention` needed.

### 1.3 How diffs/reviews are displayed
- CLI-side: TUI-rendered diffs with syntax-highlighted pager.
- Commands: `/diff` shows unstaged/staged changes; `/review` triggers agent analysis of the diff.
- No native IDE diff view is opened from the CLI; review happens in the terminal.

### 1.4 How inline editing works
- The `/ide` bridge itself does **not** perform inline edits. Inline editing is handled by the **Codex IDE extension** inside VS Code/Cursor, which has direct editor access.
- The CLI uses its own TUI composer with Vim mode (`/vim`) for terminal-native editing.

### 1.5 What Harbor could adopt
- **Unix socket / named pipe bridge**: Harbor already has `Harbor.Ipc.*` (Abstractions, InProcess, Client, Server). A UDS-based bridge would let Harbor CLI receive live editor context from any IDE extension.
- **Context injection format**: The JSON payload (file + selection + tabs) is simple enough to model as a `Harbor.Abstractions.Contracts` record.
- **Background connection task**: Harbor's `Channel<T>` streaming and `IEventBus` make this natural — publish `IdeContextEvent` on the bus whenever the socket pushes new state.

---

## 2. Cursor IDE

### 2.1 How the CLI/tool communicates with the IDE
- **Architecture**: Cursor is a **fork of VS Code**, not an extension. This means it owns the rendering pipeline, file system hooks, and extension host — no extension API limitations.
- **CLI**: `cursor-agent` CLI runs independently but shares the same model subscription and `.cursor/rules` configuration. It can also act as an ACP server (`agent acp` over stdio).
- **IDE ↔ CLI**: The CLI reads `mcp.json` automatically; there is no deep IDE↔CLI IPC beyond shared config and filesystem.

### 2.2 What data flows back and forth
- **Local context**: Open files, terminal output, selections, diagnostics.
- **Codebase index**: Cursor chunks code locally, embeds via remote server, stores vectors in **Turbopuffer** (serverless vector DB). Only embeddings + obfuscated path metadata are stored remotely; source code stays local.
- **Index sync**: Merkle trees (SHA-256 per file) detect changes; simhash similarity search enables cross-codebase index seeding at 50% write discount.
- **Rules**: `.cursor/rules/*.mdc` and `AGENTS.md`/`CLAUDE.md` at project root.

### 2.3 How diffs/reviews are displayed
- **Cmd+K (Inline Edit)**: Diff appears inline in the editor; accept with `Tab`, reject with `Esc`.
- **Composer (Cmd+I / Cmd+Shift+I)**: Multi-file diff view with per-file and per-hunk accept/reject. Red/green inline diff visualization.
- **Agent mode**: Background agents push changes to a separate branch/PR; diffs are reviewed in the cloud or on merge.

### 2.4 How inline editing works
- **Tab completion**: Multi-line ghost text predictions (not single-token). Can jump cursor to next edit location across a file.
- **Cmd+K**: Select code → type instruction → Cursor rewrites selection and shows a diff before accepting.
- **Composer**: Autonomous multi-file editing agent. Plans, edits, runs terminal commands, reads errors, iterates.
- **Key insight**: Because Cursor owns the fork, it intercepts keypresses, renders ghost text, manages the undo stack around AI edits, and controls diff application across files — none of this is possible from the VS Code extension API.

### 2.5 What Harbor could adopt
- **Codebase indexing**: Harbor could embed a local chunker + vector store (e.g., SQLite + `sqlite-vec` or a remote Turbopuffer-compatible endpoint) for semantic search across large codebases.
- **Fork-vs-extension decision**: If Harbor ever builds a desktop GUI (see `docs/DESKTOP_APP_PLAN.md`), an embedded editor control (AvaloniaEdit / AvalonEdit / Monaco) gives the same pipeline access Cursor enjoys.
- **Merkle-tree sync**: Useful for Harbor's remote/daemon mode (`Harbor.Transport.Remote`) to detect which files changed between sessions.

---

## 3. GitHub Copilot

### 3.1 How the CLI/tool communicates with the IDE
- **IDE extension → GitHub Backend**: HTTPS proxy architecture. IDE assembles a prompt, sends it to `github.com` endpoints.
- **Auth flow**: OAuth → short-lived code completion token (10–30 min, like a train ticket). Proxy validates signature without calling external auth services.
- **ACP server mode**: `copilot --acp` spawns a JSON-RPC 2.0 server over stdio. JetBrains, Zed, and other ACP clients can drive Copilot CLI directly.
- **Agent Host Protocol (AHP)**: VS Code's Agent Host manages persistent sessions. The `AgentHostClient` connects editor windows to a single host process, decoupling the agent runtime from one editor window.
- **MCP**: Copilot IDE consumes MCP servers (stdio/SSE/Streamable HTTP) configured in `.vscode/mcp.json`.

### 3.2 What data flows back and forth
- **Prompt assembly**: Open files, selected text, workspace symbols, `.github/copilot-instructions.md`.
- **Working Set** (Copilot Edits): User-curated set of files to edit; auto-includes active editors.
- **Streaming**: LLM tokens streamed back; speculative decoding endpoint applies edits faster than the foundation model alone.
- **Telemetry**: Extensive usage metrics (request latency, token counts, acceptance rates).

### 3.3 How diffs/reviews are displayed
- **Inline Chat (Ctrl+I)**: VS Code shows an inline diff overlay in the editor with `Keep` / `Undo` buttons. Hovering an inline change accepts/rejects just that hunk.
- **Copilot Edits**: "Working Set" UI — edits stream inline across multiple files. Pending edits shown with squared-dot indicators in Explorer/tabs. Auto-navigate to next pending edit on resolve.
- **Source Control integration**: Staging automatically accepts pending edits; discarding rejects them.
- **Auto-accept**: `chat.editing.autoAcceptDelay` setting for hands-free review after a countdown.
- **Agent mode**: Folder-isolated or worktree-isolated sessions. Changes are applied to disk; reviewed via multi-file diff, Source Control, or PR workflow. Checkpoints enable rollback.

### 3.4 How inline editing works
- **Inline Chat**: `Ctrl+I` opens a prompt scoped to the active editor. Copilot replies with a diff that streams inline. Accept with `Tab`, dismiss with `Esc`.
- **Edit mode**: User describes a change across a working set. Copilot applies **speculative decoding** — a fast endpoint transforms the foundation model's output into inline edits.
- **Agent mode**: Autonomous loop — reads files, edits, runs terminal commands, reacts to errors, iterates. Uses `apply_patch` tool (custom patch format with `*** Begin Patch` / `*** End Patch` envelopes, `@@` hunks, `+`/`-`/` ` lines).

### 3.5 What Harbor could adopt
- **Dual-model architecture**: A fast "speculative" edit-applier alongside a slower reasoning model. Harbor could implement this as a two-stage tool pipeline.
- **Working Set concept**: Harbor's existing `PermissionRuleset` (per-tool allow/ask/deny) already scopes what the agent can touch. A UI "working set" would just be a user-friendly overlay on top of those rules.
- **Patch tool**: Harbor has a `patch` builtin tool. Copilot's `apply_patch` format (with healing prompts for fuzzy matching) is a good reference for making the tool more resilient.
- **ACP server mode**: Harbor CLI could expose `harbor --acp` to become a first-class citizen in JetBrains/VS Code (via community ACP clients).

---

## 4. JetBrains AI Assistant

### 4.1 How the CLI/tool communicates with the IDE
- **Native plugin**: Kotlin-based plugin deeply embedded in the IntelliJ platform. Not an afterthought extension.
- **ACP**: JetBrains IDEs now co-develop ACP. External agents register in `~/.jetbrains/acp.json` and appear in the AI Assistant agent picker. Communication is JSON-RPC over stdio.
- **MCP**: JetBrains IDEs (2025.2+) ship with an **integrated MCP server**, allowing external clients (Claude Code, Codex, VS Code) to control the IDE via MCP tools.
- **Permission model**: JetBrains determines which actions an AI agent may perform. Planned changes are shown as diffs that the user must approve before application.

### 4.2 What data flows back and forth
- **IDE structural context**: Unlike text-based agents, JetBrains AI Assistant has access to type analysis, inheritance trees, interface implementations, and refactoring engines — things text-only LLMs cannot see.
- **Open files, selection, chat history**.
- **MCP tools**: External MCP servers can be injected into the agent's toolset.
- **Agent capabilities**: ACP handles capability discovery, message routing, model selection hints, and streaming responses.

### 4.3 How diffs/reviews are displayed
- **Native IntelliJ diff viewer**: The gold standard — syntax-highlighted, supports partial hunk acceptance, integrates with undo.
- **Junie agent mode**: Shows diffs before applying. Users can review, selectively accept/reject, and rollback to previous checkpoints.
- **AI Assistant chat panel**: Code changes appear as inline diffs in the editor or as diff previews in the chat.

### 4.4 How inline editing works
- **Ghost text**: Next-line / next-block completions appear in gray; accept with `Tab`.
- **Inline chat**: `Ctrl+\` opens a prompt directly in the editor. Diff view appears inline for review.
- **AI Actions**: Right-click context menu for explain, refactor, document, generate tests, etc.
- **Codebase Mode**: Toggle in chat that gives the model additional project-wide context.

### 4.5 What Harbor could adopt
- **IDE-as-MCP-server pattern**: JetBrains 2025.2 lets external tools control the IDE via MCP. Harbor could build an MCP server that exposes IDE-like capabilities (file tree, diagnostics, terminal) so Harbor agents can drive any MCP-compatible editor.
- **Permission-gated diffs**: Harbor's `ask` permission mode maps directly to JetBrains' "show diff before apply" model.
- **Checkpoint/rollback**: Harbor's `ISessionStore` + git worktree snapshots could implement JetBrains-style rollback.

---

## 5. Continue.dev

### 5.1 How the CLI/tool communicates with the IDE
- **Core + extensions pattern**: A shared TypeScript `core/` package contains all AI logic (model routing, context, indexing, tool execution). It has **zero IDE dependencies**.
- **IDE interface abstraction**: Each extension implements the `IDE` interface (40+ methods for file I/O, navigation, subprocess execution, git operations, workspace access).
- **GUI webview**: React-based UI rendered inside the IDE's webview panel. Communication uses a typed `postMessage` / `onMessage` protocol defined in `protocol/webviewProtocol.ts`.
- **JetBrains bridge**: Kotlin plugin uses JCEF (Chromium Embedded) for the webview and a proxy layer to talk to the TypeScript core.

### 5.2 What data flows back and forth
- **IDE → Core**: File contents, selection ranges, open files list, git status, terminal output, problems/diagnostics, debug locals.
- **Core → GUI**: Streaming LLM tokens, tool call events, diff previews, agent state.
- **Context providers**: `@code`, `@codebase`, `@diff`, `@terminal`, `@problems`, `@github`, `@gitlab`, `@docs`, `@web`, `@url`, `@clipboard`, etc.
- **External services**: MCP servers, Git/GitHub/GitLab, documentation sites, databases.

### 5.3 How diffs/reviews are displayed
- **Edit mode**: User selects code → types instruction → model response is streamed back as an **inline diff** directly in the highlighted range.
- **Accept/reject**: `Cmd+Opt+Y` / `Ctrl+Alt+Y` accepts; `Cmd+Opt+N` / `Ctrl+Alt+N` rejects. `Cmd+Shift+Enter` accepts all.
- **Chat mode**: Multi-file changes appear as a unified diff in the chat panel with per-file accept/reject.
- **Diff engine**: `core/diff/` processes model outputs into structured edits.

### 5.4 How inline editing works
- **Edit mode**: Gathers highlighted code + current file → prompts model → streams diff inline → user accepts/rejects per-change or all-at-once.
- **Autocomplete**: FIM (fill-in-middle) completion with ghost text in the editor.
- **Next Edit**: Experimental feature that predicts the next code modification before the user types.
- **Agent mode**: Multi-step agentic workflows with tool calls, file creation, and terminal execution.

### 5.5 What Harbor could adopt
- **The `IDE` interface**: Harbor should define a similar `IHarborIdeClient` (or reuse the name `IDE`) with methods like `readFile`, `writeFile`, `readRangeInFile`, `getOpenFiles`, `getCurrentFile`, `getBranch`, `getGitRootPath`, `subprocess`, `runCommand`. This makes any future IDE extension a thin adapter.
- **Typed webview protocol**: If Harbor builds desktop/Blazor UIs, a typed message contract (like Continue's `webviewProtocol.ts`) between the .NET core and the webview prevents drift.
- **Context providers**: Harbor's existing `@codebase`, `@diff`, `@terminal` slash commands in the TUI are already analogous to Continue's context providers. Extract them into a shared `Harbor.Context` project so both TUI and IDE can consume them.

---

## 6. Cline

### 6.1 How the CLI/tool communicates with the IDE
- **gRPC-over-postMessage**: The React webview (sandboxed iframe) cannot call Node.js APIs directly. Cline serializes all calls as JSON-serialized Protobuf messages over VS Code's `postMessage` bridge.
- **WebviewGrpcBridge**: Central dispatcher in the extension backend. Supports unary (request/response) and server-streaming (subscription) call patterns.
- **HostProvider abstraction**: Singleton registry that abstracts platform-specific APIs (filesystem, terminal, windowing, diff view). `VscodeSessionHost` is the VS Code implementation; `LocalRuntimeHost` is used for CLI/standalone.
- **SDK architecture**: `@cline/core` and `@cline/agents` power the backend. `SdkController` bridges SDK events to the webview's gRPC streams.

### 6.2 What data flows back and forth
- **ExtensionState** (extension → webview): `clineMessages[]`, `apiConfiguration`, `autoApprovalSettings`, `taskHistory`, `turnState`, `workspaceRoots`, `backgroundCommandRunning`, `mcpServers`, `platform`, `version`, `distinctId`.
- **ClineMessage**: Each message has `type: "ask" | "say"`, with subtypes like `COMMAND`, `TOOL`, `FOLLOWUP`, `TEXT`, `REASONING`, `completion_result`, `diff_error`, `checkpoint_created`.
- **ClineSayTool**: Tool results include `editedExistingFile`, `newFileCreated`, `fileDeleted`, `readFile`, `searchFiles`, with optional `diff`, `startLineNumbers`, `content`.
- **State synchronization**: `getStateToPostToWebview` aggregates `StateManager`, `McpHub`, and active `Task` into a snapshot. Webview applies it via a reducer.

### 6.3 How diffs/reviews are displayed
- **VS Code native diff editor**: Cline registers `DIFF_VIEW_URI_SCHEME` and `EDIT_PREVIEW_URI_SCHEME` content providers. Diffs open as native editor tabs.
- **Edit preview**: Mutable content provider plays a **simulated streaming animation** on the diff's right side.
- **Chat view**: File changes appear in the chat with `Accept` / `Reject` buttons. The `SdkDiffEditCoordinator` manages the diff-edit pipeline.
- **Checkpoint system**: Git-based checkpoints enable rollback to any prior state.

### 6.4 How inline editing works
- **Inline mode**: Cline can edit specific sections of a file with targeted improvements (focused on visible editor region).
- **Diff edits**: `editedExistingFile` tool calls include a unified diff string. The extension applies these via the native diff editor or inline edit pipeline.
- **Approval flow**: Every edit requires explicit user approval (unless auto-approval rules match). The extension bridges SDK `requestToolApproval` to VS Code UI.

### 6.5 What Harbor could adopt
- **gRPC-over-postMessage pattern**: If Harbor builds a VS Code extension, wrapping `postMessage` in a proto-defined RPC contract (like Cline's `task.proto`, `state.proto`, `ui.proto`) gives type safety and streaming for free.
- **Checkpoint/rollback**: Harbor's `ISessionStore` (Jsonl/Sqlite) could store pre-edit snapshots. A `git worktree` or file-backup approach mirrors Cline's git-based checkpoints.
- **Diff animation**: Harbor's `DiffEngine` in `ConsoleEx` already renders cell-grid diffs. A streaming animation for IDE diffs would reuse the same incremental diff logic.
- **HostProvider pattern**: Harbor's `Harbor.Ipc.*` and `Harbor.Plugins.Host` already implement a host abstraction. Generalizing it into a `IHarborHost` with `createWebview`, `createDiffView`, `createTerminalManager` would let the same agent core run in VS Code, JetBrains, CLI, and Avalonia.

---

## 7. Cross-Cutting Patterns & Recommendations for Harbor

### 7.1 Communication protocols
| Tool | Protocol | Why |
|------|----------|-----|
| Codex CLI | Unix socket + JSON | Simple, local-only, low-latency |
| Cursor | Native fork APIs | Full pipeline control |
| Copilot | HTTPS + ACP stdio + AHP | Cloud-first, with local ACP escape hatch |
| JetBrains | ACP stdio + MCP HTTP | Open standards, IDE-native |
| Continue | Typed postMessage / JCEF bridge | Core-driven, thin adapters |
| Cline | gRPC-over-postMessage | Strongly typed, streaming, multi-platform |

**Harbor recommendation**: Start with **ACP stdio** for broadest editor compatibility (JetBrains, Zed, Neovim, VS Code via community bridge). Add a **Unix socket bridge** (like Codex) for fast local IDE context. Keep the existing `Harbor.Ipc.*` layer as the transport abstraction.

### 7.2 Data flow architecture
- All six tools separate **agent core** from **UI surface**.
- The core publishes events; the UI subscribes.
- Harbor already does this via `IEventBus`. The missing piece is a **typed IDE message contract** (similar to Continue's `protocol/` or Cline's `.proto` files).

### 7.3 Diff/review display
- **Native diff is king**: JetBrains and Cline both open the IDE's native diff editor. Terminal-only tools (Codex CLI, classic Copilot) show diffs in chat/terminal.
- **Streaming diffs**: Copilot Edits and Cline's edit preview stream diffs incrementally.
- **Harbor opportunity**: Reuse the `DiffEngine` from `ConsoleEx` for terminal diffs, and add an IDE-native diff provider (VS Code `TextDocumentContentProvider`, JetBrains `DiffView`) for graphical diffs.

### 7.4 Inline editing
- **Ghost text** (Copilot, JetBrains, Cursor Tab) is the lowest-friction path.
- **Inline diff overlay** (Copilot inline chat, Continue Edit, Cursor Cmd+K) is better for targeted changes.
- **Multi-file composer** (Cursor Composer, Copilot Edits, Continue Chat) is required for cross-file refactors.
- **Harbor path**: Implement ghost text first (easiest via LSP or editor extension APIs), then inline diff overlay, then composer.

### 7.5 What Harbor should adopt *now*
1. **`IHarborIdeClient` interface** in `Harbor.Abstractions` — file I/O, nav, git, terminal, workspace. Thin adapters in `Harbor.Ide.{Vscode,JetBrains,None}`.
2. **ACP server mode** in `apps/Harbor.App.Cli` — `harbor --acp` spawns stdio JSON-RPC, making Harbor instantly available in JetBrains AI Assistant and Zed.
3. **Typed message protocol** between core and any webview/UI — define in `Harbor.Abstractions.Contracts.Messages`.
4. **Unix socket context bridge** — mirror Codex `/ide` so Harbor CLI can pull file/selection/tabs from a companion IDE extension.
5. **Diff provider abstraction** — `IDiffRenderer` with implementations for ConsoleEx (cell-grid), VS Code (native diff), JetBrains (native diff), and Avalonia (AvaloniaEdit diff).
6. **Leverage existing strengths**: Harbor's event bus, plugin system, MCP support, permission rules, and storage backends are already world-class. The IDE gap is purely in the **presentation layer** — exactly what the desktop app plan (`docs/DESKTOP_APP_PLAN.md`) is scoped to solve.

---

## 8. Source References
- Codex `/ide` IPC: `openai/codex` repo, `codex-rs/tui/src/ide_context/ipc.rs`, GitHub issue #25402
- Cursor: `cursor.com/blog/cli`, `medium.com/@ayush.s0410/inside-cursor-3`
- Copilot: `github.blog/copilot-ask-edit-and-agent-modes`, `code.visualstudio.com/docs/chat/inline-chat`, `vscode-copilot-chat` source (`editFromDiffGeneration.ts`, `applyPatchTool.tsx`)
- JetBrains: `jetbrains.com/help/ai-assistant/acp.html`, `jetbrains.com/help/ai-assistant/mcp.html`
- Continue: `deepwiki.com/continuedev/continue`, `docs.continue.dev/ide-extensions/edit`
- Cline: `deepwiki.com/cline/cline`, `cline.bot/blog/cline-v3-12`, `sdk/ARCHITECTURE.md`
- ACP: `agentclientprotocol.org`, `dev.to/alextdev/agent-client-protocol`
- Harbor: `/mnt/projects/Harbor-Harness/README.md`, `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/DESKTOP_APP_PLAN.md`
