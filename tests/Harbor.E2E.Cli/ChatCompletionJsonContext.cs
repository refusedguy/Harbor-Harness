// ChatCompletionJsonContext.cs — the source-generated context for the
// OpenAI-compatible request bodies this project posts (#1822).
//
// WHY IT IS PROJECT-LOCAL
// -----------------------
// The body is the OpenAI chat-completions request — a shape the product's own
// provider builds, but one this project writes by hand because it drives the
// mock server directly rather than through a provider client. It belongs to
// the protocol this project speaks, not to the shared kit.
//
// The shape is named member by member so the posted body cannot drift: the
// mock server parses `model` and `messages`, and a renamed member would make
// the server answer something else.

using System.Text.Json.Serialization;

namespace Harbor.E2E.Cli;

/// <summary>     The chat-completions request body. </summary>
public sealed record ChatCompletionRequestBody(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] ChatCompletionMessage[] Messages);

/// <summary>     One message of a chat-completions request. </summary>
public sealed record ChatCompletionMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
///     Source-generated metadata for the chat-completions request body.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ChatCompletionRequestBody), TypeInfoPropertyName = "ChatCompletionRequestBody")]
[JsonSerializable(typeof(ChatCompletionMessage), TypeInfoPropertyName = "ChatCompletionMessage")]
internal sealed partial class ChatCompletionJsonContext : JsonSerializerContext
{
}
