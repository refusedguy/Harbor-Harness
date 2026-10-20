// StorageBackendParityTests.cs — the parity contract the two session-store
// backends used to hold only by hand (#1591).
//
// JsonlLineParser / JsonlMessageCodec and SqliteMappers used to carry parallel
// manual projections of the same domain message types onto their persist forms,
// with no test noticing when one backend's projection drifted from the other's.
// Both backends now ride one shared table (Harbor.Storage.Shared
// MessagePartAdapter) for the part projection, and these tests pin the rest:
// the two transports must serialize a part to byte-identical JSON, must read
// each other's bytes back to the same domain value, must refuse the same
// corrupt records by the same vocabulary, and must round-trip a full message
// corpus to the same domain records.

using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Sqlite;

namespace Harbor.Storage.Tests;

/// <summary>
///     The Jsonl ↔ Sqlite parity suite (#1591): both backends' domain ↔
///     persist-form projections agree on one vocabulary, both read each
///     other's bytes, both refuse the same corrupt records, and both
///     round-trip the same message corpus.
/// </summary>
public class StorageBackendParityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
    private const string SessionId = "sess-1";

    // ── 1. byte shape: both transports write the same persist JSON ─────────

    /// <summary>
    ///     The core byte contract: for every part kind, the JSONL payload
    ///     shape and the SQLite converter shape are the SAME document — the
    ///     transports differ in envelope only. A new member added to one
    ///     write path and not the other turns this red instead of splitting
    ///     the persisted vocabulary.
    /// </summary>
    /// <remarks>
    ///     This is also the seat of the <c>FilePart.Data</c> fidelity rule:
    ///     both stores drop the byte payload on write (the road already taken
    ///     by #51 for metadata) — the corpus carries a file part WITH bytes so
    ///     the drop rides the same test as the shape.
    /// </remarks>
    [Test]
    public async Task ContentPart_PersistedShape_IsIdenticalAcrossBackends()
    {
        using var argsDoc = JsonDocument.Parse("""{"path":"README.md","limit":10}""");
        ContentPart[] parts =
        [
            new TextPart("here you go"),
            new ThinkingPart("hmm"),
            ToolCallPart.Create("tc-1", "read", argsDoc.RootElement),
            new FilePart("img.png", "image/png", 42),
            new FilePart("big.png", "image/png", 99, [1, 2, 3, 4]),
        ];

        foreach (ContentPart part in parts)
        {
            object dto = JsonlMessageCodec.SerializePart(part);
            string jsonlJson = JsonSerializer.Serialize(dto, dto.GetType(), JsonlCodecContext.JsonOptions);
            // NOT JsonSerializer.Serialize(part, options): ContentPart carries
            // [JsonPolymorphic], and the declared-type generic overload needs a
            // metadata-capable converter to build its PolymorphicTypeResolver — the
            // options-level row converter is not one, so configuring would already
            // throw NotSupportedException regardless of the value. The row cell's
            // write path is the converter itself (ContentPartListJsonConverter
            // delegates every element to it — see that file's header for the #177
            // precedent on the same trap).
            string sqliteJson = WriteViaRowConverter(part);

            await Assert.That(sqliteJson).IsEqualTo(jsonlJson)
                .Because($"Both backends must write the same persisted shape for {part.Type}.");

            // #51's twin, pinned at the part level: neither store may persist
            // FilePart.Data, or the two byte shapes diverge above.
            await Assert.That(jsonlJson.Contains("\"data\"")).IsFalse();
        }
    }

    // ── 2. cross-reading: each backend decodes the other's bytes ───────────

    [Test]
    public async Task Each_Backend_Reads_The_Other_Backend_Part_Bytes()
    {
        using var argsDoc = JsonDocument.Parse("""{"cwd":"."}""");
        ContentPart[] parts =
        [
            new TextPart("here you go"),
            new ThinkingPart("hmm"),
            ToolCallPart.Create("tc-1", "read", argsDoc.RootElement),
            new FilePart("img.png", "image/png", 42),
        ];

        foreach (ContentPart part in parts)
        {
            object dto = JsonlMessageCodec.SerializePart(part);
            string jsonlJson = JsonSerializer.Serialize(dto, dto.GetType(), JsonlCodecContext.JsonOptions);
            string sqliteJson = WriteViaRowConverter(part);

            ContentPart codecOverJsonl = ReadViaElementCodec(jsonlJson);
            ContentPart rowOverJsonl = ReadViaRowConverter(jsonlJson);
            ContentPart codecOverSqlite = ReadViaElementCodec(sqliteJson);
            ContentPart rowOverSqlite = ReadViaRowConverter(sqliteJson);

            await AssertPartsEqual(part, codecOverJsonl, $"codec reader over JSONL bytes for {part.Type}");
            await AssertPartsEqual(part, rowOverJsonl, $"row reader over JSONL bytes for {part.Type}");
            await AssertPartsEqual(part, codecOverSqlite, $"codec reader over SQLite bytes for {part.Type}");
            await AssertPartsEqual(part, rowOverSqlite, $"row reader over SQLite bytes for {part.Type}");
        }
    }

    // ── 3. refusals: the same corrupt record is refused by the same build ──

    [Test]
    public async Task Foreign_Part_Tag_Is_Refused_By_The_Same_Reader_Vocabulary()
    {
        const string foreign = """{"type":"video","text":"x"}""";
        const string tag = "video";

        await AssertRefusal(ReadViaElementCodec, foreign, tag, "element codec");
        await AssertRowRefusal(foreign, tag, "row converter");
        await AssertLineRefusal(foreign, tag, "span parser");
    }

    [Test]
    public async Task Known_Tag_Missing_A_Member_Is_Refused_By_Name_In_Both_Backends()
    {
        const string corrupt = """{"type":"file","path":"a.png"}""";

        await AssertRefusal(ReadViaElementCodec, corrupt, "file", "element codec");
        await AssertRowRefusal(corrupt, "file", "row converter");
        await AssertLineRefusal(corrupt, "file", "span parser");
    }

    [Test]
    public async Task Part_With_No_Discriminator_Is_Refused_By_Name_In_Both_Backends()
    {
        const string tagless = """{"text":"no discriminator at all"}""";

        await AssertRefusal(ReadViaElementCodec, tagless, "type", "element codec");
        await AssertRowRefusal(tagless, "type", "row converter");
        await AssertLineRefusal(tagless, "type", "span parser");
    }

    // ── 4. full-message parity: both round trips return the corpus ────────

    [Test]
    public async Task Message_Corpus_RoundTrips_To_The_Same_Records_In_Both_Backends()
    {
        using var argsDoc = JsonDocument.Parse("""{"path":"README.md"}""");
        foreach (AgentMessage message in Corpus(argsDoc.RootElement))
        {
            AgentMessage fromSqlite = RoundTripViaSqlite(message);
            AgentMessage fromJsonl = RoundTripViaJsonlLine(message);

            await AssertMessagesEqual(message, fromSqlite, $"SQLite round trip of {message.Role}/{message.Id}");
            await AssertMessagesEqual(message, fromJsonl, $"JSONL round trip of {message.Role}/{message.Id}");
        }
    }

    // ── corpus ─────────────────────────────────────────────────────────────

    private static readonly Usage TidyUsage = new(10, 20, ReasoningTokens: 5);

    private static IEnumerable<AgentMessage> Corpus(JsonElement argsRoot) =>
    [
        new UserMessage("u1", SessionId, T0, "list the files", "code", "p/m"),
        new UserMessage(
            "u2", SessionId, T0.AddSeconds(1), "read the picture", "code", "p/m",
            ParentId: "u1",
            Attachments: [new ImageAttachment("shot.png", "image/png", 3, 4, [9, 8, 7])]),
        new AssistantMessage(
            "a1", SessionId, T0.AddSeconds(2),
            [
                new TextPart("here they are"),
                new ThinkingPart("list them silently"),
                ToolCallPart.Create("tc-1", "read", argsRoot),
            ],
            StopReason.ToolUse,
            TidyUsage,
            "p/m"),
        new AssistantMessage(
            "a2", SessionId, T0.AddSeconds(3),
            [new TextPart("done"), new FilePart("img.png", "image/png", 42, [1, 2])],
            StopReason.Stop,
            new Usage(1, 2),
            "p/m",
            ParentId: "u1",
            IsSummary: true,
            SummaryFirstKeptId: "u1"),
        new ToolResultMessage(
            "t1", SessionId, T0.AddSeconds(4),
            [
                new ToolResultEntry("tc-1", "read", "file contents", IsError: false),
                new ToolResultEntry("tc-2", "bash", "boom", IsError: true),
            ],
            ParentId: "a1"),
    ];

    // ── transport helpers ──────────────────────────────────────────────────

    private static ContentPart ReadViaElementCodec(string partJson)
    {
        using var doc = JsonDocument.Parse(partJson);
        return JsonlMessageCodec.DeserializePart(doc.RootElement.Clone());
    }

    private static ContentPart ReadViaRowConverter(string partJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(partJson);
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        return SqliteMappers.ContentPartJsonConverter.Instance.Read(ref reader, typeof(ContentPart), SqliteMappers.JsonOptions)
            ?? throw new JsonException("row converter returned null — the #550 refusal must throw instead");
    }

    /// <summary>
    ///     The row cell's write path, byte for byte: <c>ContentPartJsonConverter</c>
    ///     is the converter the SQLite store writes one part through (the parts-list
    ///     converter delegates each element to it). The declared-type generic
    ///     overload is unreachable for ContentPart — its [JsonPolymorphic] metadata
    ///     cannot configure over the row converter (NotSupportedException in
    ///     PolymorphicTypeResolver) — so the persisted shape is sampled at the seam
    ///     the row actually has.
    /// </summary>
    private static string WriteViaRowConverter(ContentPart part)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            SqliteMappers.ContentPartJsonConverter.Instance.Write(writer, part, SqliteMappers.JsonOptions);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void RowRead(string partJson, out string error)
    {
        try
        {
            _ = ReadViaRowConverter(partJson);
            error = string.Empty;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }
    }

    private static AgentMessage RoundTripViaSqlite(AgentMessage message)
    {
        var decoded = SqliteMappers.TryDeserializeMessage(message.Role, SqliteMappers.SerializeMessage(message));
        return decoded.IsSuccess
            ? decoded.Value
            : throw new InvalidOperationException($"SQLite round trip failed: {decoded.Error}");
    }

    private static AgentMessage RoundTripViaJsonlLine(AgentMessage message)
    {
        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));

        byte[] line = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);

        var decoded = JsonlMessageCodec.DeserializeMessage(
            SessionId, JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone());
        return decoded.IsSuccess
            ? decoded.Value
            : throw new InvalidOperationException($"JSONL round trip failed: {decoded.Error}");
    }

    // The span parser and the element reader must never disagree about the
    // same line — the #550 pair that used to be pinned only by hope.
    [Test]
    public async Task LineParser_And_ElementCodec_ReturnEqualMessages()
    {
        using var argsDoc = JsonDocument.Parse("""{"path":"x"}""");
        foreach (AgentMessage message in Corpus(argsDoc.RootElement))
        {
            var entry = new MessageEntry(
                "message", message.Id, message.ParentId, message.Role, message.CreatedAt,
                JsonlMessageCodec.SerializeMessagePayload(message));
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);

            AgentMessage viaParser = JsonlLineParser.Parse(line, SessionId).Value;
            AgentMessage viaCodec = JsonlMessageCodec.DeserializeMessage(SessionId, JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone()).Value;

            await AssertMessagesEqual(viaCodec, viaParser, $"parser/codec agreement on {message.Role}/{message.Id}");
        }
    }

    // ── assertions ─────────────────────────────────────────────────────────

    private static async Task AssertRefusal(Func<string, ContentPart> read, string partJson, string tag, string which)
    {
        try
        {
            _ = read(partJson);
            throw new InvalidOperationException($"expected {which} to refuse the part");
        }
        catch (JsonException ex)
        {
            await Assert.That(ex.Message).Contains(tag)
                .Because($"the {which} refusal must name the offending tag it cannot rebuild.");
        }
    }

    private static async Task AssertRowRefusal(string partJson, string tag, string which)
    {
        RowRead(partJson, out string error);
        await Assert.That(error).Contains(tag)
            .Because($"the {which} refusal must name the offending tag it cannot rebuild.");
    }

    private static async Task AssertLineRefusal(string partJson, string tag, string which)
    {
        string line = AssistantLine(partJson);
        var parsed = JsonlLineParser.Parse(Encoding.UTF8.GetBytes(line), SessionId);

        await Assert.That(parsed.IsFailure).IsTrue()
            .Because($"the {which} must refuse a record this build cannot rebuild.");

        await Assert.That(parsed.Error).Contains(tag);
    }

    private static string AssistantLine(string partJson) =>
        "{\"type\":\"message\",\"id\":\"aX\",\"createdAt\":\"" + T0.ToString("O") +
        "\",\"parentId\":null,\"role\":\"assistant\",\"payload\":{\"parts\":[" +
        partJson +
        "],\"stopReason\":\"stop\",\"usage\":{\"inputTokens\":1,\"outputTokens\":2},\"model\":\"p/m\"}}";

    private static async Task AssertPartsEqual(ContentPart expected, ContentPart actual, string which)
    {
        await Assert.That(actual.GetType()).IsEqualTo(expected.GetType()).Because(which);
        switch (expected)
        {
            case TextPart t:
                await Assert.That(((TextPart)actual).Text).IsEqualTo(t.Text).Because(which);
                break;
            case ThinkingPart t:
                await Assert.That(((ThinkingPart)actual).Text).IsEqualTo(t.Text).Because(which);
                break;
            case ToolCallPart t:
                var other = (ToolCallPart)actual;
                await Assert.That(other.Id).IsEqualTo(t.Id).Because(which);
                await Assert.That(other.ToolName).IsEqualTo(t.ToolName).Because(which);
                await Assert.That(other.Args.GetRawText()).IsEqualTo(t.Args.GetRawText()).Because(which);
                break;
            case FilePart t:
                var file = (FilePart)actual;
                await Assert.That(file.Path).IsEqualTo(t.Path).Because(which);
                await Assert.That(file.MimeType).IsEqualTo(t.MimeType).Because(which);
                await Assert.That(file.SizeBytes).IsEqualTo(t.SizeBytes).Because(which);
                // Both stores drop the byte payload on write; the read side
                // must agree with the write side in both backends.
                await Assert.That(file.Data).IsNull().Because(which);
                break;
        }
    }

    private static async Task AssertMessagesEqual(AgentMessage expected, AgentMessage actual, string which)
    {
        await Assert.That(actual.GetType()).IsEqualTo(expected.GetType()).Because(which);
        await Assert.That(actual.Id).IsEqualTo(expected.Id).Because(which);
        await Assert.That(actual.SessionId).IsEqualTo(expected.SessionId).Because(which);
        await Assert.That(actual.CreatedAt).IsEqualTo(expected.CreatedAt).Because(which);
        await Assert.That(actual.ParentId).IsEqualTo(expected.ParentId).Because(which);

        switch (expected)
        {
            case UserMessage u:
                var user = (UserMessage)actual;
                await Assert.That(user.Content).IsEqualTo(u.Content).Because(which);
                await Assert.That(user.Agent).IsEqualTo(u.Agent).Because(which);
                await Assert.That(user.Model).IsEqualTo(u.Model).Because(which);
                await Assert.That(user.HasAttachments).IsEqualTo(u.HasAttachments).Because(which);
                for (int i = 0; i < (u.Attachments?.Count ?? 0); i++)
                {
                    ImageAttachment expectedImage = u.Attachments![i];
                    ImageAttachment actualImage = user.Attachments![i];
                    await Assert.That(actualImage.Path).IsEqualTo(expectedImage.Path).Because(which);
                    await Assert.That(actualImage.MimeType).IsEqualTo(expectedImage.MimeType).Because(which);
                    await Assert.That(actualImage.Width).IsEqualTo(expectedImage.Width).Because(which);
                    await Assert.That(actualImage.Height).IsEqualTo(expectedImage.Height).Because(which);
                    await Assert.That(actualImage.Data).IsEquivalentTo(expectedImage.Data).Because(which);
                }

                return;
            case AssistantMessage a:
                var assistant = (AssistantMessage)actual;
                await Assert.That(assistant.Model).IsEqualTo(a.Model).Because(which);
                await Assert.That(assistant.StopReason).IsEqualTo(a.StopReason).Because(which);
                await Assert.That(assistant.Usage).IsEqualTo(a.Usage).Because(which);
                await Assert.That(assistant.IsSummary).IsEqualTo(a.IsSummary).Because(which);
                await Assert.That(assistant.SummaryFirstKeptId).IsEqualTo(a.SummaryFirstKeptId).Because(which);
                await Assert.That(assistant.Parts.Count).IsEqualTo(a.Parts.Count).Because(which);
                for (int i = 0; i < a.Parts.Count; i++)
                {
                    await AssertPartsEqual(a.Parts[i], assistant.Parts[i], which);
                }

                return;
            case ToolResultMessage result:
                var toolResult = (ToolResultMessage)actual;
                await Assert.That(toolResult.Results.Count).IsEqualTo(result.Results.Count).Because(which);
                for (int i = 0; i < result.Results.Count; i++)
                {
                    await Assert.That(toolResult.Results[i].ToolCallId).IsEqualTo(result.Results[i].ToolCallId).Because(which);
                    await Assert.That(toolResult.Results[i].ToolName).IsEqualTo(result.Results[i].ToolName).Because(which);
                    await Assert.That(toolResult.Results[i].Output).IsEqualTo(result.Results[i].Output).Because(which);
                    await Assert.That(toolResult.Results[i].IsError).IsEqualTo(result.Results[i].IsError).Because(which);
                }

                return;
        }
    }
}
