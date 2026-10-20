// MockLlmJsonContext.cs — the source-generated context for the mock LLM
// server's wire shapes (#1822).
//
// WHY IT IS PROJECT-LOCAL
// -----------------------
// These are the OpenAI-compatible shapes the mock SERVER writes: the model
// list, the streamed chat-completion chunks, and the recorded completion.
// They are this project's own protocol vocabulary, and the recorded
// completion is a NESTED type of `MockLlmServer` — the source generator can
// only see a type it can name from the assembly the context lives in, so the
// context has to live here.
//
// THE SHAPES ARE NAMED MEMBER BY MEMBER
// -------------------------------------
// The reflection path wrote `{"id":...,"object":...,"created":...}` with the
// members in that order and those exact spellings. Every member below carries
// its `[JsonPropertyName]`, so the bytes the mock puts on the wire do not
// depend on a naming policy — including `finish_reason`, which is written as
// `null` and must STAY on the wire (the product's OpenAI-compatible reader
// looks for the key).
//
// The three chunk shapes are kept apart rather than merged behind nullable
// members, because the tool-call chunk's `delta` carries `tool_calls` and NO
// `content` key at all: a merged shape would have written `"content":null`
// into a chunk the product never wrote.

using System.Text.Json.Serialization;

namespace Harbor.E2E.Framework;

/// <summary>     The <c>/models</c> response body. </summary>
public sealed record MockModelListPayload(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] MockModelListEntry[] Data);

/// <summary>     One entry of the <c>/models</c> response body. </summary>
public sealed record MockModelListEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);

/// <summary>     A streamed chat-completion chunk that carries text. </summary>
public sealed record MockChatCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] MockChatCompletionChoice[] Choices,
    // Only the FINAL chunk carries usage. The member is omitted rather than
    // written as null, because the text chunks never had the key.
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    MockChatCompletionUsage? Usage = null);

/// <summary>     One choice of a text chunk. </summary>
public sealed record MockChatCompletionChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] MockChatCompletionDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

/// <summary>     The delta of a text chunk. </summary>
public sealed record MockChatCompletionDelta(
    [property: JsonPropertyName("content")] string Content);

/// <summary>     The usage block of the final chunk. </summary>
public sealed record MockChatCompletionUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

/// <summary>     A streamed chat-completion chunk that carries a tool call. </summary>
public sealed record MockToolCallChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] MockToolCallChoice[] Choices);

/// <summary>     One choice of a tool-call chunk. </summary>
public sealed record MockToolCallChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] MockToolCallDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

/// <summary>     The delta of a tool-call chunk. </summary>
public sealed record MockToolCallDelta(
    [property: JsonPropertyName("tool_calls")] MockToolCall[] ToolCalls);

/// <summary>     One tool call of a tool-call chunk. </summary>
public sealed record MockToolCall(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] MockToolCallFunction Function);

/// <summary>     The function a tool call names. </summary>
public sealed record MockToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>
///     Source-generated metadata for the mock server's wire shapes.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MockModelListPayload), TypeInfoPropertyName = "MockModelListPayload")]
[JsonSerializable(typeof(MockModelListEntry), TypeInfoPropertyName = "MockModelListEntry")]
[JsonSerializable(typeof(MockChatCompletionChunk), TypeInfoPropertyName = "MockChatCompletionChunk")]
[JsonSerializable(typeof(MockChatCompletionChoice), TypeInfoPropertyName = "MockChatCompletionChoice")]
[JsonSerializable(typeof(MockChatCompletionDelta), TypeInfoPropertyName = "MockChatCompletionDelta")]
[JsonSerializable(typeof(MockChatCompletionUsage), TypeInfoPropertyName = "MockChatCompletionUsage")]
[JsonSerializable(typeof(MockToolCallChunk), TypeInfoPropertyName = "MockToolCallChunk")]
[JsonSerializable(typeof(MockToolCallChoice), TypeInfoPropertyName = "MockToolCallChoice")]
[JsonSerializable(typeof(MockToolCallDelta), TypeInfoPropertyName = "MockToolCallDelta")]
[JsonSerializable(typeof(MockToolCall), TypeInfoPropertyName = "MockToolCall")]
[JsonSerializable(typeof(MockToolCallFunction), TypeInfoPropertyName = "MockToolCallFunction")]
[JsonSerializable(typeof(MockLlmServer.RecordedCompletion), TypeInfoPropertyName = "RecordedCompletion")]
internal sealed partial class MockLlmJsonContext : JsonSerializerContext
{
}
