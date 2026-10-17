using System.Text.RegularExpressions;

namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     #1585 (Flyweight): compiled regexes are shared — a hit returns the same
///     instance, the cache is keyed by (pattern, options, timeout), and it is
///     bounded with LRU eviction (precedent: BoundedPatternRegexCache).
/// </summary>
public class BoundedRegexCacheTests
{
    [Test]
    public async Task SamePatternAndOptions_ReturnsSameInstance()
    {
        var first = Compile("* {1,}");
        var second = Compile("* {1,}");

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task DifferentOptions_AreDistinctEntries()
    {
        var before = BoundedRegexCache.Shared.Count;

        var caseSensitive = BoundedRegexCache.Shared.GetOrAdd(
            "never-a-collide", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2),
            static (p, o, t) => new Regex(p, o, t));
        var ignoreCase = BoundedRegexCache.Shared.GetOrAdd(
            "never-a-collide", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2),
            static (p, o, t) => new Regex(p, o, t));

        await Assert.That(ReferenceEquals(caseSensitive, ignoreCase)).IsFalse();
        await Assert.That(BoundedRegexCache.Shared.Count).IsGreaterThanOrEqualTo(before + 2);
    }

    [Test]
    public async Task DifferentTimeout_IsDifferentEntry()
    {
        var two = BoundedRegexCache.Shared.GetOrAdd(
            "never-b-collide", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2),
            static (p, o, t) => new Regex(p, o, t));
        var five = BoundedRegexCache.Shared.GetOrAdd(
            "never-b-collide", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5),
            static (p, o, t) => new Regex(p, o, t));

        await Assert.That(ReferenceEquals(two, five)).IsFalse();
    }

    [Test]
    public async Task SharedCache_Matches()
    {
        var regex = BoundedRegexCache.Shared.GetOrAdd(
            "harbor-", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2),
            static (p, o, t) => new Regex(p, o, t));

        await Assert.That(regex.IsMatch("harbor-code")).IsTrue();
        await Assert.That(regex.IsMatch("harbor [code]")).IsFalse();
    }

    [Test]
    public async Task BoundedCache_EvictsOldest_WhenOverCapacity()
    {
        var cache = new BoundedRegexCache(2);
        cache.GetOrAdd("a", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2), static (p, o, t) => new Regex(p, o, t));
        cache.GetOrAdd("b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2), static (p, o, t) => new Regex(p, o, t));
        await Assert.That(cache.Count).IsEqualTo(2);

        cache.GetOrAdd("c", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2), static (p, o, t) => new Regex(p, o, t));
        await Assert.That(cache.Count).IsEqualTo(2);

        // "a" was evicted: recompiling it keeps the bound and still matches.
        var recompiled = cache.GetOrAdd("a", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2), static (p, o, t) => new Regex(p, o, t));
        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(recompiled.IsMatch("a")).IsTrue();
    }

    [Test]
    public async Task BoundedCache_RejectsNonPositiveCapacity()
    {
        bool threw = false;
        try
        {
            _ = new BoundedRegexCache(0);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    private static Regex Compile(string pattern) =>
        BoundedRegexCache.Shared.GetOrAdd(
            pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2),
            static (p, o, t) => new Regex(p, o, t));
}
