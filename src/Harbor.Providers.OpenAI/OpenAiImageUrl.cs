using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Harbor.Providers.OpenAI;

/// <summary>
///     The <c>image_url</c> object of an OpenAI chat-completions image part
///     (issue #386).
/// </summary>
/// <remarks>
///     A named DTO rather than an anonymous type so it can be registered in
///     <see cref="OpenAiWireContext" />: the base64 payload is a plain string
///     built by <c>OpenAiImageContent.ToDataUrl</c>, and the surrounding
///     <c>Dictionary&lt;string, object?&gt;</c> payload resolves THIS type through
///     the source-generated metadata instead of the reflection fallback
///     (§PERF-002).
/// </remarks>
/// <param name="Url">
///     A <c>data:&lt;mime&gt;;base64,…</c> URL (or an <c>https://</c> URL the caller
///     already resolved).
/// </param>
internal sealed record OpenAiImageUrl(
    [property: JsonPropertyName("url")] string Url);

/// <summary>
///     AOT-friendly <see cref="JsonSerializerContext" /> for the OpenAI wire DTOs
///     that can appear as <c>object</c>-typed values inside a request payload.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenAiImageUrl))]
internal sealed partial class OpenAiWireContext : JsonSerializerContext
{
    /// <summary>
    ///     Resolver used by <see cref="OpenAiRequestBuilder" />: the
    ///     source-generated metadata FIRST, reflection as the fallback for the
    ///     remaining anonymous payload shapes (tools, tool_choice, …), so this
    ///     changes no existing wire output.
    /// </summary>
    public static JsonTypeInfoResolver Resolver { get; } =
        JsonTypeInfoResolver.Combine(Default, new DefaultJsonTypeInfoResolver());
}
