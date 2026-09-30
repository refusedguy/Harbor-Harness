// DefaultFileTreePolicyTests.cs — issue #492.
//
// The ignore list and the extension→icon map used to be two private members of a
// view-model: a `name.StartsWith('.') || name is "bin" or "obj" or …` and a
// `switch` from extension to glyph. Nothing could reach either of them, so
// nothing could be asserted about them either — including the two questions that
// decide whether the list is any good: does it cover the directories real
// projects put their build output in, and does it not cover directories a user
// would be annoyed to lose?
//
// These tests are the whole reason the decision is in Domain with an
// implementation in Application. If it went back into a view-model, this file
// would stop compiling — which is the correct outcome.

using Harbor.Application.Filesystem;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #492 — the default file-tree policy's two tables, asserted from the
///     outside.
/// </summary>
public sealed class DefaultFileTreePolicyTests
{
    private readonly DefaultFileTreePolicy _policy = new();

    // ── the ignore list ───────────────────────────────────────────────────

    [Test]
    [Arguments("bin")]
    [Arguments("obj")]
    [Arguments("node_modules")]
    [Arguments("packages")]
    [Arguments("target")]
    [Arguments("dist")]
    [Arguments("build")]
    [Arguments("coverage")]
    [Arguments(".git")]
    [Arguments(".svn")]
    [Arguments(".hg")]
    public async Task BuildOutputAndVcsDirectories_AreIgnored(string name)
    {
        await Assert.That(_policy.IsIgnoredDirectory(name)).IsTrue();
    }

    [Test]
    [Arguments("BIN")]
    [Arguments("Obj")]
    [Arguments("Node_Modules")]
    public async Task TheNamedList_IsCaseInsensitive(string name)
    {
        // A tree that hides `bin` on Linux and shows it on Windows is a tree whose
        // contents depend on the developer's keyboard, and the filesystem itself
        // is case-insensitive on two of the three supported platforms.
        await Assert.That(_policy.IsIgnoredDirectory(name)).IsTrue();
    }

    [Test]
    [Arguments(".idea")]
    [Arguments(".vs")]
    [Arguments(".config")]
    [Arguments(".github")]
    public async Task EveryDotfile_IsIgnored_WhateverItIs(string name)
    {
        await Assert.That(_policy.IsIgnoredDirectory(name)).IsTrue()
            .Because("the dotfile rule is the broad one; the named list exists for the directories "
                   + "that are worth hiding DESPITE being visible");
    }

    [Test]
    [Arguments("src")]
    [Arguments("tests")]
    [Arguments("docs")]
    [Arguments("apps")]
    [Arguments("Harbor.Application")]
    [Arguments("buildings")]
    [Arguments("distribution")]
    [Arguments("targets")]
    [Arguments("objx")]
    [Arguments("packages-manifest")]
    public async Task ProjectContent_IsNeverIgnored(string name)
    {
        await Assert.That(_policy.IsIgnoredDirectory(name)).IsFalse()
            .Because(
                "these are the false positives the audit warned about: a project that keeps sources in a "
                + "directory whose name merely CONTAINS a build-output word would silently lose them, and "
                + "the user would have no way to tell an empty sidebar from a bug");
    }

    [Test]
    public async Task AnEmptyName_IsNotIgnored()
    {
        // Not because "" is a plausible directory name, but because a policy that
        // says "yes" to it has no branch that can say "no", and the caller should
        // not have to know that.
        await Assert.That(_policy.IsIgnoredDirectory(string.Empty)).IsFalse();
        await Assert.That(_policy.IsIgnoredDirectory(null!)).IsFalse();
    }

    // ── the icon map ──────────────────────────────────────────────────────

    [Test]
    [Arguments("Program.cs")]
    [Arguments("ShellView.axaml")]
    [Arguments("Harbor.slnx")]
    [Arguments("Harbor.csproj")]
    [Arguments("README.md")]
    [Arguments("appsettings.json")]
    [Arguments("ci.yml")]
    [Arguments("index.ts")]
    [Arguments("main.py")]
    public async Task SourceShapedFiles_GetTheCodeIcon(string fileName)
    {
        await Assert.That(_policy.IconFor(fileName)).IsEqualTo(DefaultFileTreePolicy.CodeIcon);
    }

    [Test]
    [Arguments("Program.CS")]
    [Arguments("Readme.MD")]
    public async Task TheIconMap_IsCaseInsensitive(string fileName)
    {
        // The old switch lower-cased the extension first, so this held — but it
        // held because of a call the policy now owns, and that is worth pinning.
        await Assert.That(_policy.IconFor(fileName)).IsEqualTo(DefaultFileTreePolicy.CodeIcon);
    }

    [Test]
    [Arguments("logo.png")]
    [Arguments("app.bin")]
    [Arguments("archive.zip")]
    [Arguments("photo.jpeg")]
    public async Task OtherFiles_GetTheDefaultIcon(string fileName)
    {
        await Assert.That(_policy.IconFor(fileName)).IsEqualTo(DefaultFileTreePolicy.DefaultIcon);
    }

    [Test]
    [Arguments("README")]
    [Arguments("LICENSE")]
    [Arguments("Makefile")]
    public async Task ExtensionlessFiles_GetTheDefaultIcon(string fileName)
    {
        await Assert.That(_policy.IconFor(fileName)).IsEqualTo(DefaultFileTreePolicy.DefaultIcon)
            .Because("there is no extension to classify, and inventing one from the name is how a "
                   + "policy starts guessing");
    }

    [Test]
    public async Task ADotfileName_IsNotMistakenForAnExtension()
    {
        // `Path.GetExtension(".gitignore")` returns ".gitignore" — the whole name,
        // leading dot included. That is a BCL quirk, and it is harmless here only
        // because no dotfile name is in the source set. The assertion is what keeps
        // it harmless: adding ".gitignore" to that set would otherwise turn every
        // dotfile in the project into a "code" file, silently.
        await Assert.That(Path.GetExtension(".gitignore")).IsEqualTo(".gitignore");
        await Assert.That(_policy.IconFor(".gitignore")).IsEqualTo(DefaultFileTreePolicy.DefaultIcon);
    }

    [Test]
    public async Task AnEmptyName_GetsTheDefaultIcon()
    {
        await Assert.That(_policy.IconFor(string.Empty)).IsEqualTo(DefaultFileTreePolicy.DefaultIcon);
        await Assert.That(_policy.IconFor(null!)).IsEqualTo(DefaultFileTreePolicy.DefaultIcon);
    }

    [Test]
    public async Task TheIconCategories_AreNotEmpty()
    {
        // The row model stores this string and a view turns it into a glyph. An
        // empty category is a row that renders as nothing, with no error anywhere.
        await Assert.That(DefaultFileTreePolicy.DefaultIcon).IsNotEmpty();
        await Assert.That(DefaultFileTreePolicy.CodeIcon).IsNotEmpty();
        await Assert.That(DefaultFileTreePolicy.FolderIcon).IsNotEmpty();
        await Assert.That(DefaultFileTreePolicy.DefaultIcon)
            .IsNotEqualTo(DefaultFileTreePolicy.CodeIcon)
            .Because("two categories that are equal are one category, and a policy that cannot "
                   + "distinguish them is not classifying anything");
    }
}
