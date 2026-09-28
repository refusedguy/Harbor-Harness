using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks the strongly-typed identifier value objects
///     (<see cref=\"SessionId\" />, <see cref=\"MessageId\" />, <see cref=\"ToolName\" />,
///     <see cref=\"ProviderId\" />) to verify they do not box when used in
///     dictionaries, hash-sets, and serialization paths.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> build + probe one <c>Dictionary</c>/<c>HashSet</c> of
///          <c>Count</c> entries, or one <c>ProviderId.TryCreate</c>, or <c>Count</c>
///          <c>ToolName</c> create/to-string roundtrips.</item>
///          <item><c>Payload:</c> <c>Count</c> pre-built identifiers ("session-000000",
///          "tool_0", …) created once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — every collection row allocates its own
///          container, so nothing is retained or reused between iterations.</item>
///          <item><c>Drain:</c> none — all rows are synchronous.</item>
///          <item><c>RetainedState:</c> only the pre-built identifier arrays from
///          <c>Setup</c>; they are read-only.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the container (buckets + entries) dominates; the
///          value-type rows allocate nothing beyond it, which is the "no boxing" claim under
///          test. Parsing rows allocate the parsed identifier.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class IdentifiersValueTypeBenchmark
{
    private SessionId[] _sessionIds = null!;
    private MessageId[] _messageIds = null!;
    private ToolName[] _toolNames = null!;
    private ProviderId[] _providerIds = null!;

    [Params(100, 1000, 10000)]
    public int Count;

    [GlobalSetup]
    public void Setup()
    {
        _sessionIds = new SessionId[Count];
        _messageIds = new MessageId[Count];
        _toolNames = new ToolName[Count];
        _providerIds = new ProviderId[Count];

        for (int i = 0; i < Count; i++)
        {
            _sessionIds[i] = SessionId.Create($"session-{i:D6}");
            _messageIds[i] = MessageId.Create($"msg-{i:D6}");
            _toolNames[i] = ToolName.Create($"tool_{i}");
            _providerIds[i] = ProviderId.Create($"provider-{i}");
        }
    }

    [Benchmark(Description = "Dictionary<SessionId, string> lookup", Baseline = true)]
    public string Dictionary_SessionId_Lookup()
    {
        var dict = new Dictionary<SessionId, string>();
        for (int i = 0; i < Count; i++)
            dict[_sessionIds[i]] = $"value-{i}";
        return dict[_sessionIds[0]];
    }

    [Benchmark(Description = "Dictionary<string, string> lookup")]
    public string Dictionary_String_Lookup()
    {
        var dict = new Dictionary<string, string>();
        for (int i = 0; i < Count; i++)
            dict[_sessionIds[i].Value] = $"value-{i}";
        return dict[_sessionIds[0].Value];
    }

    [Benchmark(Description = "HashSet<SessionId> contains")]
    public bool HashSet_SessionId_Contains()
    {
        var set = new HashSet<SessionId>(_sessionIds);
        return set.Contains(_sessionIds[0]);
    }

    [Benchmark(Description = "HashSet<string> contains")]
    public bool HashSet_String_Contains()
    {
        var set = new HashSet<string>();
        for (int i = 0; i < Count; i++)
            set.Add(_sessionIds[i].Value);
        return set.Contains(_sessionIds[0].Value);
    }

    [Benchmark(Description = "ProviderId.TryCreate parse")]
    public Result<ProviderId> ProviderId_TryCreate()
    {
        return ProviderId.TryCreate("test-provider");
    }

    [Benchmark(Description = "ToolName.Create + ToString roundtrip")]
    public string ToolName_Roundtrip()
    {
        string result = string.Empty;
        for (int i = 0; i < Count; i++)
        {
            var name = ToolName.Create($"tool_{i}");
            result = name.Value;
        }
        return result;
    }
}
