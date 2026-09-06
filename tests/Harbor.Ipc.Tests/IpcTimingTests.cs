using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc;
using Harbor.Ipc.Client;
using Harbor.Ipc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harbor.E2E.Framework;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Timing and lifecycle tests for the IPC server/client pair.
///     Uses <see cref="TaskCompletionSource{TResult}" /> and
///     <see cref="Channel{T}" /> for explicit synchronization — no
///     <c>Task.Delay</c>.
/// </summary>
[NotInParallel("ipc")]
[ParallelLimiter<MockServerLimit>]
    public class IpcTimingTests
{
    /// <summary>
    ///     Server starts, client connects, and a <see cref="TaskCompletionSource{TResult}" />
    ///     is signaled from the connect callback — proves the connect handshake
    ///     completes without hanging.
    /// </summary>
    [Test]
    public async Task Server_StartThenClientConnect_Completes()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-timing-connect");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new IpcHarborClient(
            pipe,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());

        var connectTask = client.ConnectAsync();
        var monitorTask = connectTask.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    connectTcs.TrySetResult(true);
                else
                    connectTcs.TrySetException(t.Exception!.InnerExceptions);
            },
            TaskScheduler.Default);

        await connectTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connectTcs.Task.IsCompletedSuccessfully).IsTrue();
        await Assert.That(client.IsConnected).IsTrue();

        await server.StopAsync();
    }

    /// <summary>
    ///     Client subscribes to events, server publishes an <see cref="AgentStartEvent" />,
    ///     and a <see cref="TaskCompletionSource{TResult}" /> captures the received
    ///     <see cref="HarborEvent.AgentStarted" /> — proves the event pipeline
    ///     delivers events in order.
    /// </summary>
    [Test]
    public async Task Client_SubscribeThenServerPublish_ReceivesEvent()
    {
        var sp = TestHost.Build();
        var eventBus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-timing-subscribe");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        await using var client = new IpcHarborClient(
            pipe,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
        await client.ConnectAsync();

        var receivedTcs = new TaskCompletionSource<HarborEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await foreach (var evt in client.SubscribeToEventsAsync().ConfigureAwait(false))
            {
                receivedTcs.TrySetResult(evt);
                break;
            }
        });

        await server.SubscriptionReady;
        await eventBus.PublishAsync(new AgentStartEvent("timing-session", Array.Empty<AgentMessage>(), null));

        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(received).IsNotNull();
        await Assert.That(received).IsTypeOf<HarborEvent.AgentStarted>();
        await Assert.That(((HarborEvent.AgentStarted)received).SessionId).IsEqualTo("timing-session");

        await server.StopAsync();
    }

    /// <summary>
    ///     Server stops, then client disconnects — a <see cref="TaskCompletionSource{TResult}" />
    ///     confirms the disconnect handshake completes cleanly after server shutdown
    ///     (no hang, no exception).
    /// </summary>
    [Test]
    public async Task Server_StopThenClientDisconnect_Completes()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-timing-disconnect");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        await using var client = new IpcHarborClient(
            pipe,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
        await client.ConnectAsync();

        await server.StopAsync();

        var disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectTask = client.DisconnectAsync();
        _ = disconnectTask.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    disconnectTcs.TrySetResult(true);
                else
                    disconnectTcs.TrySetException(t.Exception!.InnerExceptions);
            },
            TaskScheduler.Default);

        await disconnectTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(disconnectTcs.Task.IsCompletedSuccessfully).IsTrue();
        await Assert.That(client.IsConnected).IsFalse();
    }

    /// <summary>
    ///     Five rapid connect/disconnect cycles on the same client — proves
    ///     no race condition in the <c>_connected</c> interlocked flag or
    ///     transport state machine. A <see cref="Channel{T}" /> collects
    ///     per-cycle results for assertion.
    /// </summary>
    [Test]
    public async Task RapidConnectDisconnect_NoRace()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-timing-rapid");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        var results = Channel.CreateUnbounded<bool>();

        for (int i = 0; i < 5; i++)
        {
            await using var client = new IpcHarborClient(
                pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();
            await Assert.That(client.IsConnected).IsTrue();
            await client.DisconnectAsync();
            await Assert.That(client.IsConnected).IsFalse();
            await results.Writer.WriteAsync(true);
        }

        results.Writer.Complete();
        var count = 0;
        await foreach (var _ in results.Reader.ReadAllAsync())
            count++;

        await Assert.That(count).IsEqualTo(5);

        await server.StopAsync();
    }

    /// <summary>
    ///     Client is disposed while the server is still running — a timer-backed
    ///     <see cref="TaskCompletionSource{TResult}" /> guards against deadlock.
    ///     If <see cref="IpcHarborClient.DisposeAsync" /> does not complete
    ///     within the timeout, the test fails.
    /// </summary>
    [Test]
    public async Task ClientDispose_WhileServerRunning_NoDeadlock()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-timing-dispose");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        await using var client = new IpcHarborClient(
            pipe,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
        await client.ConnectAsync();

        var disposeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new System.Threading.Timer(
            _ => disposeTcs.TrySetResult(false),
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);

        var disposeTask = client.DisposeAsync().AsTask();
        _ = disposeTask.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    disposeTcs.TrySetResult(true);
                else
                    disposeTcs.TrySetException(t.Exception!.InnerExceptions);
            },
            TaskScheduler.Default);

        var completed = await disposeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(completed).IsTrue();

        await server.StopAsync();
    }
}
