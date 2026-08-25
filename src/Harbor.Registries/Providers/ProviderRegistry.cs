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
    // Architecture audit v2 §CONCURRENCY-001 (RESOLVED): InvalidateFrozenSnapshot
    // previously took `lock(_frozenLock)` to null a single reference field.
    // Under plugin hot-reload or runtime provider re-registration this
    // serialised every Register/Unregister call and forced concurrent GetClient
    // readers through the ConcurrentDictionary slow path while the lock was
    // held. Replaced with a single Interlocked.Exchange on a volatile field —
    // see ToolRegistry.cs for the full rationale.
    private readonly ConcurrentDictionary<ProviderId, Lazy<ILlmClient>> _clients = new();
    private readonly ILogger<ProviderRegistry> _logger;
    private readonly ConcurrentDictionary<ProviderId, IReadOnlyList<ModelInfo>> _modelCache = new();
    /// <summary>
    ///     The frozen lookup table for fast lock-free reads; <see langword="null" /> until
    ///     <see cref="Freeze" /> is called. Marked <c>volatile</c> so reads have
    ///     acquire semantics (the lock-free <see cref="InvalidateFrozenSnapshot" />
    ///     publishes null via <see cref="Interlocked.Exchange(ref object?, object?)" />
    ///     which already implies a full barrier on success).
    /// </summary>
    private volatile FrozenDictionary<ProviderId, Lazy<ILlmClient>>? _frozenClients;

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
        // ROP-B П.23: both lookup branches share one Instantiate seam — the
        // duplicated catch blocks (and the error text they format) live in
        // exactly one place now.
        var frozen = _frozenClients;
        if (frozen is not null && frozen.TryGetValue(providerId, out var lazy))
        {
            return Instantiate(lazy, providerId);
        }

        if (_clients.TryGetValue(providerId, out var lazyClient))
        {
            return Instantiate(lazyClient, providerId);
        }

        _logger.LogDebug("Provider not registered: {ProviderId}", providerId);
        return Result.Failure<ILlmClient>($"Provider '{providerId}' is not registered.");
    }

    /// <summary>Force the lazy factory and classify any instantiation failure.</summary>
    private Result<ILlmClient> Instantiate(Lazy<ILlmClient> lazy, ProviderId providerId) =>
        Result.Success(lazy)
            .MapTry(static l => l.Value,
                ex => $"Failed to instantiate provider '{providerId}': {ex.Message}")
            .TapError(e => _logger.LogWarning(
                "Provider instantiation failed: {ProviderId}: {Error}", providerId, e));

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
            // Kick off all provider queries in parallel. Each provider gets its
            // own 5-second timeout so a missing local provider (e.g. Ollama
            // not running) doesn't cancel the entire fan-out.
            const int PerProviderTimeoutMs = 5000;
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

                        using var perProviderCts = new CancellationTokenSource(PerProviderTimeoutMs);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, perProviderCts.Token);
                        var models = await client.Value.GetModelsAsync(linkedCts.Token).ConfigureAwait(false);
                        if (models.IsFailure)
                        {
                            return new ModelBatch(pid, Array.Empty<ModelInfo>(), models.Error);
                        }

                        _modelCache[pid] = models.Value;
                        return new ModelBatch(pid, models.Value, null);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogWarning(ex, "Model fetch timed out for provider: {ProviderId}", pid);
                        return new ModelBatch(pid, Array.Empty<ModelInfo>(), "timeout");
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
        // Atomic publish: readers observe either the prior snapshot or the new
        // one — never a half-built dictionary. Volatile write has release
        // semantics, so the frozen dictionary is fully visible before the
        // reference is published.
        _frozenClients = _clients.ToFrozenDictionary();
    }

    private void InvalidateFrozenSnapshot()
    {
        // Lock-free invalidation — see ToolRegistry.InvalidateFrozenSnapshot
        // for the full rationale. Concurrent GetClient readers may observe the
        // stale-but-consistent prior snapshot or null (falling through to the
        // ConcurrentDictionary slow path); both outcomes are safe.
        Interlocked.Exchange(ref _frozenClients, null);
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
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    ///     Construct a builder backed by the supplied registry.
    /// </summary>
    /// <param name="registry">The registry to wrap.</param>
    /// <param name="loggerFactory">Logger factory for provider construction.</param>
    public ProviderRegistryBuilder(IProviderRegistry registry, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    // S1133 fires on [Obsolete] asking "remember to remove this deprecated code someday".
    // We will — in v0.6, per the [Obsolete] message. Suppress until then.
#pragma warning disable S1133
    [Obsolete("Use AddProvider(ProviderId, Func<ILlmClient>) or AddProvider(string, Func<ILlmClient>) " +
              "to avoid eager factory invocation. This overload instantiates the client once at " +
              "registration time just to read ProviderId, which defeats the Lazy<ILlmClient> wrapping " +
              "in ProviderRegistry. See Architecture audit v2 §3.4.")]
    public void AddProvider(Func<ILlmClient> factory)
#pragma warning restore S1133
    {
        // Architecture audit v2 §3.4: this overload eagerly invokes the factory
        // just to read ProviderId. The ProviderRegistry wraps the same factory
        // in a Lazy<ILlmClient> so subsequent invocations are lazy, but the
        // first call constructs the full client object (HttpClient config,
        // auth resolver, logger) at startup — even if the provider is never
        // used during the session.
        //
        // Prefer the explicit-id overloads:
        //   AddProvider(ProviderId providerId, Func<ILlmClient> factory)
        //   AddProvider(string providerId, Func<ILlmClient> factory)
        // which never invoke the factory until a client is actually requested.
        var tempClient = factory();
        _registry.Register(tempClient.ProviderId, factory);
    }

    /// <inheritdoc />
    public void AddProvider(ProviderId providerId, Func<ILlmClient> factory) => _registry.Register(providerId, factory);

    /// <summary>
    ///     Register a provider by its string id. Parses <paramref name="providerId" />
    ///     via <see cref="ProviderId.TryCreate" /> and delegates to
    ///     <see cref="AddProvider(ProviderId, Func{ILlmClient})" />. The factory
    ///     is never invoked at registration time — see §3.4 of the architecture
    ///     audit for the lazy-init rationale.
    /// </summary>
    /// <param name="providerId">The string form of the provider id (e.g. <c>ollama</c>).</param>
    /// <param name="factory">The factory that constructs the <see cref="ILlmClient" /> on first use.</param>
    /// <exception cref="ArgumentException"><paramref name="providerId" /> is not a valid <see cref="ProviderId" />.</exception>
    public void AddProvider(string providerId, Func<ILlmClient> factory)
    {
        var result = ProviderId.TryCreate(providerId);
        if (result.IsFailure)
        {
            throw new ArgumentException(result.Error, nameof(providerId));
        }

        _registry.Register(result.Value, factory);
    }

    /// <summary>
    ///     Register a provider via a factory interface.
    /// </summary>
    /// <param name="factory">The factory producing the client instance.</param>
    public void AddProvider(IProviderFactory factory)
    {
        var pid = factory.ProviderId;
        _registry.Register(pid, () => factory.CreateClient(_loggerFactory));
    }
}
