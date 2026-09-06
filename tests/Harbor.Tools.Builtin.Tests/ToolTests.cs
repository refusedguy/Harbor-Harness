using System.Text.Json;
using Harbor.TestKit;
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
    [Arguments("{}", false)]
    [Arguments("""{"path": "/nonexistent/file.txt"}""", true)]
    [Arguments("""{"path": "/tmp/some-file.txt"}""", true)]
    public async Task ValidateArguments_Theory(string json, bool expectSuccess)
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        var args = JsonDocument.Parse(json).RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsEqualTo(expectSuccess);
    }

    [Test]
    public async Task ExecuteAsync_NonExistentFile_ReturnsError()
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        var args = JsonDocument.Parse("""{"path": "/nonexistent/file.txt"}""").RootElement;
            var ctx = TestAgents.CreateToolContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("File not found");
    }

    [Test]
    [Arguments("Hello, World!", "Hello, World!")]
    [Arguments("line1\nline2\nline3", "[0001];[0002];[0003]")]
    public async Task ExecuteAsync_ReadsFileContent(string fileContent, string expectedSubstrings)
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, fileContent);

        try
        {
            var tool = new ReadTool(NullLogger<ReadTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\"}}").RootElement;
            var ctx = TestAgents.CreateToolContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            foreach (var substring in expectedSubstrings.Split(';'))
                await Assert.That(result.Output).Contains(substring);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

public class WriteToolTests
{
    [Test]
    [Arguments("test content", "file.txt")]
    [Arguments("nested", "sub/dir/file.txt")]
    public async Task ExecuteAsync_WritesContent(string content, string relativePath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        string tempFile = Path.Combine(tempDir, relativePath);
        try
        {
            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var escapedPath = tempFile.Replace("\\", "\\\\");
            var args = JsonDocument.Parse($"{{\"path\": \"{escapedPath}\", \"content\": \"{content}\"}}").RootElement;
            var ctx = TestAgents.CreateToolContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.Exists(tempFile)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo(content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

public class EditToolTests
{
    [Test]
    [Arguments("Hello, World!", "World", "Harbor", false, false, "Hello, Harbor!", "")]
    [Arguments("ababab", "a", "x", true, false, "xbxbxb", "")]
    [Arguments("Hello", "notthere", "x", false, true, "Hello", "not found")]
    [Arguments("ababab", "a", "x", false, true, "ababab", "")]
    public async Task ExecuteAsync_EditsFile(
        string content,
        string oldString,
        string newString,
        bool replaceAll,
        bool expectError,
        string expectedFileContent,
        string expectedOutputContains)
    {
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, content);
        try
        {
            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var escapedPath = tempFile.Replace("\\", "\\\\");
            var replaceAllJson = replaceAll ? ", \"replaceAll\": true" : "";
            var args = JsonDocument.Parse($"{{\"path\": \"{escapedPath}\", \"oldString\": \"{oldString}\", \"newString\": \"{newString}\" {replaceAllJson}}}").RootElement;
            var ctx = TestAgents.CreateToolContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsEqualTo(expectError);
            await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo(expectedFileContent);
            if (!string.IsNullOrEmpty(expectedOutputContains))
                await Assert.That(result.Output).Contains(expectedOutputContains);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
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
            var ctx = TestAgents.CreateToolContext();

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
}

public class GrepToolTests
{
    [Test]
    [Arguments("foo\nbar\nbaz;another foo here", "foo", "file0.txt;file1.txt;foo")]
    [Arguments("foo", "xyz", "No matches")]
    public async Task ExecuteAsync_Grep(string fileContents, string pattern, string expectedSubstrings)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-grep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var contents = fileContents.Split(';');
        for (int i = 0; i < contents.Length; i++)
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"file{i}.txt"), contents[i]);

        try
        {
            var tool = new GrepTool(NullLogger<GrepTool>.Instance);
            var escapedPath = tempDir.Replace("\\", "\\\\");
            var args = JsonDocument.Parse($"{{\"pattern\": \"{pattern}\", \"path\": \"{escapedPath}\"}}").RootElement;
            var ctx = TestAgents.CreateToolContext();

            var result = await tool.ExecuteAsync(args, ctx);

            await Assert.That(result.IsError).IsFalse();
            foreach (var substring in expectedSubstrings.Split(';'))
                await Assert.That(result.Output).Contains(substring);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

public class BashToolTests
{
    [Test]
    [Arguments("echo hello", false, "hello")]
    [Arguments("exit 1", true, "")]
    public async Task ExecuteAsync_Command(string command, bool expectError, string expectedOutputContains)
    {
        var tool = new BashTool(NullLogger<BashTool>.Instance);
        var args = JsonDocument.Parse($"{{\"command\": \"{command}\"}}").RootElement;
            var ctx = TestAgents.CreateToolContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsEqualTo(expectError);
        if (!string.IsNullOrEmpty(expectedOutputContains))
            await Assert.That(result.Output).Contains(expectedOutputContains);
    }
}
