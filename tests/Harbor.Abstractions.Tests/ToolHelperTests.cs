using System.Text.Json;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Tools;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     ROP-A zone-1 shared tool helpers — the single sources of truth that
///     replaced per-tool hand copies (path resolve, JSON arg readers, boundary
///     error classifier, OCE-rethrow handler).
/// </summary>
public class ToolHelperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── JsonArgs ──

    [Test]
    public async Task JsonArgs_ReadsOptionalValues()
    {
        var args = Parse("""{"s":"hi","i":42,"b":true,"n":null}""");

        await Assert.That(JsonArgs.GetString(args, "s")).IsEqualTo("hi");
        await Assert.That(JsonArgs.GetInt(args, "i")).IsEqualTo(42);
        await Assert.That(JsonArgs.GetBool(args, "b")).IsTrue();
        await Assert.That(JsonArgs.GetBoolOrNull(args, "b")).IsTrue();

        await Assert.That(JsonArgs.GetString(args, "missing")).IsNull();
        await Assert.That(JsonArgs.GetInt(args, "s")).IsNull();
        await Assert.That(JsonArgs.GetInt(args, "n")).IsNull();
        await Assert.That(JsonArgs.GetBool(args, "missing")).IsFalse();
        await Assert.That(JsonArgs.GetBoolOrNull(args, "missing")).IsNull();
    }

    [Test]
    public async Task JsonArgs_RequireString_FailsWithFieldName()
    {
        var args = Parse("""{"url":"https://x","empty":""}""");

        var ok = JsonArgs.RequireString(args, "url");
        await Assert.That(ok.IsSuccess).IsTrue();
        await Assert.That(ok.Value).IsEqualTo("https://x");

        var missing = JsonArgs.RequireString(args, "nope");
        await Assert.That(missing.IsFailure).IsTrue();
        await Assert.That(missing.Error).Contains("'nope'");

        var empty = JsonArgs.RequireString(args, "empty");
        await Assert.That(empty.IsFailure).IsTrue();
        await Assert.That(empty.Error).Contains("'empty'");
    }

    [Test]
    public async Task JsonArgs_GetClampedInt_ClampsAndDefaults()
    {
        var args = Parse("""{"big":50000,"small":0,"neg":-5,"one":7}""");

        await Assert.That(JsonArgs.GetClampedInt(args, "big", 1, 10_000, 100)).IsEqualTo(10_000);
        await Assert.That(JsonArgs.GetClampedInt(args, "small", 1, 10_000, 100)).IsEqualTo(1);
        await Assert.That(JsonArgs.GetClampedInt(args, "neg", 1, 10_000, 100)).IsEqualTo(1);
        await Assert.That(JsonArgs.GetClampedInt(args, "one", 1, 10_000, 100)).IsEqualTo(7);

        // absent / wrong-kind → default, not clamp edges
        await Assert.That(JsonArgs.GetClampedInt(args, "missing", 1, 10_000, 100)).IsEqualTo(100);
        await Assert.That(JsonArgs.GetClampedInt(args, "s", 1, 10_000, 100)).IsEqualTo(100);
    }

    [Test]
    public async Task JsonArgs_GetObject_ReturnsElementOrDefault()
    {
        var args = Parse("""{"obj":{"k":1},"arr":[1],"notObj":"x"}""");

        JsonElement obj = JsonArgs.GetObject(args, "obj");
        await Assert.That(obj.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(obj.GetProperty("k").GetInt32()).IsEqualTo(1);

        // Absent / wrong kind → default (Undefined), the "absent ⇒ {}"
        // spelling MCP pass-through arguments relied on.
        await Assert.That(JsonArgs.GetObject(args, "missing").ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(JsonArgs.GetObject(args, "arr").ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(JsonArgs.GetString(args, "obj")).IsNull();
    }

    [Test]
    public async Task JsonArgs_GetFirstString_ReturnsFirstStringAlias()
    {
        var args = Parse("""{"toolName":"camel","ToolName":"pascal","count":3}""");

        // First alias present as a string wins; non-string/absent aliases skipped.
        await Assert.That(JsonArgs.GetFirstString(args, "toolName", "ToolName")).IsEqualTo("camel");
        await Assert.That(JsonArgs.GetFirstString(args, "ToolName", "toolName")).IsEqualTo("pascal");
        await Assert.That(JsonArgs.GetFirstString(args, "missing", "ToolName")).IsEqualTo("pascal");
        await Assert.That(JsonArgs.GetFirstString(args, "missing", "count", "other")).IsNull();
    }

    // ── ToolPaths ──

    [Test]
    public async Task ToolPaths_Resolve_MapsRelativeAgainstCwd()
    {
        var result = ToolPaths.Resolve("sub/file.txt");
        string expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "sub/file.txt"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task ToolPaths_Resolve_PassesRootedThrough()
    {
        string rooted = Path.Combine(Directory.GetCurrentDirectory(), "harbor-helper-tests-tmp");
        var result = ToolPaths.Resolve(rooted);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(Path.GetFullPath(rooted));
    }

    [Test]
    public async Task ToolPaths_Resolve_InvalidPath_ReturnsCanonicalError()
    {
        var result = ToolPaths.Resolve(new string(Path.GetInvalidPathChars()) + "x");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).StartsWith("Invalid path:");
    }

    // ── ToolErrors.Handler ──

    [Test]
    public async Task ToolErrors_Handler_ClassifiesCancellationAndTimeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = ToolErrors.Handler("read", cts.Token, TimeSpan.FromSeconds(5));

        // Timeout-shaped OCE: no caller cancellation yet.
        await Assert.That(handler(new OperationCanceledException())).IsEqualTo("read timed out after 5s.");
        await Assert.That(handler(new InvalidOperationException("boom"))).IsEqualTo("boom");

        // Once the caller cancels, every OCE reads as "cancelled".
        cts.Cancel();
        await Assert.That(handler(new OperationCanceledException(cts.Token))).IsEqualTo("read cancelled");
    }

    [Test]
    [Timeout(30_000)]
    public async Task ToolErrors_Handler_TimeoutWithoutSpan_FallsBackToCancelledText(CancellationToken cancellationToken)
    {
        var handler = ToolErrors.Handler("grep", cancellationToken);

        await Assert.That(handler(new OperationCanceledException())).IsEqualTo("grep cancelled");
    }

    // ── ResultErrors ──

    [Test]
    [Timeout(30_000)]
    public async Task ResultErrors_Message_RethrowsOceAndUnwrapsOthers(CancellationToken cancellationToken)
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Task.Run(() => ResultErrors.Message(new OperationCanceledException(cancellationToken))));

        await Assert.That(ResultErrors.Message(new InvalidOperationException("disk full")))
            .IsEqualTo("disk full");
    }
}
