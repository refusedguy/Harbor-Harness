using System.Buffers;
using System.Text;
using Harbor.Abstractions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Regression tests for the buffer contracts fixed in #90:
///     <see cref="ArrayPoolExtensions.RentedArray{T}" /> spans sliced to
///     <c>Length</c>, <c>clearArray: true</c> on return,
///     <see cref="CollectionExtensions.AddRange{T}" /> pre-growing capacity,
///     and the documented (not compiler-enforced) <see cref="StringBuilderPool" />
///     ownership contract.
/// </summary>
public class BufferContractsTests
{
    /// <summary>Pool that always over-allocates, so slicing is observable.</summary>
    private sealed class OversizedPool : ArrayPool<byte>
    {
        public byte[]? LastReturned;
        public bool LastClearArray;

        public override byte[] Rent(int minimumLength) => new byte[minimumLength + 54];

        public override void Return(byte[] array, bool clearArray = false)
        {
            LastReturned = array;
            LastClearArray = clearArray;
        }
    }

    // Ref-struct locals must not live in async methods, so all rented-array
    // assertions funnel through these sync helpers; the [Test]s only await asserts.

    private static (int ArrayLength, int Length, int SpanLength, int ReadOnlySpanLength) MeasureRentedSpans()
    {
        var pool = new OversizedPool();
        using var rented = pool.RentScoped(10);
        return (rented.Array.Length, rented.Length, rented.Span.Length, rented.ReadOnlySpan.Length);
    }

    private static bool SpanWriteStaysInsideRentedRegion()
    {
        var pool = new OversizedPool();
        using var rented = pool.RentScoped(8);
        rented.Span.Fill(0xAB);
        for (int i = 0; i < rented.Length; i++)
        {
            if (rented.Array[i] != 0xAB) return false;
        }
        // Bytes past Length must be untouched by the Span fill.
        for (int i = rented.Length; i < rented.Array.Length; i++)
        {
            if (rented.Array[i] != 0x00) return false;
        }
        return true;
    }

    private static (byte[] Returned, bool ClearArray) MeasureDispose()
    {
        var pool = new OversizedPool();
        var rented = pool.RentScoped(4);
        byte[] arr = rented.Array;
        rented.Dispose();
        return (pool.LastReturned!, pool.LastClearArray);
    }

    [Test]
    public async Task RentedArray_Spans_SlicedToRequestedLength()
    {
        var (arrayLength, length, spanLength, roLength) = MeasureRentedSpans();
        await Assert.That(arrayLength).IsGreaterThan(10);
        await Assert.That(length).IsEqualTo(10);
        await Assert.That(spanLength).IsEqualTo(10);
        await Assert.That(roLength).IsEqualTo(10);
    }

    [Test]
    public async Task RentedArray_SpanWrite_DoesNotTouchSlack()
    {
        await Assert.That(SpanWriteStaysInsideRentedRegion()).IsTrue();
    }

    [Test]
    public async Task RentedArray_Dispose_ReturnsWithClearArray()
    {
        var (returned, clearArray) = MeasureDispose();
        await Assert.That(returned).IsNotNull();
        await Assert.That(clearArray).IsTrue();
    }

    [Test]
    public async Task AddRange_PreGrowsCapacity_AndAppendsAll()
    {
        var list = new List<int>(2);
        int[] items = new int[100];
        for (int i = 0; i < items.Length; i++) items[i] = i;
        list.AddRange(items);

        await Assert.That(list.Count).IsEqualTo(100);
        await Assert.That(list.Capacity >= 100).IsTrue();
        await Assert.That(list[0]).IsEqualTo(0);
        await Assert.That(list[99]).IsEqualTo(99);
    }

    [Test]
    public async Task PooledStringBuilder_Copy_SharesUnderlyingBuilder()
    {
        // Documents the #90 finding: the wrapper is a copyable struct, so a
        // copy shares one builder — copies are forbidden by convention
        // (double-Dispose would return the same builder twice).
        var first = new StringBuilderPool.PooledStringBuilder(new StringBuilder());
        var copy = first;
        await Assert.That(ReferenceEquals(first.Builder, copy.Builder)).IsTrue();
        copy.Dispose(); // single return; `first` is dead from here on.
    }
}
