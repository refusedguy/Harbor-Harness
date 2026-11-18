using ConsoleAppFramework;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.App.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the pre-turn model option check (#2686 Q07).</summary>
internal static class CafProviderOptionCommands
{
    /// <summary>Validate options for a model before a turn: models preflight &lt;provider&gt; &lt;model&gt; [--option name=value].</summary>
    public static Task<int> Preflight([Argument] params string[] args) =>
        ProviderOptionRunner.RunAsync(args, CancellationToken.None);
}

/// <summary>
///     Reads the live capability facts for one model from the provider registry
///     and prints the pure preflight verdict. The validator owns every rule;
///     this runner only resolves the model and shapes the output.
/// </summary>
internal static class ProviderOptionRunner
{
    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        IProviderRegistry? registry = null)
    {
        Result<IReadOnlyList<OptionAssignment>> parsed = ProviderOptionPreflight.ParseOptions(args, out string? providerId, out string? modelId);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine($"models preflight: {parsed.Error}");
            return 1;
        }

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

        ModelInfo? selected = Find(models.Value, id.Value, modelId!);
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

        Result outcome = ProviderOptionPreflight.Validate(profile, parsed.Value);
        Print(ProviderOptionPreflight.FormatResult(providerId!, modelId!, outcome));
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

    private static void Print(string json) => Console.Out.WriteLine(json);
}
