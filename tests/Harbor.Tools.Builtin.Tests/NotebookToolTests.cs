using System.Text.Json;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="NotebookTool" /> — set/get/add/clear/list flow against a
///     temp directory so the default <c>~/.harbor/notes</c> root is never touched.
/// </summary>
public class NotebookToolTests
{
    [Test]
    public async Task Name_IsNotebook()
    {
        var tool = NewTool();
        await Assert.That(tool.Name.Value).IsEqualTo("notebook");
    }

    [Test]
    public async Task ExecutionMode_IsSequential()
    {
        var tool = NewTool();
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
    }

    [Test]
    [Arguments("{}", false, null)]
    [Arguments("""{"action":"frobnicate","key":"k"}""", false, "frobnicate")]
    [Arguments("""{"action":"set","key":"k"}""", false, "content")]
    [Arguments("""{"action":"set","key":"k","content":"v"}""", true, null)]
    public async Task ValidateArguments_Theory(string json, bool expectSuccess, string? expectedErrorSubstring = null)
    {
        var tool = NewTool();
        var args = JsonDocument.Parse(json).RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsEqualTo(expectSuccess);
        if (expectedErrorSubstring is not null)
            await Assert.That(result.Error).Contains(expectedErrorSubstring);
    }

    [Test]
    public async Task Set_ThenGet_ReturnsContent()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "todo"), ("content", "buy milk")), TestToolContext.Create());
        var result = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "get"), ("key", "todo")), TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("buy milk");
    }

    [Test]
    public async Task Get_MissingKey_ReturnsError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "get"), ("key", "nope")), TestToolContext.Create());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("nope");
    }

    [Test]
    public async Task Add_AppendsToExisting()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "log"), ("content", "first")), TestToolContext.Create());
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "add"), ("key", "log"), ("content", "second")), TestToolContext.Create());
        var result = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "get"), ("key", "log")), TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("first");
        await Assert.That(result.Output).Contains("second");
    }

    [Test]
    public async Task List_ReturnsAllKeysWithPreview()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "alpha"), ("content", "first note")), TestToolContext.Create());
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "beta"), ("content", "second note")), TestToolContext.Create());
        var result = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "list")), TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("alpha");
        await Assert.That(result.Output).Contains("beta");
        await Assert.That(result.Output).Contains("first note");
    }

    [Test]
    public async Task Clear_SingleKey_RemovesJustThatKey()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "alpha"), ("content", "x")), TestToolContext.Create());
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "beta"), ("content", "y")), TestToolContext.Create());

        var clearResult = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "clear"), ("key", "alpha")), TestToolContext.Create());
        await Assert.That(clearResult.IsError).IsFalse();

        var listResult = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "list")), TestToolContext.Create());
        await Assert.That(listResult.Output).Contains("beta");
        await Assert.That(listResult.Output.Contains("alpha")).IsFalse();
    }

    [Test]
    public async Task Clear_All_RemovesEverything()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "a"), ("content", "x")), TestToolContext.Create());
        await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", "b"), ("content", "y")), TestToolContext.Create());

        var clearResult = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "clear")), TestToolContext.Create());
        await Assert.That(clearResult.IsError).IsFalse();
        await Assert.That(clearResult.Output).Contains("Cleared");

        var listResult = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "list")), TestToolContext.Create());
        await Assert.That(listResult.Output).Contains("no notes");
    }

    [Test]
    public async Task Notes_PersistAcrossToolInstances_ForSameSession()
    {
        // Each tool instance uses the same notesRoot on disk — different sessions are
        // isolated by session id, but the same session id should see the same notes.
        string notesRoot = NewNotesRoot();
        try
        {
            var tool1 = new NotebookTool(NullLogger<NotebookTool>.Instance, notesRoot);
            await tool1.ExecuteAsync(
                TestArgs.FromPairs(("action", "set"), ("key", "persisted"), ("content", "across instances")),
                TestToolContext.Create(sessionId: "shared-session"));

            var tool2 = new NotebookTool(NullLogger<NotebookTool>.Instance, notesRoot);
            var result = await tool2.ExecuteAsync(
                TestArgs.FromPairs(("action", "get"), ("key", "persisted")),
                TestToolContext.Create(sessionId: "shared-session"));

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("across instances");
        }
        finally
        {
            if (Directory.Exists(notesRoot)) Directory.Delete(notesRoot, true);
        }
    }

    [Test]
    public async Task Notes_AreIsolatedBySessionId()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(
            TestArgs.FromPairs(("action", "set"), ("key", "secret"), ("content", "session-A only")),
            TestToolContext.Create(sessionId: "session-A"));

        var result = await tool.ExecuteAsync(
            TestArgs.FromPairs(("action", "get"), ("key", "secret")),
            TestToolContext.Create(sessionId: "session-B"));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("No note");
    }

    [Test]
    public async Task List_LargeNoteCount_CompletesWithCappedInitialRent()
    {
        // #53 audit: List rents with a capped initial capacity (8 KB max) and
        // lets the builder grow — 200 notes (under the 256/session cap) must
        // succeed without a huge up-front allocation.
        var tool = NewTool();
        for (int i = 0; i < 200; i++)
            await tool.ExecuteAsync(TestArgs.FromPairs(("action", "set"), ("key", $"k{i:000}"), ("content", $"note number {i}")), TestToolContext.Create());
        var result = await tool.ExecuteAsync(TestArgs.FromPairs(("action", "list")), TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("200 note(s)");
        await Assert.That(result.Output).Contains("k199");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static NotebookTool NewTool()
        => new(NullLogger<NotebookTool>.Instance, NewNotesRoot());

    private static string NewNotesRoot()
        => Path.Combine(Path.GetTempPath(), $"harbor-notes-{Guid.NewGuid():N}");

}
