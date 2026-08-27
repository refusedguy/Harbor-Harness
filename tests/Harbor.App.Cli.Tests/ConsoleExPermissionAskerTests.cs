using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.App.Cli.Repl;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Юнит-тесты asker'а разрешений ConsoleEx: маппинг решений гейта на
/// PermissionResponse, потоковый контракт (queue→Tick→роут-клавиша) и
/// отмену-как-deny.
/// </summary>
public class ConsoleExPermissionAskerTests
{
    private static ChatScreenBridge MakeBridge(out ChatTimelinePanel panel)
    {
        panel = new ChatTimelinePanel("chat", 40, 6);
        return new ChatScreenBridge(new InMemoryEventBus(), panel, new StatusViewModel(), autoSubscribe: false);
    }

    private static PermissionRequest Request(string tool, string json) => new(
        tool, "*", JsonDocument.Parse(json).RootElement.Clone(), ["allow", "deny"]);

    [Test]
    public async Task AlwaysAllow_Maps_ToAllowWithPersist()
    {
        using var bridge = MakeBridge(out var panel);
        var asker = new ConsoleExPermissionAsker(() => bridge);

        var ask = asker.AskAsync(Request("bash", "{\"command\":\"cargo build\"}"), CancellationToken.None);
        bridge.Tick(0);
        await Assert.That(panel.Timeline.Count).IsEqualTo(1); // gate landed on the timeline

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('a')))).IsTrue();
        var response = await ask;
        await Assert.That(response.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(response.PersistDecision).IsTrue();
    }

    [Test]
    public async Task Deny_Maps_ToDenyWithoutPersist()
    {
        using var bridge = MakeBridge(out _);
        var asker = new ConsoleExPermissionAsker(() => bridge);

        var ask = asker.AskAsync(Request("write", "{\"path\":\"out.cs\",\"x\":1}"), CancellationToken.None);
        bridge.Tick(0);

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('n')))).IsTrue();
        var response = await ask;
        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(response.PersistDecision).IsFalse();
    }

    [Test]
    public async Task Cancellation_Fails_Ask_TokenCancelled()
    {
        using var bridge = MakeBridge(out _);
        var asker = new ConsoleExPermissionAsker(() => bridge);
        using var cts = new CancellationTokenSource();

        var ask = asker.AskAsync(Request("bash", "{\"command\":\"sleep 10\"}"), cts.Token);
        bridge.Tick(0); // gate is visible but undecided
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await ask);
    }

    [Test]
    public async Task Describe_Collapses_Json_AndTruncates()
    {
        string longJson = "{\"command\":\"" + new string('x', 150) + "\"}";
        string detail = ConsoleExPermissionAsker.Describe(Request("bash", longJson));

        await Assert.That(detail.StartsWith("* ", StringComparison.Ordinal)).IsTrue(); // pattern + space
        await Assert.That(detail.Length).IsLessThanOrEqualTo(98); // pattern(1) + space + 96
        await Assert.That(detail.Contains('\n')).IsFalse();
        await Assert.That(detail.EndsWith("…", StringComparison.Ordinal)).IsTrue();
    }
}
