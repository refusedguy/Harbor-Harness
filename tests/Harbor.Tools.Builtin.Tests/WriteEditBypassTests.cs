using System.Text.Json;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

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

    private static JsonElement Args<T>(T payload) =>
        JsonDocument.Parse(TestJson.Serialize(payload)).RootElement.Clone();

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
                Args(new ToolPathContentArgs(traversalPath, "pwned")),
                TestToolContext.Create());

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
                Args(new ToolPathEditArgs(traversalPath, "AAA", "BBB")),
                TestToolContext.Create());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(victim)).IsEqualTo("AAA");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [ExcludeOn(OS.Windows | OS.Browser)]
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
                Args(new ToolPathContentArgs(linkRelativePath, "pwned")),
                TestToolContext.Create());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(File.Exists(escapeTarget)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [ExcludeOn(OS.Windows | OS.Browser)]
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
                Args(new ToolPathEditArgs(linkRelativePath, "AAA", "BBB")),
                TestToolContext.Create());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(victim)).IsEqualTo("AAA");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
