using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Tools;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1780 — the parameter schema of a tool is the tool's contract with the
///     model: the JSON the model is allowed to send. It used to be written out
///     by hand in every tool, and a second time in this project's golden file.
///     It is now declared once, as <c>[ToolParam]</c> attributes, and rendered
///     by <c>Harbor.CodeGen.ToolSchemaGenerator</c>.
/// </summary>
/// <remarks>
///     <para>
///         The generator refuses to compile a tool whose rendered schema differs
///         from the hash it was pinned with, so a change to a tool's arguments
///         is a build failure until the pin is re-signed in the same commit.
///         These tests are the other half of that: they check that what the
///         RUNTIME hands out is the bytes that were approved, and that the pin
///         and the declarations cover the same tools.
///     </para>
///     <para>
///         The bytes are compared as the model receives them: the schema is
///         written into the provider request with <c>JsonElement.WriteTo</c>
///         and into the MCP <c>tools/list</c> answer the same way, so the
///         canonical form — not the whitespace of the literal — is the
///         contract. The pin is the SHA-256 of that canonical form.
///     </para>
/// </remarks>
public class ParameterSchemaPinTests
{
    /// <summary>The name of the pin, as it is embedded in the product assembly.</summary>
    private const string PinResourceName = "Harbor.Tools.Builtin.tool-schemas.sha256.json";

    [Test]
    public async Task EveryToolSchema_IsTheOneThatWasPinned()
    {
        IReadOnlyDictionary<string, string> pins = ReadPins();

        foreach ((string key, Func<ITool> factory) in ToolSchemaFixtures.ToolFactories)
        {
            ITool tool = factory();
            JsonDocument schema = tool.ParameterSchema;

            await Assert.That(pins.ContainsKey(key)).IsTrue()
                .Because($"tool '{key}' has no pinned schema");

            string pinned = pins[key];
            string rendered = schema.RootElement.GetRawText();

            await Assert.That(Digest(rendered)).IsEqualTo(pinned)
                .Because($"tool '{key}' changed its parameter schema");
        }
    }

    [Test]
    public async Task EveryPin_HasAToolAndEveryTool_HasAPin()
    {
        IReadOnlyDictionary<string, string> pins = ReadPins();
        string[] tools = ToolSchemaFixtures.ToolFactories.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] pinned = pins.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        await Assert.That(string.Join(", ", pinned)).IsEqualTo(string.Join(", ", tools))
            .Because("a tool without a pin would change shape unnoticed, and a pin "
                + "without a tool is a schema nobody renders");
    }

    [Test]
    public async Task EveryToolSchema_IsValidJsonAndAnObject()
    {
        foreach ((string key, Func<ITool> factory) in ToolSchemaFixtures.ToolFactories)
        {
            ITool tool = factory();
            JsonDocument schema = tool.ParameterSchema;

            await Assert.That(schema.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object)
                .Because($"the schema of '{key}' is not a JSON object");

            await Assert.That(schema.RootElement.TryGetProperty("properties", out _)).IsTrue()
                .Because($"the schema of '{key}' declares no properties");
        }
    }

    /// <summary>The SHA-256 of the canonical schema, lower-case hex.</summary>
    private static string Digest(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>The pin, as the product assembly carries it.</summary>
    private static IReadOnlyDictionary<string, string> ReadPins()
    {
        Assembly assembly = typeof(BashTool).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(PinResourceName);
        if (stream is null)
            throw new InvalidOperationException(
                $"the pin file is not embedded in the product assembly as '{PinResourceName}'");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string text = reader.ReadToEnd();

        using JsonDocument document = JsonDocument.Parse(text);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            pins[property.Name] = property.Value.GetString() ?? string.Empty;

        return pins;
    }
}
