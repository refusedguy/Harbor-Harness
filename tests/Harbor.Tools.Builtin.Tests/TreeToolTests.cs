using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="TreeTool" /> — directory tree rendering against a synthetic
///     temp directory. Hidden files, depth caps, and the file/dir summary line are checked.
/// </summary>
public class TreeToolTests
{
    [Test]
    public async Task Name_IsTree()
    {
        var tool = new TreeTool(NullLogger<TreeTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("tree");
    }

    [Test]
    public async Task ExecutionMode_IsParallel()
    {
        var tool = new TreeTool(NullLogger<TreeTool>.Instance);
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
    }

    [Test]
    [Arguments("{}", true)]
    [Arguments("""{"maxDepth":99}""", false)]
    public async Task ValidateArguments_Theory(string json, bool expectSuccess)
    {
        var tool = new TreeTool(NullLogger<TreeTool>.Instance);
        var args = JsonDocument.Parse(json).RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsEqualTo(expectSuccess);
    }

    [Test]
    public async Task ExecuteAsync_NonExistentDir_ReturnsError()
    {
        var tool = new TreeTool(NullLogger<TreeTool>.Instance);
        var args = JsonDocument.Parse(
            $$"""{"path":"/tmp/harbor-tree-missing-{{Guid.NewGuid():N}}"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext());
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    public async Task ExecuteAsync_RendersTreeWithFilesAndDirs()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src", "sub"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "hi");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Program.cs"), "code");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "sub", "Util.cs"), "more");
        await File.WriteAllTextAsync(Path.Combine(root, "tests", "Foo.cs"), "test");

        try
        {
            var tool = new TreeTool(NullLogger<TreeTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{root.Replace("\\", "\\\\")}}","maxDepth":4,"gitignore":false}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("src/");
            await Assert.That(result.Output).Contains("tests/");
            await Assert.That(result.Output).Contains("README.md");
            await Assert.That(result.Output).Contains("Program.cs");
            await Assert.That(result.Output).Contains("Util.cs");
            await Assert.That(result.Output).Contains("Foo.cs");
            // The branch characters should appear at least once for any non-trivial tree.
            await Assert.That(result.Output.Contains("├── ") || result.Output.Contains("└── ")).IsTrue();
            // Summary footer
            await Assert.That(result.Output).Contains("file(s)");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ExecuteAsync_RespectsMaxDepth()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c", "d"));
        await File.WriteAllTextAsync(Path.Combine(root, "a", "b", "c", "d", "deep.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "a", "top.txt"), "x");

        try
        {
            var tool = new TreeTool(NullLogger<TreeTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{root.Replace("\\", "\\\\")}}","maxDepth":2,"gitignore":false}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("a/");
            await Assert.That(result.Output).Contains("top.txt");
            // deep.txt at depth 4 should NOT appear when maxDepth=2
            await Assert.That(result.Output.Contains("deep.txt")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ExecuteAsync_HidesHiddenFilesByDefault()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "v");
        await File.WriteAllTextAsync(Path.Combine(root, ".hidden"), "h");

        try
        {
            var tool = new TreeTool(NullLogger<TreeTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{root.Replace("\\", "\\\\")}}","gitignore":false}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("visible.txt");
            await Assert.That(result.Output.Contains(".hidden")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ExecuteAsync_IncludeHidden_ShowsHiddenFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "v");
        await File.WriteAllTextAsync(Path.Combine(root, ".env"), "h");

        try
        {
            var tool = new TreeTool(NullLogger<TreeTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{root.Replace("\\", "\\\\")}}","includeHidden":true,"gitignore":false}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains(".env");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
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
