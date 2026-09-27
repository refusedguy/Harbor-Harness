# Harbor.Providers.Shared

Shared source code compiled into provider assemblies via `<Compile Include>` link items (ROP-A). `OpenAiWire.cs` (the canonical chat-completions chunk parser) is linked into the OpenAI and OpenAI-Compatible providers; `SsePump.cs` (stream pump + `ChunkStreamState` diagnostics) is linked into all four providers (OpenAI, OpenAiCompatible, Ollama, Anthropic).

## Layer

**Provider infrastructure (shared source).** Not a standalone runtime library — files are linked into `Harbor.Providers.OpenAI` and `Harbor.Providers.OpenAiCompatible` at build time.

## What's in it

| File | Purpose |
|------|---------|
| `OpenAiWire.cs` | `OpenAiWire.ParseChatChunk`, `TryParseChatChunkLine` — canonical OpenAI chat-completions SSE chunk parser (span-based, no per-chunk DOM) with stable tool-call id mapping (`indexToId`), counted positional-id fallbacks and synthesized step finishes for usage-without-finish_reason chunks (#203). |
| `SsePump.cs` | `SsePump.RunAsync/RunSseAsync` — reads SSE streams, feeds `OpenAiWire`, handles malformed chunks, and yields `LlmEvent` sequences. Hosts `ChunkStreamState` (index→id map, malformed/remap/dropped-usage counters, finish-reason memory) and the shared warn-once remap helper. |

## Public API summary

- **`OpenAiWire.ParseChatChunk(ReadOnlySpan<byte>, Dictionary<int,string>, ChunkStreamState? = null)`**: yields `LlmEvent` deltas for a single SSE chunk. Usage objects always surface (attached to the finish, or via a synthesized finish reusing the remembered reason); already-delivered trailing duplicates are counted, not re-emitted.
- **`OpenAiWire.TryParseChatChunkLine(string, ChunkStreamState, ILogger)`**: unified parse-or-skip policy — malformed chunks are logged, counted, and skipped; first positional id fallback per stream logs a warning.
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
