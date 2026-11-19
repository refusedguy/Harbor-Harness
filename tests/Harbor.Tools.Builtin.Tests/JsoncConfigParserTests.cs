// #2680 F167. Behaviour tests for the JSONC config parser: these pin the two
// JSONC extensions the SDK options enable, the object-root contract and the
// failure surface. Nothing here reads a file, starts a process or touches the
// config store.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class JsoncConfigParserTests
{
    [Test]
    public async Task Parse_PlainJsonObject_Succeeds()
    {
        Result<JsonElement> parsed = JsoncConfigParser.Parse("""{"provider":"kilocode","model":"hy3"}""");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(parsed.Value.GetProperty("provider").GetString()).IsEqualTo("kilocode");
    }

    [Test]
    public async Task Parse_AcceptsLineComments()
    {
        const string jsonc = """
            {
              // which provider to use
              "provider": "kilocode",
              "model": "hy3" // trailing line comment
            }
            """;

        Result<JsonElement> parsed = JsoncConfigParser.Parse(jsonc);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.GetProperty("provider").GetString()).IsEqualTo("kilocode");
        await Assert.That(parsed.Value.GetProperty("model").GetString()).IsEqualTo("hy3");
    }

    [Test]
    public async Task Parse_AcceptsBlockComments()
    {
        const string jsonc = """/* header */ {"a": /* inline */ 1, "b": 2 /* tail */}""";

        Result<JsonElement> parsed = JsoncConfigParser.Parse(jsonc);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.GetProperty("a").GetInt32()).IsEqualTo(1);
        await Assert.That(parsed.Value.GetProperty("b").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_AcceptsTrailingCommasInObjectsAndArrays()
    {
        const string jsonc = """
            {"a":1,"b":[2,3,],}
            """;

        Result<JsonElement> parsed = JsoncConfigParser.Parse(jsonc);

        await Assert.That(parsed.IsSuccess).IsTrue();
        JsonElement items = parsed.Value.GetProperty("b");
        await Assert.That(items.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(items.GetArrayLength()).IsEqualTo(2);
        await Assert.That(items[0].GetInt32()).IsEqualTo(2);
        await Assert.That(items[1].GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_CommentMarkersInsideStringsAreData()
    {
        const string jsonc = """{"url":"https://example.com/a//b","note":"/* not a comment */"}""";

        Result<JsonElement> parsed = JsoncConfigParser.Parse(jsonc);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.GetProperty("url").GetString()).IsEqualTo("https://example.com/a//b");
        await Assert.That(parsed.Value.GetProperty("note").GetString()).IsEqualTo("/* not a comment */");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   \n")]
    [Arguments("\uFEFF")]
    public async Task Parse_EmptyInput_Fails(string? text)
    {
        Result<JsonElement> parsed = JsoncConfigParser.Parse(text);

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error).IsEqualTo("Configuration is empty.");
    }

    [Test]
    [Arguments("[1,2]")]
    [Arguments("\"text\"")]
    [Arguments("42")]
    [Arguments("null")]
    [Arguments("true")]
    public async Task Parse_NonObjectRoot_Fails(string text)
    {
        Result<JsonElement> parsed = JsoncConfigParser.Parse(text);

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error).IsEqualTo("Configuration root must be a JSON object.");
    }

    [Test]
    public async Task Parse_MalformedJson_FailsWithDiagnostic()
    {
        Result<JsonElement> parsed = JsoncConfigParser.Parse("""{"a": }""");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.StartsWith("Invalid configuration JSON", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Parse_LeadingBom_IsIgnored()
    {
        const string jsonc = "\uFEFF{\"a\":1}";

        Result<JsonElement> parsed = JsoncConfigParser.Parse(jsonc);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.GetProperty("a").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ReturnsDetachedRoot()
    {
        Result<JsonElement> first = JsoncConfigParser.Parse("""{"a":1}""");
        Result<JsonElement> second = JsoncConfigParser.Parse("""{"a":2}""");

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(first.Value.GetProperty("a").GetInt32()).IsEqualTo(1);
        await Assert.That(second.Value.GetProperty("a").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Options_EnableCommentsAndTrailingCommas()
    {
        await Assert.That(JsoncConfigParser.Options.CommentHandling).IsEqualTo(JsonCommentHandling.Skip);
        await Assert.That(JsoncConfigParser.Options.AllowTrailingCommas).IsTrue();
    }

    [Test]
    public async Task TryParse_FailureYieldsUndefinedRoot()
    {
        bool ok = JsoncConfigParser.TryParse("""{"a": }""", out JsonElement root);

        await Assert.That(ok).IsFalse();
        await Assert.That(root.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task TryParse_SuccessYieldsObjectRoot()
    {
        const string jsonc = """
            // c
            {"a":1,}
            """;

        bool ok = JsoncConfigParser.TryParse(jsonc, out JsonElement root);

        await Assert.That(ok).IsTrue();
        await Assert.That(root.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(root.GetProperty("a").GetInt32()).IsEqualTo(1);
    }
}
