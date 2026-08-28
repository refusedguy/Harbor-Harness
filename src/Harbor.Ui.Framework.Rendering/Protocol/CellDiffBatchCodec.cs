namespace Harbor.Ui.Framework.Rendering.Protocol;

using System.Buffers.Binary;
using System.Collections.Immutable;

/// <summary>
///     Compact binary codec for <see cref="CellDiffBatch"/> (renderer-unification
///     sprint Phase 6.2) — the transport form for out-of-process backends and
///     remote surfaces (UDS, SignalR). Fixed-size fields, no reflection, AOT-safe.
/// </summary>
/// <remarks>
///     Layout:
///     <code>
///     magic      u32  'C','D','I','F'
///     version    u8
///     sequence   i64
///     cols, rows i32, i32
///     changeN    i32
///     changeN × CellDiffMessage.EncodedSize bytes (fixed layout per message)
///     hintN      i32                      (V2+ only)
///     hintN × 4 × i32                      (x, y, width, height)
///     </code>
///     Decoders accept any version they understand and skip unknown trailing
///     sections of future versions via the section counts, which keeps old
///     readers forward-tolerant in the common additive case.
/// </remarks>
public static class CellDiffBatchCodec
{
    private const uint Magic = 0x46494443u; // 'CDIF' little-endian

    /// <summary>Encodes <paramref name="batch"/> into a newly allocated byte array.</summary>
    public static byte[] Encode(in CellDiffBatch batch)
    {
        int hintCount = batch.Version >= CellDiffProtocolVersion.V2 ? batch.FrameHints.Length : 0;
        int size = 4 + 1 + 8 + 8 + 4
            + (batch.Changes.Length * CellDiffMessage.EncodedSize)
            + 4 + (hintCount * 16);
        byte[] buffer = new byte[size];
        Write(buffer, batch);
        return buffer;
    }

    /// <summary>Writes <paramref name="batch"/> into <paramref name="buffer"/>; returns bytes written.</summary>
    public static int Write(Span<byte> buffer, in CellDiffBatch batch)
    {
        int hintCount = batch.Version >= CellDiffProtocolVersion.V2 ? batch.FrameHints.Length : 0;
        int required = 25 + (batch.Changes.Length * CellDiffMessage.EncodedSize) + 4 + (hintCount * 16);
        if (buffer.Length < required)
        {
            throw new ArgumentException($"Buffer too small for batch: need {required}, got {buffer.Length}.", nameof(buffer));
        }

        int o = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[o..], Magic);
        o += 4;
        buffer[o++] = (byte)batch.Version;
        BinaryPrimitives.WriteInt64LittleEndian(buffer[o..], batch.Sequence);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], batch.Cols);
        o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], batch.Rows);
        o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], batch.Changes.Length);
        o += 4;

        for (int i = 0; i < batch.Changes.Length; i++)
        {
            var m = batch.Changes[i];
            WriteCellMessage(buffer, ref o, m.X, m.Y, m.OldCell, m.NewCell);
        }

        if (batch.Version >= CellDiffProtocolVersion.V2)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], hintCount);
            o += 4;
            for (int i = 0; i < hintCount; i++)
            {
                var r = batch.FrameHints[i];
                BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], r.X);
                o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], r.Y);
                o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], r.Width);
                o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], r.Height);
                o += 4;
            }
        }

        return o;
    }

    /// <summary>
    ///     Decodes a batch produced by <see cref="Encode"/>. Accepts every
    ///     protocol version this assembly knows (backward-compatible with V1).
    /// </summary>
    public static CellDiffBatch Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 25)
        {
            throw new ArgumentException("Buffer too small for a cell-diff batch header.", nameof(buffer));
        }

        int o = 0;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[o..]);
        if (magic != Magic)
        {
            throw new InvalidProtocolDataException("Bad magic in cell-diff batch.");
        }

        o += 4;
        var version = (CellDiffProtocolVersion)buffer[o++];
        if (version is not (CellDiffProtocolVersion.V1 or CellDiffProtocolVersion.V2))
        {
            throw new InvalidProtocolDataException($"Unsupported cell-diff protocol version: {version}.");
        }

        long sequence = BinaryPrimitives.ReadInt64LittleEndian(buffer[o..]);
        o += 8;
        int cols = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
        o += 4;
        int rows = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
        o += 4;
        int changeCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
        o += 4;

        var changes = ImmutableArray.CreateBuilder<CellDiffMessage>(changeCount);
        for (int i = 0; i < changeCount; i++)
        {
            (int x, int y, Cell oldCell, Cell newCell) = ReadCellMessage(buffer, ref o);
            changes.Add(new CellDiffMessage(x, y, oldCell, newCell));
        }

        var hints = ImmutableArray<Rect>.Empty;
        if (version >= CellDiffProtocolVersion.V2)
        {
            int hintCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
            o += 4;
            if (hintCount > 0)
            {
                var builder = ImmutableArray.CreateBuilder<Rect>(hintCount);
                for (int i = 0; i < hintCount; i++)
                {
                    int x = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
                    o += 4;
                    int y = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
                    o += 4;
                    int width = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
                    o += 4;
                    int height = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
                    o += 4;
                    builder.Add(new Rect(x, y, width, height));
                }

                hints = builder.MoveToImmutable();
            }
        }

        return new CellDiffBatch(version, sequence, cols, rows, changes.MoveToImmutable(), hints);
    }

    private static void WriteCellMessage(Span<byte> buffer, ref int o, int x, int y, in Cell oldCell, in Cell newCell)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], x);
        o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], y);
        o += 4;
        WriteCell(buffer, ref o, oldCell);
        WriteCell(buffer, ref o, newCell);
    }

    private static void WriteCell(Span<byte> buffer, ref int o, in Cell cell)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer[o..], cell.Rune);
        o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[o..], cell.Fg);
        o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[o..], cell.Bg);
        o += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[o..], cell.Flags);
        o += 2;
        buffer[o++] = cell.Width;
    }

    private static (int X, int Y, Cell Old, Cell New) ReadCellMessage(ReadOnlySpan<byte> buffer, ref int o)
    {
        int x = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
        o += 4;
        int y = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
        o += 4;
        Cell oldCell = ReadCell(buffer, ref o);
        Cell newCell = ReadCell(buffer, ref o);
        return (x, y, oldCell, newCell);
    }

    private static Cell ReadCell(ReadOnlySpan<byte> buffer, ref int o)
    {
        int rune = BinaryPrimitives.ReadInt32LittleEndian(buffer[o..]);
        o += 4;
        uint fg = BinaryPrimitives.ReadUInt32LittleEndian(buffer[o..]);
        o += 4;
        uint bg = BinaryPrimitives.ReadUInt32LittleEndian(buffer[o..]);
        o += 4;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[o..]);
        o += 2;
        byte width = buffer[o++];
        return Cell.FromRaw(rune, fg, bg, flags, width);
    }
}

/// <summary>Thrown when a cell-diff batch payload is malformed or too new.</summary>
public sealed class InvalidProtocolDataException : Exception
{
    public InvalidProtocolDataException()
    {
    }

    public InvalidProtocolDataException(string message)
        : base(message)
    {
    }

    public InvalidProtocolDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
