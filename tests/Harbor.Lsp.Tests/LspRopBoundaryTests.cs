using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Lsp;
using Harbor.Lsp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>
///     ROP boundaries from #200: foreign-server payloads normalize to
///     <c>Result&lt;Maybe&lt;LspLocation&gt;&gt;</c> with machine-readable reasons
///     (not null), and the manager degrades with the reason instead of throwing.
/// </summary>
public class LspNormalizeBoundaryTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Test]
    public async Task NullPayload_MapsToNone()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(null, "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task JsonNull_MapsToNone()
    {
        Result<Maybe<LspLocation>> normalized =
            LspServerSession.TryNormalizeFirstLocation(Parse("null"), "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task ScalarPayload_FailsWithNotObjectOrArray()
    {
        Result<Maybe<LspLocation>> normalized =
            LspServerSession.TryNormalizeFirstLocation(Parse("42"), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("not-object-or-array (got Number)");
    }

    [Test]
    public async Task StringPayload_FailsWithNotObjectOrArray()
    {
        Result<Maybe<LspLocation>> normalized =
            LspServerSession.TryNormalizeFirstLocation(Parse("\"oops\""), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("not-object-or-array (got String)");
    }

    [Test]
    public async Task ValidLocationLink_NormalizesToPath()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(Parse("""
            {
              "targetUri": "file:///ws/a.ts",
              "targetSelectionRange": {
                "start": { "line": 4, "character": 2 },
                "end": { "line": 4, "character": 8 }
              }
            }
            """), "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.HasValue).IsTrue();
        await Assert.That(normalized.Value.Value.FilePath).IsEqualTo("/ws/a.ts");
        await Assert.That(normalized.Value.Value.Line).IsEqualTo(4);
        await Assert.That(normalized.Value.Value.Column).IsEqualTo(2);
    }

    [Test]
    public async Task ValidLocation_NormalizesToPath()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(Parse("""
            {
              "uri": "file:///ws/b.ts",
              "range": {
                "start": { "line": 1, "character": 0 },
                "end": { "line": 1, "character": 5 }
              }
            }
            """), "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.Value.FilePath).IsEqualTo("/ws/b.ts");
    }

    [Test]
    public async Task LocationLink_MissingSelectionRange_FailsWithReason()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(
            Parse("""{ "targetUri": "file:///ws/a.ts" }"""), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("missing-target-selection-range");
    }

    [Test]
    public async Task Location_MissingUri_FailsWithReason()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(
            Parse("""{ "range": { "start": { "line": 0, "character": 0 } } }"""), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("missing-uri");
    }

    [Test]
    public async Task Location_MissingRange_FailsWithReason()
    {
        // Previously threw KeyNotFoundException via GetProperty — now a reason.
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(
            Parse("""{ "uri": "file:///ws/a.ts" }"""), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("missing-range");
    }

    [Test]
    public async Task Location_NonStringUri_FailsWithReason()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(Parse("""
            {
              "uri": 42,
              "range": { "start": { "line": 0, "character": 0 } }
            }
            """), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("uri-not-string");
    }

    [Test]
    public async Task EmptyObject_FailsWithMissingUri()
    {
        Result<Maybe<LspLocation>> normalized =
            LspServerSession.TryNormalizeFirstLocation(Parse("{}"), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("missing-uri");
    }

    [Test]
    public async Task Array_FirstUsableLocationWins()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(Parse("""
            [
              { "bogus": true },
              { "uri": "file:///ws/c.ts",
                "range": { "start": { "line": 7, "character": 3 } } }
            ]
            """), "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.Value.FilePath).IsEqualTo("/ws/c.ts");
        await Assert.That(normalized.Value.Value.Line).IsEqualTo(7);
    }

    [Test]
    public async Task Array_AllGarbage_FailsWithFirstReason()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(
            Parse("""[{ "bogus": 1 }, 42]"""), "/fallback.ts");

        await Assert.That(normalized.IsFailure).IsTrue();
        await Assert.That(normalized.Error).IsEqualTo("missing-uri");
    }

    [Test]
    public async Task EmptyArray_MapsToNone()
    {
        Result<Maybe<LspLocation>> normalized =
            LspServerSession.TryNormalizeFirstLocation(Parse("[]"), "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task NonFileUri_FallsBackToRequestPath()
    {
        Result<Maybe<LspLocation>> normalized = LspServerSession.TryNormalizeFirstLocation(Parse("""
            {
              "uri": "untitled:Untitled-1",
              "range": { "start": { "line": 0, "character": 0 } }
            }
            """), "/fallback.ts");

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.Value.FilePath).IsEqualTo("/fallback.ts");
    }
}

/// <summary>Manager degradation from #200: reasons in the log, null/[] in the contract, never a throw.</summary>
public class LspManagerDegradeTests
{
    private static LspServerDefinition GhostDefinition(string extension = ".gst") =>
        new("ghost", "Ghost", "harbor-no-such-lsp-binary", [], [extension]);

    [Test]
    public async Task MissingBinary_ReferencesDegradeToEmpty()
    {
        await using var manager = new LspManager(NullLogger<LspManager>.Instance, [GhostDefinition()]);

        await manager.OpenFileAsync("/x/a.gst", "text");
        IReadOnlyList<LspDiagnostic> diagnostics = await manager.GetDiagnosticsAsync("/x/a.gst");
        await Assert.That(diagnostics).IsEmpty();

        IReadOnlyList<LspLocation> references =
            await manager.FindReferencesAsync("/x/a.gst", 0, 0, CancellationToken.None);
        await Assert.That(references).IsEmpty();

        // Best-effort notifications must not throw when the server never spawned.
        await manager.NotifyChangeAsync("/x/a.gst", "new text");
        await manager.CloseFileAsync("/x/a.gst");
    }

    [Test]
    public async Task UnsupportedFile_AllLookupsDegradeToEmpty()
    {
        await using var manager = new LspManager(NullLogger<LspManager>.Instance, []);

        await manager.OpenFileAsync("/x/a.wasm", "text");
        await manager.NotifyChangeAsync("/x/a.wasm", "new text");
        await manager.CloseFileAsync("/x/a.wasm");

        IReadOnlyList<LspDiagnostic> diagnostics = await manager.GetDiagnosticsAsync("/x/a.wasm");
        await Assert.That(diagnostics).IsEmpty();

        LspLocation? location = await manager.FindDefinitionAsync("/x/a.wasm", 0, 0, CancellationToken.None);
        await Assert.That(location).IsNull();

        IReadOnlyList<LspLocation> references =
            await manager.FindReferencesAsync("/x/a.wasm", 0, 0, CancellationToken.None);
        await Assert.That(references).IsEmpty();
    }

    [Test]
    public async Task DefinitionWithoutOpenSession_DegradesToNull()
    {
        // Supported extension but the file was never opened, so no session was spawned.
        await using var manager = new LspManager(NullLogger<LspManager>.Instance, [GhostDefinition(".gts")]);

        LspLocation? location = await manager.FindDefinitionAsync("/x/a.gts", 0, 0, CancellationToken.None);
        await Assert.That(location).IsNull();
    }
}
