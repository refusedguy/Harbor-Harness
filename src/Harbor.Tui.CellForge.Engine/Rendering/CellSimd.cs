using System.Numerics;
using System.Runtime.InteropServices;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Hand-rolled SIMD cell comparison (PF1, issue #1237): vectorized equality
/// over <see cref="Cell"/> spans. BCL-only (<see cref="Vector{T}"/>,
/// <see cref="MemoryMarshal"/>), no new dependencies, NativeAOT-friendly
/// (no reflection, no unsafe keyword — <c>MemoryMarshal.Cast</c> is safe).
/// Each <see cref="Cell"/> is 16 bytes (4×<see cref="int"/> /
/// 2×<see cref="ulong"/>), so a row of 80–200 cols is 40–100 vectors of
/// <see cref="ulong"/> — the win at this width is NOT obvious, hence the
/// mandatory BenchmarkDotNet coverage in <c>CellSimdBenchmark</c>.
/// Falls back to scalar tail-by-tail; result is bit-identical to the
/// five-compare <see cref="Cell.Equals(Cell)"/> loop.
/// </summary>
internal static class CellSimd
{
    /// <summary>
    /// True when both spans hold identical cells in order. Length mismatch
    /// returns false; empty spans are equal.
    /// </summary>
    public static bool SequenceEqual(ReadOnlySpan<Cell> first, ReadOnlySpan<Cell> second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        if (first.IsEmpty)
        {
            return true;
        }

        // 16 bytes per cell → 2 ulongs; ulong compare is exactly the
        // five-field equality folded (rune/fg/bg/flags/width + 1 pad byte).
        ReadOnlySpan<ulong> a = MemoryMarshal.Cast<Cell, ulong>(first);
        ReadOnlySpan<ulong> b = MemoryMarshal.Cast<Cell, ulong>(second);

        int width = Vector<ulong>.Count;
        int limit = a.Length - (a.Length % width);
        int i = 0;
        for (; i < limit; i += width)
        {
            var va = new Vector<ulong>(a.Slice(i, width));
            var vb = new Vector<ulong>(b.Slice(i, width));
            if (!Vector.EqualsAll(va, vb))
            {
                return false;
            }
        }

        for (; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
