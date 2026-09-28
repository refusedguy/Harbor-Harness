using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="ProviderRegistry" /> hot paths:
///     - <see cref="ProviderRegistry.GetClient" />: frozen vs unfrozen lookup
///     - <see cref="ProviderRegistry.GetAllModelsAsync" />: aggregated model fetch
///     The frozen path uses <c>FrozenDictionary</c> for O(1) lookup; the
///     unfrozen path falls back to <c>NonBlocking.ConcurrentDictionary</c>.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>ProviderRegistry.GetClient</c> lookup, or one
///          <c>GetAllModelsAsync</c> fan-out across every registered provider.</item>
///          <item><c>Payload:</c> <c>ProviderCount</c> (1 / 5 / 20) stub providers, each
///          returning a fixed 2-model list synchronously — no network I/O.</item>
///          <item><c>StateReset:</c> none — both registries are built once in <c>Setup</c>;
///          only the frozen one is frozen (the unfrozen registry is deliberately left on the
///          <c>ConcurrentDictionary</c> path).</item>
///          <item><c>Drain:</c> none — lookups are synchronous; the model fan-out is stubbed
///          to a completed task.</item>
///          <item><c>RetainedState:</c> the frozen snapshot (client map + memoised model list)
///          after <c>Freeze()</c>; that is the contract the baseline row measures.</item>
///          <item><c>AwaitSemantics:</c> the model row awaits <c>GetAllModelsAsync</c>, which
///          awaits every stub client — task overhead is included but no real I/O is.</item>
///          <item><c>AllocAttribution:</c> the frozen lookup row allocates nothing once warm;
///          the model row allocates the aggregated <c>List&lt;ModelInfo&gt;</c> sized by
///          <c>ProviderCount</c>.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ProviderRegistryBenchmark
{
    private ProviderRegistry _frozenRegistry = null!;
    private ProviderId _providerId = null!;
    private ProviderRegistry _unfrozenRegistry = null!;

    [Params(1, 5, 20)]
    public int ProviderCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frozenRegistry = new ProviderRegistry();
        _unfrozenRegistry = new ProviderRegistry();
        _providerId = ProviderId.Create("provider-0");

        for (int i = 0; i < ProviderCount; i++)
        {
            var pid = ProviderId.Create($"provider-{i}");
            var factory = (Func<ILlmClient>)(() => new StubLlmClient(pid));
            _frozenRegistry.Register(pid, factory);
            _unfrozenRegistry.Register(pid, factory);
        }

        _frozenRegistry.Freeze();
        // _unfrozenRegistry intentionally not frozen
    }

    [Benchmark(Description = "GetClient (frozen)", Baseline = true)]
    public Result<ILlmClient> GetClient_Frozen() => _frozenRegistry.GetClient(_providerId);

    [Benchmark(Description = "GetClient (unfrozen)")]
    public Result<ILlmClient> GetClient_Unfrozen() => _unfrozenRegistry.GetClient(_providerId);

    [Benchmark(Description = "GetAllModelsAsync (frozen)")]
    public async Task<IReadOnlyList<ModelInfo>> GetAllModelsAsync_Frozen()
    {
        var result = await _frozenRegistry.GetAllModelsAsync().ConfigureAwait(false);
        return result.IsSuccess ? result.Value : Array.Empty<ModelInfo>();
    }
}

/// <summary>
///     Minimal stub LLM client for benchmarking the registry without network I/O.
///     Returns a small fixed set of models synchronously.
/// </summary>
internal sealed class StubLlmClient : ILlmClient
{
    private static readonly IReadOnlyList<ModelInfo> Models = new ModelInfo[]
    {
        new("stub-1", "stub", "Stub Model 1", 8192, 4096, false, false, true, Pricing.Unknown, "openai"),
        new("stub-2", "stub", "Stub Model 2", 16384, 8192, false, false, true, Pricing.Unknown, "openai")
    };

    public StubLlmClient(ProviderId providerId)
    {
        ProviderId = providerId;
    }

    public ProviderId ProviderId { get; }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => AsyncEnumerable.Empty<LlmEvent>();

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success(Models));
}
