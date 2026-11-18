using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 J06. A handoff packet is a pure projection of one forge pull
///     request plus the session id and worktree path. These tests pin the
///     fields the receiving session reads, not how the PR was fetched.
/// </summary>
public class HandoffProjectionTests
{
    [Test]
    public async Task Build_CarriesSessionPrAndWorktree()
    {
        var pr = new ForgePullRequest(2916, "A pull request", "OPEN", "https://example.test/2916");

        HandoffPacket packet = HandoffProjection.Build("session-42", pr, "/worktrees/handoff");

        await Assert.That(packet.Session).IsEqualTo("session-42");
        await Assert.That(packet.PullRequestNumber).IsEqualTo(2916);
        await Assert.That(packet.PullRequestTitle).IsEqualTo("A pull request");
        await Assert.That(packet.PullRequestState).IsEqualTo("OPEN");
        await Assert.That(packet.PullRequestUrl).IsEqualTo("https://example.test/2916");
        await Assert.That(packet.Worktree).IsEqualTo("/worktrees/handoff");
    }

    [Test]
    public async Task Build_PromptLetsTheReceivingSessionIdentifyTheHandoff()
    {
        var pr = new ForgePullRequest(7, "Title", "MERGED", "https://example.test/7");

        HandoffPacket packet = HandoffProjection.Build("abc", pr, "/repo");

        await Assert.That(packet.Prompt).Contains("abc");
        await Assert.That(packet.Prompt).Contains("#7");
        await Assert.That(packet.Prompt).Contains("https://example.test/7");
        await Assert.That(packet.Prompt).Contains("/repo");
    }

    [Test]
    public async Task Build_TwoPacketsForTheSameInputs_AreIdentical()
    {
        var pr = new ForgePullRequest(1, "Title", "OPEN", "https://example.test/1");

        HandoffPacket first = HandoffProjection.Build("s", pr, "/w");
        HandoffPacket second = HandoffProjection.Build("s", pr, "/w");

        await Assert.That(first).IsEqualTo(second);
    }
}
