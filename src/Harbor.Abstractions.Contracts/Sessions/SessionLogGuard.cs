// SessionLogGuard.cs — the "the agent's text never disappears" guard (#1784).
//
// THE INVARIANT
// -------------
// After any mutating verb of a session store, the MULTISET of message-record
// fingerprints in the log still CONTAINS the multiset it had before. A store
// that removes a record shrinks that multiset, and that is the defect this
// guard is written to catch.
//
// The invariant is deliberately about the LOG and not about the SURFACE: a
// rewind removes messages from the surface and keeps every record in the log,
// and compaction removes messages from the MODEL CONTEXT and keeps every
// record in the log. Neither shrinks the multiset. Physical deletion does —
// and that is the one behaviour the guard must be able to fail on.
//
// WHY A FINGERPRINT AND NOT THE ID
// ---------------------------------
// Two records with the same message id are two DIFFERENT records: the second
// is an edited version of the first, and both must stay in the log. A guard
// that compared ids would call the edit a replacement and miss the loss of
// the earlier version. So the fingerprint covers the id AND the content.
//
// WHY IT IS PURE
// --------------
// FindLostRecords reads no file, opens no store and has no side effect: it
// takes two logs and answers a question about them. That is what makes the
// negative control honest — the guard is exercised on hand-built logs, and a
// guard that could only be exercised through a real file could not be pointed
// at a deliberately broken sample.

using System.Security.Cryptography;
using System.Text;
using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The guard over a session log: what a store must never lose, and how to
///     ask whether it lost it.
/// </summary>
public static class SessionLogGuard
{
    /// <summary>
    ///     The fingerprint of one message record: its id, its role, and a digest
    ///     of its content.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The content digest is what distinguishes two versions of the same
    ///         message id. An edit appends a new version; the earlier version is
    ///         a different record and must survive on its own.
    ///     </para>
    ///     <para>
    ///         A message whose runtime type is outside the known union is
    ///         fingerprinted by its full record text rather than refused: the
    ///         guard must be able to measure a log it does not fully
    ///         understand, and a refused message would silently disappear from
    ///         the comparison instead.
    ///     </para>
    /// </remarks>
    /// <param name="message">The message to fingerprint.</param>
    /// <returns>A stable fingerprint string.</returns>
    public static string Fingerprint(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string content = new MessageDigestVisitor().Accept(message);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(content));

