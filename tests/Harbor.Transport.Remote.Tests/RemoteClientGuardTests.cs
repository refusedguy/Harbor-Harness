using Harbor.Transport.Remote;
using TUnit.Assertions;

namespace Harbor.Transport.Remote.Tests;

/// <summary>
///     Proxy-guard coverage for <see cref="RemoteClient" /> (#1522):
///     <c>SendAsync</c> outside an open connection must return a
///     <c>Result</c> failure — never throw a raw socket exception.
///     No network involved: an unconnected <c>ClientWebSocket</c> sits in
///     <c>None</c> state, so the guard trips before any I/O.
/// </summary>
public class RemoteClientGuardTests
{
    private static UiTransportPacket Heartbeat() =>
        new(Type: "heartbeat", Event: null, Timestamp: DateTimeOffset.UnixEpoch);

    [Test]
    public async Task SendAsync_BeforeConnect_ReturnsFailure()
    {
        await using var client = new RemoteClient(PsAuthHandler.GeneratePsk());

        var result = await client.SendAsync(Heartbeat(), CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task SendAsync_AfterDispose_ReturnsFailure()
    {
        var client = new RemoteClient(PsAuthHandler.GeneratePsk());
        await client.DisposeAsync();

        var result = await client.SendAsync(Heartbeat(), CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Ctor_EmptyPsk_Throws()
    {
        await Assert.That(() => new RemoteClient(string.Empty))
            .Throws<ArgumentException>();
    }
}
