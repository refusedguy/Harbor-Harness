using System.Buffers.Binary;
using System.Reflection;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;

namespace Harbor.Ipc.Tests.Fuzz;

/// <summary>
///     Reflection probe over the internal <c>Harbor.Ipc.Protocol.ResilientFrameReader</c>
///     (Harbor.Ipc.Server assembly). The reader and its <c>FrameReadOutcome</c> /
///     <c>FrameReadResult</c> types are <c>internal</c> and the assembly grants no
///     <c>InternalsVisibleTo("Harbor.Ipc.Tests")</c>, so the fuzz tests cannot reference
///     them at compile time. This probe keeps the tests <b>compilable</b> regardless of
///     parallel refactors of src/Harbor.Ipc.Server/** while still asserting the
///     classification <b>policy</b> at runtime.
/// </summary>
/// <remarks>
///     <para>
///         Two reader shapes are supported so the tests exercise the committed D3 API
///         today and activate unchanged once the parallel D2 change lands:
///     </para>
///     <list type="bullet">
///       <item><b>D3 (static):</b> <c>static Task&lt;FrameReadResult&gt;
///           ReadRequestAsync(Stream, CancellationToken)</c>, cap = WireCodec.MaxFrameBytes.</item>
///       <item><b>D2 (instance):</b> <c>ValueTask&lt;FrameReadResult&gt; ReadRequestAsync(Stream,
///           CancellationToken)</c> on <c>new ResilientFrameReader(...)</c> (per-connection
///           budget state), default cap = ResilientFrameReader.DefaultMaxFrameBytes.</item>
///     </list>
///     <para>
///         If the reader API is renamed or removed entirely, tests fail at runtime with
///         a descriptive <see cref="InvalidOperationException" /> (desired bypass-style
///         signal) — never with a compile error. Outcomes are compared by enum
///         <i>name</i> ("Request", "StreamEnded", "EmptyFrame", "UndecodableFrame",
///         "OversizedFrame") rather than by typed enum values.
///     </para>
/// </remarks>
internal static class ResilientFrameReaderProbe
{
    private const long FallbackMaxFrameBytes = 16L * 1024 * 1024;

    private static readonly MethodInfo? ReadMethod = ResolveReadMethod();
    private static readonly ConstructorInfo? InstanceCtor = ResolveInstanceCtor();

