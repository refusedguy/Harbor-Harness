// FileHistoryCaptureTests.cs — issue #3107, atomic slice 3.
//
// FileHistoryTracker, RewindBlobStore, RewindPathGuard and RewindOriginGuard
// had zero production callers. The write/edit tools are now wrapped at the
// composition root by FileHistoryToolDecorator, which captures the current
// file bytes as a rewind pre-image before the tool writes. These tests drive
// that real path: an edit inside the working directory stores a blob under
// `<workspace>/.rewind/<sessionId>/objects`, and a path outside the working
// directory is refused before the inner tool touches the disk.

using Harbor.Abstractions.Models;
using Harbor.Hosting;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class FileHistoryCaptureTests
{
    private static string NewTempRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "harbor-file-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Test]
    public async Task Edit_WritesInsideWorkspace_StoresPreImageInRewindStore()
    {
        string root = NewTempRoot();
        try
        {
            string file = Path.Combine(root, "note.txt");
            await File.WriteAllTextAsync(file, "original");

            var tool = new FileHistoryToolDecorator(
                new EditTool(NullLogger<EditTool>.Instance),
                NullLogger<FileHistoryToolDecorator>.Instance);
            var args = TestArgs.FromPayload(new ToolPathEditArgs(file, "original", "edited"));
            var context = TestToolContext.Create(sessionId: "session-1", workingDirectory: root);

            ToolResult result = await tool.ExecuteAsync(args, context);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("edited");

            string sessionRewind = Path.Combine(root, ".rewind", "session-1");
            var store = new RewindBlobStore(sessionRewind);
            string expectedHash = RewindBlobStore.ComputeHash("original"u8.ToArray());
            await Assert.That(File.Exists(Path.Combine(sessionRewind, "objects", expectedHash))).IsTrue();
            byte[]? preImage = store.Read(expectedHash);
            await Assert.That(preImage).IsNotNull();
            await Assert.That(System.Text.Encoding.UTF8.GetString(preImage!)).IsEqualTo("original");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Write_PathOutsideWorkspace_IsRejectedWithoutWritingToDisk()
    {
        string root = NewTempRoot();
        string outside = NewTempRoot();
        try
        {
            string escapeTarget = Path.Combine(outside, "escape.txt");
            var tool = new FileHistoryToolDecorator(
                new WriteTool(NullLogger<WriteTool>.Instance),
                NullLogger<FileHistoryToolDecorator>.Instance);
            var args = TestArgs.FromPayload(new ToolPathContentArgs(escapeTarget, "pwned"));
            var context = TestToolContext.Create(sessionId: "session-2", workingDirectory: root);

            ToolResult result = await tool.ExecuteAsync(args, context);

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("Pre-image capture refused");
            await Assert.That(File.Exists(escapeTarget)).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(root, ".rewind", "session-2", "objects"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
