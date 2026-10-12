using System.Collections.Generic;
using Harbor.Abstractions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Correctness tests for <see cref="DictionaryPool{TKey, TValue}" /> (#1239):
///     clear-on-rent, size bands, the comparer rule, and the drop-oversized
///     invariant that keeps the pool from turning into a memory leak.
/// </summary>
/// <remarks>
///     Serialised with the builder-pool tests: the pool is process-wide static state
///     and these assertions are about which instance a rent hands back.
/// </remarks>
[NotInParallel("object-pools")]
public class DictionaryPoolTests
{
    [Test]
    public async Task Rent_ReturnsEmptyDictionary()
    {
        using var rented = DictionaryPool<string, int>.Rent();
        await Assert.That(rented.Dictionary.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Rent_AfterReturn_IsCleared()
    {
        {
            using var first = DictionaryPool<string, int>.Rent();
            first.Dictionary["secret-key"] = 42;
            await Assert.That(first.Dictionary.Count).IsEqualTo(1);
        }

        using var second = DictionaryPool<string, int>.Rent();
        await Assert.That(second.Dictionary.Count).IsEqualTo(0);
        await Assert.That(second.Dictionary.ContainsKey("secret-key")).IsFalse();
    }

    [Test]
    public async Task Rent_ServesRequestedEntryCount()
    {
        using var rented = DictionaryPool<string, int>.Rent(64);
        for (int i = 0; i < 64; i++)
        {
            rented.Dictionary[$"key-{i}"] = i;
        }

        await Assert.That(rented.Dictionary.Count).IsEqualTo(64);
        await Assert.That(rented.Dictionary["key-63"]).IsEqualTo(63);
    }

    [Test]
    public async Task RepeatedRents_FromSameBand_ReuseTheSameDictionary()
    {
        Dictionary<string, int> first;
        {
            using var rented = DictionaryPool<string, int>.Rent(16);
            rented.Dictionary["a"] = 1;
            first = rented.Dictionary;
        }

        using var again = DictionaryPool<string, int>.Rent(16);
        await Assert.That(ReferenceEquals(first, again.Dictionary)).IsTrue();
    }

    [Test]
    public async Task CustomComparer_IsNeverRecycledIntoADefaultRent()
    {
        // A dictionary's comparer is fixed at construction, so pooling one with a
        // custom comparer would hand the next comparer-agnostic renter a dictionary
        // that compares by the wrong rule.
        using (var custom = DictionaryPool<string, int>.Rent(StringComparer.OrdinalIgnoreCase))
        {
            custom.Dictionary["k"] = 1;
            await Assert.That(custom.Dictionary.ContainsKey("K")).IsTrue();
        }

        using var pooled = DictionaryPool<string, int>.Rent();
        await Assert.That(pooled.Dictionary.ContainsKey("K")).IsFalse();
        pooled.Dictionary["lower"] = 1;
        await Assert.That(pooled.Dictionary.ContainsKey("LOWER")).IsFalse();
    }

    [Test]
    public async Task DictionaryThatOutgrewTheLargestBand_IsDropped()
    {
        // Past MaxRetainEntries (1024) a dictionary is dropped on return rather
        // than retained — the pool must not hold it for the next renter.
        var big = new PooledDictionary<string, int>(
            new Dictionary<string, int>(2 * 1024));
        for (int i = 0; i < 2 * 1024; i++)
        {
            big.Dictionary[$"key-{i}"] = i;
        }

        big.Dispose();

        using var reused = DictionaryPool<string, int>.Rent(8);
        await Assert.That(ReferenceEquals(big.Dictionary, reused.Dictionary)).IsFalse();
        await Assert.That(reused.Dictionary.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DifferentGenericTypes_DoNotShareAPool()
    {
        // Static state on DictionaryPool<TKey, TValue> is per closed generic type.
        Dictionary<string, int> stringKeyed;
        {
            using var rented = DictionaryPool<string, int>.Rent();
            rented.Dictionary["a"] = 1;
            stringKeyed = rented.Dictionary;
        }

        using var longKeyed = DictionaryPool<long, string>.Rent();
        await Assert.That(ReferenceEquals(stringKeyed, longKeyed.Dictionary)).IsFalse();

        using var stringKeyedAgain = DictionaryPool<string, int>.Rent();
        await Assert.That(ReferenceEquals(stringKeyed, stringKeyedAgain.Dictionary)).IsTrue();
    }

    [Test]
    public async Task ConcurrentRents_NeverShareOneDictionary()
    {
        // Every rent is HELD until the loop ends: releasing inside the iteration
        // would let a later iteration legally reuse the instance — that is
        // pooling, not sharing. The assertion is about concurrent holders: no
        // two live rents may alias one dictionary.
        var rented = new PooledDictionary<string, int>[64];
        Parallel.For(0, 64, i =>
        {
            var r = DictionaryPool<string, int>.Rent(8);
            r.Dictionary[$"key-{i}"] = i;
            rented[i] = r;
        });

        try
        {
            var distinct = new HashSet<Dictionary<string, int>>();
            foreach (var r in rented)
            {
                distinct.Add(r.Dictionary);
            }

            await Assert.That(distinct.Count).IsEqualTo(64);
        }
        finally
        {
            foreach (var r in rented)
            {
                r.Dispose();
            }
        }
    }
}
