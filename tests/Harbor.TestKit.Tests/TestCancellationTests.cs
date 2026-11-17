// TestCancellationTests.cs — the kit's cancellation factory, pinned (#1569).
//
// The factory exists because the tree wrote the same two lines in thirteen
// places, and the fact it encodes ("the token is ALREADY cancelled") was
// spread over both of them. What has to hold: the token the factory hands
// out is cancelled, and the source is still a source (a caller may cancel it
// again, or read its token as often as it likes).

using TUnit.Assertions;

namespace Harbor.TestKit.Tests;

public sealed class TestCancellationTests
{
    [Test]
    public async Task The_Token_Is_Already_Cancelled()
    {
        using var source = TestCancellation.Cancelled();

        await Assert.That(source.Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task The_Source_Is_Still_A_Source()
    {
        using var source = TestCancellation.Cancelled();

        // Cancelling an already-cancelled source is a no-op, not a throw: the
        // factory must not hand back something a caller cannot touch.
        await source.CancelAsync();

        await Assert.That(source.Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task The_Token_Reads_The_Same_Before_And_After()
    {
        using var source = TestCancellation.Cancelled();

        await Assert.That(source.Token).IsEqualTo(source.Token);
    }
}
