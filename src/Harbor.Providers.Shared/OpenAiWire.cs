// Shared source: compiled INTO the OpenAI and OpenAiCompatible provider
// assemblies via <Compile Include> link items (ROP-A ПР.2). One canonical
// chat-completions chunk parser for both the native client and the generic
// adapter, with stable tool-call ids (ROP-A ПР.3).

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

/// <summary>
///     Canonical OpenAI chat-completions wire parsing shared by
///     <c>OpenAILlmClient</c> and <c>OpenAiCompatibleLlmClient</c> (ROP-A ПР.2).
///     Tool-call ids are stabilised through a per-stream index→id map so
///     servers that omit <c>id</c> on delta chunks still coalesce Start/Delta
///     events instead of silently losing arguments (ROP-A ПР.3).
/// </summary>
internal static class OpenAiWire
{
    /// <summary>
    ///     Parse one chat-completions chunk. <paramref name="indexToId" /> is
    ///     per-stream state: first seen id wins for a tool-call index, missing
    ///     ids fall back to the map, then to <c>tc{index}</c>.
    ///     Evaluation is eager: the returned list owns no
    ///     <see cref="JsonElement" /> references, so the caller's
    ///     <see cref="JsonDocument" /> may be disposed immediately.
    /// </summary>
    public static IReadOnlyList<LlmEvent> ParseChatChunk(JsonElement root, Dictionary<int, string> indexToId)
    {
        var events = new List<LlmEvent>(capacity: 2);
        if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
        {
            Usage? usageOnly = ReadUsage(root);
            if (usageOnly is not null)
            {
                events.Add(new StepFinishEvent(0, "stop", usageOnly));
            }

            return events;
        }

        // First choice only — OpenAI streams one choice at a time for non-parallel tool calls.
        using var choicesIter = choicesEl.EnumerateArray();
        if (!choicesIter.MoveNext()) return events;
        var choice = choicesIter.Current;
        var delta = choice.TryGetProperty("delta", out var d) ? d : default;
        string? finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString()
            : null;

        if (delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                string? text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    events.Add(new TextDeltaEvent("0", text));
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            {
                string? text = reasoning.GetString();
                if (!string.IsNullOrEmpty(text))
                    events.Add(new ThinkingDeltaEvent("0", text));
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    int index = tc.TryGetProperty("index", out var idxEl)
                        ? ReadTokenCount(idxEl)
                        : 0;

                    // Stable id (ROP-A ПР.3): wire id → remembered id → positional
                    // fallback. Never a fresh Guid per chunk — that broke coalescing.
                    // The ValueKind guard matters: some servers send numeric ids,
                    // and GetString() throws on non-string values.
                    string? wireId = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString()
                        : null;
                    string id = !string.IsNullOrEmpty(wireId)
                        ? wireId!
                        : indexToId.GetValueOrDefault(index) ?? $"tc{index}";
                    indexToId[index] = id;

                    var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;
                    if (fn.ValueKind != JsonValueKind.Object) continue;

                    string? name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(name))
                        events.Add(new ToolCallStartEvent(id, name!));

                    if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                    {
                        string? argsStr = args.GetString();
                        if (!string.IsNullOrEmpty(argsStr))
                            events.Add(new ToolCallDeltaEvent(id, argsStr));
                    }
                }
            }
        }

        if (finishReason is not null)
        {
            events.Add(new StepFinishEvent(0, finishReason, ReadUsage(root)));
        }

        return events;
    }

    /// <summary>
    ///     Best-effort integer read: some servers send counts as floats
    ///     (<c>7.0</c>) or strings (<c>"7"</c>). Never throws — returns 0
    ///     when the value is missing or unparseable.
    /// </summary>
    private static int ReadTokenCount(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt32(out int direct))
                return direct;
            if (element.TryGetDouble(out double dbl))
                return (int)dbl;
            return 0;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), out int parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>
    ///     Read prompt_tokens/completion_tokens usage off a chunk root.
    ///     Never throws: non-integer wire values (float/string) fall back
    ///     to 0 instead of killing the chunk via InvalidOperationException.
    /// </summary>
    public static Usage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        int prompt = usage.TryGetProperty("prompt_tokens", out var pt) ? ReadTokenCount(pt) : 0;
        int completion = usage.TryGetProperty("completion_tokens", out var ct2) ? ReadTokenCount(ct2) : 0;
        return new Usage(prompt, completion);
    }

    /// <summary>
    ///     Unified malformed-chunk policy (ROP-A ПР.4): a chunk that fails to
    ///     parse is logged, counted and SKIPPED — the stream survives a single
    ///     bad line. Terminal error events are reserved for auth/HTTP/network
    ///     failures; they never fire for wire noise.
    /// </summary>
    public static IReadOnlyList<LlmEvent> TryParseChatChunkLine(
        string data, ChunkStreamState state, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return ParseChatChunk(doc.RootElement, state.IndexToId).ToArray();
        }
        catch (Exception ex)
        {
            state.CountMalformed();
            logger.LogWarning(ex, "Skipping malformed chat-completions chunk #{Count}: {Data}",
                state.MalformedChunks, data);
            return Array.Empty<LlmEvent>();
        }
    }
}
