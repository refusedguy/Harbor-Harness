/// <summary>
///     The seed contract a fuzz failure is replayed from. These cases assert
///     what a caller can observe: which seeds a run gets when the environment
///     is absent, pins one seed, or widens the walk. Nothing here inspects how
///     the helper parses a string; the subject is the sequence it hands back.
/// </summary>
/// <remarks>
///     The two variables are process state, so the class owns a key and every
///     case goes through <see cref="EnvScope" />, which restores what it read.
/// </remarks>
[NotInParallel("testkit-seed")]
public class TestSeedsTests
{
    [Test]
    public async Task WithoutOverrides_CallersDefaultsAreUntouched()
    {
        IReadOnlyList<int> seeds = Resolve(null, null, 11, 23, 47);

        await Assert.That(seeds).IsEquivalentTo(new[] { 11, 23, 47 })
            .Because("an ordinary run must stay identical to the pre-override fuzz tests");
    }

    [Test]
    public async Task SeedOverride_NarrowsToOneReproducibleRun()
    {
        IReadOnlyList<int> seeds = Resolve("99", null, 11, 23, 47);

        await Assert.That(seeds).IsEquivalentTo(new[] { 99 })
            .Because("pinning a seed means replaying exactly that seed, not adding it to the defaults");
    }

    [Test]
    public async Task IterationsOverride_WalksForwardFromTheFirstDefault()
    {
        IReadOnlyList<int> seeds = Resolve(null, "3", 11, 23, 47);

        await Assert.That(seeds).IsEquivalentTo(new[] { 11, 12, 13 })
            .Because("a bigger run still starts at the first default, so its first failure is the same one a smaller run found");
    }

    [Test]
    public async Task BothOverrides_SeedIsTheBaseAndIterationsTheCount()
    {
        IReadOnlyList<int> seeds = Resolve("100", "2", 11, 23, 47);

        await Assert.That(seeds).IsEquivalentTo(new[] { 100, 101 })
            .Because("a reproduced failure can be stress-run around its own seed without editing the test");
    }

    [Test]
    public async Task MalformedOverrides_AreIgnored()
    {
        IReadOnlyList<int> seeds = Resolve("not-a-number", "-4", 11, 23, 47);

        await Assert.That(seeds).IsEquivalentTo(new[] { 11, 23, 47 })
            .Because("a stray CI variable must not turn every stochastic test into a discovery error");
    }

    [Test]
    public async Task NoDefaults_AndNoOverrides_StillYieldsOneSeed()
    {
        IReadOnlyList<int> seeds = Resolve(null, null);

        await Assert.That(seeds).IsEquivalentTo(new[] { 0 })
            .Because("a caller with no defaults still needs one deterministic seed to build an RNG from");
    }

    /// <summary>Sets the two overrides for the duration of one resolution.</summary>
    private static IReadOnlyList<int> Resolve(string? seed, string? iterations, params int[] defaults)
    {
        using var env = new EnvScope(TestSeeds.SeedVariable, TestSeeds.IterationsVariable);
        env.Set(TestSeeds.SeedVariable, seed);
        env.Set(TestSeeds.IterationsVariable, iterations);
        return TestSeeds.Values(defaults);
    }
}
