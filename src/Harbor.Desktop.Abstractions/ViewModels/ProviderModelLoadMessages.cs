namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Wording of the provider/model failure surface, shared by the picker and
///     the browser so both answer the same question the same way: a provider
///     that <i>failed to load</i> is never presented like a provider that
///     <i>returned no models</i>.
/// </summary>
public static class ProviderModelLoadMessages
{
    /// <summary>Hint appended whenever a provider answered with zero models.</summary>
    public const string LocalProviderHint = "If this is a local provider (e.g. Ollama), make sure it's running.";

    /// <summary>Message for a model fetch that failed.</summary>
    /// <param name="error">Underlying failure text (already non-empty).</param>
    public static string LoadFailed(string error) => $"Could not load models: {error}";

    /// <summary>Message for a provider that answered but returned nothing.</summary>
    /// <param name="providerId">Provider that returned an empty catalog.</param>
    public static string NoModelsForProvider(string providerId) =>
        $"No models returned by provider '{providerId}'. {LocalProviderHint}";

    /// <summary>Message for a successful fetch that yielded no models anywhere.</summary>
    public static string NoModelsAnyProvider() =>
        $"No models returned by any provider. {LocalProviderHint}";
}
