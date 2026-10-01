using System.Text;
using Harbor.Abstractions.Filesystem;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Filesystem;

/// <summary>
///     <see cref="ITextFileStore" /> over <see cref="File" /> — the single place in
///     the harness that reads or writes one text file on behalf of a UI view
///     (#934).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why Application and not Presentation.</b> The same answer
///     <see cref="SystemDirectoryLister" /> gives for the walk one capability over:
///     the I/O was not the defect, its LOCATION was. Application is where the other
///     blocking I/O already lives (<c>ProcessGitQuery</c>, <c>WorkspaceInspector</c>,
///     <c>BashTool</c>'s neighbours), and the port belongs in Domain above it.
/// </para>
/// <para>
///     <b>Threading.</b> Every member hands the syscall to
///     <see cref="Task.Run(Action, CancellationToken)" />. That is not incidental.
///     The contract's whole reason for being async is that the caller is a UI
///     thread, and the specific defect this replaces was a SYNCHRONOUS
///     <c>File.Exists</c> in front of two properly async calls — so an
///     implementation with a "fast path" that returned
///     <see cref="Task.FromResult{TResult}" /> would put the stall straight back
///     where it came from and satisfy every signature in the interface.
/// </para>
/// <para>
///     <b>Encoding.</b> UTF-8 without a byte-order mark on the way out, and the
///     BCL's own detection on the way in (a BOM is honoured and stripped, and
///     BOM-less input is read as UTF-8). That is deliberately identical to what the
///     call sites did before, so this commit changes WHERE the decision lives and
///     nothing about what a saved file looks like. Emitting the BOM explicitly is
///     the load-bearing part: <c>new UTF8Encoding(false)</c> rather than
///     <c>Encoding.UTF8</c>, whose preamble property is <see langword="true" /> on
///     the Framework and on .NET's own default overload of
///     <see cref="File.WriteAllTextAsync(string, string, System.Text.Encoding, CancellationToken)" />
///     would put three invisible bytes at the head of every file the editor saves.
/// </para>
/// <para>
///     <b>Not atomic, and deliberately.</b> See the port's remarks: a write here
///     overwrites in place, exactly as <c>File.WriteAllTextAsync</c> did, so
///     behaviour under a crash is unchanged. Callers that need temp-plus-move
///     compose it, as <c>JsonlSessionStore</c> does.
/// </para>
/// </remarks>
public sealed class SystemTextFileStore : ITextFileStore
{
    /// <summary>
    ///     UTF-8 with no byte-order mark. A field rather than a property so the
    ///     encoder instance is allocated once: <see cref="Encoding" /> is immutable
    ///     and thread-safe, and a write per save does not need a new one.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<SystemTextFileStore> _logger;

    /// <summary>Construct a text-file store.</summary>
    /// <param name="logger">Logger for the debug trail of a failed read or write.</param>
    public SystemTextFileStore(ILogger<SystemTextFileStore> logger) => _logger = logger;

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        string target = Resolve(path);

        // The whole reason this member is async: `File.Exists` is a synchronous
        // `stat`, and the caller is a UI thread. A `Task.FromResult` here would be
        // the same blocking call wearing an async signature.
        return Task.Run(
            () =>
            {
                // `File.Exists` swallows EVERY exception and answers `false`. That
                // collapses "not there" and "I could not look" into one answer, and
                // the caller renders `false` as "file not found" — a claim about the
                // user's disk that a malformed path cannot support. So the path is
                // normalised first, which is where a NUL or a bad separator actually
                // throws, and the throw becomes a failed result.
                try
                {
                    _ = Path.GetFullPath(target);
                    return Result.Success(File.Exists(target));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    _logger.LogDebug(ex, "Text file probe failed for {Path}", target);
                    return Result.Failure<bool>($"Cannot probe '{target}': {ex.Message}");
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<string>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        string target = Resolve(path);

        // The BCL's own overload: BOM detection, UTF-8 fallback, and an async read
        // that does not park the calling thread on a network mount.
        return Task.Run(
            async () =>
            {
                try
                {
                    return Result.Success(await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    // Superseded, or the host is shutting down. Not a failure: the
                    // caller discards stale results anyway, and reporting an error
                    // for a read nobody is waiting for any more is a lie.
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _logger.LogDebug(ex, "Text file read failed for {Path}", target);
                    return Result.Failure<string>($"Cannot read '{target}': {ex.Message}");
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> WriteAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        string target = Resolve(path);

        return Task.Run(
            async () =>
            {
                try
                {
                    await File.WriteAllTextAsync(target, contents, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                    return Result.Success();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _logger.LogError(ex, "Text file write failed for {Path}", target);
                    return Result.Failure($"Cannot write '{target}': {ex.Message}");
                }
            },
            cancellationToken);
    }

    /// <summary>
    ///     An empty path means the process working directory, matching
    ///     <see cref="IDirectoryLister.ListAsync" /> rather than the BCL, where an
    ///     empty path is a different failure per member.
    /// </summary>
    private static string Resolve(string path) =>
        string.IsNullOrEmpty(path) ? Environment.CurrentDirectory : path;
}
