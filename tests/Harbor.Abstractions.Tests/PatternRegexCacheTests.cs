using Harbor.Abstractions.Permissions;
namespace Harbor.Abstractions.Tests;
/// <summary>
///     #195 (immutability batch): the process-wide glob-regex cache is bounded
///     (LRU eviction) and replaceable via
///     <see cref="PermissionRule.RegexCacheProvider" />.
/// </summary>
public class PatternRegexCacheTests
{
    [Test]
    public async Task BoundedCache_EvictsOldest_WhenOverCapacity()
    {
        var cache = new BoundedPatternRegexCache(2);
        cache.GetOrAdd("aaa*");
        cache.GetOrAdd("bbb*");
        await Assert.That(cache.Count).IsEqualTo(2);

        cache.GetOrAdd("ccc*");
        await Assert.That(cache.Count).IsEqualTo(2);

        // "aaa*" was evicted: recompiling it keeps the bound and still matches.
        var recompiled = cache.GetOrAdd("aaa*");
        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(recompiled.IsMatch("aaax")).IsTrue();
    }

    [Test]
    public async Task BoundedCache_RejectsNonPositiveCapacity()
    {
        bool threw = false;
        try
        {
            _ = new BoundedPatternRegexCache(0);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task BoundedCache_DefaultCapacity_Is512()
    {
        await Assert.That(BoundedPatternRegexCache.DefaultCapacity).IsEqualTo(512);
    }

    [Test]
    public async Task RegexCacheProvider_IsInjectable_AndRestorable()
    {
        var original = PermissionRule.RegexCacheProvider;
        try
        {
            var custom = new BoundedPatternRegexCache(8);
            PermissionRule.RegexCacheProvider = custom;

            var rule = new PermissionRule("read", "src/*", PermissionAction.Allow);
            await Assert.That(rule.MatchesPattern("src/a.cs")).IsTrue();
            await Assert.That(custom.Count >= 1).IsTrue();
        }
        finally
        {
            PermissionRule.RegexCacheProvider = original;
        }
    }
}
