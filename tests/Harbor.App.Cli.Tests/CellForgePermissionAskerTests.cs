using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.App.Cli.Repl;
using Harbor.Application.Permissions;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Юнит-тесты asker'а разрешений CellForge: маппинг решений гейта на
/// PermissionResponse, потоковый контракт (queue→Tick→роут-клавиша) и
/// отмену-как-deny.
/// </summary>
public class CellForgePermissionAskerTests
{
    private static ChatScreenBridge MakeBridge(out ChatTimelinePanel panel, IApprovalCoordinator coordinator)
    {
        panel = new ChatTimelinePanel("chat", 40, 6);
        return new ChatScreenBridge(new InMemoryEventBus(), panel, new StatusViewModel(), autoSubscribe: false, coordinator: coordinator);
    }

    private static ApprovalCoordinator NewCoordinator() => new(NullLogger<ApprovalCoordinator>.Instance);

    private static PermissionRequest Request(string tool, string json) => new(
        tool, "*", JsonDocument.Parse(json).RootElement.Clone(), ["allow", "deny"]);

    [Test]
    public async Task AlwaysAllow_Maps_ToAllowWithPersist()
    {
        var coordinator = NewCoordinator();
        using var bridge = MakeBridge(out var panel, coordinator);
        var asker = new CellForgePermissionAsker(() => bridge, coordinator);

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
        var coordinator = NewCoordinator();
        using var bridge = MakeBridge(out _, coordinator);
        var asker = new CellForgePermissionAsker(() => bridge, coordinator);

        var ask = asker.AskAsync(Request("write", "{\"path\":\"out.cs\",\"x\":1}"), CancellationToken.None);
        bridge.Tick(0);

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('n')))).IsTrue();
        var response = await ask;
        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(response.PersistDecision).IsFalse();
    }

    [Test]
    public async Task Cancellation_FailsClosed_Deny()
    {
        // #49 PR1: a fired abort token unblocks the approval wait as
        // fail-closed Deny (previously the TCS was TrySetCanceled and the
        // await threw OperationCanceledException).
        var coordinator = NewCoordinator();
        using var bridge = MakeBridge(out _, coordinator);
        var asker = new CellForgePermissionAsker(() => bridge, coordinator);
        using var cts = new CancellationTokenSource();

        var ask = asker.AskAsync(Request("bash", "{\"command\":\"sleep 10\"}"), cts.Token);
        bridge.Tick(0); // gate is visible but undecided
        cts.Cancel();

        var response = await ask;
        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(response.PersistDecision).IsFalse();
    }

    [Test]
    public async Task Describe_Collapses_Json_AndTruncates()
    {
        string longJson = "{\"command\":\"" + new string('x', 150) + "\"}";
        string detail = CellForgePermissionAsker.Describe(Request("bash", longJson));

        await Assert.That(detail.StartsWith("* ", StringComparison.Ordinal)).IsTrue(); // pattern + space
        await Assert.That(detail.Length).IsLessThanOrEqualTo(98); // pattern(1) + space + 96
        await Assert.That(detail.Contains('\n')).IsFalse();
        await Assert.That(detail.EndsWith("…", StringComparison.Ordinal)).IsTrue();
    }
}
