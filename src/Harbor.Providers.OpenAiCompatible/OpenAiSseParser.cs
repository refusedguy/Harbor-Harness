using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAiCompatible;

/// <summary>
///     Thin wrapper over the shared <see cref="OpenAiWire" /> chunk parser
///     (ROP-A ПР.2) — the canonical wire code is compiled into this assembly
///     and the native OpenAI client alike, so the two cannot drift. The
///     per-stream index→id map keeps tool-call ids stable across delta
///     chunks (ROP-A ПР.3).
/// </summary>
internal static class OpenAiSseParser
{
    public static IEnumerable<LlmEvent> ParseChunk(ReadOnlySpan<char> data, Dictionary<int, string> indexToId, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToString());
            return OpenAiWire.ParseChatChunk(doc.RootElement, indexToId).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse chunk: {Data}", data.ToString());
            return new[] { new ErrorEvent($"Parse failed: {ex.Message}", Kind: ProviderErrorKind.Malformed) };
        }
    }
}
