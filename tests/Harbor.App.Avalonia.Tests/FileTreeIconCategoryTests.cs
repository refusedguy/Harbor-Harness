// FileTreeIconCategoryTests.cs — issue #755.
//
// WHAT THIS FILE IS FOR
// --------------------
// #755 reported that `FileTreeNode.IconPath` "has no reader": the file-tree
// policy classifies every row as `file-code` or `file`, the scanner writes that
// into the node, and the view that paints the row decided the glyph from
// `IsDirectory` / `IsExpanded` alone. The classification was correct, tested at
// both ends, and had no observable effect — every non-directory row was painted
// with the code glyph, classified or not.
//
// The fix routes the category into the view. That is a claim about behaviour, so
// this file asserts the behaviour: a classified row and an unclassified row must
// resolve to DIFFERENT glyphs. If somebody re-derives the answer from
// `IsDirectory` again, the two keys collapse back to one and these fail.
//
// The mapping is `public static` on both converters so it can be asserted with no
// Avalonia `Application` standing up — the resource plumbing around it is not the
// part that was broken, and `IconTests` already validates the dictionary as XML
// for the same reason.

using System.Xml.Linq;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Shell;
using Harbor.Application.Filesystem;
using Harbor.Ui.Framework.Services;
using TUnit.Assertions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #755 — the icon category the file-tree policy computed is the one that
///     picks the glyph, and every glyph a converter can name is really declared.
/// </summary>
public class FileTreeIconCategoryTests
{
    private static FileTreeNode FileRow(string iconPath) =>
        new() { Name = "Row", IconPath = iconPath };

    private static FileTreeNode DirRow(bool isExpanded) =>
        new()
        {
            Name = "Dir",
            IsDirectory = true,
            IsExpanded = isExpanded,
            IconPath = DefaultFileTreePolicy.FolderIcon,
        };

    [Test]
    public async Task A_Classified_File_And_An_Unclassified_One_Are_Not_The_Same_Glyph()
    {
        // The heart of the issue: these two rows are the whole feature, and before
        // the fix both resolved to the same icon.
        string code = FileTypeToGeometryConverter.IconResourceKeyFor(FileRow(DefaultFileTreePolicy.CodeIcon));
        string plain = FileTypeToGeometryConverter.IconResourceKeyFor(FileRow(DefaultFileTreePolicy.DefaultIcon));

        await Assert.That(code).IsEqualTo(FileTypeToGeometryConverter.CodeResourceKey);
        await Assert.That(plain).IsEqualTo(FileTypeToGeometryConverter.FileResourceKey);
        await Assert.That(code).IsNotEqualTo(plain);
    }

    [Test]
    public async Task Expansion_Picks_The_Folder_Glyph_And_Nothing_Else_Does()
    {
        await Assert.That(FileTypeToGeometryConverter.IconResourceKeyFor(DirRow(isExpanded: false)))
            .IsEqualTo(FileTypeToGeometryConverter.FolderResourceKey);
        await Assert.That(FileTypeToGeometryConverter.IconResourceKeyFor(DirRow(isExpanded: true)))
            .IsEqualTo(FileTypeToGeometryConverter.FolderOpenResourceKey);

        // A file row carries no expansion state, so the flag must not reach it.
        FileTreeNode expandedFile = FileRow(DefaultFileTreePolicy.CodeIcon);
        expandedFile.IsExpanded = true;
        await Assert.That(FileTypeToGeometryConverter.IconResourceKeyFor(expandedFile))
            .IsEqualTo(FileTypeToGeometryConverter.CodeResourceKey);
    }

    [Test]
    public async Task A_Category_We_Do_Not_Know_Still_Paints_Something()
    {
        // `IFileTreePolicy` is a DI seam, so a second implementation may return a
        // vocabulary this view has never heard of. Degrading to the generic
        // document is a decision; painting no icon at all is a bug.
        await Assert.That(FileTypeToGeometryConverter.IconResourceKeyFor(FileRow("file-binary")))
            .IsEqualTo(FileTypeToGeometryConverter.FileResourceKey);
        await Assert.That(FileTypeToGeometryConverter.IconResourceKeyFor(FileRow(string.Empty)))
            .IsEqualTo(FileTypeToGeometryConverter.FileResourceKey);
    }

    [Test]
    public async Task Every_Toast_Kind_Resolves_To_A_Declared_Glyph()
    {
        // The four kinds are the whole of `ToastKind`, so an enum member added
        // later cannot arrive without a glyph here: it falls through to the
        // generic info icon, and that key is checked too.
        foreach (ToastKind kind in Enum.GetValues<ToastKind>())
            await Assert.That(DeclaredIconKeys.Value.Contains(ToastIconConverter.IconResourceKeyFor(kind))).IsTrue();

        // And the fallback for a value that is not a ToastKind at all.
        await Assert.That(ToastIconConverter.IconResourceKeyFor(null)).IsEqualTo("IcInfo");
        await Assert.That(DeclaredIconKeys.Value.Contains("IcInfo")).IsTrue();
    }

    [Test]
    public async Task Every_Glyph_The_File_Tree_Converter_Can_Return_Is_Declared()
    {
        // A typo in a key paints nothing and says so nowhere, so the keys are
        // checked against the dictionary rather than trusted.
        string[] keys =
        [
            FileTypeToGeometryConverter.CodeResourceKey,
            FileTypeToGeometryConverter.FileResourceKey,
            FileTypeToGeometryConverter.FolderResourceKey,
            FileTypeToGeometryConverter.FolderOpenResourceKey,
        ];

        foreach (string key in keys)
        {
            await Assert.That(DeclaredIconKeys.Value.Contains(key))
                .IsTrue();
        }
    }

    private static readonly string IconDictionaryPath = Path.Combine(
        FindRepoRoot(), "apps", "Harbor.App.Avalonia", "Themes", "Hds", "Icons.axaml");

    private static readonly Lazy<HashSet<string>> DeclaredIconKeys = new(ReadDeclaredIconKeys);

    private static HashSet<string> ReadDeclaredIconKeys()
    {
        XDocument doc = XDocument.Load(IconDictionaryPath);
        XName keyAttribute = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "StreamGeometry")
            .Select(e => e.Attribute(keyAttribute)?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Harbor.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}
