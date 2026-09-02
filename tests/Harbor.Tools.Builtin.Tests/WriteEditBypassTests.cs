using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class WriteEditBypassTests
{
    private static string NewTempRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "harbor-writeedit-bypass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static JsonElement Args(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

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

    [Test]
    public async Task Write_ExecuteAsync_AbsoluteTraversalPathEscapingWorkspace_IsRefused()
    {
        string root = NewTempRoot();
        try
        {
            string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
            string escapedTarget = Path.Combine(root, "outside-escape.txt");
            string traversalPath = Path.Combine(workspace, "src", "..", "..", "outside-escape.txt");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                Args(new { path = traversalPath, content = "pwned" }),
                CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(File.Exists(escapedTarget)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Edit_ExecuteAsync_TraversalPathEscapingWorkspace_IsRefusedAndLeavesFileUnchanged()
    {
        string root = NewTempRoot();
        try
        {
            string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
            string victim = Path.Combine(root, "victim.txt");
            await File.WriteAllTextAsync(victim, "AAA");
            string traversalPath = Path.Combine(workspace, "sub", "..", "..", "victim.txt");

            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var result = await tool.ExecuteAsync(
                Args(new { path = traversalPath, oldString = "AAA", newString = "BBB" }),
                CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(victim)).IsEqualTo("AAA");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [SkipWhenSymlinksUnsupported]
    public async Task Write_ExecuteAsync_SymlinkInsideWorkspacePointingOutside_IsRefused()
    {
        string root = NewTempRoot();
        try
        {
            string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            _ = Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside);
            string escapeTarget = Path.Combine(outside, "escape.txt");
            string linkRelativePath = Path.Combine(workspace, "link", "escape.txt");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                Args(new { path = linkRelativePath, content = "pwned" }),
                CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(File.Exists(escapeTarget)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [SkipWhenSymlinksUnsupported]
    public async Task Edit_ExecuteAsync_SymlinkInsideWorkspacePointingOutside_IsRefusedAndLeavesFileUnchanged()
    {
        string root = NewTempRoot();
        try
        {
            string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            string victim = Path.Combine(outside, "victim.txt");
            await File.WriteAllTextAsync(victim, "AAA");
            _ = Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside);
            string linkRelativePath = Path.Combine(workspace, "link", "victim.txt");

            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var result = await tool.ExecuteAsync(
                Args(new { path = linkRelativePath, oldString = "AAA", newString = "BBB" }),
                CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(victim)).IsEqualTo("AAA");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class SkipWhenSymlinksUnsupportedAttribute : SkipAttribute
{
    public SkipWhenSymlinksUnsupportedAttribute() : base("Symbolic link creation is not supported on this platform.")
    {
    }

    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS());
}
