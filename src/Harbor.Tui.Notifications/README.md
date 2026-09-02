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

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Terminal.Abstractions` (`BaseTuiRenderer`, `ITuiRenderContext`)
- `Microsoft.Extensions.Logging.Abstractions`

No external NuGet packages — uses only `System.Diagnostics.Process` to shell
out to the OS notification tool.

## Files

- `Harbor.Tui.Notifications.csproj` — `net10.0`, references `Harbor.Terminal.Abstractions`.
- `NotificationTuiRenderer.cs` — sealed `NotificationTuiRenderer : BaseTuiRenderer`. Listens to
  `AgentEndEvent`, `AgentErrorEvent`, `CompactionCompletedEvent`, and
  `ToolExecutionEndEvent` (errors only); routes each to the detected backend.
- `INotificationBackend` — platform abstraction with `Notify(title, body, isError)`.
- Concrete backends (all in the same file): `LinuxNotifySendBackend`, `MacOsascriptBackend`,
  `WindowsToastBackend`, `NullNotificationBackend`.
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

Platform detection uses `RuntimeInformation.IsOSPlatform`. On Linux it shells
out to `notify-send`; on macOS to `osascript -e 'display notification...'`;
on Windows to `msg.exe` (modal dialog). For proper Windows Action Center
toasts, install `snoretoast.exe` and replace `WindowsToastBackend`.

## Build

```bash
# Cross-platform build, no extra workloads needed
dotnet build src/Harbor.Tui.Notifications/Harbor.Tui.Notifications.csproj -c Release

# Run Harbor with the notifications renderer (background mode)
HARBOR_TUI=notifications harbor ask "Refactor src/Harbor.Core/ for SOLID compliance"
```

On Linux you may need to install libnotify first:

```bash
sudo apt install libnotify-bin   # Debian/Ubuntu
sudo dnf install libnotify       # Fedora
```

## Selecting this renderer

Set `HARBOR_TUI=notifications` in your environment, or add `tui: "notifications"`
to `~/.harbor/config.json`.

## Memory footprint

Lowest of the lot: ~2 MB RSS idle. The renderer itself is stateless beyond
the `BaseTuiRenderer` base; each notification spawns a short-lived
`ProcessStartInfo` shell-out (a few KB and one process for ~100 ms).

## Limitations / TODO

- Cannot display interactive prompts — `ReadLineAsync` returns empty. Use only
  with `harbor ask` (one-shot) or with `--no-input` style invocations.
- No notification deduplication — a fast-failing agent loop could spam. Add a
  debounce window (e.g. max one notification per 5 seconds per category).
- Windows backend uses `msg.exe` (modal dialog). Swap in `snoretoast.exe` or
  the WinRT `ToastNotificationManager` for proper Action Center integration.
- No click-through — notifications are fire-and-forget. For "click to view",
  the backend would need to launch a URL or open the session log file.
