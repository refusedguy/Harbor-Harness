# Harbor.Providers.Shared

Shared source code compiled into the OpenAI and OpenAI-Compatible provider assemblies via `<Compile Include>` link items (ROP-A). Contains the canonical SSE/chat-completions wire parser and the SSE pump with stable tool-call id handling.

## Layer

**Provider infrastructure (shared source).** Not a standalone runtime library — files are linked into `Harbor.Providers.OpenAI` and `Harbor.Providers.OpenAiCompatible` at build time.

## What's in it

| File | Purpose |
|------|---------|
| `OpenAiWire.cs` | `OpenAiWire.ParseChatChunk`, `ReadUsage`, `TryParseChatChunkLine` — canonical OpenAI chat-completions SSE chunk parser with stable tool-call id mapping (`indexToId`). |
| `SsePump.cs` | `SsePump.RunAsync/RunSseAsync` — reads SSE streams, feeds `OpenAiWire`, handles malformed chunks, and yields `LlmEvent` sequences. |

## Public API summary

- **`OpenAiWire.ParseChatChunk(JsonElement, Dictionary<int,string>)`**: yields `LlmEvent` deltas for a single SSE chunk.
- **`OpenAiWire.ReadUsage(JsonElement)`**: extracts `Usage` from a chunk root.
- **`OpenAiWire.TryParseChatChunkLine(string, ChunkStreamState, ILogger)`**: unified parse-or-skip policy — malformed chunks are logged, counted, and skipped.
- **`SsePump.RunSseAsync(...)`**: high-level SSE pump that wires HTTP response into an `IAsyncEnumerable<LlmEvent>`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `System.Text.Json` | JSON parsing of SSE chunks |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `LlmEvent`, `AgentMessage`, `Usage`, `ChunkStreamState` |
| `Harbor.Abstractions.Contracts` | Value objects |

## Tests

No dedicated test project. Validated by `tests/Harbor.Providers.Tests/` (OpenAI provider tests).

## Build

This project does not produce a standalone artifact. It is compiled as linked source into:
```bash
dotnet build src/Harbor.Providers.OpenAI/Harbor.Providers.OpenAI.csproj
dotnet build src/Harbor.Providers.OpenAiCompatible/Harbor.Providers.OpenAiCompatible.csproj
```

## Known limitations

- No NuGet package — purely shared compilation.
- OpenAI-specific wire format assumptions baked into `OpenAiWire`; generic adapters must transform or extend.
