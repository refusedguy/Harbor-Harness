using System.Text;
using Harbor.Abstractions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Regression tests for the <see cref="StringBuilderPool" /> ownership
///     contract audited in #53: clear-on-rent, 16 KB retention cap, and
///     materialize-before-return.
/// </summary>
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
        var big = new PooledStringBuilder(new StringBuilder(20 * 1024));
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
}
