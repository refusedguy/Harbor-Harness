Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт IDE Integration.

## Context
Harbor — terminal-native harness, но IDE integration отложен на v0.7 (LSP) и /ide IPC bridge
вообще выбит из UI-FINAL deferred list. Конкуренты (Orca с electron-pty + xterm, Kilo с VS Code extension)
уже имеют нативную интеграцию.

Harbor должен выиграть не на "лучшем LSP", а на **терминальном опыте**: attach к запущенному Harbor
из любого редактора, инжектить промпты, читать поток, не теряя контекст сессии.
Floating terminal pane внутри Avalonia — cherry on top; /ide bridge — competitive moat.

## Tasks (выполняй по порядку)

1. **/ide IPC bridge (stdio + NDJSON).**
   Реализуй протокол: external editor запускает `harbor ide --session <id>`, Harbor подключается по stdio,
   принимает JSON-RPC: `inject_prompt`, `read_stream`, `abort`, `list_sessions`.
   Используй существующий MessagePackRpcClient/Server как референс.
   Acceptance: простой ts/node клиент может attach к running Harbor и отправить промпт —
   ответ появляется в TUI в реальном времени.

2. **LSP client in-process.**
   Добавь OmniSharp.Extensions.LanguageServer.Client, реализуй 5 builtin language servers
   (TypeScript, Python, Go, Rust, C#) auto-spawn на открытии файла в Avalonia.
   Диагностики, references, definition — поверх Harbor tools (`read`, `edit` с injected diagnostics).
   Acceptance: открываешь .ts файл → diagnostics подсвечиваются, `go to definition` работает через tool call.

3. **Floating terminal pane в Avalonia.**
   Detachable PTY panel: split/stack, session-aware working directory, environment inheritance.
   Не webview, не Electron — нативный Avalonia control поверх PtyHarness (уже есть в ConsoleEx PTY tests).
   Acceptance: можно открыть 2 PTY панели, одна в /tmp, другая в проекте, каждая имеет свой history.

4. **MCP HTTP + SSE transports.**
   Добавь McpHttpTransport + McpSseTransport в `src/Harbor.Tools.Builtin/Tools/Mcp/`.
   Lazy connect, reconnect on failure, OAuth placeholder.
   Acceptance: Harbor running в IDE может подключаться к удалённому MCP серверу по HTTP без stdio.

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. /ide bridge НИКОГДА не блокирует agent loop — все IPC вызовы асинхронны с per-request CTS.
3. LSP серверы runs out-of-process (stdio), не in-process — AOT-friendly, не ломает NativeAOT roadmap.
4. В Avalonia НИКОГДА не добавляй webview / Electron / браузерный контрол. Только Avalonia + PTY.
5. После каждого коммита: dotnet test tests/Harbor.Ipc.Tests/ -c Release --no-build.

## Deliverables
- /ide IPC bridge (stdio + NDJSON protocol)
- Node.js / TypeScript клиент для attach к running Harbor
- LSP client + 5 builtin language servers
- Floating terminal pane в Avalonia (split/stack PTY panels)
- MCP HTTP + SSE transports
- report.html в `.kilo-docs/sprints/ide-integration/`
