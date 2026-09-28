using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Tests for the shipped <c>samples/plugins-cs/SessionBroadcastPlugin.cs</c> sample
///     (issue #352): it loads through the production <see cref="CsPluginLoader" /> pipeline
///     and its <c>session_broadcast</c> / <c>session_inbox</c> tools fan out quietly with
///     per-session rate limiting. Each test loads the sample into a fresh fixture, so the
///     plugin's static hub starts empty every time.
/// </summary>
public sealed class SessionBroadcastPluginTests
{
    [Test]
    public async Task ShippedSample_LoadsPlugin_RegistersBroadcastAndInboxTools()
    {
        (FakePluginLoadHost host, _, _) = await LoadShippedSampleAsync().ConfigureAwait(false);

        await Assert.That(host.RegisteredTools.Count).IsEqualTo(2);
        string[] names = [.. host.RegisteredTools.Select(t => t.Name.Value).OrderBy(n => n)];
        await Assert.That(names).IsEquivalentTo(["session_broadcast", "session_inbox"]);
    }

    [Test]
    public async Task Broadcast_ValidateArguments_RejectsInvalidInput()
    {
        (_, ITool broadcast, _) = await LoadShippedSampleAsync().ConfigureAwait(false);

        using var missing = JsonDocument.Parse("""{"filter":"peer"}""");
        await Assert.That(broadcast.ValidateArguments(missing.RootElement).IsFailure).IsTrue();

        using var empty = JsonDocument.Parse("""{"text":"  "}""");
        await Assert.That(broadcast.ValidateArguments(empty.RootElement).IsFailure).IsTrue();

        using var tooLong = JsonDocument.Parse($"""{{"text":"{new string('x', 4001)}"}}""");
        await Assert.That(broadcast.ValidateArguments(tooLong.RootElement).IsFailure).IsTrue();

        using var badFilter = JsonDocument.Parse("""{"text":"hi","filter":42}""");
        await Assert.That(broadcast.ValidateArguments(badFilter.RootElement).IsFailure).IsTrue();

        using var valid = JsonDocument.Parse("""{"text":"hello peers","filter":"peer"}""");
        await Assert.That(valid.RootElement.GetProperty("text").GetString()).IsEqualTo("hello peers");
        await Assert.That(broadcast.ValidateArguments(valid.RootElement).IsSuccess).IsTrue();
    }

    [Test]
    public async Task Broadcast_DeliversToSibling_InboxDrainsWithTrailer()
    {
        (_, ITool broadcast, ITool inbox) = await LoadShippedSampleAsync().ConfigureAwait(false);

        // The sibling becomes known by touching its inbox first; the drain is empty.
        using var emptyDoc = JsonDocument.Parse("""{}""");
        ToolResult empty = await inbox.ExecuteAsync(emptyDoc.RootElement, MakeContext("sess-alice")).ConfigureAwait(false);
        await Assert.That(empty.IsError).IsFalse();
        await Assert.That(empty.Output).Contains("Inbox empty");

        using var sendDoc = JsonDocument.Parse("""{"text":"context ready"}""");
        ToolResult sent = await broadcast.ExecuteAsync(sendDoc.RootElement, MakeContext("sess-bob")).ConfigureAwait(false);
        await Assert.That(sent.IsError).IsFalse();
        await Assert.That(sent.Output).Contains("sess-alice");

        using var drainDoc = JsonDocument.Parse("""{}""");
        ToolResult drained = await inbox.ExecuteAsync(drainDoc.RootElement, MakeContext("sess-alice")).ConfigureAwait(false);
        await Assert.That(drained.IsError).IsFalse();
        await Assert.That(drained.Output).Contains("context ready");
        await Assert.That(drained.Output).Contains("[broadcast-from:sess-bob]");

        // Second drain is empty — delivery is consume-once, no turn interrupt involved.
        using var drainAgainDoc = JsonDocument.Parse("""{}""");
        ToolResult again = await inbox.ExecuteAsync(drainAgainDoc.RootElement, MakeContext("sess-alice")).ConfigureAwait(false);
        await Assert.That(again.Output).Contains("Inbox empty");
    }

