using Harbor.Abstractions.Git;

namespace Harbor.Application.Git;

/// <summary>
///     Parses <c>git worktree list --porcelain</c> output into
///     <see cref="GitWorktreeInfo" /> records (#666).
/// </summary>
/// <remarks>
/// <para>
///     This is <c>WorktreeJumpSeeder.ParsePorcelain</c>, moved. The parser was
///     always pure — BCL-only, no Harbor dependencies, no process — but it lived
///     in <c>Harbor.Ui.Framework.Services</c> because that is where the spawn was,
///     and the spawn was the thing that had to move. Leaving the parser behind
///     would have meant one of two worse shapes: the Domain contract returning
///     porcelain text so <c>ProcessGitQuery</c> could hand the wire format back
///     to a Presentation assembly to interpret, or a second copy of the parser
///     next to the one that already existed.
/// </para>
/// <para>
///     Public rather than internal, and that is deliberate: the parse is the
///     part worth pinning, and <c>Harbor.Application</c> exposes
///     <c>InternalsVisibleTo</c> only to the benchmarks. A parser with no test is
///     a parser nobody is watching.
/// </para>
/// <para>
///     The dispatch is a <see cref="Fields" /> table since #1560: one porcelain
///     key = one handler record, and the loop walks the table per line — the
///     old else-if chain knew each branch's prefix inline, so every new key
///     meant editing the middle of the chain. Adding a key is now one record at
///     the end of the table; existing records untouched. Record order in the
///     table is the dispatch order (first match wins) — it is declared here
///     rather than being implied by how the chain happened to be written.
///     The parse itself is unchanged: same infos on the same inputs, including
///     its tolerance of CRLF and of unrecognised lines, and <see cref="Flush" />
///     fires the same way — before a record-start key and once at the end.
/// </para>
/// </remarks>
public static class WorktreePorcelainParser
{
    /// <summary>
    ///     One porcelain key's handler. <see cref="LineIsKey" /> selects
    ///     whole-line equality over StartsWith (for keys with no payload, like
    ///     <c>bare</c>); <see cref="StartsRecord" /> declares that the key
    ///     begins a new record, so the previous one flushes first — on
    ///     <c>worktree </c>, and only there, the parser emits and resets before
    ///     dispatching.
    /// </summary>
    /// <param name="Prefix">What the line is matched against.</param>
    /// <param name="LineIsKey">Match the whole line, not just the prefix.</param>
    /// <param name="StartsRecord">Flush the record being built before handling.</param>
    /// <param name="Handle">
    ///     Writes the line's payload into the record being built. The payload is
    ///     the rest of the line after the key (empty for whole-line keys); it is
    ///     trimmed to <see langword="null" /> when empty, which is what makes a
    ///     detached worktree report a null branch rather than an empty string.
    /// </param>
    private sealed record PorcelainField(
        string Prefix,
        bool LineIsKey,
        bool StartsRecord,
        Action<WorktreeRecord, string> Handle);

    /// <summary>
    ///     The record being built — the fields the table handlers write and
    ///     <see cref="Flush" /> turns into a <see cref="GitWorktreeInfo" />.
    /// </summary>
    private sealed class WorktreeRecord
    {
        public string? Path;
        public string? Branch;
        public bool Bare;

        public void Reset()
        {
            Path = null;
            Branch = null;
            Bare = false;
        }
    }

    /// <summary>
    ///     The porcelain keys this parser knows. Order is significant (the first
    ///     matching record handles the line) and declared: keys here never
    ///     overlap in practice, but the contract is first-match-wins, so a new
    ///     record must go where that wins correctly — at the end is the safe
    ///     default for a fresh key.
    /// </summary>
    private static readonly PorcelainField[] Fields =
    [
        new("worktree ", LineIsKey: false, StartsRecord: true, Handle: SetPath),
        new("branch refs/heads/", LineIsKey: false, StartsRecord: false, Handle: SetBranch),
        new("bare", LineIsKey: true, StartsRecord: false, Handle: MarkBare),
    ];

    /// <summary>
    ///     Parse <c>git worktree list --porcelain</c> output into records, in the
    ///     order git emitted them (the main checkout first). Malformed lines are
    ///     ignored; a null/empty input yields no records.
    /// </summary>
    /// <param name="output">Raw stdout of the porcelain listing, possibly null.</param>
    public static IReadOnlyList<GitWorktreeInfo> Parse(string? output)
    {
        var infos = new List<GitWorktreeInfo>();
        if (string.IsNullOrEmpty(output))
        {
            return infos;
        }

        var record = new WorktreeRecord();
        void Flush()
        {
            if (!string.IsNullOrEmpty(record.Path))
            {
                infos.Add(new GitWorktreeInfo(record.Path, record.Branch, record.Bare));
            }

            record.Reset();
        }

        // Split on '\n' and trim a trailing '\r' per line (git emits LF; tolerate CRLF).
        int start = 0;
        for (int i = 0; i <= output.Length; i++)
        {
            bool end = i == output.Length || output[i] == '\n';
            if (!end)
            {
                continue;
            }

            int len = i - start;
            if (len > 0 && output[i - 1] == '\r')
            {
                len--;
            }

            string line = len > 0 ? output.Substring(start, len) : string.Empty;
            start = i + 1;

            foreach (PorcelainField field in Fields)
            {
                bool match = field.LineIsKey
                    ? line == field.Prefix
                    : line.StartsWith(field.Prefix, StringComparison.Ordinal);
                if (!match)
                {
                    continue;
                }

                if (field.StartsRecord)
                {
                    Flush();
                }

                field.Handle(record, field.LineIsKey ? string.Empty : line[field.Prefix.Length..]);
                break;
            }
        }

        Flush();
        return infos;
    }

    private static void SetPath(WorktreeRecord record, string payload)
    {
        string candidate = payload.Trim();
        record.Path = candidate.Length > 0 ? candidate : null;
    }

    private static void SetBranch(WorktreeRecord record, string payload)
    {
        string candidate = payload.Trim();
        record.Branch = candidate.Length > 0 ? candidate : null;
    }

    private static void MarkBare(WorktreeRecord record, string payload)
    {
        record.Bare = true;
    }
}
