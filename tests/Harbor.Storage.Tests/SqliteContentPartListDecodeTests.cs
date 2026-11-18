using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;

namespace Harbor.Storage.Tests;

/// <summary>
///     #2624: the SQLite message read path decodes the whole <c>Parts</c> array
///     through one <see cref="JsonDocument"/> instead of allocating a DOM per
///     element. These tests pin the behaviour the rewrite must preserve: every
///     part kind decodes, legacy PascalCase still survives, <c>args</c> is cloned
///     into a usable <see cref="JsonElement"/>, and an unreadable part still
///     refuses the message by name (the #550 contract).
/// </summary>
public class SqliteContentPartListDecodeTests
{
    private static IReadOnlyList<ContentPart> ReadParts(string arrayJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(arrayJson);
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        return ContentPartListJsonConverter.Instance.Read(
                   ref reader, typeof(IReadOnlyList<ContentPart>), SqliteMappers.JsonOptions)
               ?? throw new JsonException("list converter returned null");
    }

    [Test]
    public async Task Read_ManyParts_DecodesEveryKindFromOneArrayDocument()
    {
        const string json = """
            [
              {"type":"text","text":"hello"},
              {"type":"thinking","text":"hmm"},
              {"type":"tool_call","id":"tc-1","toolName":"read","args":{"path":"README.md","limit":10}},
              {"type":"file","path":"img.png","mimeType":"image/png","sizeBytes":42}
            ]
            """;

        var parts = ReadParts(json);

        await Assert.That(parts.Count).IsEqualTo(4);
        await Assert.That(((TextPart)parts[0]).Text).IsEqualTo("hello");
        await Assert.That(((ThinkingPart)parts[1]).Text).IsEqualTo("hmm");
        var toolCall = (ToolCallPart)parts[2];
        await Assert.That(toolCall.Id).IsEqualTo("tc-1");
        await Assert.That(toolCall.ToolName).IsEqualTo("read");
        await Assert.That(toolCall.Args.GetProperty("limit").GetInt32()).IsEqualTo(10);
        var file = (FilePart)parts[3];
        await Assert.That(file.Path).IsEqualTo("img.png");
        await Assert.That(file.MimeType).IsEqualTo("image/png");
        await Assert.That(file.SizeBytes).IsEqualTo(42);
    }

    [Test]
    public async Task Read_LegacyPascalCaseParts_StillDecode()
    {
        const string json = """
            [{"Type":"text","Text":"pascal case survives"}]
            """;

        var parts = ReadParts(json);

        await Assert.That(parts.Count).IsEqualTo(1);
        await Assert.That(((TextPart)parts[0]).Text).IsEqualTo("pascal case survives");
    }

    [Test]
    public async Task Read_ForeignTagInTheMiddleOfTheArray_RefusesNamingTheTag()
    {
        const string json = """
            [
              {"type":"text","text":"before"},
              {"type":"video","url":"https://example.invalid/v.mp4"},
              {"type":"text","text":"after"}
            ]
            """;

        try
        {
            _ = ReadParts(json);
            throw new InvalidOperationException("expected the list converter to refuse a foreign tag");
        }
        catch (JsonException ex)
        {
            await Assert.That(ex.Message).Contains("video");
        }
    }

    [Test]
    public async Task Read_KnownTagMissingItsField_RefusesNamingTheField()
    {
        const string json = """[{"type":"file","path":"a.png"}]""";

        try
        {
            _ = ReadParts(json);
            throw new InvalidOperationException("expected the list converter to refuse a corrupt part");
        }
        catch (JsonException ex)
        {
            await Assert.That(ex.Message).Contains("file");
        }
    }

    [Test]
    public async Task Read_EmptyArray_ReturnsNoParts()
    {
        var parts = ReadParts("[]");
        await Assert.That(parts.Count).IsEqualTo(0);
    }
}
