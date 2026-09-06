using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harbor.E2E.Framework;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Multi-client isolation (sprint 6 A3): a second client's StartAgent on
///     a busy session gets the structured SESSION_BUSY error, events of an
///     owned session are addressed to the owner only, and disconnecting the
///     owner releases the lease so broadcast resumes.
/// </summary>
[NotInParallel("ipc")]
[ParallelLimiter<MockServerLimit>]
public class MultiClientIsolationTests
{
    private static AgentDefinition Definition() => new(
        AgentName.TryCreate("code").Value,
        "Code",
        "test agent",
        "test-model",
        ProviderId.TryCreate("ollama").Value,
        PermissionRuleset.Default);

    private static async Task<(HarborResponse Response, MessagePackRpcClient Client, ClientPipeTransport Transport)>
        StartAgentAsync(IServiceProvider sp, string pipe, string sessionId)
    {
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var transport = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
        var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>());
        await client.ConnectAsync();
        HarborResponse response = await client.SendAsync(new StartAgentRequest(sessionId, "code"));
        return (response, client, transport);
    }

    [Test]
    public async Task SecondClient_StartAgentOnBusySession_GetsStructuredBusyError()
    {
        var sp = TestHost.Build();
        sp.GetRequiredService<IAgentRegistry>().Register(Definition());
        string pipe = TestHost.UniquePipeName("harbor-ipc-busy");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            // Sessions must exist before StartAgent can bind them.
            var bootstrap = new MessagePackRpcClient(
                new ClientPipeTransport(pipe, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClientPipeTransport>()),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<MessagePackRpcClient>());
            await bootstrap.ConnectAsync();

            async Task<string> CreateAndIdAsync(MessagePackRpcClient c)
            {
                HarborResponse created = await c.SendAsync(
                    new CreateSessionRequest("/tmp/test", "code", "ollama", "test-model"));
                await Assert.That(created).IsTypeOf<OkResponse>();
                Session session = WireCodec.DeserializeDomain<Session>(((OkResponse)created).Payload)!;
                return session.Id;
            }

            string sid = await CreateAndIdAsync(bootstrap);

            // Client A owns the session.
            (HarborResponse respA, MessagePackRpcClient clientA, ClientPipeTransport transportA) =
                await StartAgentAsync(sp, pipe, sid);
            await Assert.That(respA).IsTypeOf<OkResponse>();

            // Client B is refused with the structured error.
            (HarborResponse respB, MessagePackRpcClient clientB, ClientPipeTransport transportB) =
                await StartAgentAsync(sp, pipe, sid);
            await Assert.That(respB).IsTypeOf<ErrorResponse>();
            await Assert.That(((ErrorResponse)respB).Message).Contains("SESSION_BUSY:" + sid);

            // Same client re-acquiring its OWN lease is idempotent.
            HarborResponse respA2 = await clientA.SendAsync(new StartAgentRequest(sid, "code"));
            await Assert.That(respA2).IsTypeOf<OkResponse>();

            await transportA.DisposeAsync();
            await transportB.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task OwnedSession_EventsGoToOwnerOnly_BroadcastResumesAfterOwnerLeaves()
    {
        var sp = TestHost.Build();
        var bus = sp.GetRequiredService<IEventBus>();
        sp.GetRequiredService<IAgentRegistry>().Register(Definition());
        string pipe = TestHost.UniquePipeName("harbor-ipc-isolation");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();

            // Both clients subscribe.
            async Task<MessagePackRpcClient> SubscribeAsync()
            {
                var t = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
                var c = new MessagePackRpcClient(t, lf.CreateLogger<MessagePackRpcClient>());
                await c.ConnectAsync();
                // Awaited ack ⇒ registration is COMPLETE when this returns
                // (SubscriptionReady only signals once for the FIRST registrant).
                HarborResponse ack = await c.SendAsync(new SubscribeToEventsRequest());
                await Assert.That(ack).IsTypeOf<OkResponse>();
                return c;
            }

            MessagePackRpcClient owner = await SubscribeAsync();
            MessagePackRpcClient outsider = await SubscribeAsync();

            // Session must exist; owner then leases it.
            var seeder = new MessagePackRpcClient(
                new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>()),
                lf.CreateLogger<MessagePackRpcClient>());
            await seeder.ConnectAsync();
            var seeded = await seeder.SendAsync(new CreateSessionRequest("/tmp/test", "code", "ollama", "test-model"));
            await Assert.That(seeded).IsTypeOf<OkResponse>();
            Session ownedSession = WireCodec.DeserializeDomain<Session>(((OkResponse)seeded).Payload)!;

            HarborResponse leaseResp = await owner.SendAsync(new StartAgentRequest(ownedSession.Id, "code"));
            await Assert.That(leaseResp).IsTypeOf<OkResponse>();

            // Run-scoped event on the owned session.
            await bus.PublishAsync(new AgentStartEvent(ownedSession.Id, Array.Empty<AgentMessage>(), null));

            bool ownerGotIt = await ReadOneFrameWithin(owner, TimeSpan.FromSeconds(5));
            await Assert.That(ownerGotIt).IsTrue();

            bool outsiderGotIt = await ReadOneFrameWithin(outsider, TimeSpan.FromMilliseconds(400));

            // Owner disconnects → its leases die with the connection → the
            // addressed stream becomes broadcast again. The server learns of
            // the death asynchronously (EOF), so probe until the release has
            // landed rather than assuming it is instantaneous.
            await owner.DisposeAsync();
            bool outsiderGotBroadcast = false;
            for (int probe = 0; probe < 25 && !outsiderGotBroadcast; probe++)
            {
                await bus.PublishAsync(new TurnStartEvent(probe));
                outsiderGotBroadcast = await ReadOneFrameWithin(outsider, TimeSpan.FromMilliseconds(300));
            }

            await Assert.That(outsiderGotBroadcast).IsTrue();

            await outsider.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<bool> ReadOneFrameWithin(MessagePackRpcClient client, TimeSpan window)
    {
        using var timeout = new CancellationTokenSource(window);
        try
        {
            await foreach (var _ in client.EventFrames.ReadAllAsync(timeout.Token))
            {
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
