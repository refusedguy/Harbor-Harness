using System.Collections.Generic;
using System.Text;
using Harbor.Abstractions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Regression tests for the <see cref="StringBuilderPool" /> ownership
///     contract audited in #53: clear-on-rent, 16 KB retention cap, and
///     materialize-before-return. Also pins the #1239 v2 bucketing contract
///     (right-sized rent, per-bucket recycling, shared-instance safety).
/// </summary>
/// <remarks>
///     The pool is process-wide static state, and several assertions below are about
///     which instance a rent hands back — that needs a quiesced pool, so the whole
///     class runs serialised against the dictionary-pool tests via one
///     <c>NotInParallel</c> key.
/// </remarks>
[NotInParallel("object-pools")]
public class StringBuilderPoolTests
{
    [Test]
    public async Task Rent_AfterReturn_ReturnsClearedBuilder()
    {
        {
            using var first = StringBuilderPool.Rent(256);
            first.Builder.Append("sensitive-secret-payload");
        }

        using var second = StringBuilderPool.Rent(256);
        await Assert.That(second.Builder.Length).IsEqualTo(0);
        await Assert.That(second.ToString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task OversizedBuilder_DroppedFromPool()
    {
        // A builder that grew past MaxRetainCapacity (16 KB) must be dropped
        // on return, never recycled — a later Rent must not observe it.
        var big = new StringBuilderPool.PooledStringBuilder(new StringBuilder(20 * 1024));
        big.Builder.Append('x', 20 * 1024);
        big.Dispose();

        using var rented = StringBuilderPool.Rent(256);
        await Assert.That(rented.Builder.Capacity <= 16 * 1024).IsTrue();
        await Assert.That(rented.Builder.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ToString_MaterializesCopy_UnaffectedByPoolReuse()
    {
        string snapshot;
        var pooled = StringBuilderPool.Rent(64);
        pooled.Builder.Append("abc");
        snapshot = pooled.ToString();
        pooled.Dispose();

        using var reuse = StringBuilderPool.Rent(64);
        reuse.Builder.Append("zzzz");

        await Assert.That(snapshot).IsEqualTo("abc");
    }

    // -- #1239 v2: size-bucketed pool -------------------------------------------

    [Test]
    public async Task Rent_ReturnsBuilderOfAtLeastRequestedCapacity()
    {
        using var rented = StringBuilderPool.Rent(1024);
        await Assert.That(rented.Builder.Capacity).IsGreaterThanOrEqualTo(1024);
    }

    [Test]
    public async Task SmallRent_AfterLargeReturn_StaysInItsOwnBucket()
    {
        // v1 kept one global bag: the first caller to leave a 16 KiB builder in it
        // made every later renter — including one that asked for 256 characters —
        // walk away with 16 KiB of char[]. The bucket for a 256-char rent holds
        // only 256–511-char builders now, whatever a neighbour left behind.
        {
            using var large = StringBuilderPool.Rent(16 * 1024);
            large.Builder.Append('b', 16 * 1024);
        }

        using var small = StringBuilderPool.Rent(256);
        await Assert.That(small.Builder.Capacity).IsGreaterThanOrEqualTo(256);
        await Assert.That(small.Builder.Capacity).IsLessThan(512);
        await Assert.That(small.Builder.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RepeatedRents_FromSameBucket_ReuseTheSameBuilder()
    {
        StringBuilder first;
        {
            using var rented = StringBuilderPool.Rent(512);
            rented.Builder.Append("x");
            first = rented.Builder;
        }

        using var again = StringBuilderPool.Rent(512);
        await Assert.That(ReferenceEquals(first, again.Builder)).IsTrue();
    }

    [Test]
    public async Task RequestAboveRetainCap_AllocatesUnpooledBuilder()
    {
        // A rent the pool will not retain must still be served correctly, and the
        // oversized builder must not be recycled into a later small rent.
        StringBuilder oversized;
        {
            using var big = StringBuilderPool.Rent(20 * 1024);
            oversized = big.Builder;
            await Assert.That(oversized.Capacity).IsGreaterThanOrEqualTo(20 * 1024);
            big.Builder.Append('y', 20 * 1024);
        }

        using var small = StringBuilderPool.Rent(256);
        await Assert.That(small.Builder.Capacity).IsLessThan(512);
        await Assert.That(ReferenceEquals(oversized, small.Builder)).IsFalse();
    }

    [Test]
    public async Task ConcurrentRents_NeverShareOneBuilder()
    {
        // ConcurrentBag hands each renter a distinct instance; the pool must not
        // corrupt that under contention (#29: a non-thread-safe pool is worse
        // than no pool — two renters would append into one builder).
        var handedOut = new HashSet<StringBuilder>();
        var duplicates = new List<string>();

        Parallel.For(0, 64, i =>
        {
            using var rented = StringBuilderPool.Rent(128);
            StringBuilder builder = rented.Builder;
            lock (handedOut)
            {
                if (!handedOut.Add(builder))
                {
                    duplicates.Add(i.ToString());
                }
            }

            builder.Append("payload-").Append(i);
        });

        await Assert.That(duplicates).IsEmpty();
        await Assert.That(handedOut.Count).IsGreaterThan(0);
    }
}
