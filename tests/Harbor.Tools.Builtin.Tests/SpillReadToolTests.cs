using System.Text.Json;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for the <see cref="SpillReadTool" /> — the way back to a tool
///     output that did not fit the model context (#1748).
/// </summary>
/// <remarks>
///     Pinned:
///     <list type="bullet">
///       <item>the READ case: the spilled text comes back, framed by the path it came from;</item>
///       <item>the MISSING FILE case: the store's failure reaches the model as a failed tool result, not as silence;</item>
///       <item>the read is PAGED: a window returns the lines it was asked for and names the next offset;</item>
///       <item>the arguments are validated before the store is touched.</item>
///     </list>
/// </remarks>
public class SpillReadToolTests
{
    private const string Path = "/spill/session/abcdef.txt";

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static SpillReadTool Tool(FakeSpillStore store) =>
        new(store, NullLogger<SpillReadTool>.Instance);

    [Test]
    public async Task ExecuteAsync_SpilledFile_ReturnsTheContent()
    {
        var store = new FakeSpillStore();
        store.Files[Path] = "line one\nline two\n";

        var result = await Tool(store).ExecuteAsync(
            Args($$"""{"path": "{{Path}}"}"""),
            TestToolContext.Create(sessionId: "session-spill"),
            CancellationToken.None);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("line one");
        await Assert.That(result.Output).Contains("line two");
        await Assert.That(result.Output).Contains(Path);
    }

    [Test]
    public async Task ExecuteAsync_MissingFile_ReturnsAnError()
    {
        var store = new FakeSpillStore();

        var result = await Tool(store).ExecuteAsync(
            Args("""{"path": "/spill/session/gone.txt"}"""),
            TestToolContext.Create(sessionId: "session-spill"),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    public async Task ExecuteAsync_Window_ReturnsTheRequestedLines_AndTheNextOffset()
    {
        var store = new FakeSpillStore();
        store.Files[Path] = "one\ntwo\nthree\nfour\n";

        var result = await Tool(store).ExecuteAsync(
            Args($$"""{"path": "{{Path}}", "offset": 2, "limit": 2}"""),
            TestToolContext.Create(sessionId: "session-spill"),
            CancellationToken.None);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("two");
        await Assert.That(result.Output).Contains("three");
        await Assert.That(result.Output.Contains("one")).IsFalse();
        await Assert.That(result.Output.Contains("four")).IsFalse();
        await Assert.That(result.Output).Contains("offset=4");
    }

    [Test]
    public async Task ValidateArguments_MissingPath_Fails()
    {
        var tool = Tool(new FakeSpillStore());
        JsonElement args = JsonDocument.Parse("{}").RootElement.Clone();

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsFailure).IsTrue();
    }
}