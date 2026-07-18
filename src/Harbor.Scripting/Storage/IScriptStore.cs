// Storage layer — script store contract. See ScriptEntry.cs for layering rules.
namespace Harbor.Scripting.Storage;

/// <summary>
///     Script storage: where script files live and how to read / write them.
/// </summary>
/// <remarks>
///     <para>
///         Implementations target a concrete backend — the local filesystem
///         (<see cref="FileSystemScriptStore" />), an in-memory dictionary
///         (<see cref="InMemoryScriptStore" />, for tests), or future cloud /
///         archive backends. The contract is intentionally minimal: list,
///         read, write, delete. Discovery of script files (e.g. recursive
///         enumeration, extension filtering) is the store's responsibility.
///     </para>
///     <para>
///         <b>Layering:</b> this interface MUST NOT reference engines,
///         compilation, or the Harbor bridge. It is a leaf — depends only on
///         <c>Harbor.Abstractions</c> for <see cref="Result{T}" />.
///     </para>
///     <para>
///         <b>Thread safety:</b> implementations MUST be safe for concurrent
///         read calls. Writes for the same <c>name</c> from concurrent callers
///         may race; callers should serialize writes if they care.
///     </para>
/// </remarks>
public interface IScriptStore
{
    /// <summary>
    ///     List all script entries in the store, in store-defined order
    ///     (typically alphabetical by name).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success with the list of entries, or failure with an error.</returns>
    Task<Result<IReadOnlyList<ScriptEntry>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Read a single script by name.
    /// </summary>
    /// <param name="name">Script name (file stem, no extension).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success with the entry, or failure if not found / unreadable.</returns>
    Task<Result<ScriptEntry>> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Write (create or replace) a script.
    /// </summary>
    /// <param name="name">Script name.</param>
    /// <param name="content">Script source code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or failure with an error.</returns>
    Task<Result> WriteAsync(string name, string content, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Delete a script. Returns success if the script existed and was
    ///     deleted, or failure if not found.
    /// </summary>
    /// <param name="name">Script name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or failure if not found.</returns>
    Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
