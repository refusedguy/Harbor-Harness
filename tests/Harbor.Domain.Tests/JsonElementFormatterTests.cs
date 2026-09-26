using System.Text.Json;
using Harbor.Abstractions.Models;
using MemoryPack;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     #87.1: the <see cref="JsonElement" /> MemoryPack formatter must
///     round-trip through the source-generated context (AOT-safe, no
///     reflection fallback) and be registered order-independently via the
///     module initializer — not only once <c>ToolCallPart</c> is touched.
/// </summary>
public class JsonElementFormatterTests
{
    private static ToolCallPart PartWithArgs(string id, string tool, string argsJson)
    {
        // Clone detaches from the pooled document (see #87.2); the part owns
        // its args beyond the using block.
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCallPart(id, tool, doc.RootElement.Clone());
    }

    private static async Task AssertRoundTrips(ToolCallPart part, string expectedArgsJson)
    {
        byte[] bytes = MemoryPackSerializer.Serialize(part);
        ToolCallPart? back = MemoryPackSerializer.Deserialize<ToolCallPart>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Id).IsEqualTo(part.Id);
        await Assert.That(back.ToolName).IsEqualTo(part.ToolName);
        await Assert.That(back.Args.GetRawText()).IsEqualTo(expectedArgsJson);
    }

    [Test]
    public async Task Formatter_IsRegistered_WithoutTouchingToolCallPart()
    {
        // The module initializer in Harbor.Abstractions.Contracts registers the
        // formatter at assembly load; this must hold even if no test in the
        // process has serialized a ToolCallPart yet.
        await Assert.That(MemoryPackFormatterProvider.IsRegistered<JsonElement>()).IsTrue();
    }

    [Test]
    public async Task EnsureRegistered_IsIdempotent()
    {
        JsonElementMemoryPackFormatter.EnsureRegistered();
        JsonElementMemoryPackFormatter.EnsureRegistered();

        await Assert.That(MemoryPackFormatterProvider.IsRegistered<JsonElement>()).IsTrue();
    }

    [Test]
    public async Task RoundTrip_ObjectArgs_PreservesRawJson()
    {
        const string args = """{"path":"README.md","limit":10}""";

        await AssertRoundTrips(PartWithArgs("tc1", "read", args), args);
    }

    [Test]
    public async Task RoundTrip_EmptyObjectArgs_PreservesRawJson()
    {
        const string args = """{}""";

        await AssertRoundTrips(PartWithArgs("tc2", "bash", args), args);
    }

    [Test]
    public async Task RoundTrip_NestedArgs_PreservesRawJson()
    {
        const string args = """{"items":[1,"two",{"three":true}],"n":1.5,"z":null}""";

        await AssertRoundTrips(PartWithArgs("tc3", "write", args), args);
    }

    [Test]
    public async Task RoundTrip_UnicodeArgs_PreservesRawJson()
    {
        const string args = """{"text":"héllo"}""";

        await AssertRoundTrips(PartWithArgs("tc4", "edit", args), args);
    }
}
