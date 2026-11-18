// FileTreeTerminalTargetTests.cs - issue #2682 Z22.
//
// "Open in terminal" only has one decision of its own: the working directory
// it hands to the floating terminal service. These tests pin that decision
// without an Avalonia session or a shell process.

using Harbor.App.Avalonia.Services;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

public class FileTreeTerminalTargetTests
{
    private const string Root = "/work/project";

    private static FileTreeNode Node(string fullPath, bool isDirectory) => new()
    {
        Name = Path.GetFileName(fullPath),
        FullPath = fullPath,
        IsDirectory = isDirectory,
    };

    [Test]
    public async Task DirectoryNode_TargetsItself()
    {
        string path = Path.Combine(Root, "src");

        await Assert.That(FileTreeTerminalTarget.Directory(Node(path, true), Root))
            .IsEqualTo(Path.GetFullPath(path));
    }

    [Test]
    public async Task FileNode_TargetsContainingDirectory()
    {
        string path = Path.Combine(Root, "src", "Program.cs");

        await Assert.That(FileTreeTerminalTarget.Directory(Node(path, false), Root))
            .IsEqualTo(Path.GetFullPath(Path.Combine(Root, "src")));
    }

    [Test]
    public async Task FileAtRoot_TargetsRoot()
    {
        string path = Path.Combine(Root, "README.md");

        await Assert.That(FileTreeTerminalTarget.Directory(Node(path, false), Root))
            .IsEqualTo(Path.GetFullPath(Root));
    }

    [Test]
    public async Task NodeWithoutPath_UsesFallbackRoot()
    {
        await Assert.That(FileTreeTerminalTarget.Directory(Node(string.Empty, false), Root))
            .IsEqualTo(Path.GetFullPath(Root));
    }

    [Test]
    public async Task NodeWithoutPathAndFallback_IsEmpty()
    {
        await Assert.That(FileTreeTerminalTarget.Directory(Node(string.Empty, false), string.Empty))
            .IsEmpty();
    }

    [Test]
    public async Task RelativeNodePath_IsAnchoredToAnAbsoluteDirectory()
    {
        string target = FileTreeTerminalTarget.Directory(Node("Program.cs", false), Root);

        await Assert.That(Path.IsPathFullyQualified(target)).IsTrue();
        await Assert.That(target).IsEqualTo(Path.GetDirectoryName(Path.GetFullPath("Program.cs")));
    }
}
