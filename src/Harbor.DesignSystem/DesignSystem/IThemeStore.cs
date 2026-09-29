namespace Harbor.DesignSystem;

/// <summary>
/// Read a theme document from disk, and probe when it last changed.
/// </summary>
/// <remarks>
/// <para>
///     The port exists because more than one place answered "read this theme
///     file". <c>ThemeStore.LoadEntry</c> was the original; a public static
///     <c>LoadFile</c> in the terminal renderer duplicated it, and the terminal
///     <c>ThemeFileWatcher</c> reached the disk twice of its own (#668, and the
///     "THIRD theme-parse path" complaint in #479-A6). Several answers to one
///     question is the same defect as a shared verdict answered twice, and none
///     of the answers is wrong on its own — which is why duplication of this
///     shape survives review so reliably.
/// </para>
/// <para>
///     Two sites are still outside this port, deliberately and recorded here
///     rather than left to be rediscovered: <c>ThemeDirectoryWatcher.Apply</c>
///     does its own read-and-parse beside its owner's (that watcher is #479-A6's
///     own file, and its poll-skeleton question is a separate decision), and
///     <c>apps/Harbor.App.Avalonia</c>'s <c>ThemeService.LoadJson</c> reads the
///     file for the desktop app. The Avalonia one is an app composition root,
///     which the layer matrix leaves unrestricted, so no rule reaches it — it
///     is listed here because "the port has exactly one implementer" is only
///     true if the sites that never adopted it are named.
/// </para>
/// <para>
///     It is declared HERE, in the zero-reference token leaf, and not in Domain
///     where #536 proposed, because at the time <see cref="ThemeStore" /> lived in
///     this assembly and the layer matrix gives this assembly an EMPTY
///     allowed-reference set: implementing a Domain-declared contract from here
///     would need a forbidden edge. Putting the contract in DesignSystem was
///     therefore the placement #668 could actually reach, and #536 said out loud
///     that it did not block the move — which is why the port did not have to move
///     with the implementation. #536 has now landed and the two halves are apart:
///     this contract and the tokens stayed in the leaf, and
///     <c>ThemeStore</c> / <c>ThemeDirectoryWatcher</c> moved to
///     <c>Harbor.Hosting.Themes</c>. The contract is the theme catalog's; where
///     the bytes come from is an outer layer's.
/// </para>
/// <para>
///     The result type is <see cref="ThemeParseResult" />, not
///     <c>Result&lt;T&gt;</c>: this assembly has no PackageReference at all, and
///     <see cref="ThemeParseResult" /> is the hand-rolled shape that keeps it
///     that way. An interface that could not be declared here would not be a
///     port, it would be a workaround.
/// </para>
/// </remarks>
public interface IThemeStore
{
    /// <summary>
    ///     Reads and parses the theme document at <paramref name="path" />, merging
    ///     missing slots from the active <see cref="TerminalColorPalette" />.
    ///     Never throws: a missing file, an unreadable one and a malformed
    ///     document all come back as a failed result.
    /// </summary>
    ThemeParseResult LoadFile(string path);

    /// <summary>
    ///     Last-write timestamp of <paramref name="path" /> in UTC, for pollers
    ///     deciding whether a file changed. Returns <c>false</c> when the file is
    ///     absent or cannot be stat'ed, which a poller reads as "nothing to do"
    ///     rather than as a change.
    /// </summary>
    /// <remarks>
    ///     The shape is TryGet-out rather than a nullable or a
    ///     <c>Maybe</c> for the same reason <see cref="LoadFile" /> returns
    ///     <see cref="ThemeParseResult" />: this assembly has no package
    ///     references, so the contract may only be written in BCL types.
    /// </remarks>
    bool TryGetLastWriteUtc(string path, out DateTime lastWriteUtc);
}
