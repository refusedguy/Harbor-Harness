# Harbor.Tui.Notifications

Non-interactive renderer that fires desktop OS notifications on key agent
events: errors, completion, compaction, and tool failures. Designed for
long-running agents in the background where the user has switched to another
window and wants to be notified when the agent needs attention.

## When to use

- You started Harbor with `harbor ask "refactor this entire folder"` and
  switched to a different task.
- You run Harbor inside CI and want a notification when a long job finishes.
- You want a "watch loop" renderer: agent runs, you go do other work, you get
  pinged when it's done or stuck.
- You don't want any terminal output — just notifications.

## Platform support

| OS      | Backend                           | Notes                                    |
|---------|-----------------------------------|------------------------------------------|
| Linux   | `notify-send` (libnotify)         | Install with `apt install libnotify-bin` |
| macOS   | `osascript` (Notification Center) | Built-in, no install needed              |
| Windows | `msg.exe` (modal dialog)          | Swap in `snoretoast.exe` for toasts      |
| Other   | Null backend (silent)             | Logs a warning, never throws             |

## Who starts the process

This assembly starts nothing. Each backend builds an argv and hands it to
`INotificationProcessRunner` (Domain, `Harbor.Abstractions/Notifications/`); the
production implementation is `ProcessNotificationRunner` in
`Harbor.Application.Notifications`, beside `ProcessGitQuery` (#665).

The seam is injected through the constructor and deliberately has **no
default**: before it, each backend owned a `Process` and a three-second
`WaitForExit` that no caller could cancel, which is what
`PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES` was grandfathering this assembly
for. Constructing the renderer requires naming a runner.

```csharp
using Harbor.Application.Notifications;   // Infrastructure side
using Harbor.Tui.Notifications;           // Presentation side

var runner = new ProcessNotificationRunner(loggerFactory.CreateLogger<ProcessNotificationRunner>());
var renderer = new NotificationTuiRenderer(logger, runner);
```

`ProcessNotificationRunner` is best effort by contract: a missing notifier, a
timeout, or a non-zero exit is logged and never thrown. A test can substitute
`RecordingNotificationRunner` (`tests/Harbor.Tui.RendererTests/Support/`) and
observe the exact argv without forking anything.

## Dependencies

- `Harbor.Abstractions` (`INotificationProcessRunner`)
- `Harbor.Terminal.Abstractions` (`BaseTuiRenderer`, `ITuiRenderContext`)
- `Microsoft.Extensions.Logging.Abstractions`

No external NuGet packages. The renderer holds no `System.Diagnostics.Process`;
the spawn lives in `Harbor.Application`.

## Files

- `Harbor.Tui.Notifications.csproj` — `net10.0`, references `Harbor.Terminal.Abstractions`;
  `InternalsVisibleTo` for `Harbor.Tui.RendererTests` so the backends' argv can be pinned.
- `NotificationTuiRenderer.cs` — sealed `NotificationTuiRenderer : BaseTuiRenderer`. Listens to
  `AgentEndEvent`, `AgentErrorEvent`, `CompactionCompletedEvent`, and
  `ToolExecutionEndEvent` (errors only); routes each to the detected backend.
- `INotificationBackend` — platform abstraction with
  `Notify(title, body, isError, ct)`. Abstracts the *shape* of a notification.
- Concrete backends (all in the same file): `LinuxNotifySendBackend`, `MacOsascriptBackend`,
  `WindowsToastBackend`, `NullNotificationBackend`. Each holds an
  `INotificationProcessRunner` and nothing else — no process, no logger, no try/catch.
- `NotificationRenderContext : ITuiRenderContext` — absorbs render calls; this renderer is output-free.

## Event → notification mapping

| Agent event                     | Notification                  |
|---------------------------------|-------------------------------|
| `AgentErrorEvent`               | "Harbor — error" (red)        |
| `AgentEndEvent`                 | "Harbor — done"               |
| `CompactionCompletedEvent`      | "Harbor — compacted"          |
| `ToolExecutionEndEvent` (error) | "Harbor — tool <name> failed" |

Successful tool calls do not fire notifications (too noisy).

## How it works

```csharp
public override async Task RenderAsync(AgentEvent @event, CancellationToken ct)
{
    await base.RenderAsync(@event, ct);
    switch (@event)
    {
        case AgentErrorEvent err:
            _backend.Notify("Harbor — error", err.Message, isError: true);
            break;
        case AgentEndEvent:
            _backend.Notify("Harbor — done", "Agent finished.", isError: false);
            break;
        case CompactionCompletedEvent cc:
            _backend.Notify("Harbor — compacted", $"Pruned {cc.PrunedMessageCount} msgs.", false);
            break;
        case ToolExecutionEndEvent tee when tee.IsError:
            _backend.Notify($"Harbor — tool {tee.ToolName} failed", tee.Result.Output, true);
            break;
    }
}
```

Platform detection uses `RuntimeInformation.IsOSPlatform`. On Linux it asks
the runner for `notify-send`; on macOS for `osascript -e 'display
notification...'`; on Windows for `msg.exe` (modal dialog). For proper Windows
Action Center toasts, install `snoretoast.exe` and replace
`WindowsToastBackend`. The `switch` in `RenderAsync` below is historical: the
mapping moved into the registered `IAgentEventHandler`s in #185 — the snippet is
kept to show the event mapping, not the current call path.

## Build

```bash
# Cross-platform build, no extra workloads needed
dotnet build src/Harbor.Tui.Notifications/Harbor.Tui.Notifications.csproj -c Release

# Exercise the renderer (it is not a registered HARBOR_TUI backend — see
# "Selecting this renderer" above):
dotnet run --project tests/Harbor.Tui.RendererTests -c Release --no-build
```

On Linux you may need to install libnotify first:

```bash
sudo apt install libnotify-bin   # Debian/Ubuntu
sudo dnf install libnotify       # Fedora
```

## Selecting this renderer

> **Not currently selectable.** `NotificationTuiRenderer` is **not** registered
> in `TuiBackendRegistry`, and `src/Harbor.Hosting` carries no
> `ProjectReference` to this project — so `HARBOR_TUI=notifications` does not
> resolve, and `tui: "notifications"` in `~/.harbor/config.json` falls through
> the fallback rule to `ansi` (or `plain`) with a warning. This project is
> exercised by `tests/Harbor.Tui.RendererTests`, not by the shipped CLI.
>
> Earlier revisions of this README documented `HARBOR_TUI=notifications` as the
> activation path. That claim was wrong. It is corrected here rather than
> deleted, because "the code exists but is not wired" is the more useful thing
> to know. Wiring it means adding an `ITuiRendererFactory` to
> `TuiBackendRegistry` and a conditional `ProjectReference` in
> `Harbor.Hosting.csproj` — a follow-up, not part of #430.

The renderer is still usable directly, which is how the tests drive it:

```csharp
using Harbor.Tui.Notifications;
using Harbor.Tui.RendererTests.Support;   // in tests; RecordingNotificationRunner

var renderer = new NotificationTuiRenderer(
    NullLogger<NotificationTuiRenderer>.Instance,
    new RecordingNotificationRunner());   // or ProcessNotificationRunner
await renderer.InitializeAsync(ct);
await renderer.RenderAsync(evt, ct);   // fires an OS notification, writes nothing
```

## Memory footprint

Lowest of the lot: ~2 MB RSS idle. The renderer itself is stateless beyond
the `BaseTuiRenderer` base; each notification still costs one short-lived
process, but the spawn now belongs to `ProcessNotificationRunner` in
`Harbor.Application`, which also bounds it at 3 s and kills it on timeout
instead of leaving the handle to the finalizer.

## Limitations / TODO

- Cannot display interactive prompts — `ReadLineAsync` returns empty. Use only
  with `harbor ask` (one-shot) or with `--no-input` style invocations.
- No notification deduplication — a fast-failing agent loop could spam. Add a
  debounce window (e.g. max one notification per 5 seconds per category).
- Windows backend uses `msg.exe` (modal dialog). Swap in `snoretoast.exe` or
  the WinRT `ToastNotificationManager` for proper Action Center integration.
- No click-through — notifications are fire-and-forget. For "click to view",
  the backend would need to launch a URL or open the session log file.
