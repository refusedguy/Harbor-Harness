using System.Text;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Sessions;

/// <summary>
///     Merge a fork's delta back into its parent by 3way comparison, without
///     copying the fork's whole transcript (issue #2660).
/// </summary>
/// <remarks>
///     <para>
///         The base is the fork's own prefix up to and including the boundary
///         message (the fork point). That prefix is the parent's history as it
///         stood when the fork was taken, so it is the common ancestor of the
///         two branches. The delta is everything the fork appended after the
///         boundary. Only the delta is written to the parent.
///     </para>
///     <para>
///         A parent edit of any base message is a conflict: the branch was
///         taken from different content than the parent now holds. A fork
///         message whose id already exists in the parent with different
///         content is a conflict too. Conflicts fail the whole merge closed:
///         no batch is appended, and the caller gets all three versions per
///         message to resolve.
///     </para>
///     <para>
///         Message comparison uses <see cref="UnfinishedRunDetector.Fingerprint" />,
///         which is session-id agnostic. Fork copies are re-stamped to the
///         child's session id, so record equality would report a false change.
///     </para>
///     <para>
///         The reads ride one <c>Bind</c> chain: a failed read short-circuits
///         without an <c>if (IsFailure) return</c> ladder, and no <c>.Value</c>
///         is read anywhere in this service.
///     </para>
/// </remarks>
public sealed class SessionMergeBackService
{
    /// <summary>The only supported strategy.</summary>
    public const string ThreeWayStrategy = "3way";

    private const string MissingText = "<missing>";

    /// <summary>
    ///     Merge the fork <paramref name="sourceSessionId" /> back into its parent.
    /// </summary>
    /// <param name="store">Store owning both sessions.</param>
    /// <param name="sourceSessionId">Id of the fork carrying the delta.</param>
    /// <param name="fromMessageId">Fork-point message id (inclusive base boundary).</param>
    /// <param name="strategy">Merge strategy; only <c>3way</c> is accepted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The applied delta or the conflicts that blocked it.</returns>
    public async Task<Result<SessionMergeBack>> MergeBackAsync(
        ISessionStore store,
        string sourceSessionId,
        string fromMessageId,
        string? strategy = ThreeWayStrategy,
        CancellationToken ct = default)
    {
        string selected = string.IsNullOrWhiteSpace(strategy)
            ? ThreeWayStrategy
            : strategy!.Trim().ToLowerInvariant();
        if (!string.Equals(selected, ThreeWayStrategy, StringComparison.Ordinal))
            return Result.Failure<SessionMergeBack>(
                $"Unsupported merge-back strategy '{strategy}'. Only '{ThreeWayStrategy}' is supported.");

        Result<Session> sourceRes = await store.GetAsync(sourceSessionId, ct).ConfigureAwait(false);
        return await sourceRes
            .Bind(source => LoadSourceAsync(store, source, fromMessageId, ct))
            .ConfigureAwait(false);
    }

    private static async Task<Result<SessionMergeBack>> LoadSourceAsync(
        ISessionStore store,
        Session source,
        string fromMessageId,
        CancellationToken ct)
    {
        string? parentId = source.ParentSessionId;
        if (parentId is null)
            return Result.Failure<SessionMergeBack>(
                $"Session '{source.Id}' is not a fork; it has no parent to merge into.");

        Result<IReadOnlyList<AgentMessage>> messages =
            await store.GetMessagesAsync(source.Id, ct).ConfigureAwait(false);
        return await messages
            .Bind(list => LoadTargetAsync(store, source, list, fromMessageId, parentId, ct))
            .ConfigureAwait(false);
    }

    private static async Task<Result<SessionMergeBack>> LoadTargetAsync(
        ISessionStore store,
        Session source,
        IReadOnlyList<AgentMessage> sourceMessages,
        string fromMessageId,
        string parentId,
        CancellationToken ct)
    {
        int boundary = -1;
        for (int i = 0; i < sourceMessages.Count; i++)
        {
            if (string.Equals(sourceMessages[i].Id, fromMessageId, StringComparison.Ordinal))
            {
                boundary = i;
                break;
            }
        }

        if (boundary < 0)
            return Result.Failure<SessionMergeBack>(
                $"Message '{fromMessageId}' not found in fork '{source.Id}'.");

        Result<IReadOnlyList<AgentMessage>> target =
            await store.GetMessagesAsync(parentId, ct).ConfigureAwait(false);
        return await target
            .Bind(list => MergeCoreAsync(
                store, source, sourceMessages, list, fromMessageId, parentId, boundary, ct))
            .ConfigureAwait(false);
    }

