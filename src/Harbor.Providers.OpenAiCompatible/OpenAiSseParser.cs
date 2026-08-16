using System.Collections.Generic;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAiCompatible;

internal static class OpenAiSseParser
{
    public static IEnumerable<LlmEvent> ParseChunk(ReadOnlySpan<char> data, Dictionary<int, string> indexToId, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToString());
            return ParseChunkFromDocument(doc.RootElement, indexToId).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse chunk: {Data}", data.ToString());
            return new[] { new ErrorEvent($"Parse failed: {ex.Message}") };
        }
    }

    private static IEnumerable<LlmEvent> ParseChunkFromDocument(JsonElement root, Dictionary<int, string> indexToId)
    {
        var choices = root.TryGetProperty("choices", out var c) ? c.EnumerateArray().ToList() : new List<JsonElement>();
        if (choices.Count == 0)
        {
            if (root.TryGetProperty("usage", out var usage))
            {
                int inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                int outputTokens = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
                yield return new StepFinishEvent(0, "stop", new Usage(inputTokens, outputTokens));
            }
            yield break;
        }

        var choice = choices[0];
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

                    string id = tc.TryGetProperty("id", out var idEl) && !string.IsNullOrEmpty(idEl.GetString())
                        ? idEl.GetString()!
                        : indexToId.GetValueOrDefault(index) ?? $"tc{index}";
                    indexToId[index] = id;

                    var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;

                    if (fn.ValueKind == JsonValueKind.Object)
                    {
                        string? name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (!string.IsNullOrEmpty(name))
                        {
                            yield return new ToolCallStartEvent(id, name!);
                        }

                        if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        {
                            string? argsStr = args.GetString();
                            if (!string.IsNullOrEmpty(argsStr))
                                yield return new ToolCallDeltaEvent(id, argsStr);
                        }
                    }
                }
            }
        }

        if (finishReason is not null)
        {
            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            var usageObj = usage.ValueKind == JsonValueKind.Object
                ? new Usage(
                    usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                    usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0)
                : null;

            yield return new StepFinishEvent(0, finishReason, usageObj);
        }
    }
}
