using System.Threading.Channels;
using Harbor.Abstractions.Models;
using Harbor.Cli.Hosting;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     A4 regression: <see cref="DummySessionContext" />.SteeringQueue used to
///     create a FRESH channel on every property access, so anything written
///     through one fetch was invisible to a reader holding another — steering
///     messages were silently lost.
/// </summary>
public class DummySessionContextTests
{
    private static DummySessionContext NewContext() =>
        new(Session.Create("/tmp/harbor-dummy-session-tests", "code", "test", "test-model"));

    [Test]
    public async Task SteeringQueue_ReturnsSameChannelInstanceAcrossAccesses()
    {
        var context = NewContext();

        Channel<AgentMessage> first = context.SteeringQueue;
        Channel<AgentMessage> second = context.SteeringQueue;

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task SteeringQueue_WrittenMessage_IsReadableFromSameContext()
    {
        var context = NewContext();
        var message = new UserMessage(
            Guid.NewGuid().ToString("N"),
            context.Session.Id,
            DateTimeOffset.UtcNow,
            "steer now",
            "user",
            "test-model");

        bool written = context.SteeringQueue.Writer.TryWrite(message);

        await Assert.That(written).IsTrue();
        bool read = context.SteeringQueue.Reader.TryRead(out AgentMessage? readBack);
        await Assert.That(read).IsTrue();
        await Assert.That(ReferenceEquals(readBack, message)).IsTrue();
    }

    [Test]
    public async Task SteeringQueue_TwoContexts_HaveIndependentChannels()
    {
        var a = NewContext();
        var b = NewContext();

        _ = a.SteeringQueue.Writer.TryWrite(new UserMessage(
            Guid.NewGuid().ToString("N"),
            a.Session.Id,
            DateTimeOffset.UtcNow,
            "for a",
            "user",
            "test-model"));

        await Assert.That(b.SteeringQueue.Reader.TryRead(out _)).IsFalse();
    }
}
