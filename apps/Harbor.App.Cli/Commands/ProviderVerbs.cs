using CSharpFunctionalExtensions;
using Harbor.Abstractions.Dsl;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.App.Cli.Hosting;
using Harbor.Ui.Framework.Projection;
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
    internal static async Task<int> RunListProvidersAsync(
        ILogger logger,
        IProviderRegistry? registry = null,
        PanelDescriptorProjector? projector = null)
    {
        ProviderVerbsLog.ListingProviders(logger);
        using var host = registry is null ? HostBuilder.Build() : null;
        registry ??= host!.Services.GetRequiredService<IProviderRegistry>();
        projector ??= ProviderListPanelDescriptor.CreateProjector();
        var ids = registry.GetRegisteredProviderIds();
        ProviderVerbsLog.FoundProviders(logger, ids.Count);
        var state = ProviderListState.From(registry, ids);
        var lines = (IReadOnlyList<string>)projector.Project(state)!;
        foreach (var line in lines) Console.WriteLine(line);
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

/// <summary>One provider row in the list verb's projected state.</summary>
/// <param name="Id">Provider id as printed in the CLI.</param>
/// <param name="Available">Whether the registry could create a client for it.</param>
internal sealed record ProviderListRow(string Id, bool Available);

/// <summary>Immutable input to the panel descriptor that renders the provider list.</summary>
/// <param name="Rows">Rows in registry order, which the CLI prints unchanged.</param>
internal sealed record ProviderListState(IReadOnlyList<ProviderListRow> Rows)
{
    /// <summary>Snapshots the registry ids and their client-creation results.</summary>
    /// <param name="registry">Registry to query.</param>
    /// <param name="ids">Ids to snapshot, in print order.</param>
    internal static ProviderListState From(IProviderRegistry registry, IReadOnlyList<ProviderId> ids)
    {
        var rows = new List<ProviderListRow>(ids.Count);
        foreach (var id in ids)
            rows.Add(new ProviderListRow(id.Value, registry.GetClient(id).IsSuccess));
        return new ProviderListState(rows);
    }
}

/// <summary>
///     Builds the <c>providers</c> verb's lines from a <see cref="ProviderListState" />.
///     Registered as a global rule so the projector resolves it through
///     <see cref="IPanelDescriptor.Match" /> without naming a type at runtime.
/// </summary>
internal sealed class ProviderListPanelDescriptor : IPanelDescriptor
{
    /// <inheritdoc />
    public Type StateType => typeof(ProviderListState);

    /// <inheritdoc />
    public bool Match(object state) => state is ProviderListState;

    /// <inheritdoc />
    public object? Build(object state)
    {
        var list = (ProviderListState)state;
        var lines = new List<string>(list.Rows.Count + 1) { $"Providers ({list.Rows.Count}):" };
        foreach (var row in list.Rows)
            lines.Add($"  [{(row.Available ? "OK" : "FAIL")}] {row.Id}");
        return lines;
    }

    /// <summary>Creates a projector with this descriptor as its global rule.</summary>
    internal static PanelDescriptorProjector CreateProjector()
    {
        var registry = new PanelDescriptorRegistry();
        registry.AddGlobal(new ProviderListPanelDescriptor());
        return new PanelDescriptorProjector(registry);
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

        ModelInfo? selected = Find(models.Value, id.Value, modelId);
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

    private static ModelInfo? Find(IReadOnlyList<ModelInfo> models, ProviderId providerId, string modelId)
    {
        foreach (ModelInfo model in models)
        {
            if (!string.Equals(model.ProviderId, providerId.Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)) return model;
        }

        return null;
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
