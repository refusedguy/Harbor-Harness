using MemoryPack;

namespace Harbor.Abstractions.Extensions;

/// <summary>
/// MemoryPack serialization helpers for zero-copy binary serialization.
/// Use for internal snapshots, caching, and inter-process communication.
/// </summary>
public static class MemoryPackExtensions
{
    /// <summary>
    /// Serialize a MemoryPackable object to a pooled byte array.
    /// </summary>
    public static byte[] ToMemoryPackBytes<T>(this T value) where T : IMemoryPackable<T>
    {
        return MemoryPackSerializer.Serialize(value);
    }

    /// <summary>
    /// Deserialize from MemoryPack binary.
    /// </summary>
    public static T? FromMemoryPackBytes<T>(this byte[] bytes) where T : IMemoryPackable<T>
    {
        return MemoryPackSerializer.Deserialize<T>(bytes);
    }
}
