using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
public class ReadToolTests
{
    [Test]
    public async Task Name_IsRead()
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("read");
    }

    [Test]
    public async Task ValidateArguments_MissingPath_ReturnsFailure()
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task ExecuteAsync_NonExistentFile_ReturnsError()
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        var args = JsonDocument.Parse("""{"path": "/nonexistent/file.txt"}""").RootElement;
        var ctx = CreateContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("File not found");
    }

    [Test]
    public async Task ExecuteAsync_ReadsFileContent()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "Hello, World!");

        try
        {
            var tool = new ReadTool(NullLogger<ReadTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("Hello, World!");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_AddsLineNumbers()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "line1\nline2\nline3");

        try
        {
            var tool = new ReadTool(NullLogger<ReadTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.Output).Contains("[0001]");
            await Assert.That(result.Output).Contains("[0002]");
            await Assert.That(result.Output).Contains("[0003]");
        }
        finally
        {
            File.Delete(tempFile);
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

public class WriteToolTests
{
    [Test]
    public async Task ExecuteAsync_CreatesNewFile()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}.txt");
        try
        {
            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"content\": \"test content\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.Exists(tempFile)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo("test content");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_CreatesParentDirectories()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        string tempFile = Path.Combine(tempDir, "sub", "dir", "file.txt");
        try
        {
            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"content\": \"nested\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.Exists(tempFile)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
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

public class EditToolTests
{
    [Test]
    public async Task ExecuteAsync_ReplacesString()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "Hello, World!");
        try
        {
            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"oldString\": \"World\", \"newString\": \"Harbor\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo("Hello, Harbor!");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_NotFound_ReturnsError()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "Hello");
        try
        {
            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"oldString\": \"notthere\", \"newString\": \"x\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("not found");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_MultipleOccurrences_WithoutReplaceAll_ReturnsError()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "ababab");
        try
        {
            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"oldString\": \"a\", \"newString\": \"x\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ExecuteAsync_ReplaceAll_ReplacesAllOccurrences()
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "ababab");
        try
        {
            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"oldString\": \"a\", \"newString\": \"x\", \"replaceAll\": true}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo("xbxbxb");
        }
        finally
        {
            File.Delete(tempFile);
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

public class GlobToolTests
{
    [Test]
    public async Task ExecuteAsync_FindsMatchingFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-glob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "b.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "c.txt"), "");

        try
        {
            var tool = new GlobTool(NullLogger<GlobTool>.Instance);
            var args = JsonDocument.Parse($"{{\"pattern\": \"*.cs\", \"path\": \"{tempDir.Replace("\\", "\\\\")}\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("a.cs");
            await Assert.That(result.Output).Contains("b.cs");
            await Assert.That(result.Output.Contains("c.txt")).IsFalse();
        }
        finally
        {
            Directory.Delete(tempDir, true);
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

public class GrepToolTests
{
    [Test]
    public async Task ExecuteAsync_FindsMatches()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-grep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txt"), "foo\nbar\nbaz");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "b.txt"), "another foo here");

        try
        {
            var tool = new GrepTool(NullLogger<GrepTool>.Instance);
            var args = JsonDocument.Parse($"{{\"pattern\": \"foo\", \"path\": \"{tempDir.Replace("\\", "\\\\")}\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("foo");
            await Assert.That(result.Output).Contains("a.txt");
            await Assert.That(result.Output).Contains("b.txt");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExecuteAsync_NoMatches_ReturnsEmpty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-grep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txt"), "foo");

        try
        {
            var tool = new GrepTool(NullLogger<GrepTool>.Instance);
            var args = JsonDocument.Parse($"{{\"pattern\": \"xyz\", \"path\": \"{tempDir.Replace("\\", "\\\\")}\"}}").RootElement;
            var ctx = CreateContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("No matches");
        }
        finally
        {
            Directory.Delete(tempDir, true);
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

public class BashToolTests
{
    [Test]
    public async Task ExecuteAsync_Echo_ReturnsOutput()
    {
        var tool = new BashTool(NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse("""{"command": "echo hello"}""").RootElement;
        var ctx = CreateContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("hello");
    }

    [Test]
    public async Task ExecuteAsync_ExitCode_NonZero_ReturnsError()
    {
        var tool = new BashTool(NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse("""{"command": "exit 1"}""").RootElement;
        var ctx = CreateContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsTrue();
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
