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
//
// #1517: the inline `switch (LlmContentBlock)` that used to sit in
// WriteUserContent is consolidated into a single Object Adapter (composition,
// no class inheritance) below: LlmContentBlock values and the OpenAI
// content-part wire shape are incompatible, and the adapter is the ONE place
// that translates between them — text → {"type":"text"}, image → image_url
// part, anything else → loud drop. Dispatch is type-pattern matching on the
// block values, never a string switch on block.Type. Provider builders with
// their own dialects keep their own branches; providerId is only the label the
// drop warning carries, nothing branches on it.

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
    /// <remarks>
    ///     Per-block translation is delegated to
    ///     <see cref="OpenAiContentPartAdapter" /> (#1517) — one adapter per
    ///     image-bearing turn, no per-block state.
    /// </remarks>
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

        var contentParts = new OpenAiContentPartAdapter(writer, logger, providerId);
        for (int i = 0; i < content.Count; i++)
        {
            contentParts.Write(content[i]);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
///     Object Adapter (#1517, Structural): makes <see cref="LlmContentBlock" />
///     values work with the OpenAI content-part wire. The two are incompatible
///     shapes — a block is a domain value, the wire needs a
///     <c>{"type":…}</c> JSON part — and because that same mismatch recurs in
///     every OpenAI-shaped builder, the translation lives here once instead of
///     in per-callsite switches. Object Adapter: composition over the target
///     <see cref="Utf8JsonWriter" />, no coupling to a block base class.
///     <para>
///         The adapter is the single dispatch point <c>LlmContentBlock →
///         OpenAI content[]</c>: text → text part, image → <c>image_url</c>
///         part, anything else → dropped LOUDLY (ROP-A ПР.12), never
///         stringified. Dispatch is type-pattern matching on the block values;
///         provider-specific dialects (input_text/input_image, response-item
///         shapes) stay in their builders, and nothing here branches on
///         providerId — the label exists only so a drop warning can name its
///         caller.
///     </para>
///     <para>
///         One instance covers a single user turn; it holds no stream state and
///         is never shared across calls or threads.
///     </para>
/// </summary>
internal sealed class OpenAiContentPartAdapter
{
    private readonly Utf8JsonWriter _writer;
    private readonly ILogger _logger;
    private readonly string _providerId;

    public OpenAiContentPartAdapter(Utf8JsonWriter writer, ILogger logger, string providerId)
    {
        _writer = writer;
        _logger = logger;
        _providerId = providerId;
    }

    /// <summary>
    ///     Adapt one block in place: exactly one content part is appended to
    ///     the caller's array — or, for an unsupported kind, nothing is appended
    ///     and a warning names the dropped block and its provider.
    /// </summary>
    public void Write(LlmContentBlock block)
    {
        if (block is LlmTextBlock text)
        {
            WriteTextPart(text);
        }
        else if (block is LlmImageBlock image)
        {
            WriteImagePart(image);
        }
        else
        {
            // ROP-A ПР.12: unsupported blocks are dropped loudly on this path.
            OpenAiImageContentLog.DroppingUnsupportedBlock(_logger, block.Type, _providerId);
        }
    }

    private void WriteTextPart(LlmTextBlock text)
    {
        _writer.WriteStartObject();
        _writer.WriteString("type", "text");
        _writer.WriteString("text", text.Text);
        _writer.WriteEndObject();
    }

    /// <summary>Write a single <c>{"type":"image_url","image_url":{"url":…}}</c> part.</summary>
    private void WriteImagePart(LlmImageBlock image)
    {
        // §PERF-002: the base64 is built by the shared data-URL helper, so
        // only the finished string reaches the writer.
        string url = OpenAiImageContent.ToDataUrl(image.MimeType, image.Data);

        _writer.WriteStartObject();
        _writer.WriteString("type", "image_url");
        _writer.WriteStartObject("image_url");
        _writer.WriteString("url", url);
        _writer.WriteEndObject();
        _writer.WriteEndObject();
    }
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="OpenAiImageContent" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class OpenAiImageContentLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Dropping unsupported content block(s) of type {BlockType} for {Provider}")]
    public static partial void DroppingUnsupportedBlock(ILogger logger, string blockType, string provider);
}
