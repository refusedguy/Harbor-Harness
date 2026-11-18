namespace Harbor.App.Cli.Commands;

/// <summary>
///     The handoff packet one session prints and another session picks up
///     (#2686 J06): the session id, the pull request and the worktree path.
/// </summary>
internal sealed record HandoffPacket(
    string Session,
    int PullRequestNumber,
    string PullRequestTitle,
    string PullRequestState,
    string PullRequestUrl,
    string Worktree,
    string Prompt);

/// <summary>
///     Pure projection of a forge pull request into a handoff packet. No I/O:
///     the verb owns reading the PR and the worktree path; this class only
///     shapes the packet and the prompt the receiving session will see.
/// </summary>
internal static class HandoffProjection
{
    internal static HandoffPacket Build(string sessionId, ForgePullRequest pullRequest, string worktree)
    {
        string prompt = string.Concat(
            "Continue session ", sessionId,
            " on PR #", pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " (", pullRequest.Url, "). ",
            "Inspect the worktree at ", worktree,
            ", review the open changes, then pick up the next task.");

        return new HandoffPacket(
            sessionId,
            pullRequest.Number,
            pullRequest.Title,
            pullRequest.State,
            pullRequest.Url,
            worktree,
            prompt);
    }
}
