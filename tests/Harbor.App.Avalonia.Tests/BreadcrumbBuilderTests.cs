// BreadcrumbBuilderTests.cs — K24: clickable path breadcrumbs in the editor.
//
// These pin the pure path split, not the Avalonia control. The view only turns
// each crumb into a button; which crumbs exist, what path each one carries and
// which one is current are decided here. No window, dispatcher or config store
// is stood up.

using Harbor.App.Avalonia.ViewModels;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

public class BreadcrumbBuilderTests
{
    [Test]
    public async Task NullEmptyOrWhitespace_YieldsNoCrumbs()
    {
        await Assert.That(BreadcrumbBuilder.Build(null)).IsEmpty();
        await Assert.That(BreadcrumbBuilder.Build("")).IsEmpty();
        await Assert.That(BreadcrumbBuilder.Build("   ")).IsEmpty();
    }

    [Test]
    public async Task RelativePath_OneCrumbPerSegment_WithCumulativePaths()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build("src/foo/bar.cs");

        await Assert.That(crumbs.Count).IsEqualTo(3);
        await Assert.That(crumbs.Select(c => c.Label))
            .IsEquivalentTo(new[] { "src", "foo", "bar.cs" });
        await Assert.That(crumbs.Select(c => c.Path))
            .IsEquivalentTo(new[] { "src", "src/foo", "src/foo/bar.cs" });
        await Assert.That(crumbs[0].IsCurrent).IsFalse();
        await Assert.That(crumbs[1].IsCurrent).IsFalse();
        await Assert.That(crumbs[2].IsCurrent).IsTrue();
    }

    [Test]
    public async Task UnixAbsolutePath_LeadsWithRootCrumb()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build("/home/user/file.cs");

        await Assert.That(crumbs.Count).IsEqualTo(4);
        await Assert.That(crumbs[0].Label).IsEqualTo("/");
        await Assert.That(crumbs[0].Path).IsEqualTo("/");
        await Assert.That(crumbs.Select(c => c.Path))
            .IsEquivalentTo(new[] { "/", "/home", "/home/user", "/home/user/file.cs" });
        await Assert.That(crumbs[^1].IsCurrent).IsTrue();
    }

    [Test]
    public async Task WindowsDrivePath_LeadsWithDriveRoot()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build(@"C:\Users\me\file.cs");

        await Assert.That(crumbs.Count).IsEqualTo(4);
        await Assert.That(crumbs[0].Label).IsEqualTo("C:/");
        await Assert.That(crumbs.Select(c => c.Path))
            .IsEquivalentTo(new[] { "C:/", "C:/Users", "C:/Users/me", "C:/Users/me/file.cs" });
    }

    [Test]
    public async Task UncPath_LeadsWithServerShareRoot()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build(@"\\server\share\dir\f.cs");

        await Assert.That(crumbs.Count).IsEqualTo(3);
        await Assert.That(crumbs[0].Label).IsEqualTo("//server/share");
        await Assert.That(crumbs.Select(c => c.Path))
            .IsEquivalentTo(new[] { "//server/share", "//server/share/dir", "//server/share/dir/f.cs" });
    }

    [Test]
    public async Task BareRoot_IsASingleCurrentCrumb()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build("/");

        await Assert.That(crumbs.Count).IsEqualTo(1);
        await Assert.That(crumbs[0].Label).IsEqualTo("/");
        await Assert.That(crumbs[0].IsCurrent).IsTrue();
    }

    [Test]
    public async Task TrailingAndDuplicateSeparators_DoNotCreateCrumbs()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build("src//foo///");

        await Assert.That(crumbs.Count).IsEqualTo(2);
        await Assert.That(crumbs.Select(c => c.Label))
            .IsEquivalentTo(new[] { "src", "foo" });
    }

    [Test]
    public async Task DotAndDotDot_Crumbs_AreDisabled()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build("src/./../file.cs");

        await Assert.That(crumbs.Count).IsEqualTo(4);
        await Assert.That(crumbs[1].Label).IsEqualTo(".");
        await Assert.That(crumbs[1].IsEnabled).IsFalse();
        await Assert.That(crumbs[2].Label).IsEqualTo("..");
        await Assert.That(crumbs[2].IsEnabled).IsFalse();
        await Assert.That(crumbs[0].IsEnabled).IsTrue();
        await Assert.That(crumbs[3].IsEnabled).IsTrue();
    }

    [Test]
    public async Task EveryCrumbExceptTheLast_IsNotCurrent()
    {
        IReadOnlyList<BreadcrumbCrumb> crumbs = BreadcrumbBuilder.Build("a/b/c/d/e");

        await Assert.That(crumbs.Count).IsEqualTo(5);
        await Assert.That(crumbs.Take(4).Any(c => c.IsCurrent)).IsFalse();
        await Assert.That(crumbs[^1].IsCurrent).IsTrue();
    }

    [Test]
    public async Task Separator_IsASingleGlyph()
    {
        await Assert.That(BreadcrumbBuilder.Separator.Length).IsEqualTo(1);
    }
}
