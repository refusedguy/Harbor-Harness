// SessionFormatMigrations.cs — the chain that carries a session file from its
// own generation to the current one (#1749).
//
// THE CHAIN
// ---------
// A step takes the header of one generation and returns the header the next
// generation writes. The chain is walked one generation at a time, so a file
// several generations behind passes through several steps, and the ORDER of
// the steps is the order of the generations.
//
// A generation that has no step is a hard error at read time, not a guess:
// "this build has no migration from session format version N" is an honest
// answer, and reading the file as if it were the current one is not.
//
// THE SCOPE OF A STEP
// -------------------
// A step rewrites the HEADER. The record vocabulary did not change between
// V0 and V1 — the feedback records (#1746) and the keyless marker (#1740)
// are ADDITIONS to a format whose records were already there, which is why
// the current format is generation 1 and not generation 4 (see
// SessionFormatCatalog). A generation that changed a record's SHAPE would
// need a step that touches records too, and the catalog is where that
// generation would be declared.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     One step of the session-format migration chain (#1749).
/// </summary>
internal interface ISessionFormatMigration
{
    /// <summary>The generation this step reads.</summary>
    int FromVersion { get; }

    /// <summary>The generation this step writes.</summary>
    int ToVersion { get; }

    /// <summary>
    ///     The header of one session file, as this step's generation wrote
    ///     it, becomes the header this step's generation writes.
    /// </summary>
    /// <param name="header">The header as the file states it.</param>
    /// <param name="fileLastWriteUtc">
    ///     The file's last-write time — the only thing a generation that
    ///     states no last-activity stamp can be given.
    /// </param>
    SessionHeaderEntry Apply(SessionHeaderEntry header, DateTimeOffset fileLastWriteUtc);
}

/// <summary>
///     V0 → V1: the legacy generation becomes the current one (#1749).
/// </summary>
/// <remarks>
///     <para>
///         A V0 header states the ten fields the first version of the store
///         wrote: the type, the record version, the session's identity, its
///         working directory, its title, the agent, the model, the provider,
///         and when the session was created. It states no last-activity
///         stamp, no status, no git facts, no kind and no tier marker.
///     </para>
///     <para>
///         The step changes the header in exactly two ways. The format
///         marker appears — that is what makes the file a V1 file — and the
///         last-activity stamp is materialized from the file's own write
///         time, because the V1 generation always states one and a V0 file
///         states none. Every OTHER field the V1 generation states is
///         already at its V1 value in a V0 header, because a V0 header
///         simply did not state it: the reader has been filling the V1
///         defaults in since the day those fields were added.
///     </para>
///     <para>
///         The step is idempotent: applied to a header that already carries
///         the marker and a stamp, it returns the same header. That is what
///         lets the read path apply the chain without first asking whether
///         it has to.
///     </para>
/// </remarks>
internal sealed class V0ToV1SessionFormatMigration : ISessionFormatMigration
{
    /// <inheritdoc />
    public int FromVersion => SessionFormatCatalog.Legacy;

    /// <inheritdoc />
    public int ToVersion => SessionFormatCatalog.Current;

    /// <inheritdoc />
    public SessionHeaderEntry Apply(SessionHeaderEntry header, DateTimeOffset fileLastWriteUtc) =>
        header with
        {
            FormatVersion = ToVersion,
            UpdatedAt = header.UpdatedAt != default ? header.UpdatedAt : fileLastWriteUtc,
        };
}

/// <summary>
///     The session-format migration chain (#1749): the steps that carry a
///     session file from its own generation to the current one.
/// </summary>
internal static class SessionFormatMigrator
{
    /// <summary>
    ///     The chain, in generation order. Every step takes one generation
    ///     to the next; there is no gap, and a gap is a read-time error
    ///     rather than a guess.
    /// </summary>
    private static readonly ISessionFormatMigration[] Chain =
    [
        new V0ToV1SessionFormatMigration(),
    ];

    /// <summary>
    ///     The steps of the chain. The corpus gate reads it: a step whose
    ///     source generation has no committed fixture is a step nothing
    ///     proves.
    /// </summary>
    internal static IReadOnlyList<ISessionFormatMigration> Steps => Chain;

    /// <summary>
    ///     The generation a header declares, or the legacy generation when
    ///     it declares none.
    /// </summary>
    internal static int VersionOf(SessionHeaderEntry header) =>
        header.FormatVersion ?? SessionFormatCatalog.Legacy;

    /// <summary>
    ///     The refusal for a file whose format generation this build does
    ///     not read. One text, so every reader of such a file says the same
    ///     thing about it.
    /// </summary>
    /// <param name="version">The generation the file declares.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="path">The session file's path.</param>
    internal static string UnsupportedVersionMessage(int version, string sessionId, string path) =>
        $"Session '{sessionId}' is written in session format version {version}, "
        + $"which this build does not read (it reads up to version {SessionFormatCatalog.Current}): {path}.";

    /// <summary>
    ///     Carry a header from its own generation to the current one.
    /// </summary>
    /// <param name="header">The header as the file states it.</param>
    /// <param name="fileLastWriteUtc">The file's last-write time.</param>
    /// <param name="sessionId">The session id, for the diagnostic.</param>
    /// <param name="path">The session file's path, for the diagnostic.</param>
    /// <returns>
    ///     The header as the current generation states it, or a failure
    ///     naming the generation this build does not read.
    /// </returns>
    internal static Result<SessionHeaderEntry> MigrateToCurrent(
        SessionHeaderEntry header,
        DateTimeOffset fileLastWriteUtc,
        string sessionId,
        string path)
    {
        int version = VersionOf(header);

        // A file from a generation this build does not know is not read as
        // if it were the present. The version travels in the message, so
        // the reader knows which build to reach for.
        if (version > SessionFormatCatalog.Current)
        {
            return Result.Failure<SessionHeaderEntry>(
                UnsupportedVersionMessage(version, sessionId, path));
        }

        SessionHeaderEntry current = header;
        for (int generation = version; generation < SessionFormatCatalog.Current; generation++)
        {
            ISessionFormatMigration? step = StepFrom(generation);
            if (step is null)
            {
                return Result.Failure<SessionHeaderEntry>(
                    $"Session '{sessionId}' is written in session format version {generation}, "
                    + $"and this build has no migration from that generation: {path}.");
            }

            current = step.Apply(current, fileLastWriteUtc);
        }

        return Result.Success(current);
    }

    /// <summary>
    ///     The step that leaves <paramref name="generation" />, or null when
    ///     the chain has none — the gap the read must refuse rather than
    ///     step over.
    /// </summary>
    private static ISessionFormatMigration? StepFrom(int generation)
    {
        foreach (ISessionFormatMigration step in Chain)
        {
            if (step.FromVersion == generation)
            {
                return step;
            }
        }

        return null;
    }
}
