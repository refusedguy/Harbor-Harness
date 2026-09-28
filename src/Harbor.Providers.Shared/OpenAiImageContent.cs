// Shared source: compiled INTO the OpenAI and OpenAiCompatible provider
// assemblies via <Compile Include> link items (same pattern as OpenAiWire.cs).
// One canonical `image_url` content-part writer for both OpenAI-shaped clients.
//
// #386: an LlmImageBlock used to be dropped with a warning on this path — the
// adapter that serves most providers (Kilocode, OpenRouter, Groq, Mistral, …)
// had no way to put an image in front of a model. The provider-SPECIFIC wire
// difference (Anthropic's image source block vs OpenAI's image_url data URL)
// lives in the provider builders, never in shared provider-id branching
// (§OOP-002).

using System.Text.Json;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

/// <summary>
///     Serialises <see cref="LlmImageBlock" />s as OpenAI-style <c>content[]</c>
///     image parts.
/// </summary>
internal static class OpenAiImageContent
{
    /// <summary>
    ///     True when the message carries at least one <see cref="LlmImageBlock" />.
    ///     Lets callers keep the compact <c>"content": "text"</c> string shape for
    ///     text-only turns and switch to the array shape only when an image is present.
    /// </summary>
    public static bool HasImage(IReadOnlyList<LlmContentBlock> content)
    {
        for (int i = 0; i < content.Count; i++)
        {
            if (content[i] is LlmImageBlock)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Build the <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c> URL for one image.
    /// </summary>
    /// <remarks>
    ///     §PERF-002: the base64 is produced explicitly here, and only the
    ///     finished string reaches the serializer — a <c>byte[]</c> is never
    ///     handed to the reflection-based
    ///     <c>JsonSerializer.Serialize(Dictionary&lt;string, object?&gt;)</c>
    ///     payload path, which would resolve the array's element type at
    ///     runtime. The compat client writes straight to its
    ///     <see cref="Utf8JsonWriter" />, so nothing here is serialised twice.
    /// </remarks>
    public static string ToDataUrl(string mimeType, ReadOnlySpan<byte> data)
    {
        const string Scheme = "data:";
        const string Marker = ";base64,";

        return string.Concat(Scheme, mimeType, Marker, Convert.ToBase64String(data));
    }

    /// <summary>
    ///     Write one user turn: the compact string form when it is text-only,
    ///     otherwise the <c>content[]</c> array with a text part per text block
    ///     and an <c>image_url</c> part per image.
    /// </summary>
    /// <param name="writer">Destination writer, positioned inside the message object.</param>
    /// <param name="content">Content blocks of the user message.</param>
    /// <param name="logger">Diagnostics sink for dropped block kinds.</param>
    /// <param name="providerId">Provider label used in the drop warning.</param>
    public static void WriteUserContent(
        Utf8JsonWriter writer,
        IReadOnlyList<LlmContentBlock> content,
        ILogger logger,
        string providerId)
    {
        if (!HasImage(content))
        {
            // ROP-A ПР.12: non-text blocks still dropped loudly on this path.
            writer.WriteString("content", ProviderPayload.FirstTextOrEmpty(content, logger, providerId));
            return;
        }

        writer.WritePropertyName("content");
        writer.WriteStartArray();
        for (int i = 0; i < content.Count; i++)
        {
            switch (content[i])
            {
                case LlmTextBlock text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;

                case LlmImageBlock image:
                    WriteImagePart(writer, image);
                    break;

                default:
                    logger.LogWarning(
                        "Dropping unsupported content block(s) of type {BlockType} for {Provider}",
                        content[i].Type, providerId);
                    break;
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>Write a single <c>{"type":"image_url","image_url":{"url":…}}</c> part.</summary>
    private static void WriteImagePart(Utf8JsonWriter writer, LlmImageBlock image)
    {
        string url = ToDataUrl(image.MimeType, image.Data);

        writer.WriteStartObject();
        writer.WriteString("type", "image_url");
        writer.WriteStartObject("image_url");
        writer.WriteString("url", url);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
