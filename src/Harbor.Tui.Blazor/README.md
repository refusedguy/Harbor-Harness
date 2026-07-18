# Harbor.Tui.Blazor

Blazor Server renderer for Harbor. Spins up a Kestrel HTTP host on
`http://localhost:5000`, opens the default browser, and serves a Razor-based
chat UI that streams agent activity over a SignalR circuit. Same `UiStore` as
the terminal renderers — only the projection differs.

## When to use

- You want to drive Harbor from any browser (Chrome, Firefox, Safari, Edge).
- You want to run Harbor on a remote server / inside Docker and access the UI
  from your laptop, phone, or tablet over SSH tunnel or LAN.
- You need full CSS — accessibility, screen readers, copy/paste, theming.
- You want to embed Harbor's chat in a larger web app.

## Platform support

| OS              | Server         | Browser client                |
|-----------------|----------------|-------------------------------|
| Windows         | ✅ Kestrel     | Any modern browser            |
| Linux           | ✅ Kestrel     | Any modern browser            |
| macOS           | ✅ Kestrel     | Any modern browser            |
| Mobile (browser)| N/A (server)   | ✅ Mobile Safari / Chrome      |

The Harbor process runs on a server; the browser is just a thin client.

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Tui.Abstractions`
- `Microsoft.AspNetCore.Components.Web` (Razor Components + Blazor Server)
- `Microsoft.Extensions.Logging.Abstractions`

## Files

- `Harbor.Tui.Blazor.csproj` — `net10.0`, `Sdk="Microsoft.NET.Sdk.Web"`.
- `BlazorTuiRenderer.cs` — sealed `ITuiRenderer`/`IInteractiveTuiRenderer`. Builds
  the WebApplication, registers `UiStore` + `TuiEffectHost` in DI, maps
  `Components/App` Razor component, opens the default browser, waits for shutdown.
- `Program.cs` — N/A (the renderer owns the WebApplication; no separate entry).
- `Components/App.razor` — root component: `<!DOCTYPE html>`, `<HeadOutlet/>`,
  references `app.css`, renders `<Chat/>` with `InteractiveServer` render mode.
- `Components/Chat.razor` — the chat page: header, scrollable history,
  streaming indicator with `pulse` animation, multi-line textarea input,
  status bar with token cost. Subscribes to `UiStore.Changed` and calls
  `InvokeAsync(StateHasChanged)` to re-render.
- `Components/_Imports.razor` — Razor project-wide imports.
- `wwwroot/app.css` — Catppuccin-Mocha dark theme.
- `appsettings.json` — Kestrel URL `http://localhost:5000`, log level Warning.

## How it reads from UiStore

```razor
@code {
    protected override void OnInitialized() => Store.Changed += OnStoreChanged;

    private void OnStoreChanged(object? _, UiStateChangedEventArgs e)
    {
        // Marshal back to the Blazor render circuit.
        InvokeAsync(StateHasChanged);
    }
}
```

The `Store` is injected via `@inject UiStore Store` — registered as a singleton
in `BlazorTuiRenderer.RunInteractiveAsync`. The `TuiEffectHost` is injected the
same way so the chat page can call `Effects.Run(new TuiEffect.PromptAgent(text))`
on submit.

## Build

```bash
# Restore + build (cross-platform)
dotnet build src/Harbor.Tui.Blazor/Harbor.Tui.Blazor.csproj -c Release

# Run Harbor with the Blazor renderer
HARBOR_TUI=blazor dotnet run --project src/Harbor.Cli/Harbor.Cli.csproj

# Then open http://localhost:5000 in any browser.
```

For remote access, forward the port over SSH:

```bash
ssh -L 5000:localhost:5000 user@server
# Now http://localhost:5000 on your laptop hits the remote Harbor.
```

## Selecting this renderer

Set `HARBOR_TUI=blazor` in your environment, or add `tui: "blazor"` to
`~/.harbor/config.json`.

## Memory footprint

Approx. 50 MB RSS idle for the server process. Each connected browser adds
~5–10 MB for the SignalR circuit and circuit state. With 1–2 concurrent users
it's the lightest "GUI" option in this set.

## Limitations / TODO

- Blazor Server means latency on every keystroke. For LAN/local use it's
  imperceptible; for cross-continent use, consider switching to Blazor
  WebAssembly (would require client-side state sync via SignalR).
- No markdown rendering — wire `Markdig` → sanitized HTML.
- No diff overlay panel.
- No auth — anyone on the same network who can reach port 5000 can drive the
  agent. Add ASP.NET Core auth before exposing remotely.
- No streaming buffer truncation policy — long streams will keep the DOM large
  until `MessageEndEvent` folds them into the transcript.
