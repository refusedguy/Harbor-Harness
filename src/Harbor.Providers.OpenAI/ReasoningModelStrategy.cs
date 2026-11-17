// ReasoningModelStrategy.cs — Strategy (GOF) for issue #1498.
//
// WHY THIS FILE EXISTS
// --------------------
// OpenAiRequestBuilder.IsReasoningModel used to hard-code the o1/o3/o4/gpt-5
// string test directly in the native OpenAI request builder, and OpenAILlmClient
// called that static method to pick the Responses API. That is the same
// Open/Closed violation §OOP-002 removed from the request-payload path: a new
// reasoning-model naming convention required editing two places instead of
// registering a new rule.
//
// The OpenAI-compatible provider already solves this with IProviderCompatFlag
// plus a hand-written ProviderCompatFlags registry (see DeepSeekReasonerCompatFlag,
// which does a model-name Contains("reasoner") as a Strategy). Harbor's layering
// forbids one infrastructure provider project referencing another, so the native
// client cannot consume that assembly. This file is the same pattern, scoped to
// the native provider: each convention is a strategy, and the registry is a
// hand-written array (NativeAOT-safe, no reflection/assembly scanning).
//
// Adding a new reasoning-model family now means adding one IReasoningModelStrategy
// implementation and one entry in ReasoningModelStrategies._all. Neither
// OpenAiRequestBuilder nor OpenAILlmClient changes.

namespace Harbor.Providers.OpenAI;

/// <summary>
///     Strategy (GOF): decide whether a model id names an OpenAI reasoning model.
///     Native-provider counterpart of the compat assembly's IProviderCompatFlag.
/// </summary>
internal interface IReasoningModelStrategy
{
    /// <summary>True when <paramref name="modelId"/> is a reasoning model.</summary>
    bool IsReasoningModel(string modelId);
}

/// <summary>
///     The OpenAI model-name convention this provider has always used: o1/o3/o4
///     prefixes and the gpt-5 substring. Encapsulated as a strategy so a future
///     naming convention is a new registration, not an edit to the client.
/// </summary>
internal sealed class OpenAiModelNameReasoningStrategy : IReasoningModelStrategy
{
    /// <inheritdoc />
    public bool IsReasoningModel(string modelId) =>
        modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     Hand-written registry of reasoning-model strategies. Deliberately an
///     array, not assembly scanning: Harbor targets NativeAOT, where reflection
///     based discovery is banned. A registry can fail silently when a strategy
///     is written but not registered, so ReasoningModelStrategyTests guards both
///     directions (every concrete strategy is registered; the registry is not empty).
/// </summary>
internal static class ReasoningModelStrategies
{
    private static readonly IReasoningModelStrategy[] _all =
    {
        new OpenAiModelNameReasoningStrategy()
    };

    /// <summary>True when any registered strategy recognises <paramref name="modelId"/>.</summary>
    public static bool IsReasoningModel(string modelId)
    {
        for (int i = 0; i < _all.Length; i++)
        {
            if (_all[i].IsReasoningModel(modelId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Registered strategies, exposed only so the registration guard can
    ///     prove that every concrete strategy is wired up and that discovery
    ///     is not vacuous.
    /// </summary>
    internal static IReadOnlyList<IReasoningModelStrategy> All => _all;
}