    [Test]
    public async Task Broadcast_SkipsSelf_AndHonorsFilter()
    {
        (_, ITool broadcast, ITool inbox) = await LoadShippedSampleAsync().ConfigureAwait(false);

        using var touchA = JsonDocument.Parse("""{}""");
        await inbox.ExecuteAsync(touchA.RootElement, MakeContext("peer-alpha")).ConfigureAwait(false);
        using var touchB = JsonDocument.Parse("""{}""");
        await inbox.ExecuteAsync(touchB.RootElement, MakeContext("peer-beta")).ConfigureAwait(false);

        using var sendDoc = JsonDocument.Parse("""{"text":"for alpha only","filter":"alpha"}""");
        ToolResult sent = await broadcast.ExecuteAsync(sendDoc.RootElement, MakeContext("sender")).ConfigureAwait(false);
        await Assert.That(sent.IsError).IsFalse();
        await Assert.That(sent.Output).Contains("peer-alpha");

        using var drainA = JsonDocument.Parse("""{}""");
        ToolResult gotA = await inbox.ExecuteAsync(drainA.RootElement, MakeContext("peer-alpha")).ConfigureAwait(false);
        await Assert.That(gotA.Output).Contains("for alpha only");

        using var drainB = JsonDocument.Parse("""{}""");
        ToolResult gotB = await inbox.ExecuteAsync(drainB.RootElement, MakeContext("peer-beta")).ConfigureAwait(false);
        await Assert.That(gotB.Output).Contains("Inbox empty");

        // The sender never receives its own broadcast.
        using var drainSelf = JsonDocument.Parse("""{}""");
        ToolResult gotSelf = await inbox.ExecuteAsync(drainSelf.RootElement, MakeContext("sender")).ConfigureAwait(false);
        await Assert.That(gotSelf.Output).Contains("Inbox empty");
    }

    [Test]
    public async Task Broadcast_RateLimit_RejectsSixthAttemptWithinWindow()
    {
        (_, ITool broadcast, _) = await LoadShippedSampleAsync().ConfigureAwait(false);

        for (int i = 0; i < 5; i++)
        {
            using var doc = JsonDocument.Parse($"{{\"text\":\"ping {i}\"}}");
            ToolResult sent = await broadcast.ExecuteAsync(doc.RootElement, MakeContext("sess-spammy")).ConfigureAwait(false);
            await Assert.That(sent.IsError).IsFalse();
        }

        using var sixth = JsonDocument.Parse("""{"text":"one too many"}""");
        ToolResult rejected = await broadcast.ExecuteAsync(sixth.RootElement, MakeContext("sess-spammy")).ConfigureAwait(false);
        await Assert.That(rejected.IsError).IsTrue();
        await Assert.That(rejected.Output).Contains("Rate limit");
    }

    [Test]
    public async Task DefaultPermission_AllowsBroadcastAndInbox()
    {
        await Assert.That(PermissionRuleset.Default.Evaluate("session_broadcast", "*")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(PermissionRuleset.Default.Evaluate("session_inbox", "*")).IsEqualTo(PermissionAction.Allow);
    }

    /// <summary>
    ///     Load the shipped sample source into a fresh fixture and return the host plus both tools.
    /// </summary>
    private static async Task<(FakePluginLoadHost Host, ITool Broadcast, ITool Inbox)> LoadShippedSampleAsync()
    {
        string repoRoot = LocateRepoRoot();
        string source = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "samples", "plugins-cs", "SessionBroadcastPlugin.cs")).ConfigureAwait(false);

        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "B").ConfigureAwait(false);
        await fixture.WritePluginAsync(source, "SessionBroadcastPlugin.cs").ConfigureAwait(false);

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);

        var result = await loader.DiscoverAndLoadAsync().ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Shipped SessionBroadcastPlugin.cs failed to load: {result.Error}");
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("session-broadcast");

        ITool broadcast = host.RegisteredTools.Single(t => t.Name.Value == "session_broadcast");
        ITool inbox = host.RegisteredTools.Single(t => t.Name.Value == "session_inbox");
        return (host, broadcast, inbox);
    }

    /// <summary>
    ///     Build a <see cref="ToolContext" /> mirroring production: the agent loop passes
    ///     <c>Services=null</c>, so the tools under test must never touch it.
    /// </summary>
    private static ToolContext MakeContext(string sessionId) => new(
        sessionId,
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"),
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Deny, false)),
        null!);

    /// <summary>
    ///     Walk up from the test binaries to the repository root (the directory
    ///     that contains <c>samples/plugins-cs</c>).
    /// </summary>
    private static string LocateRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "samples", "plugins-cs");
            if (Directory.Exists(probe))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root with samples/plugins-cs not found above {AppContext.BaseDirectory}.");
    }
}
