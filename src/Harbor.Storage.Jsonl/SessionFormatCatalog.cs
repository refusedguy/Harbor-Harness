// SessionFormatCatalog.cs — the catalog of session-file format generations
// (#1749).
//
// WHY THE FORMAT IS VERSIONED AT ALL
// ---------------------------------
// The session file is a public artifact. It is what the session picker lists,
// what the export copies, and — in the keyless tier (#1740) — what a
// repository carries, because a log written in that tier holds no provider
// credential. Its shape has already grown three times:
//
//   * the feedback records (#1746) — a rating of one message and a remark
//     about the session, each written as a record of its own;
//   * the keyless marker (#1740) — the header says which tier wrote the file;
//   * the transcript contract (#1806) — the reader's view of the history,
//     derived in memory rather than stored.
//
// EVERY ONE OF THOSE GREW THE FORMAT ADDITIVELY: an optional field, an
// optional record kind, never a change to a record that already existed. That
// is why the current format is generation 1 and NOT generation 4 — the
// extensions are members of the first generation, not migrations into later
// ones. A file written before any of them is still a V1 file, because a V1
// reader reads it exactly as it reads a file written yesterday. That is the
// property this catalog exists to make checkable: an extension that had
// needed a retroactive migration would have shown up here as a second
// generation, with a step and a fixture of its own.
//
// THE ONE FILE THAT IS NOT V1
// ---------------------------
// A file whose header carries no format marker was written before the marker
// existed. It is generation 0 — the legacy generation — and the only thing
// that distinguishes it on disk is the header shape: it states none of the
// fields the current generation states. The V0→V1 step gives it the current
// shape, and the read path applies that step to every legacy file it opens.
//
// WHAT THE CATALOG IS NOT
// -----------------------
// It is not a promise that the format will never change again. It is the
// place where a change says WHICH generation it makes, and the migration
// chain says HOW a file of the old generation is read. A generation that
// this build does not know is not read as if it were the present: the read
// fails with the version in the message.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The catalog of session-file format generations (#1749).
/// </summary>
/// <remarks>
///     <para>
///         The generation is written in the session file's header, in the
///         <c>formatVersion</c> field. It is NOT the header's own
///         <c>version</c> field: that one is the version of the header
///         RECORD's field set, and it has been 1 since the first commit of
///         the store. The generation is the version of the SESSION FILE —
///         the whole contract, header and records together.
///     </para>
///     <para>
///         A header that states no generation is a legacy header: see
///         <see cref="Legacy" />.
///     </para>
/// </remarks>
internal static class SessionFormatCatalog
{
    /// <summary>
    ///     The generation before the format carried a marker: every session
    ///     file written before #1749. Its header states none of the fields
    ///     the current generation states.
    /// </summary>
    internal const int Legacy = 0;

    /// <summary>
    ///     The generation this build writes, and the only one it reads
    ///     without a migration.
    /// </summary>
    internal const int Current = 1;

    /// <summary>
    ///     Every generation the catalog knows, oldest first. A generation
    ///     that is not in this list is one this build does not know how to
    ///     read.
    /// </summary>
    internal static IReadOnlyList<int> Generations { get; } = [Legacy, Current];

    /// <summary>
    ///     True when this build reads a file of that generation — either
    ///     directly (<see cref="Current" />) or through the migration
    ///     chain (every generation below it).
    /// </summary>
    internal static bool IsSupported(int version) => version is >= Legacy and <= Current;
}
