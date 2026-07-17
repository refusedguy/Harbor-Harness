using System.Buffers;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NonBlocking;
namespace Harbor.Abstractions.Providers;
/// <summary>
///     Thread-safe provider registry with lazy instantiation and frozen lookup table.
///     Implements Registry pattern (GOF).
///     Hot path is <see cref="GetClient" /> — uses FrozenDictionary for O(1) lookup
///     after first registration. Write-heavy state is stored in
///     <see cref="NonBlocking.ConcurrentDictionary{TKey, TValue}" />
///     which uses lock-free algorithms for better scaling under contention than
///     <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}" />.
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly NonBlocking.ConcurrentDictionary<ProviderId, Lazy<ILlmClient>> _clients = new();
    private readonly object _frozenLock = new();
    private readonly NonBlocking.ConcurrentDictionary<ProviderId, IReadOnlyList<ModelInfo>> _modelCache = new();
    private readonly ILogger<ProviderRegistry> _logger;
    /// <summary>
    ///     The frozen lookup table for fast lock-free reads; <see langword="null" /> until
    ///     <see cref="Freeze" /> is called.
    /// </summary>
    private FrozenDictionary<ProviderId, Lazy<ILlmClient>>? _frozenClients;

    public ProviderRegistry() : this(NullLogger<ProviderRegistry>.Instance) { }

    public ProviderRegistry(ILogger<ProviderRegistry> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderId> GetRegisteredProviderIds()
    {
        // Materialize once into an array (caller expects IReadOnlyList<T>).
        int count = _clients.Count;
        if (count == 0)
        {
            return Array.Empty<ProviderId>();
        }

        var result = new ProviderId[count];
        int i = 0;
        foreach (var key in _clients.Keys)
        {
            result[i++] = key;
        }

        return result;
    }

    /// <inheritdoc />
    public Result<ILlmClient> GetClient(ProviderId providerId)
    {
        // Try frozen snapshot first (fast path, lock-free, no dictionary lookup overhead)
        var frozen = _frozenClients;
        if (frozen is not null && frozen.TryGetValue(providerId, out var lazy))
        {
            try
            {
                return Result.Success(lazy.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider instantiation failed: {ProviderId}: {Error}", providerId, ex.Message);
                return Result.Failure<ILlmClient>($"Failed to instantiate provider '{providerId}': {ex.Message}");
            }
        }

        // Fallback to concurrent dictionary
        if (_clients.TryGetValue(providerId, out var lazyClient))
        {
            try
            {
                return Result.Success(lazyClient.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider instantiation failed: {ProviderId}: {Error}", providerId, ex.Message);
                return Result.Failure<ILlmClient>($"Failed to instantiate provider '{providerId}': {ex.Message}");
            }
        }

        _logger.LogDebug("Provider not registered: {ProviderId}", providerId);
        return Result.Failure<ILlmClient>($"Provider '{providerId}' is not registered.");
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot keys into a pooled buffer to avoid ToArray() allocating a new array each call.
        var providers = _clients.Keys.ToArray();
        if (providers.Length == 0)
        {
            return Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());
        }

        int providerCount = providers.Length;
        var tasksArray = ArrayPool<Task<ModelBatch>>.Shared.Rent(providerCount);
        List<string>? errors = null;
        try
        {
            // Kick off all provider queries in parallel.
            for (int i = 0; i < providerCount; i++)
            {
                var pid = providers[i];
                tasksArray[i] = Task.Run(async () =>
                {
                    try
                    {
                        if (_modelCache.TryGetValue(pid, out var cached))
                        {
                            return new ModelBatch(pid, cached, null);
                        }

                        var client = GetClient(pid);
                        if (client.IsFailure)
                        {
                            return new ModelBatch(pid, Array.Empty<ModelInfo>(), client.Error);
                        }

                        var models = await client.Value.GetModelsAsync(cancellationToken).ConfigureAwait(false);
                        if (models.IsFailure)
                        {
                            return new ModelBatch(pid, Array.Empty<ModelInfo>(), models.Error);
                        }

                        _modelCache[pid] = models.Value;
                        return new ModelBatch(pid, models.Value, null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get models for provider: {ProviderId}", pid);
                        return new ModelBatch(pid, Array.Empty<ModelInfo>(), ex.Message);
                    }
                }, cancellationToken);
            }

            // Copy the active range into a Task[] for Task.WhenAll (it requires IEnumerable<Task>).
            // We use ArrayPool + manual count to avoid the per-call array allocation of new Task[n].
            // Wrapping the slice in an ArraySegment<Task<ModelBatch>> avoids the previous
            // `tasksArray.AsSpan(0, providerCount).ToArray()` secondary allocation.
            var resolved = await Task.WhenAll(new ArraySegment<Task<ModelBatch>>(tasksArray, 0, providerCount)).ConfigureAwait(false);

            // First pass: compute total capacity to avoid List resizes.
            int totalModels = 0;
            for (int i = 0; i < resolved.Length; i++)
            {
                totalModels += resolved[i].Models.Count;
            }

            // Pre-size a List<ModelInfo> to the exact total and append via index-based loop.
            // AddRange(IReadOnlyList<T>) on a List<T> performs its own per-item Add, so this
            // is equivalent in cost but elides the IEnumerable<T> enumeration overhead.
            var results = new List<ModelInfo>(totalModels);
            for (int i = 0; i < resolved.Length; i++)
            {
                ref readonly var batch = ref resolved[i];
                if (batch.Error is not null)
                {
                    errors ??= new List<string>();
                    errors.Add($"{batch.ProviderId}: {batch.Error}");
                }
                else
                {
                    var models = batch.Models;
                    for (int j = 0; j < models.Count; j++)
                    {
                        results.Add(models[j]);
                    }
                }
            }

            if (results.Count == 0 && errors is not null && errors.Count > 0)
            {
                return Result.Failure<IReadOnlyList<ModelInfo>>($"Failed to load any models. Errors: {string.Join("; ", errors)}");
            }

            return Result.Success<IReadOnlyList<ModelInfo>>(results);
        }
        finally
        {
            // Clear the rented portion (Task references) before returning to the pool.
            Array.Clear(tasksArray, 0, providerCount);
            ArrayPool<Task<ModelBatch>>.Shared.Return(tasksArray);
        }
    }

    /// <inheritdoc />
    public void Register(ProviderId providerId, Func<ILlmClient> factory)
    {
        var lazy = new Lazy<ILlmClient>(() => factory(), LazyThreadSafetyMode.ExecutionAndPublication);
        _clients[providerId] = lazy;
        _modelCache.TryRemove(providerId, out _);
        InvalidateFrozenSnapshot();
    }

    /// <inheritdoc />
    public Result Unregister(ProviderId providerId)
    {
        if (_clients.TryRemove(providerId, out _))
        {
            _modelCache.TryRemove(providerId, out _);
            InvalidateFrozenSnapshot();
            return Result.Success();
        }

        return Result.Failure($"Provider '{providerId}' is not registered.");
    }

    /// <summary>
    ///     Invalidate the model cache for a specific provider. The next call to
    ///     <see cref="GetAllModelsAsync" /> will re-fetch the model list from the provider.
    /// </summary>
    /// <param name="providerId">The provider whose model cache to invalidate.</param>
    public void InvalidateModelCache(ProviderId providerId) => _modelCache.TryRemove(providerId, out _);

    /// <summary>
    ///     Freeze the current provider set for fast lock-free lookups.
    ///     Call this after all providers are registered at startup.
    /// </summary>
    public void Freeze()
    {
        lock (_frozenLock)
        {
            _frozenClients = _clients.ToFrozenDictionary();
        }
    }

    private void InvalidateFrozenSnapshot()
    {
        lock (_frozenLock)
        {
            _frozenClients = null;
        }
    }

    /// <summary>
    ///     Readonly struct result holder for parallel model fetches. Avoids boxing
    ///     ValueTuple into an object on the heap when stored in a Task.
    /// </summary>
    private readonly record struct ModelBatch(
        ProviderId ProviderId,
        IReadOnlyList<ModelInfo> Models,
        string? Error);
}

/// <summary>
///     Builder implementation for <see cref="IProviderRegistryBuilder" />.
/// </summary>
public sealed class ProviderRegistryBuilder : IProviderRegistryBuilder
{
    private readonly IProviderRegistry _registry;

    /// <summary>
    ///     Construct a builder backed by the supplied registry.
    /// </summary>
    /// <param name="registry">The registry to wrap.</param>
    public ProviderRegistryBuilder(IProviderRegistry registry)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public void AddProvider(Func<ILlmClient> factory)
    {
        var tempClient = factory();
        _registry.Register(tempClient.ProviderId, factory);
    }

    /// <inheritdoc />
    public void AddProvider(ProviderId providerId, Func<ILlmClient> factory) => _registry.Register(providerId, factory);

    /// <inheritdoc />
    public void AddProvider(string providerId, Func<ILlmClient> factory)
    {
        var result = ProviderId.TryCreate(providerId);
        if (result.IsFailure)
        {
            throw new ArgumentException(result.Error, nameof(providerId));
        }

        _registry.Register(result.Value, factory);
    }
}
