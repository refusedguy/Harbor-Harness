// CommandReceiptWiringTests.cs — the #3387 behaviour proof.
//
// The five receipt types used to have zero callers in src/ and apps/: a store
// nobody filled and an interface nobody resolved. This file pins the two ends
// of the new path: CoreModule publishes one InMemoryCommandReceiptStore as
// ICommandReceiptStore, and a completed BashTool process records a receipt
// into that very store, so a replay observed through the interface sees the
// first receipt with a Duplicate outcome.

using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Hosting.Tests;

[NotInParallel("hosting")]
public class CommandReceiptWiringTests
{
    private static HarborComposeOptions Options() => new()
    {
        HarborDir = Path.Combine(
            Path.GetTempPath(), "harbor-hosting-3387", Guid.NewGuid().ToString("N")),
        DefaultStorageBackend = "memory",
        DefaultTuiRenderer = "plain",
    };

    [Test]
    public async Task CoreModule_PublishesTheSameStore_TheEagerToolsWriteThrough()
    {
        var services = new ServiceCollection();
        HarborCompositionContext ctx = services.AddHarbor(Options());
        using ServiceProvider sp = services.BuildServiceProvider();

        ICommandReceiptStore resolved = sp.GetRequiredService<ICommandReceiptStore>();

        await Assert.That(resolved).IsSameReferenceAs(ctx.CommandReceiptStore)
            .Because("the tool registry is built before the container; two stores would give two answers");
        await Assert.That(resolved.GetType()).IsEqualTo(typeof(InMemoryCommandReceiptStore));
    }

    [Test]
    public async Task BashTool_CompletedCommand_ReceiptAndOutcomeAreAvailableViaInterface()
    {
        var services = new ServiceCollection();
        HarborCompositionContext ctx = services.AddHarbor(Options());
        using ServiceProvider sp = services.BuildServiceProvider();
        ICommandReceiptStore store = sp.GetRequiredService<ICommandReceiptStore>();

        // The exact seam RegistryFacade installs on the production BashTool.
        Func<string, string, string, DateTimeOffset, CancellationToken, Task> writer =
            (commandId, runId, nodeId, stamp, ct) =>
                ctx.CommandReceiptStore.TryRecordAsync(commandId, runId, nodeId, stamp, ct);
        var tool = new BashTool(new SystemProcessProxy(), NullLogger<BashTool>.Instance)
        {
            ReceiptWriter = writer
        };

        JsonElement args = JsonDocument.Parse("""
            { "command": "echo receipt-marker" }
            """).RootElement;
        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create(callId: "bash-receipt-call"));

        await Assert.That(result.IsError).IsFalse();

        CommandReceipt? receipt = await store.GetAsync("bash-receipt-call");
        await Assert.That(receipt).IsNotNull();
        await Assert.That(receipt!.CommandId).IsEqualTo("bash-receipt-call");
        await Assert.That(receipt.RunId).IsEqualTo("test-session");
        await Assert.That(receipt.NodeId).IsEqualTo("test-message");

        CommandReceiptSubmission replay = await store.TryRecordAsync(
            "bash-receipt-call", "another-run", "another-node", DateTimeOffset.UtcNow);
        await Assert.That(replay.IsDuplicate).IsTrue();
        await Assert.That(replay.IsRecorded).IsFalse();
        await Assert.That(replay.Receipt).IsEqualTo(receipt)
            .Because("the first receipt wins, so a replay cannot mint a second side effect");
    }
}
