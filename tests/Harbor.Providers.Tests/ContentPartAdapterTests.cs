extern alias CompatWire;

using System.Buffers;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Providers;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;

// Same shared-source story as ImageContentBlockTests: the identical adapter
// source compiles into BOTH provider assemblies, so the type only resolves
// unambiguously through the assembly alias. The native builder's copy is
// pinned via its wire output there.
using CompatContentPartAdapter = CompatWire::Harbor.Providers.Internal.OpenAiContentPartAdapter;
using CompatImageContent = CompatWire::Harbor.Providers.Internal.OpenAiImageContent;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #1517 — the <see cref="LlmContentBlock" /> → OpenAI
///     <c>content[]</c> adaptation lives in ONE Object Adapter
///     (<see cref="CompatContentPartAdapter" />) rather than an inline
///     <c>switch</c> inside <c>WriteUserContent</c>. One failing-first pin per
///     branch: text part, <c>image_url</c> part, loudly dropped unsupported
///     block — plus the mixed-turn wire pin proving that the consolidation
///     changed no byte of the wire format.
/// </summary>
public class ContentPartAdapterTests
{
    /// <summary>Three bytes so the base64 payload is a readable, stable string.</summary>
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47];

    private const string PngDataUrlPrefix = "data:image/png;base64,";

    private static string ExpectedBase64() => Convert.ToBase64String(PngBytes);

    private static LlmContentBlock ToolCallBlock()
    {
        using var arguments = JsonDocument.Parse("""{"path":"a.txt"}""");
        return new LlmToolCallBlock("call_1", "read", arguments.RootElement);
    }

    // ── branch: text block → {"type":"text",…} part ───────────────────────

    [Test]
    public async Task ContentPartAdapter_TextBlock_EmitsTheTextPart()
    {
        var logger = new CapturingLogger();

        string json = AdaptOne(new LlmTextBlock("what is this?"), logger);

        await Assert.That(json).IsEqualTo("""[{"type":"text","text":"what is this?"}]""")
            .Because("the compact text part is the most-issued shape of this wire dialect, "
                   + "and the adapter is now its only hand-written emitter on the shared path");
        await Assert.That(EntriesOf(logger)).IsEmpty()
            .Because("a supported block adapts without touching the diagnostics sink");
    }

    // ── branch: image block → {"type":"image_url",…} part ─────────────────

    [Test]
    public async Task ContentPartAdapter_ImageBlock_EmitsTheImageUrlPart()
    {
        var logger = new CapturingLogger();

        string json = AdaptOne(new LlmImageBlock("image/png", PngBytes), logger);

        string expected = "[{\"type\":\"image_url\",\"image_url\":{\"url\":\""
            + PngDataUrlPrefix + ExpectedBase64() + "\"}}]";
        await Assert.That(json).IsEqualTo(expected)
            .Because("the data URL is prefixed exactly as the shared ToDataUrl helper "
                   + "builds it — a drifted shape here is a provider 400 mid-stream");
        await Assert.That(EntriesOf(logger)).IsEmpty();
    }

    // ── branch: unsupported block → dropped LOUDLY, nothing on the wire ───

    [Test]
    public async Task ContentPartAdapter_UnsupportedBlock_DropsLoudlyAndEmitsNothing()
    {
        var logger = new CapturingLogger();

        string json = AdaptOne(ToolCallBlock(), logger);

        await Assert.That(json).IsEqualTo("[]")
            .Because("ROP-A ПР.12: an unsupported block is dropped, never silently "
                   + "stringified and never serialised as an alien part shape");
        await Assert.That(EntriesOf(logger).Count).IsEqualTo(1)
            .Because("the drop must be loud — a silent empty part is a lost image that "
                   + "surfaces as a model-side confusion instead of a log line");
        LoggedEntry entry = EntriesOf(logger)[0];
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(entry.BlockType).IsEqualTo("tool_call")
            .Because("the block's own discriminator names what was dropped");
        await Assert.That(entry.Provider).IsEqualTo("stub")
            .Because("the drop warning names the caller so the operator can find the site");
    }

    // ── mixed turn: wire format is byte-identical to the pre-adapter shape ─

    [Test]
    public async Task WriteUserContent_MixedTurn_EmitsOnlySupportedPartsAndWarnsOnce()
    {
        var logger = new CapturingLogger();

        string json = UserContent([new LlmTextBlock("hi"), ToolCallBlock(), new LlmImageBlock("image/png", PngBytes)], logger);

        string expected = "{\"content\":[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\""
            + PngDataUrlPrefix + ExpectedBase64() + "\"}}]}";
        await Assert.That(json).IsEqualTo(expected)
            .Because("#1517 is a consolidation, not a wire change: the mixed-turn shape "
                   + "must be byte-identical to the inline-switch era");
        await Assert.That(EntriesOf(logger).Count).IsEqualTo(1);
        await Assert.That(EntriesOf(logger)[0].BlockType).IsEqualTo("tool_call");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Drive the adapter with ONE block inside a caller-owned array.</summary>
    private static string AdaptOne(LlmContentBlock block, CapturingLogger logger)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartArray();
        new CompatContentPartAdapter(writer, logger, "stub").Write(block);
        writer.WriteEndArray();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Drive <c>WriteUserContent</c> the way a message writer would.</summary>
    private static string UserContent(LlmContentBlock[] content, CapturingLogger logger)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        CompatImageContent.WriteUserContent(writer, content, logger, "stub");
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed record LoggedEntry(LogLevel Level, string BlockType, string Provider);

    /// <summary>
    ///     Projects the shared capture onto the fields this file asserts on.
    ///     The shared logger keeps the STRUCTURED STATE of every entry, and
    ///     the drop warning names the block and the provider in it.
    /// </summary>
    private static IReadOnlyList<LoggedEntry> EntriesOf(CapturingLogger logger) =>
    [
        .. logger.Capture.RawEntries.Select(entry => new LoggedEntry(
            entry.Level,
            LogCapture.FieldOf(entry.State, "BlockType") ?? string.Empty,
            LogCapture.FieldOf(entry.State, "Provider") ?? string.Empty))
    ];
}
