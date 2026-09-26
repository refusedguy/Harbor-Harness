using MemoryPack;
namespace Harbor.Abstractions.Extensions;
/// <summary>
///     MemoryPack serialization helpers for zero-copy binary serialization.
///     Use for internal snapshots, caching, and inter-process communication.
/// </summary>
public static class MemoryPackExtensions
{
    /// <summary>
    ///     Serialize a MemoryPackable object to a newly allocated byte array.
    /// </summary>
    /// <remarks>
    ///     Nothing is pooled on this path: <c>MemoryPackSerializer.Serialize</c>
    ///     allocates a fresh array per call (#90: the previous docstring
    ///     wrongly claimed a pooled array).
    /// </remarks>
    public static byte[] ToMemoryPackBytes<T>(this T value) where T : IMemoryPackable<T> => MemoryPackSerializer.Serialize(value);

    /// <summary>
    ///     Deserialize from MemoryPack binary.
    /// </summary>
    public static T? FromMemoryPackBytes<T>(this byte[] bytes) where T : IMemoryPackable<T> => MemoryPackSerializer.Deserialize<T>(bytes);
}
