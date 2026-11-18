namespace Harbor.TestKit.Tests;

/// <summary>
///     The observable contract of the shared fuzz driver: a seed reproduces a
///     walk, and a violation carries the data needed to replay it. Nothing here
///     inspects the RNG implementation; the subject is what a caller can see
///     when a walk passes or fails.
/// </summary>
public class FuzzLoopTests
{
    [Test]
    public async Task SameSeed_ReplaysTheSameInputs()
    {
        List<string> first = await Walk(4242);
        List<string> second = await Walk(4242);

        await Assert.That(first).IsEquivalentTo(second)
            .Because("a seed is only useful if a second run walks the exact same inputs — that is what makes a red CI case replayable");
        await Assert.That(first.Count).IsEqualTo(12)
            .Because("the loop must visit every case it was asked to, not stop early on a convenient subset");
    }

    [Test]
    public async Task DifferentSeeds_TakeDifferentWalks()
    {
        List<string> a = await Walk(1);
        List<string> b = await Walk(2);

        await Assert.That(string.Join(",", a)).IsNotEqualTo(string.Join(",", b))
            .Because("two different seeds must not silently collapse into one walk — that would make HARBOR_TEST_SEED meaningless");
    }

    [Test]
    public async Task Violation_ReportsNameSeedCaseAndExactInput()
    {
        FuzzViolationException thrown = await Assert.ThrowsAsync<FuzzViolationException>(async () =>
            await FuzzLoop<byte[]>.RunAsync("jsonl-line", 77, 6, MakeInput,
                (_, index) =>
                    index == 3
                        ? Task.FromException(new InvalidOperationException("classified as success"))
                        : Task.CompletedTask));

        await Assert.That(thrown.Name).IsEqualTo("jsonl-line");
        await Assert.That(thrown.Seed).IsEqualTo(77);
        await Assert.That(thrown.Index).IsEqualTo(3)
            .Because("the failing case index is the difference between a reproduction and a guess");
        await Assert.That(thrown.Count).IsEqualTo(6);
        await Assert.That(thrown.Message).Contains("seed 77, case 3/6")
            .Because("a failure message must name the seed and case without the reader opening the source");
        await Assert.That(thrown.Message).Contains(Convert.ToBase64String(thrown.Input))
            .Because("the exact bytes belong in the message, so a bug report can replay the failing input");
        await Assert.That(thrown.InnerException).IsNotNull();
        await Assert.That(thrown.InnerException!.Message).IsEqualTo("classified as success")
            .Because("the original diagnostic must survive the wrapping — the helper may add replay data, not replace the reason");
    }

    [Test]
    public async Task ViolationThrownByInvariant_IsNotWrappedTwice()
    {
        FuzzViolationException thrown = await Assert.ThrowsAsync<FuzzViolationException>(async () =>
            await FuzzLoop<byte[]>.RunAsync("direct", 5, 2, MakeInput,
                (input, index) => Task.FromException(
                    new FuzzViolationException("inner", 5, index, 2, input, "already replayable"))));

        await Assert.That(thrown.Name).IsEqualTo("inner")
            .Because("an invariant that already produced a replayable violation must not be buried under a second one");
    }

    private static async Task<List<string>> Walk(int seed)
    {
        var seen = new List<string>();
        await FuzzLoop<byte[]>.RunAsync("walk", seed, 12, MakeInput,
            (input, _) =>
            {
                seen.Add(Convert.ToHexString(input));
                return Task.CompletedTask;
            });
        return seen;
    }

    private static byte[] MakeInput(Random rng)
    {
        byte[] bytes = new byte[8];
        rng.NextBytes(bytes);
        return bytes;
    }
}
