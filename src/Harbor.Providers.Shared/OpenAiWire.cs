// Shared source: compiled INTO the OpenAI and OpenAiCompatible provider
// assemblies via <Compile Include> link items (ROP-A ПР.2). One canonical
// chat-completions chunk parser for both the native client and the generic
// adapter, with stable tool-call ids (ROP-A ПР.3).

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

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
    /// </summary>
    public static IEnumerable<LlmEvent> ParseChatChunk(JsonElement root, Dictionary<int, string> indexToId)
    {
        if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
        {
            Usage? usageOnly = ReadUsage(root);
            if (usageOnly is not null)
            {
                yield return new StepFinishEvent(0, "stop", usageOnly);
            }

            yield break;
        }

        // First choice only — OpenAI streams one choice at a time for non-parallel tool calls.
        using var choicesIter = choicesEl.EnumerateArray();
        if (!choicesIter.MoveNext()) yield break;
        var choice = choicesIter.Current;
        var delta = choice.TryGetProperty("delta", out var d) ? d : default;
        string? finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

        if (delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                string? text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new TextDeltaEvent("0", text);
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            {
                string? text = reasoning.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new ThinkingDeltaEvent("0", text);
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    int index = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                        ? idxEl.GetInt32()
                        : 0;

                    // Stable id (ROP-A ПР.3): wire id → remembered id → positional
                    // fallback. Never a fresh Guid per chunk — that broke coalescing.
                    string id = tc.TryGetProperty("id", out var idEl) && !string.IsNullOrEmpty(idEl.GetString())
                        ? idEl.GetString()!
                        : indexToId.GetValueOrDefault(index) ?? $"tc{index}";
                    indexToId[index] = id;

                    var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;
                    if (fn.ValueKind != JsonValueKind.Object) continue;

                    string? name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrEmpty(name))
                        yield return new ToolCallStartEvent(id, name!);

                    if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                    {
                        string? argsStr = args.GetString();
                        if (!string.IsNullOrEmpty(argsStr))
                            yield return new ToolCallDeltaEvent(id, argsStr);
                    }
                }
            }
        }

        if (finishReason is not null)
        {
            yield return new StepFinishEvent(0, finishReason, ReadUsage(root));
        }
    }

    /// <summary>Read prompt_tokens/completion_tokens usage off a chunk root.</summary>
    public static Usage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new Usage(
            usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
            usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0);
    }
}
