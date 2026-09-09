using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
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
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "todo"), ("content", "buy milk")), CreateContext());
        var result = await tool.ExecuteAsync(Args(("action", "get"), ("key", "todo")), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("buy milk");
    }

    [Test]
    public async Task Get_MissingKey_ReturnsError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(("action", "get"), ("key", "nope")), CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("nope");
    }

    [Test]
    public async Task Add_AppendsToExisting()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "log"), ("content", "first")), CreateContext());
        await tool.ExecuteAsync(Args(("action", "add"), ("key", "log"), ("content", "second")), CreateContext());
        var result = await tool.ExecuteAsync(Args(("action", "get"), ("key", "log")), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("first");
        await Assert.That(result.Output).Contains("second");
    }

    [Test]
    public async Task List_ReturnsAllKeysWithPreview()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "alpha"), ("content", "first note")), CreateContext());
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "beta"), ("content", "second note")), CreateContext());
        var result = await tool.ExecuteAsync(Args(("action", "list")), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("alpha");
        await Assert.That(result.Output).Contains("beta");
        await Assert.That(result.Output).Contains("first note");
    }

    [Test]
    public async Task Clear_SingleKey_RemovesJustThatKey()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "alpha"), ("content", "x")), CreateContext());
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "beta"), ("content", "y")), CreateContext());

        var clearResult = await tool.ExecuteAsync(Args(("action", "clear"), ("key", "alpha")), CreateContext());
        await Assert.That(clearResult.IsError).IsFalse();

        var listResult = await tool.ExecuteAsync(Args(("action", "list")), CreateContext());
        await Assert.That(listResult.Output).Contains("beta");
        await Assert.That(listResult.Output.Contains("alpha")).IsFalse();
    }

    [Test]
    public async Task Clear_All_RemovesEverything()
    {
        var tool = NewTool();
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "a"), ("content", "x")), CreateContext());
        await tool.ExecuteAsync(Args(("action", "set"), ("key", "b"), ("content", "y")), CreateContext());

        var clearResult = await tool.ExecuteAsync(Args(("action", "clear")), CreateContext());
        await Assert.That(clearResult.IsError).IsFalse();
        await Assert.That(clearResult.Output).Contains("Cleared");

        var listResult = await tool.ExecuteAsync(Args(("action", "list")), CreateContext());
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
                Args(("action", "set"), ("key", "persisted"), ("content", "across instances")),
                CreateContext(sessionId: "shared-session"));

            var tool2 = new NotebookTool(NullLogger<NotebookTool>.Instance, notesRoot);
            var result = await tool2.ExecuteAsync(
                Args(("action", "get"), ("key", "persisted")),
                CreateContext(sessionId: "shared-session"));

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
            Args(("action", "set"), ("key", "secret"), ("content", "session-A only")),
            CreateContext(sessionId: "session-A"));

        var result = await tool.ExecuteAsync(
            Args(("action", "get"), ("key", "secret")),
            CreateContext(sessionId: "session-B"));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("No note");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static NotebookTool NewTool()
        => new(NullLogger<NotebookTool>.Instance, NewNotesRoot());

    private static string NewNotesRoot()
        => Path.Combine(Path.GetTempPath(), $"harbor-notes-{Guid.NewGuid():N}");

    private static ToolContext CreateContext(string sessionId = "test-session") => new(
        sessionId,
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    private static JsonElement Args(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach ((string k, string v) in pairs)
            dict[k] = v;
        return JsonDocument.Parse(JsonSerializer.Serialize(dict)).RootElement.Clone();
    }
}
