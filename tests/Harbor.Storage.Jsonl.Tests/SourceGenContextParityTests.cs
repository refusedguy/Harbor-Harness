// SourceGenContextParityTests.cs — the proof that the TEST tree's
// source-generated context and the PRODUCT's source-generated context write
// the SAME bytes for the same value (#1822).
//
// WHY THIS FILE IS HERE AND NOT IN THE SHARED KIT
// ----------------------------------------------
// The product's context is `JsonlCodecContext`, an INTERNAL type of
// `Harbor.Storage.Jsonl`. The shared kit cannot see it, and the shared kit
// must not: `Harbor.TestKit.Support` reaches `Harbor.Abstractions` alone, so
// that a consumer of the kit gains no product assembly. This project already
// references `Harbor.Storage.Jsonl` and already sees the internal context, so
// it is the one place where the two contexts can be compared.
//
// WHAT IS PROVEN
// --------------
// The point of #1822 is that a test must exercise the SAME path the product
// exercises. A source-generated context is only "the same path" if it is
// configured the same way — same naming policy, same ignore condition, same
// null handling. This test compares the two contexts' OUTPUT, byte for byte,
// for the types they both own.
//
// If a test context drifts away from the product's — a different naming
// policy, a different ignore rule — the bytes stop matching and this goes
// red. That is the drift the issue is about, caught in one place.

using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The shared test context against the product's JSONL context.
/// </summary>
public class SourceGenContextParityTests
{
    [Test]
    public async Task The_Two_Contexts_Agree_On_Usage()
    {
        var usage = new Usage(10, 20);

        string fromTest = JsonSerializer.Serialize(usage, TestJsonContext.Default.Usage);
        string fromProduct = JsonSerializer.Serialize(usage, JsonlCodecContext.Default.Usage);

        await Assert.That(fromTest).IsEqualTo(fromProduct);
        await Assert.That(fromProduct).IsEqualTo("""{"inputTokens":10,"outputTokens":20,"reasoningTokens":null,"cacheReadTokens":null,"cacheWriteTokens":null}""");
    }

    [Test]
    public async Task The_Two_Contexts_Agree_On_A_JsonElement()
    {
        using var document = JsonDocument.Parse("""{"path":"README.md","limit":10}""");

        string fromTest = JsonSerializer.Serialize(document.RootElement, TestJsonContext.Default.JsonElement);
        string fromProduct = JsonSerializer.Serialize(document.RootElement, JsonlCodecContext.Default.JsonElement);

        await Assert.That(fromTest).IsEqualTo(fromProduct);
        await Assert.That(fromProduct).IsEqualTo("""{"path":"README.md","limit":10}""");
    }

    [Test]
    public async Task The_Two_Contexts_Agree_On_A_Null_Usage()
    {
        // Null handling is the other half of "the same path": a context that
        // omitted the null members would write a shorter document, and the
        // JSONL reader would read back a different record.
        string fromTest = JsonSerializer.Serialize(new Usage(0, 0), TestJsonContext.Default.Usage);
        string fromProduct = JsonSerializer.Serialize(new Usage(0, 0), JsonlCodecContext.Default.Usage);

        await Assert.That(fromTest).IsEqualTo(fromProduct);
    }
}
