namespace Harbor.Abstractions.Filesystem;

/// <summary>
///     What a file-tree view SHOWS: which directories are not part of the
///     project, and which glyph a file gets (issue #492).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this is a port and not a private static.</b> The desktop shell's
///     <c>MainViewModel</c> used to answer both questions with two private
///     members: a five-name ignore list with a dotfile rule, and a
///     <c>switch</c> from extension to icon name. A private static is not a
///     policy anyone can reach — it cannot be tested, it cannot be shared, and
///     adding a file type means editing a view-model. That is the same
///     unreachable-knowledge shape #537/#665/#672 found for the git read, the
///     notification spawn and the terminal launch.
/// </para>
/// <para>
///     <b>Why this is a SEPARATE contract from <see cref="IDirectoryLister" />.</b>
///     They answer different questions and the dependency runs one way:
///     <c>IDirectoryLister</c> answers "what is on disk", this answers "what does
///     a tree display". A view can list a directory and still choose to hide half
///     of it; a lister has no opinion about glyphs and must not acquire one. The
///     #667 port stayed pure for exactly that reason — it bounds and cancels a
///     walk, and it has no policy in it.
/// </para>
/// <para>
///     <b>Why the icon map is here at all.</b> It is presentation vocabulary,
///     and that is the honest tension in this contract. What makes it belong on
///     this side is that it is DATA — a finite table keyed by extension — with no
///     view, no control and no Avalonia type in it, and the two questions travel
///     together: a tree that hides a directory is the same tree that has to label
///     what is left. The concrete glyph stays a presentation decision; the
///     NAME of the category is policy, and it is now the one thing a second
///     renderer can be handed instead of rewriting.
/// </para>
/// <para>
///     <b>Synchronous and total, deliberately.</b> Both members are pure
///     functions of a name: no I/O, no await, no failure mode. A policy that
///     could fail would put a decision a view cannot render into the middle of
///     building a tree, and an async one would make every caller thread its own
///     reasons for awaiting.
/// </para>
/// </remarks>
public interface IFileTreePolicy
{
    /// <summary>
    ///     Whether a directory should be left out of the tree entirely.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="name" /> is a bare directory name, never a path —
    ///         a policy that took a path would have to re-derive the name, and
    ///         two derivations of "the last segment" is one more thing to get
    ///         subtly different per platform.
    ///     </para>
    ///     <para>
    ///         A file is never asked: only directories can be ignored, because
    ///         ignoring a file by name is indistinguishable from hiding a source
    ///         file, and a tree that hides source files is a tree the user cannot
    ///         debug.
    ///     </para>
    /// </remarks>
    /// <param name="name">Bare directory name, as the listing reported it.</param>
    bool IsIgnoredDirectory(string name);

    /// <summary>
    ///     The icon category for a file, from its name or its extension.
    /// </summary>
    /// <remarks>
    ///     Takes the file name rather than a pre-extracted extension so that every
    ///     caller gets the same dotfile and compound-extension handling — the
    ///     desktop tree was reading the extension four times in four slightly
    ///     different ways before the policy existed.
    /// </remarks>
    /// <param name="fileName">Bare file name, as the listing reported it.</param>
    /// <returns>A stable category name. Never null or empty; unknown files get the policy's default.</returns>
    string IconFor(string fileName);
}
