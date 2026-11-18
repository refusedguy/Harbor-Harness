// FileTreePathLinksTests.cs — issue #2682 Z20.
//
// The explorer's "copy path" actions only format strings off a FileTreeNode.
// These tests pin the three formats (absolute, relative, permalink) without an
// Avalonia session: a wrong relative root or a permalink that cannot be pasted
// into a ticket is a user-visible bug, and neither needs a UI to observe.

using Harbor.App.Avalonia.Services;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

public class FileTreePathLinksTests
{
    private const string Root = "/work/project";

    private static FileTreeNode Node(string fullPath) => new()
    {
        Name = Path.GetFileName(fullPath),
        FullPath = fullPath,
    };

    [Test]
    public async Task AbsolutePath_IsTheFullyQualifiedPath()
    {
        string path = Path.Combine(Root, "src", "Program.cs");

        await Assert.That(FileTreePathLinks.AbsolutePath(Node(path))).IsEqualTo(Path.GetFullPath(path));
    }

    [Test]
    public async Task AbsolutePath_NodeWithoutPath_IsEmpty()
    {
        await Assert.That(FileTreePathLinks.AbsolutePath(Node(string.Empty))).IsEmpty();
    }

    [Test]
    public async Task RelativePath_IsWorkspaceRelative()
    {
        string path = Path.Combine(Root, "src", "Program.cs");

        await Assert.That(FileTreePathLinks.RelativePath(Node(path), Root))
            .IsEqualTo("src/Program.cs");
    }

    [Test]
    public async Task RelativePath_AlwaysUsesForwardSlashes()
    {
        // Backslashes are legal in a name on Unix and are the separator on
        // Windows; either way the copied reference must read the same.
        string path = Path.Combine(Root, "src", "Program.cs");

        await Assert.That(FileTreePathLinks.RelativePath(Node(path), Root))
            .DoesNotContain("\\");
    }

    [Test]
    public async Task RelativePath_EmptyRoot_UsesCurrentDirectory()
    {
        string path = Path.Combine(Environment.CurrentDirectory, "README.md");

        await Assert.That(FileTreePathLinks.RelativePath(Node(path), string.Empty))
            .IsEqualTo("README.md");
    }

    [Test]
    public async Task RelativePath_OutsideRoot_KeepsParentMarkers()
    {
        string path = Path.Combine(Root, "..", "shared", "a.txt");

        await Assert.That(FileTreePathLinks.RelativePath(Node(path), Root))
            .IsEqualTo("../shared/a.txt");
    }

    [Test]
    public async Task Permalink_IsAFileUri()
    {
        string path = Path.Combine(Root, "src", "Program.cs");

        await Assert.That(FileTreePathLinks.Permalink(Node(path)))
            .IsEqualTo(new Uri(Path.GetFullPath(path), UriKind.Absolute).AbsoluteUri);
    }

    [Test]
    public async Task Permalink_NodeWithoutPath_IsEmpty()
    {
        await Assert.That(FileTreePathLinks.Permalink(Node(string.Empty))).IsEmpty();
    }

    [Test]
    public async Task Permalink_EscapesSpacesInNames()
    {
        string path = Path.Combine(Root, "my file.cs");

        await Assert.That(FileTreePathLinks.Permalink(Node(path)))
            .DoesNotContain(" ");
    }
}