    private static MethodInfo? ResolveReadMethod()
    {
        Type? readerType = typeof(MessagePackRpcServer).Assembly.GetType(
            "Harbor.Ipc.Protocol.ResilientFrameReader", throwOnError: false);
        if (readerType is null) return null;

        // D2 shape: instance method ReadRequestAsync(Stream, CancellationToken).
        MethodInfo? instance = readerType.GetMethod(
            "ReadRequestAsync",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [typeof(Stream), typeof(CancellationToken)],
            modifiers: null);
        if (instance is not null) return instance;

        // D3 shape: static method with the same signature.
        return readerType.GetMethod(
            "ReadRequestAsync",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(Stream), typeof(CancellationToken)],
            modifiers: null);
    }

    private static ConstructorInfo? ResolveInstanceCtor()
    {
        Type? readerType = typeof(MessagePackRpcServer).Assembly.GetType(
            "Harbor.Ipc.Protocol.ResilientFrameReader", throwOnError: false);
        // D2 exposes (long maxFrameBytes, long maxOutstandingBytes); D3 is a static class.
        return readerType?.GetConstructor([typeof(long), typeof(long)]);
    }

    private static long ResolveDefaultConst(string name, long fallback)
    {
        Type? readerType = typeof(MessagePackRpcServer).Assembly.GetType(
            "Harbor.Ipc.Protocol.ResilientFrameReader", throwOnError: false);
        return readerType?
                .GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) as long? ?? fallback;
    }

    /// <summary>Outcome names per the committed FrameReadOutcome enum.</summary>
    internal const string Request = "Request";
    internal const string StreamEnded = "StreamEnded";
    internal const string EmptyFrame = "EmptyFrame";
    internal const string UndecodableFrame = "UndecodableFrame";
    internal const string OversizedFrame = "OversizedFrame";

    /// <summary>
    ///     The frame-length cap in force for the shape being tested: D2's per-connection
    ///     frame cap (16 MB default) or D3's shared WireCodec.MaxFrameBytes (64 MB).
    /// </summary>
    internal static long ActiveMaxFrameBytes =>
        InstanceCtor is not null
            ? ResolveDefaultConst("DefaultMaxFrameBytes", FallbackMaxFrameBytes)
            : WireCodec.MaxFrameBytes;

    /// <summary>
    ///     True when a readable ReadRequestAsync(Stream, CancellationToken) was found in
    ///     either shape. Tests assert on this to produce a clear failure message.
    /// </summary>
    internal static bool IsAvailable => ReadMethod is not null;

    /// <summary>
    ///     Create one reader instance (D2 per-connection budget state). Returns
    ///     <see langword="null" /> for the D3 static shape, where the value is unused.
    /// </summary>
    internal static object? CreateReader()
    {
        if (InstanceCtor is null) return null;
        long maxFrame = ResolveDefaultConst("DefaultMaxFrameBytes", FallbackMaxFrameBytes);
        long maxOutstanding = ResolveDefaultConst("DefaultMaxOutstandingBytes", 8 * maxFrame);
        return InstanceCtor.Invoke([maxFrame, maxOutstanding]);
    }

    /// <summary>Flattened, reflection-free view of one FrameReadResult.</summary>
    internal sealed record ProbeResult(string Outcome, Guid? RequestId, string? ErrorText);

    /// <summary>
    ///     Invoke <c>ReadRequestAsync(stream, ct)</c> on the given reader instance (D2)
    ///     or statically (D3; <paramref name="reader" /> ignored) and project the
    ///     internal <c>FrameReadResult</c> into a <see cref="ProbeResult" />.
    /// </summary>
    internal static async Task<ProbeResult> ReadAsync(object? reader, Stream stream, CancellationToken ct = default)
    {
        MethodInfo method = ReadMethod ?? throw new InvalidOperationException(
            "Harbor.Ipc.Protocol.ResilientFrameReader.ReadRequestAsync(Stream, CancellationToken) "
            + "was not found in the Harbor.Ipc.Server assembly — the reader API changed "
            + "under the fuzz tests. Update ResilientFrameReaderProbe.");

        bool instanceShape = InstanceCtor is not null;
        if (instanceShape && reader is null)
        {
            throw new InvalidOperationException(
                "Reader is in instance (D2) shape — pass the instance from CreateReader().");
        }

        object invocation = method.Invoke(instanceShape ? reader : null, [stream, ct])
            ?? throw new InvalidOperationException("ReadRequestAsync returned null.");

        // D2 returns ValueTask<FrameReadResult>; D3 returns Task<FrameReadResult>.
        Task awaited = invocation as Task
            ?? invocation.GetType().GetMethod("AsTask")?.Invoke(invocation, null) as Task
            ?? throw new InvalidOperationException(
                "ReadRequestAsync returned neither Task nor ValueTask — reader shape changed.");

        await awaited.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        object result = awaited.GetType().GetProperty("Result")?.GetValue(awaited)
            ?? throw new InvalidOperationException(
                "Could not read FrameReadResult from the completed task — shape changed.");

        string outcome = result.GetType().GetProperty("Outcome")?.GetValue(result)?.ToString()
            ?? throw new InvalidOperationException(
                "FrameReadResult.Outcome missing — FrameReadResult shape changed.");

        object? request = result.GetType().GetProperty("Request")?.GetValue(result);
        Guid? requestId = request is null
            ? null
            : request.GetType().GetProperty("RequestId")?.GetValue(request) as Guid?;

        string? errorText = (result.GetType().GetProperty("Error")?.GetValue(result) as Exception)?.Message;

        return new ProbeResult(outcome, requestId, errorText);
    }

    /// <summary>4-byte big-endian frame length header (WireCodec wire format).</summary>
    internal static byte[] LengthHeader(uint length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        return header;
    }

    /// <summary>Append a complete frame (header + payload) to the stream buffer.</summary>
    internal static void AppendFrame(MemoryStream target, ReadOnlySpan<byte> payload)
    {
        target.Write(LengthHeader((uint)payload.Length));
        target.Write(payload);
    }

    /// <summary>Append only the 4-byte header for the given declared length.</summary>
    internal static void AppendHeader(MemoryStream target, uint declaredLength)
        => target.Write(LengthHeader(declaredLength));

    /// <summary>
    ///     MessagePack-serialize a request as the abstract union base type — the exact
    ///     bytes <c>WireCodec.WriteRequestAsync</c> would put on the wire.
    /// </summary>
    internal static byte[] SerializeRequest(HarborRequest request)
        => MessagePack.MessagePackSerializer.Serialize(request);
}
