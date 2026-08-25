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
    public async Task ToolErrors_Handler_TimeoutWithoutSpan_FallsBackToCancelledText()
    {
        using var cts = new CancellationTokenSource();
        var handler = ToolErrors.Handler("grep", cts.Token);

        await Assert.That(handler(new OperationCanceledException())).IsEqualTo("grep cancelled");
    }

    // ── ResultErrors ──

    [Test]
    public async Task ResultErrors_Message_RethrowsOceAndUnwrapsOthers()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Task.Run(() => ResultErrors.Message(new OperationCanceledException(cts.Token))));

        await Assert.That(ResultErrors.Message(new InvalidOperationException("disk full")))
            .IsEqualTo("disk full");
    }
}
