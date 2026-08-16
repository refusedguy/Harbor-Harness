# Harbor.App.Blazor

Blazor Server desktop app for Harbor — runs Kestrel on `http://localhost:5000` and serves the Harbor chat UI to any browser on the local network. Useful for: headless dev boxes, remote SSH dev (tunnel the port), iPad-as-a-monitor workflows, and accessibility (browser zoom + screen readers).

## Layer

Composition Root — same DI responsibilities as `Harbor.App.Cli`, but instead of a terminal REPL, boots a Blazor Server app.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Core` (AgentLoop, registries)
- `Harbor.Storage.Memory` (ephemeral — swap to `Jsonl` for persistence)
- `Harbor.Tui.Abstractions` (UiStore + UiReducer — shared state model with TUI)
- `Microsoft.AspNetCore.App` (framework reference)
- `CommunityToolkit.Mvvm`
- `Markdig` (Markdown rendering)

## Run

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project apps/Harbor.App.Blazor
```

Open `http://localhost:5000` in your browser. The app listens on `localhost` only by default — to expose on the LAN:

```bash
ASPNETCORE_URLS=http://0.0.0.0:5000 dotnet run --project apps/Harbor.App.Blazor
```

> **Warning**: exposing on `0.0.0.0` makes your Harbor instance reachable from any device on the network. Harbor has no auth — only do this on a trusted LAN.

## Browser support

Tested on:

| Browser          | Status        | Notes                                  |
|------------------|---------------|----------------------------------------|
| Chrome 120+      | Fully working | Best perf.                             |
| Firefox 121+     | Fully working |                                        |
| Safari 17+       | Fully working |                                        |
| Edge 120+        | Fully working |                                        |
| Chrome on iPad   | Working       | Streaming works; touch keyboard OK.    |
| Safari on iPhone | Working       | Small screen — usable but cramped.     |
| IE 11            | Not supported | Blazor Server requires modern browser. |

## Dev instructions

### Hot reload

```bash
dotnet watch --project apps/Harbor.App.Blazor
```

Edits to `.razor` files hot-reload without losing chat state (Blazor Server preserves the circuit).

### Debugging

Launch with browser DevTools open (F12). Blazor Server logs are in the terminal that started `dotnet run`. To see agent events in the browser console:

```bash
HARBOR_LOGLEVEL=Debug dotnet run --project apps/Harbor.App.Blazor
```

### Project structure

```
apps/Harbor.App.Blazor/
├── Program.cs                # Kestrel + Blazor bootstrap
├── _Imports.razor            # Common using directives
├── App.razor                 # Root component
├── Pages/
│   └── Index.razor           # Main chat page
├── Components/
│   ├── ChatTranscript.razor  # Message list
│   ├── ChatInput.razor       # Input box + send button
│   ├── ToolCallView.razor    # Tool call card (collapsible)
│   └── StatusBar.razor       # Token usage, model, provider
├── Services/
│   └── BlazorHub.cs          # Bridges UiStore -> JS interop
└── wwwroot/                  # Static assets (CSS, JS)
```

## Usage example

```csharp
// In Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
// Harbor services
builder.Services.AddHarborCore();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
var app = builder.Build();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();
```

## See also

- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full UI comparison
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../Harbor.App.Cli/README.md](../Harbor.App.Cli/README.md) — the CLI app
