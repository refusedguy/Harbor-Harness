namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The read side of a session store (#1743): one query surface over the
///     session log — filters, a time window, a bounded selection and a ranked
///     result.
/// </summary>
/// <remarks>
///     <para>
///         The store (<see cref="ISessionStore" />) owns the WRITE side and
///         answers exact reads; this interface owns the READ side that a
///         search needs: filters that reach the storage engine instead of
///         pulling every message into memory, a time window, a limit and a
///         ranking. A backend implements it over whatever it can do — a
///         full-text index where one exists, a literal scan where none does.
///     </para>
///     <para>
///         The contract is AOT-friendly by construction: plain records and
///         collections, no reflection and no <c>dynamic</c>. Every backend
///         that implements it reports WHICH kinds it can serve
///         (<see cref="SessionQueryResult.SupportedKinds" />) and HOW it
///         matched the text (<see cref="SessionQueryResult.Mode" />), so a
///         caller never has to guess whether a ranking is a relevance score
///         or a scan order.
///     </para>
///     <para>
///         <b>Failure contract:</b> expected states travel the
///         <see cref="Result" /> rail — a rejected query (an invalid limit, an
///         inverted window) is a failure, not an exception. Nothing here
///         throws except <see cref="OperationCanceledException" />.
///     </para>
/// </remarks>
public interface ISessionQuery
{
    /// <summary>
    ///     Run the query.
    /// </summary>
    /// <param name="options">
    ///     The query: filters, window, text, limit and ranking. The options are
    ///     validated first, so a malformed query fails with a message naming
    ///     the offending option.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The matched records, or a failure when the query is invalid or the
    ///     backend could not answer it.
    /// </returns>
    public Task<Result<SessionQueryResult>> QueryAsync(
        SessionQueryOptions options,
        CancellationToken ct = default);
}