        return $"{message.Id}|{message.Role}|{Convert.ToHexString(digest)}";
    }

    /// <summary>
    ///     The records that were in <paramref name="before" /> and are not in
    ///     <paramref name="after" /> any more.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The comparison is a MULTISET comparison: a fingerprint that
    ///         appears twice before and once after counts as one lost record.
    ///         That is the honest reading of "the log must not shrink" — two
    ///         records with identical content are still two records.
    ///     </para>
    ///     <para>
    ///         Only message records are counted. A rewind event is not a
    ///         message and is not the agent's text; the guard is about the text.
    ///     </para>
    /// </remarks>
    /// <param name="before">The log before the mutation.</param>
    /// <param name="after">The log after the mutation.</param>
    /// <returns>
    ///     One fingerprint per lost record; empty when nothing was lost.
    /// </returns>
    public static IReadOnlyList<string> FindLostRecords(
        IReadOnlyList<SessionLogEntry> before,
        IReadOnlyList<SessionLogEntry> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        Dictionary<string, int> beforeCounts = CountFingerprints(before);
        Dictionary<string, int> afterCounts = CountFingerprints(after);

        var lost = new List<string>();

        foreach ((string fingerprint, int count) in beforeCounts)
        {
            afterCounts.TryGetValue(fingerprint, out int afterCount);

            for (int i = afterCount; i < count; i++)
            {
                lost.Add(fingerprint);
            }
        }

        return lost;
    }

    /// <summary>
    ///     Whether the log survived the mutation without losing a record.
    /// </summary>
    /// <param name="before">The log before the mutation.</param>
    /// <param name="after">The log after the mutation.</param>
    /// <returns>True when no record was lost.</returns>
    public static bool IsLossless(
        IReadOnlyList<SessionLogEntry> before,
        IReadOnlyList<SessionLogEntry> after)
        => FindLostRecords(before, after).Count == 0;

    /// <summary>
    ///     The fingerprint multiset of a log's message records.
    /// </summary>
    /// <param name="log">The log to measure.</param>
    /// <returns>Fingerprint to occurrence count.</returns>
    private static Dictionary<string, int> CountFingerprints(IReadOnlyList<SessionLogEntry> log)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < log.Count; i++)
        {
            SessionLogEntry entry = log[i];

            if (entry.Kind != SessionLogEntryKind.Message || entry.Message is null)
            {
                continue;
            }

            string fingerprint = Fingerprint(entry.Message);
            counts.TryGetValue(fingerprint, out int count);
            counts[fingerprint] = count + 1;
        }

        return counts;
    }

    /// <summary>
    ///     Digests a message by its kind, through the repository's canonical
    ///     traversal of the message union.
    /// </summary>
    private sealed class MessageDigestVisitor : AgentMessageVisitor<string>
    {
        /// <inheritdoc />
        public override string Visit(UserMessage message) => message.Content;

        /// <inheritdoc />
        public override string Visit(AssistantMessage message) => DescribeParts(message.Parts);

        /// <inheritdoc />
        public override string Visit(ToolResultMessage message) => DescribeResults(message.Results);

        /// <summary>
        ///     A message whose runtime type is outside the known union. It is
        ///     digested by its full record text rather than refused: the guard
        ///     has to be able to measure a log it does not fully understand.
        /// </summary>
        /// <param name="message">The unrecognised message.</param>
        /// <returns>The digest material for the message.</returns>
        protected override string VisitUnknown(AgentMessage message)
            => message.ToString() ?? string.Empty;

        /// <summary>
        ///     The digest material of an assistant message's parts, in order.
        /// </summary>
        /// <param name="parts">The message's parts.</param>
        /// <returns>The digest material.</returns>
        private static string DescribeParts(IReadOnlyList<ContentPart> parts)
        {
            var visitor = new PartDigestVisitor();
            var builder = new StringBuilder();

            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(visitor.Accept(parts[i]));
            }

            return builder.ToString();
        }

        /// <summary>
        ///     The digest material of a tool-result message's results, in order.
        /// </summary>
        /// <param name="results">The message's results.</param>
        /// <returns>The digest material.</returns>
        private static string DescribeResults(IReadOnlyList<ToolResultEntry> results)
        {
            var builder = new StringBuilder();

            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                ToolResultEntry entry = results[i];
                builder.Append(entry.ToolCallId)
                    .Append('|')
                    .Append(entry.ToolName)
                    .Append('|')
                    .Append(entry.IsError ? '1' : '0')
                    .Append('|')
                    .Append(entry.Output);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    ///     Digests a content part by its kind, through the repository's
    ///     canonical traversal of the part union.
    /// </summary>
    private sealed class PartDigestVisitor : ContentPartVisitor<string>
    {
        /// <inheritdoc />
        public override string Visit(TextPart part) => part.Text;

        /// <inheritdoc />
        public override string Visit(ThinkingPart part) => part.Text;

        /// <inheritdoc />
        public override string Visit(ToolCallPart part)
        {
            string args = part.Args.ValueKind == JsonValueKind.Undefined
                ? string.Empty
                : part.Args.GetRawText();

            return $"{part.Id}|{part.ToolName}|{args}";
        }

        /// <inheritdoc />
        public override string Visit(FilePart part)
            => $"{part.Path}|{part.MimeType}|{part.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        /// <summary>
        ///     A part whose runtime type is outside the known union. It is
        ///     digested by its full record text rather than refused.
        /// </summary>
        /// <param name="part">The unrecognised part.</param>
        /// <returns>The digest material for the part.</returns>
        protected override string VisitUnknown(ContentPart part) => part.ToString() ?? string.Empty;
    }
}
