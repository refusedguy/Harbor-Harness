using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks the strongly-typed identifier value objects
///     (<see cref=\"SessionId\" />, <see cref=\"MessageId\" />, <see cref=\"ToolName\" />,
///     <see cref=\"ProviderId\" />) to verify they do not box when used in
///     dictionaries, hash-sets, and serialization paths.
/// </summary>
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