    private static async Task<Result<SessionMergeBack>> MergeCoreAsync(
        ISessionStore store,
        Session source,
        IReadOnlyList<AgentMessage> sourceMessages,
        IReadOnlyList<AgentMessage> targetMessages,
        string fromMessageId,
        string parentId,
        int boundary,
        CancellationToken ct)
    {
        var relation = new ThreadRelation(ThreadRelationKind.Fork, source.Id, fromMessageId, parentId);

        // Base = the fork's copied prefix. Its identities are the parent's at
        // fork time, so the current parent can be diffed against it directly.
        var baseFp = new Dictionary<string, string>(StringComparer.Ordinal);
        var baseText = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i <= boundary; i++)
        {
            AgentMessage m = sourceMessages[i];
            if (!baseFp.TryAdd(m.Id, UnfinishedRunDetector.Fingerprint(m)))
                return Result.Failure<SessionMergeBack>($"Duplicate message id '{m.Id}' in fork base.");
            baseText[m.Id] = Describe(m);
        }

        var targetFp = new Dictionary<string, string>(StringComparer.Ordinal);
        var targetText = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < targetMessages.Count; i++)
        {
            AgentMessage m = targetMessages[i];
            targetFp[m.Id] = UnfinishedRunDetector.Fingerprint(m);
            targetText[m.Id] = Describe(m);
        }

        var conflicts = new List<MergeBackConflict>();

        // A parent edit (or rewind) of a base message invalidates the fork's
        // common ancestor. Report all of them in one pass; write nothing.
        foreach (KeyValuePair<string, string> entry in baseFp)
        {
            string id = entry.Key;
            if (!targetFp.TryGetValue(id, out string? currentFp))
                conflicts.Add(new MergeBackConflict(id, baseText[id], MissingText, baseText[id]));
            else if (!string.Equals(currentFp, entry.Value, StringComparison.Ordinal))
                conflicts.Add(new MergeBackConflict(id, baseText[id], targetText[id], baseText[id]));
        }

        // Delta = fork messages after the boundary. Only these travel back.
        var delta = new List<AgentMessage>(sourceMessages.Count - boundary - 1);
        var appliedIds = new List<string>(delta.Capacity);
        for (int i = boundary + 1; i < sourceMessages.Count; i++)
        {
            AgentMessage m = sourceMessages[i];
            if (targetFp.TryGetValue(m.Id, out string? currentFp))
            {
                // The parent already has this id. Identical content needs no
                // write; different content is an identity collision.
                if (!string.Equals(currentFp, UnfinishedRunDetector.Fingerprint(m), StringComparison.Ordinal))
                    conflicts.Add(new MergeBackConflict(m.Id, Describe(m), targetText[m.Id], Describe(m)));
                continue;
            }

            delta.Add(m);
            appliedIds.Add(m.Id);
        }

        if (conflicts.Count > 0 || delta.Count == 0)
            return Result.Success(new SessionMergeBack(
                relation, ThreeWayStrategy, 0, Array.Empty<string>(), conflicts));

        // Re-stamp the delta to the target session so the merged transcript
        // has one owner id, the same way the fork restamped its copied prefix.
        var copies = new AgentMessage[delta.Count];
        for (int i = 0; i < delta.Count; i++)
            copies[i] = delta[i].CloneForSession(parentId);

        return await store.AppendMessagesBatchAsync(parentId, copies, ct)
            .Map(() => new SessionMergeBack(
                relation, ThreeWayStrategy, copies.Length, appliedIds, conflicts))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Readable version of a message for a conflict report. Text parts of
    ///     an assistant message and outputs of a tool result are concatenated
    ///     in order; other messages fall back to their role.
    /// </summary>
    private static string Describe(AgentMessage message)
    {
        switch (message)
        {
            case UserMessage user:
                return user.Content;
            case AssistantMessage assistant:
            {
                var sb = new StringBuilder(128);
                for (int i = 0; i < assistant.Parts.Count; i++)
                {
                    if (assistant.Parts[i] is TextPart text)
                        sb.Append(text.Text);
                }

                return sb.Length == 0 ? assistant.Role : sb.ToString();
            }
            case ToolResultMessage results:
            {
                var sb = new StringBuilder(128);
                for (int i = 0; i < results.Results.Count; i++)
                    sb.Append(results.Results[i].Output);

                return sb.Length == 0 ? results.Role : sb.ToString();
            }
            default:
                return message.Role;
        }
    }
}
