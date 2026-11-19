using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.App.Cli.Hosting;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor providers</c> / <c>harbor models [provider]</c> — list
///     registered providers and their models. Extracted from
///     <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class ProviderVerbs
{
    internal static async Task<int> RunListProvidersAsync(ILogger logger)
    {
        ProviderVerbsLog.ListingProviders(logger);
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        var ids = providers.GetRegisteredProviderIds();
        ProviderVerbsLog.FoundProviders(logger, ids.Count);
        Console.WriteLine($"Providers ({ids.Count}):");
        foreach (var id in ids)
        {
            var r = providers.GetClient(id);
            Console.WriteLine($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
        }
        await Task.CompletedTask;
        return 0;
    }

    internal static async Task<int> RunListModelsAsync(ILogger logger, string? providerId)
    {
        ProviderVerbsLog.ListingModels(logger, providerId ?? "(all)");
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        if (!string.IsNullOrEmpty(providerId))
        {
            var pidResult = ProviderId.TryCreate(providerId);
            if (pidResult.IsFailure)
            {
                Console.Error.WriteLine(pidResult.Error);
                return 1;
            }
            var clientResult = providers.GetClient(pidResult.Value);
            if (clientResult.IsFailure)
            {
                Console.Error.WriteLine(clientResult.Error);
                return 1;
            }
            var modelsResult = await clientResult.Value.GetModelsAsync().ConfigureAwait(false);
            if (modelsResult.IsFailure)
            {
                Console.Error.WriteLine(modelsResult.Error);
                return 1;
            }
            ProviderVerbsLog.FoundModels(logger, modelsResult.Value.Count, providerId);
            Console.WriteLine($"Models for {providerId}:");
            foreach (var m in modelsResult.Value) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
            return 0;
        }
        var allResult = await providers.GetAllModelsAsync().ConfigureAwait(false);
        if (allResult.IsFailure)
        {
            Console.Error.WriteLine(allResult.Error);
            return 1;
        }
        ProviderVerbsLog.FoundTotalModels(logger, allResult.Value.Count);
        Console.WriteLine($"All models ({allResult.Value.Count}):");
        foreach (var g in allResult.Value.GroupBy(m => m.ProviderId))
        {
            Console.WriteLine($"\n{g.Key}:");
            foreach (var m in g) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
        }
        return 0;
    }
}

/// <summary>
///     Reads the live capability facts for one model from the provider registry
///     and prints the pure preflight verdict (#2686 Q07). The validator owns
///     every rule; this runner only resolves the model and shapes the output.
///     It lives beside <see cref="ProviderVerbs" /> because that file already
///     consumes <see cref="IProviderRegistry" />: a new consumer file would
///     change the committed seam graph.
/// </summary>
internal static class ProviderOptionRunner
{
    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        IProviderRegistry? registry = null)
    {
        Result<PreflightRequest> parsed = ProviderOptionPreflight.ParseOptions(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine($"models preflight: {parsed.Error}");
            return 1;
        }

        PreflightRequest request = parsed.Value;
        string providerId = request.ProviderId;
        string modelId = request.ModelId;
        Result<ProviderId> id = ProviderId.TryCreate(providerId);
        if (id.IsFailure)
        {
            Console.Error.WriteLine($"models preflight: {id.Error}");
            return 1;
        }

        if (registry is null)
        {
            var host = HostBuilder.Build();
            registry = host.Services.GetRequiredService<IProviderRegistry>();
        }

        Result<IReadOnlyList<ModelInfo>> models = await registry.GetAllModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models.IsFailure)
        {
            Console.Error.WriteLine($"models preflight: {models.Error}");
            return 1;
        }

        ModelInfo? selected = ResolveModel(models.Value, id.Value.Value, modelId);
        if (selected is null)
        {
            Console.Error.WriteLine($"models preflight: no model '{modelId}' for provider '{providerId}'.");
            return 1;
        }

        var profile = new ModelOptionProfile(
            selected.SupportsToolUse,
            selected.SupportsVision,
            selected.SupportsReasoning,
            selected.MaxOutputTokens,
            selected.ContextWindow);

        Result outcome = ProviderOptionPreflight.Validate(profile, request.Options);
        Console.Out.WriteLine(ProviderOptionPreflight.FormatResult(providerId, modelId, outcome));
        return outcome.IsFailure ? 1 : 0;
    }

    /// <summary>
    ///     Resolve one model from the aggregated catalog through the shared
    ///     <see cref="ModelCatalogSearch" /> primitive (#3793). The primitive
    ///     filters by provider and by an id/name substring; preflight needs the
    ///     old strict equality on the id, so the exact match is picked from the
    ///     returned page before mapping back to the rich <see cref="ModelInfo" />.
    /// </summary>
    private static ModelInfo? ResolveModel(IReadOnlyList<ModelInfo> models, string providerId, string modelId)
    {
        if (!ModelCatalogSearch.TryCreate(
                modelId, providerId, ModelCatalogSearch.DefaultLimit, 0, out var query))
            return null;

        ModelSearchPage page = ModelCatalogSearch.Search(
            models.Select(m => new ModelSearchEntry(m.Id, m.ProviderId, m.DisplayName)).ToArray(),
            query);

        ModelSearchEntry? exact = null;
        foreach (ModelSearchEntry entry in page.Items)
        {
            if (string.Equals(entry.Id, modelId, StringComparison.OrdinalIgnoreCase))
            {
                exact = entry;
                break;
            }
        }

        if (exact is null) return null;
        ModelSearchEntry match = exact.Value;
        return models.TryFirst(m =>
                string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Id, match.Id, StringComparison.OrdinalIgnoreCase))
            .GetValueOrDefault();
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="ProviderVerbs" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ProviderVerbsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Listing providers")]
    public static partial void ListingProviders(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Found {Count} registered providers")]
    public static partial void FoundProviders(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Listing models for provider {Provider}")]
    public static partial void ListingModels(ILogger logger, string provider);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Found {Count} models for {Provider}")]
    public static partial void FoundModels(ILogger logger, int count, string? provider);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Found {Count} total models")]
    public static partial void FoundTotalModels(ILogger logger, int count);
}
