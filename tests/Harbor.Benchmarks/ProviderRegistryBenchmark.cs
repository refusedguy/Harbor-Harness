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
