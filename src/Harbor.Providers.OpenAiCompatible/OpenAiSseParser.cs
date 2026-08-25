using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAiCompatible;

/// <summary>
///     Thin wrapper over the shared <see cref="OpenAiWire" /> helpers — the
///     canonical wire code is compiled into this assembly and the native
///     OpenAI client alike, so the two cannot drift. Kept for the parser
///     benchmark; production clients call <c>OpenAiWire.TryParseChatChunkLine</c>
///     directly (ROP-A ПР.2/ПР.4).
/// </summary>
internal static class OpenAiSseParser
{
    public static IEnumerable<LlmEvent> ParseChunk(ReadOnlySpan<char> data, Dictionary<int, string> indexToId, ILogger logger)
    {
        var state = new ChunkStreamState();
        foreach ((int index, string id) in indexToId)
        {
            state.IndexToId[index] = id;
        }

        return OpenAiWire.TryParseChatChunkLine(data.ToString(), state, logger);
    }
}
