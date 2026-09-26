using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="PatchTool" /> — happy path patch application, context-mismatch
///     rejection, atomic write behaviour, and patch parsing edge cases.
/// </summary>
public class PatchToolTests
{
    [Test]
    public async Task Name_IsPatch()
    {
        var tool = new PatchTool(NullLogger<PatchTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("patch");
    }

    [Test]
    public async Task ExecutionMode_IsSequential()
    {
        var tool = new PatchTool(NullLogger<PatchTool>.Instance);
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
    }

    [Test]
    [Arguments("""{"patch":"@@ -1,1 +1,2 @@\n-a\n+b\n"}""", false)]
    [Arguments("""{"path":"/tmp/x.txt"}""", false)]
    [Arguments("""{"path":"/tmp/x.txt","patch":"@@ -1,1 +1,1 @@\n-a\n+b\n"}""", true)]
    public async Task ValidateArguments_Theory(string json, bool expectSuccess)
    {
        var tool = new PatchTool(NullLogger<PatchTool>.Instance);
        var args = JsonDocument.Parse(json).RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsEqualTo(expectSuccess);
    }

    [Test]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        var tool = new PatchTool(NullLogger<PatchTool>.Instance);
        var args = JsonDocument.Parse(
            $$"""{"path":"/tmp/harbor-not-here-{{Guid.NewGuid():N}}.txt","patch":"@@ -1,1 +1,1 @@\n-a\n+b\n"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext());
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    public async Task ExecuteAsync_AppliesSimpleAdditionPatch()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "line1\nline2\nline3\n");
        try
        {
            // Insert a new line between line1 and line2.
            string patch = """
                           @@ -1,3 +1,4 @@
                            line1
                           +inserted
                            line2
                            line3
                           """;
            var tool = new PatchTool(NullLogger<PatchTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{tempFile.Replace("\\", "\\\\")}}","patch":{{JsonSerializer.Serialize(patch)}}}""").RootElement;

            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            string updated = await File.ReadAllTextAsync(tempFile);
            await Assert.That(updated).Contains("inserted");
            await Assert.That(updated).Contains("line1");
            await Assert.That(updated).Contains("line2");
            await Assert.That(updated).Contains("line3");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_AppliesModificationPatch()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "foo\nbar\nbaz\n");
        try
        {
            string patch = """
                           @@ -1,3 +1,3 @@
                            foo
                           -bar
                           +BAR
                            baz
                           """;
            var tool = new PatchTool(NullLogger<PatchTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{tempFile.Replace("\\", "\\\\")}}","patch":{{JsonSerializer.Serialize(patch)}}}""").RootElement;

            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            string updated = await File.ReadAllTextAsync(tempFile);
            await Assert.That(updated).Contains("BAR");
            await Assert.That(updated.Contains("bar\n")).IsFalse();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_MismatchedContext_LeavesFileUntouched()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "alpha\nbeta\ngamma\n");
        string original = await File.ReadAllTextAsync(tempFile);
        try
        {
            // Patch expects "alpha\nWRONG\ngamma\n" but file has "alpha\nbeta\ngamma\n".
            string patch = """
                           @@ -1,3 +1,3 @@
                            alpha
                           -WRONG
                           +replaced
                            gamma
                           """;
            var tool = new PatchTool(NullLogger<PatchTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{tempFile.Replace("\\", "\\\\")}}","patch":{{JsonSerializer.Serialize(patch)}}}""").RootElement;

            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("match");
            // File is untouched
            string after = await File.ReadAllTextAsync(tempFile);
            await Assert.That(after).IsEqualTo(original);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_PreviewIncludesAddedAndRemovedLines()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "x\n");
        try
        {
            string patch = """
                           @@ -1,1 +1,1 @@
                           -x
                           +y
                           """;
            var tool = new PatchTool(NullLogger<PatchTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{tempFile.Replace("\\", "\\\\")}}","patch":{{JsonSerializer.Serialize(patch)}}}""").RootElement;

            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("- x");
            await Assert.That(result.Output).Contains("+ y");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_NoHunks_ReturnsError()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "hello\n");
        try
        {
            // No @@ header — just file headers.
            string patch = """
                           --- a/file.txt
                           +++ b/file.txt
                           """;
            var tool = new PatchTool(NullLogger<PatchTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{tempFile.Replace("\\", "\\\\")}}","patch":{{JsonSerializer.Serialize(patch)}}}""").RootElement;

            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("no hunks");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}
