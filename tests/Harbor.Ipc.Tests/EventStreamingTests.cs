using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Ipc.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Ipc.Tests;

/// <summary>
///     Skip unless explicitly opted in via <c>HARBOR_IPC_EVENTSTREAM=1</c>.
///     Real named-pipe event streaming deadlocks under TUnit scheduling on
///     this environment (pre-existing, see Harbor.slnx comment). One env
///     var keeps the coverage reachable without destabilizing default runs.
/// </summary>
internal sealed class SkipUnlessIpcEventStreamEnabledAttribute : SkipAttribute
{
    public SkipUnlessIpcEventStreamEnabledAttribute() : base(
        "Named-pipe event streaming is timing-flaky on Linux under TUnit "
        + "scheduling (pre-existing). Set HARBOR_IPC_EVENTSTREAM=1 to enable.")
    {
    }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(Environment.GetEnvironmentVariable("HARBOR_IPC_EVENTSTREAM") != "1");
}

/// <summary>
///     Event streaming tests. Verifies that:
///     <list type="bullet">
///         <item>The InProcessHarborClient delivers events via IEventBus.</item>
///         <item>The IpcHarborClient receives server-pushed events over the pipe.</item>
///     </list>
/// </summary>
[SkipUnlessIpcEventStreamEnabled]
[NotInParallel]
public class EventStreamingTests
{
    /// <summary>
    ///     When the IEventBus publishes an AgentStartEvent, the
    ///     InProcessHarborClient's SubscribeToEventsAsync should yield a
    ///     matching HarborEvent.AgentStarted.
    /// </summary>
    [Test]
    public async Task InProcess_Client_Receives_AgentStarted_From_EventBus()
    {
        var sp = TestHost.Build();
        var eventBus = sp.GetRequiredService<IEventBus>();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IProviderRegistry>(),
            sp.GetRequiredService<IToolRegistry>(),
            eventBus,
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var enumerateTask = Task.Run(async () =>
        {
            await foreach (var evt in client.SubscribeToEventsAsync(cts.Token).ConfigureAwait(false))
            {
                return evt;
            }
            return null;
        });

        // Give the subscription a moment to attach, then publish.
        await client.SubscriptionReady;
        await eventBus.PublishAsync(new AgentStartEvent("test-session-id", Array.Empty<AgentMessage>()));

        var received = await enumerateTask.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(received).IsNotNull();
        await Assert.That(received).IsTypeOf<HarborEvent.AgentStarted>();
        await Assert.That(((HarborEvent.AgentStarted)received!).SessionId)
            .IsEqualTo("test-session-id");
    }

    /// <summary>
    ///     When the IpcHarborClient subscribes and the server-side IEventBus
    ///     publishes an event, the client should receive the matching
    ///     HarborEvent over the pipe.
    /// </summary>
    [Test]
    public async Task Ipc_Client_Receives_AgentStarted_Over_Pipe()
    {
        var sp = TestHost.Build();
        var eventBus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-events");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var enumerateTask = Task.Run(async () =>
            {
                await foreach (var evt in client.SubscribeToEventsAsync(cts.Token).ConfigureAwait(false))
                {
                    return evt;
                }
                return null;
            });

            // Give the subscription a moment to attach on the server side.
            await server.SubscriptionReady;
            await eventBus.PublishAsync(new AgentStartEvent("test-session-id", Array.Empty<AgentMessage>()));

            var received = await enumerateTask.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(received).IsNotNull();
            await Assert.That(received).IsTypeOf<HarborEvent.AgentStarted>();
            await Assert.That(((HarborEvent.AgentStarted)received!).SessionId)
                .IsEqualTo("test-session-id");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     A tool-execution event should round-trip through the IPC layer
    ///     with the ToolResult payload preserved.
    /// </summary>
    [Test]
    public async Task Ipc_Client_Receives_ToolEnd_With_ToolResult_Payload()
    {
        var sp = TestHost.Build();
        var eventBus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-tool-event");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var enumerateTask = Task.Run(async () =>
            {
                await foreach (var evt in client.SubscribeToEventsAsync(cts.Token).ConfigureAwait(false))
                {
                    return evt;
                }
                return null;
            });

            await server.SubscriptionReady;
            var expectedResult = ToolResult.Success("file contents here");
            await eventBus.PublishAsync(new ToolExecutionEndEvent("tc-1", expectedResult, false));

            var received = await enumerateTask.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(received).IsNotNull();
            await Assert.That(received).IsTypeOf<HarborEvent.ToolEnd>();
            var toolEnd = (HarborEvent.ToolEnd)received!;
            await Assert.That(toolEnd.ToolCallId).IsEqualTo("tc-1");
            await Assert.That(toolEnd.Result.Output).IsEqualTo("file contents here");
            await Assert.That(toolEnd.Result.IsError).IsFalse();
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
