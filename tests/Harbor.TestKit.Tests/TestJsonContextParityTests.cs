// TestJsonContextParityTests.cs — the proof that the test tree's serializer
// is the SOURCE-GENERATED one, and that it writes the bytes the tests have
// always written (#1822).
//
// WHAT IS PROVEN HERE
// -------------------
// 1. THE WIRE SHAPE. Every tool-argument payload the shared context
//    registers is pinned to the exact JSON it has always produced. The
//    literals below are the bytes the anonymous types used to produce, so a
//    member that was renamed, reordered or dropped turns this red instead of
//    quietly changing what a tool receives.
//
// 2. NO REFLECTION FALLBACK. The shared context's options resolve types
//    through the generated metadata ALONE. A type the context does not know
//    cannot be serialized through them at all — the call throws. That is the
//    property that makes the gate in `tools/check-test-json-reflection.py`
//    meaningful: the reflection resolver is not standing behind the context
//    waiting for a type nobody registered.
//
// The product-side half of the proof — the shared context and the PRODUCT's
// context writing the same bytes for the same value — lives in
// `tests/Harbor.Storage.Jsonl.Tests/SourceGenContextParityTests.cs`, because
// the product's context is internal to `Harbor.Storage.Jsonl`.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The shared test context's payloads and its refusal to fall back to
///     reflection.
/// </summary>
public class TestJsonContextParityTests
{
    // ── 1. the wire shape of every tool-argument payload ──────────────────

    [Test]
    public async Task ToolPathArgs_Writes_The_Path_Key()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathArgs("src/app.cs"), TestJsonContext.Default.ToolPathArgs);

        await Assert.That(json).IsEqualTo("""{"path":"src/app.cs"}""");
    }

    [Test]
    public async Task ToolPathContentArgs_Writes_Path_Then_Content()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathContentArgs("src/app.cs", "body"), TestJsonContext.Default.ToolPathContentArgs);

        await Assert.That(json).IsEqualTo("""{"path":"src/app.cs","content":"body"}""");
    }

    [Test]
    public async Task ToolPathEditArgs_Writes_Path_OldString_NewString()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathEditArgs("src/app.cs", "old", "new"), TestJsonContext.Default.ToolPathEditArgs);

        await Assert.That(json).IsEqualTo("""{"path":"src/app.cs","oldString":"old","newString":"new"}""");
    }

    [Test]
    public async Task ToolPathContentVersionArgs_Writes_Path_Content_ExpectedVersion()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathContentVersionArgs("src/app.cs", "body", "v1"),
            TestJsonContext.Default.ToolPathContentVersionArgs);

        await Assert.That(json).IsEqualTo("""{"path":"src/app.cs","content":"body","expectedVersion":"v1"}""");
    }

    [Test]
    public async Task ToolPathEditVersionArgs_Writes_Path_OldString_NewString_ExpectedVersion()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathEditVersionArgs("src/app.cs", "old", "new", "v1"),
            TestJsonContext.Default.ToolPathEditVersionArgs);

        await Assert.That(json)
            .IsEqualTo("""{"path":"src/app.cs","oldString":"old","newString":"new","expectedVersion":"v1"}""");
    }

    [Test]
    public async Task ToolPathLimitArgs_Writes_Path_Then_Limit()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathLimitArgs("README.md", 10), TestJsonContext.Default.ToolPathLimitArgs);

        await Assert.That(json).IsEqualTo("""{"path":"README.md","limit":10}""");
    }

    [Test]
    public async Task ToolPathPatchArgs_Writes_Path_Then_Patch()
    {
        string json = JsonSerializer.Serialize(
            new ToolPathPatchArgs("src/app.cs", "@@ -1 +1 @@"), TestJsonContext.Default.ToolPathPatchArgs);

        await Assert.That(json).IsEqualTo("""{"path":"src/app.cs","patch":"@@ -1 +1 @@"}""");
    }

    [Test]
    public async Task ToolCommandArgs_Writes_The_Command_Key()
    {
        string json = JsonSerializer.Serialize(
            new ToolCommandArgs("make build"), TestJsonContext.Default.ToolCommandArgs);

        await Assert.That(json).IsEqualTo("""{"command":"make build"}""");
    }

    [Test]
    public async Task ToolPatternPathArgs_Writes_Pattern_Then_Path()
    {
        string json = JsonSerializer.Serialize(
            new ToolPatternPathArgs("secret", "../../etc"), TestJsonContext.Default.ToolPatternPathArgs);

        await Assert.That(json).IsEqualTo("""{"pattern":"secret","path":"../../etc"}""");
    }

    [Test]
    public async Task ToolInputArgs_Writes_The_Input_Key()
    {
        string json = JsonSerializer.Serialize(
            new ToolInputArgs("hello"), TestJsonContext.Default.ToolInputArgs);

        await Assert.That(json).IsEqualTo("""{"input":"hello"}""");
    }

    [Test]
    public async Task The_Dictionary_Payload_Keeps_Its_Keys_Verbatim()
    {
        // The dictionary payload is the one shape whose KEYS are data, so the
        // naming policy must not reach them — `{"path":...}`, not `{"Path":...}`.
        var payload = new Dictionary<string, object?> { ["path"] = "src/app.cs", ["limit"] = 10 };

        string json = JsonSerializer.Serialize(payload, TestJsonContext.Default.DictionaryStringObject);

        await Assert.That(json).IsEqualTo("""{"path":"src/app.cs","limit":10}""");
    }

    // ── 2. no reflection fallback behind the context ──────────────────────

    [Test]
    public async Task The_Context_Resolves_Types_Through_The_Generated_Metadata_Alone()
    {
        // The resolver behind the context's options must not be the runtime
        // reflection resolver. If it were, an unregistered type would still
        // serialize — silently, through reflection — and the gate would be
        // guarding a door that was already open.
        await Assert.That(TestJsonContext.Default.Options.TypeInfoResolver is DefaultJsonTypeInfoResolver)
            .IsFalse();
    }

    [Test]
    public async Task An_Unregistered_Type_Cannot_Be_Serialized_Through_The_Context()
    {
        // The refusal is the proof: there is no reflection behind the context
        // to catch a type nobody registered. The product's own JSONL context
        // behaves the same way — `JsonlCodecContext` refuses a runtime type
        // it does not know, which is why the JSONL codec drops the
        // `object`-typed metadata instead of writing it.
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new UnregisteredPayload(), TestJsonContext.Default.Options));

        await Assert.That(failure).IsNotNull();
    }

    /// <summary>
    ///     A type the shared context does NOT register. It exists to be
    ///     refused.
    /// </summary>
    private sealed record UnregisteredPayload;
}
