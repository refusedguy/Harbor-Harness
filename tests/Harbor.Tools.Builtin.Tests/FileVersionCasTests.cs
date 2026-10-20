using System.Text.Json;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1757 — the compare-and-swap half of Harbor's filesystem observation
///     policy. A <c>write</c>/<c>edit</c> that states the version of the file
///     it observed is REJECTED when the file moved on in the meantime, instead
///     of silently clobbering the change that landed in between.
/// </summary>
/// <remarks>
///     The "read before write" gate — refusing a write to a file the session
///     never observed — is deliberately NOT here: it needs a per-session record
///     of what was observed, which Harbor has no home for yet. These tests
///     cover the CAS half only, and the version travels explicitly through the
///     tool arguments, exactly as a model would carry it.
/// </remarks>
public class FileVersionCasTests
{
    private const string VersionMarker = "[file-version: ";

    [Test]
    public async Task Read_ExecuteAsync_ReportsTheFileVersion()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "hello");

            var tool = new ReadTool(NullLogger<ReadTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathArgs(file)),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains(VersionMarker);

            string version = ExtractVersion(result.Output);
            await Assert.That(version.StartsWith("sha256:", StringComparison.Ordinal)).IsTrue();
            await Assert.That(version.Length).IsEqualTo("sha256:".Length + 16);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_UnchangedFile_WithExpectedVersion_Succeeds()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            string version = await ReadVersionAsync(file);

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", version)),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("written by the model");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_FileChangedOnDisk_IsRejected_AndTheFileIsLeftAlone()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            string version = await ReadVersionAsync(file);

            // Somebody else — another process, a git checkout, an editor —
            // changes the file after the model observed it.
            await File.WriteAllTextAsync(file, "changed by somebody else");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", version)),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("changed on disk");
            await Assert.That(result.Output).Contains("Re-read");
            await Assert.That(result.Output).Contains(version);
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("changed by somebody else");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_AfterReread_TheRetrySucceeds()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var ctx = TestAgents.CreateToolContext();

            string stale = await ReadVersionAsync(file);
            await File.WriteAllTextAsync(file, "changed by somebody else");

            var rejected = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", stale)),
                ctx);
            await Assert.That(rejected.IsError).IsTrue();

            // The model re-reads, gets the current version, and retries.
            string fresh = await ReadVersionAsync(file);
            await Assert.That(fresh == stale).IsFalse();

            var retried = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", fresh)),
                ctx);

            await Assert.That(retried.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("written by the model");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_VersionOfADeletedFile_IsRejected()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            string version = await ReadVersionAsync(file);
            File.Delete(file);

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", version)),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("no longer exists");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_WithoutExpectedVersion_StillWrites()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentArgs(file, "written by the model")),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("written by the model");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_ReportedVersion_MatchesTheFileOnDisk()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentArgs(file, "written by the model")),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();

            // The version a write reports must be the version a later read
            // sees — otherwise the token is not a handle on the file.
            string reported = ExtractVersion(result.Output);
            string read = await ReadVersionAsync(file);
            await Assert.That(reported).IsEqualTo(read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ValidateArguments_MalformedExpectedVersion_Fails()
    {
        var tool = new WriteTool(NullLogger<WriteTool>.Instance);
        var args = TestArgs.FromPayload(new ToolPathContentVersionArgs("/tmp/x.txt", "x", "not-a-token"));

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Write_ExecuteAsync_MalformedExpectedVersion_IsRejected()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", "not-a-token")),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("original");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Edit_ExecuteAsync_UnchangedFile_WithExpectedVersion_Succeeds()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "Hello, World!");

            string version = await ReadVersionAsync(file);

            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathEditVersionArgs(file, "World", "Harbor", version)),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("Hello, Harbor!");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Edit_ExecuteAsync_FileChangedOnDisk_IsRejected_AndTheFileIsLeftAlone()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "Hello, World!");

            string version = await ReadVersionAsync(file);
            await File.WriteAllTextAsync(file, "Hello, Somebody Else!");

            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathEditVersionArgs(file, "World", "Harbor", version)),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("changed on disk");
            await Assert.That(result.Output).Contains("Re-read");
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("Hello, Somebody Else!");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Edit_ExecuteAsync_AfterReread_TheRetrySucceeds()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "Hello, World!");

            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var ctx = TestAgents.CreateToolContext();

            string stale = await ReadVersionAsync(file);
            await File.WriteAllTextAsync(file, "Hello, Somebody Else!");

            var rejected = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathEditVersionArgs(file, "World", "Harbor", stale)),
                ctx);
            await Assert.That(rejected.IsError).IsTrue();

            string fresh = await ReadVersionAsync(file);
            await Assert.That(fresh == stale).IsFalse();

            var retried = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathEditVersionArgs(file, "Somebody Else", "Harbor", fresh)),
                ctx);

            await Assert.That(retried.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("Hello, Harbor!");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Edit_ExecuteAsync_ReportedVersion_MatchesTheFileOnDisk()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "Hello, World!");

            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathEditArgs(file, "World", "Harbor")),
                TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();

            string reported = ExtractVersion(result.Output);
            string read = await ReadVersionAsync(file);
            await Assert.That(reported).IsEqualTo(read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_VersionOfAnUnchangedFile_IsAccepted_AfterAnUnrelatedWrite()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            string version = await ReadVersionAsync(file);

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var ctx = TestAgents.CreateToolContext();

            // A write to a DIFFERENT file does not disturb this one's version.
            string other = Path.Combine(root, "b.txt");
            var otherWrite = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentArgs(other, "unrelated")),
                ctx);
            await Assert.That(otherWrite.IsError).IsFalse();

            var result = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "written by the model", version)),
                ctx);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("written by the model");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExecuteAsync_AVersionStaleAfterTheFileMovedOn_IsRejected()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(file, "original");

            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var ctx = TestAgents.CreateToolContext();

            string version = await ReadVersionAsync(file);

            var first = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "first write", version)),
                ctx);
            await Assert.That(first.IsError).IsFalse();

            // The file moved on (by the write above), so the old version is stale.
            var second = await tool.ExecuteAsync(
                TestArgs.FromPayload(new ToolPathContentVersionArgs(file, "second write", version)),
                ctx);

            await Assert.That(second.IsError).IsTrue();
            await Assert.That(second.Output).Contains("changed on disk");
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("first write");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReadVersionAsync(string path)
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        var result = await tool.ExecuteAsync(
            TestArgs.FromPayload(new ToolPathArgs(path)),
            TestAgents.CreateToolContext());

        await Assert.That(result.IsError).IsFalse();
        return ExtractVersion(result.Output);
    }

    private static string ExtractVersion(string output)
    {
        int start = output.IndexOf(VersionMarker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"no file version in tool output: {output}");

        int end = output.IndexOf(']', start);
        if (end < 0)
            throw new InvalidOperationException($"unterminated file version in tool output: {output}");

        return output.Substring(start + VersionMarker.Length, end - start - VersionMarker.Length);
    }

    private static string NewTempRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "harbor-fs-cas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
