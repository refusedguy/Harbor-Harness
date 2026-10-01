namespace Harbor.Abstractions.Filesystem;

/// <summary>
///     Read and write ONE text file, for a view that opens the file the user picked
///     (issue #934).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this contract exists.</b> <c>CodeEditorViewModel</c> called
///     <c>File.Exists</c>, <c>File.ReadAllTextAsync</c> and
///     <c>File.WriteAllTextAsync</c> directly, from a view-model. The PATH already
///     came through a port — <c>IFilePicker</c> returns paths and does no I/O — and
///     the CONTENT did not, so "a view-model knows about the filesystem" was
///     literally true with no seam anywhere behind it. The consequence was not
///     aesthetic: a test of "open shows the content" had to put bytes on disk,
///     because the class had nothing to substitute.
/// </para>
/// <para>
///     This is the same move as <see cref="IDirectoryLister" /> (#667, #492) one
///     capability over. That contract answers "what is on disk in this
///     directory"; this one answers "what is in this file". They are separable for
///     the same reason: a tree may be listed without any file being read, and a
///     file may be read without any tree existing.
/// </para>
/// <para>
///     <b>Async, and the thread is part of the contract.</b> Every member is async
///     for the same reason <see cref="IDirectoryLister.ListAsync" /> is: the caller
///     is a UI thread. The pre-#934 code was already mostly honest about this —
///     <c>ReadAllTextAsync</c> and <c>WriteAllTextAsync</c> do not block — but it
///     opened with a SYNCHRONOUS <c>File.Exists</c> in front of them, which is a
///     blocking <c>stat</c> on the dispatcher the user is looking at, reached from
///     a TreeView selection change and from a toolbar button. Cheap on a local disk,
///     not cheap on a network mount, and never cheap in principle. Implementations
///     MUST NOT run the syscall on the calling thread: returning a synchronously
///     completed task is a contract violation, not an optimisation.
/// </para>
/// <para>
///     <b>No throws for expected failures.</b> A missing file, an unreadable one, a
///     permission denial and a malformed path are all outcomes the caller has to
///     render something about, so they come back as a failed
///     <see cref="Result{TValue}" />. <see cref="OperationCanceledException" /> is
///     the one exception that may propagate, and a caller reads it as "superseded",
///     not as a failure — the same division <see cref="IDirectoryLister" /> draws.
/// </para>
/// <para>
///     <b>Encoding is the implementation's business.</b> This contract says nothing
///     about UTF-8, byte-order marks or line endings, and that is the point: today
///     two call sites in the same app each get the BCL default, and a file saved by
///     one is read by the other only because both happen to be .NET. A contract
///     that named an encoding would freeze today's accident into an API.
/// </para>
/// <para>
///     <b>What this contract does not promise: atomic writes.</b>
///     <c>WriteAsync</c> overwrites in place, so a process killed mid-write leaves
///     a truncated file — the same exposure the pre-#934 code had. Making a write
///     atomic is a caller's decision, not the port's: a caller that needs it
///     composes temp-file-plus-move, which is what <c>JsonlSessionStore</c> does
///     for sessions. Promising durability here would have been a second, larger
///     change wearing the seam's clothes.
/// </para>
/// </remarks>
public interface ITextFileStore
{
    /// <summary>
    ///     Whether a file exists at <paramref name="path" />.
    /// </summary>
    /// <remarks>
    ///     Async for the reason above: the existence probe is the syscall that was
    ///     synchronous on the UI thread, and moving it behind an interface that kept
    ///     it synchronous would have relocated the hazard instead of removing it.
    ///     A <see langword="false" /> means "not there"; a probe that could not
    ///     answer (a permission denial on the parent directory, a malformed path) is
    ///     a FAILED result, never a <see langword="false" /> that reads as a
    ///     missing file.
    /// </remarks>
    /// <param name="path">Path to probe. An empty value means the process working directory.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The whole text content of the file at <paramref name="path" />.
    /// </summary>
    /// <param name="path">Path to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<Result<string>> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Replace the content of the file at <paramref name="path" /> with
    ///     <paramref name="contents" />, creating it if it is not there.
    /// </summary>
    /// <param name="path">Path to write.</param>
    /// <param name="contents">The full new content of the file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Result> WriteAsync(string path, string contents, CancellationToken cancellationToken = default);
}
