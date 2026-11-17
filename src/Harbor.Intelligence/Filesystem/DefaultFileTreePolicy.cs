using System.Collections.Frozen;
using Harbor.Abstractions.Filesystem;

namespace Harbor.Application.Filesystem;

/// <summary>
///     The default file-tree policy: the conventional build-output and VCS
///     directories are not project content, and "source-ish file" gets the
///     code glyph (issue #492).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why the implementation is in Application.</b> Same rule as
///     <see cref="SystemDirectoryLister" /> beside it: the contract is Domain
///     because Presentation has to name it, and the decision that answers it
///     belongs to an assembly Presentation may not reference. There is no I/O
///     here at all — the whole class is a lookup table, which is what makes it
///     trivially testable and what makes it safe to hand to a second renderer.
/// </para>
/// <para>
///     <b>What the list is and is not.</b> It is the union of the build-output
///     and VCS directory names that are near-universal across .NET, Node and
///     Python projects, plus a dotfile rule. It is NOT a gitignore, and it is not
///     the agent's file-ignore knowledge: the agent reaches the filesystem
///     through the gated <c>read</c>/<c>glob</c>/<c>bash</c> tools, where a
///     user-authored ignore list belongs. This list answers a narrower question —
///     "should this directory be in the sidebar at all" — and it is deliberately
///     a fixed set rather than a parser, because a gitignore engine in a sidebar
///     would need the repo's ignore file, and that read is #535's debt, not
///     this one's.
/// </para>
/// <para>
///     <b>The known limit, stated rather than hidden.</b> A project that puts its
///     build output in a differently-named directory still gets a large tree.
///     Fixing that properly means reading the repo's own ignore file, which is a
///     Presentation-side file read (#535) and therefore out of scope here. What
///     this type buys is that the list is now ONE greppable list in ONE place
///     with tests on it, instead of a private method in a view-model that nobody
///     can reach — so extending it is an ordinary edit rather than a refactor.
/// </para>
/// </remarks>
public sealed class DefaultFileTreePolicy : IFileTreePolicy
{
    /// <summary>
    ///     Directory names that are build output, package restores or VCS
    ///     metadata in enough projects to be worth hiding by default.
    /// </summary>
    /// <remarks>
    ///     A frozen set rather than a list with a linear scan:
    ///     <c>IsIgnoredDirectory</c> runs once per directory per scan, and the
    ///     lookup is what makes the dotfile-vs-named-list pair a single
    ///     expression instead of a branch nobody reads.
    ///     <para>
    ///         Built with the STATIC <c>FrozenSet.ToFrozenSet</c> rather than the
    ///         LINQ extension: <c>Harbor.Application</c> globally imports ZLinq in
    ///         place of <c>System.Linq</c> (see its GlobalUsings.cs), so
    ///         <c>array.ToFrozenSet(…)</c> does not bind there — the same trap
    ///         <c>PathArgExtractionPolicy</c> documents for
    ///         <c>ValueEnumerable</c>.
    ///     </para>
    /// </remarks>
    private static readonly FrozenSet<string> IgnoredDirectories = FrozenSet.ToFrozenSet(
        new[]
        {
            "bin",           // .NET / MSBuild output
            "obj",           // MSBuild intermediate output
            "node_modules",  // npm / pnpm / yarn
            "packages",      // NuGet restore
            "target",        // Maven / Gradle
            "dist",          // built bundles
            "build",         // CMake / Gradle
            "coverage",      // test/coverage reports
            ".git",          // VCS metadata (also covered by the dotfile rule)
            ".svn",          // VCS metadata (also covered by the dotfile rule)
            ".hg",           // VCS metadata (also covered by the dotfile rule)
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions that render with the code glyph rather than the generic file glyph.</summary>
    private static readonly FrozenSet<string> SourceExtensions = FrozenSet.ToFrozenSet(
        new[]
        {
            ".cs", ".axaml", ".xaml", ".razor",
            ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets",
            ".json", ".yaml", ".yml", ".toml", ".xml", ".md", ".markdown",
            ".ts", ".tsx", ".js", ".jsx", ".py", ".rs", ".go", ".java", ".kt", ".sql",
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon category for a file the policy does not classify.</summary>
    public const string DefaultIcon = "file";

    /// <summary>Icon category for a classified file.</summary>
    public const string CodeIcon = "file-code";

    /// <summary>Icon category for a directory, expanded or not.</summary>
    public const string FolderIcon = "folder";

    /// <inheritdoc />
    public bool IsIgnoredDirectory(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Dotfile first: it is the broader rule and the cheapest test, and the
        // named list below exists for directories that are conventional enough
        // to be worth ignoring DESPITE being visible.
        return name.StartsWith('.') || IgnoredDirectories.Contains(name);
    }

    /// <inheritdoc />
    public string IconFor(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return DefaultIcon;
        }

        // Path.GetExtension rather than a hand-rolled last-index-of: it answers
        // the same thing for "README" (no dot → empty) and for ".gitignore"
        // (a leading dot is an extension-less name, not the ".gitignore" ext),
        // and it is the BCL's answer rather than a second one.
        string extension = Path.GetExtension(fileName);
        return extension.Length > 0 && SourceExtensions.Contains(extension)
            ? CodeIcon
            : DefaultIcon;
    }
}
