// ReasoningModelStrategyTests.cs — non-vacuity + behaviour guard for issue #1498.
//
// ReasoningModelStrategies is a hand-written registry (NativeAOT-safe), and a
// registry has one failure mode a static method did not: a strategy you write
// and forget to register. Nothing throws — the request simply goes down the
// /chat/completions path with max_tokens and temperature, and the reasoning
// model answers with a 400. These tests guard both directions, the same way
// ProviderCompatFlagTests guards ProviderCompatFlags.

using System.Reflection;
using Harbor.Providers.OpenAI;

namespace Harbor.Providers.Tests;

public class ReasoningModelStrategyTests
{
    /// <summary>Every concrete IReasoningModelStrategy in the OpenAI provider assembly.</summary>
    private static IReadOnlyList<Type> ConcreteStrategyTypes() =>
        ProviderTestReflection.ConcreteTypes<IReasoningModelStrategy>();

    [Test]
    public async Task EveryConcreteReasoningStrategy_IsRegistered()
    {
        var unregistered = new List<string>();
        HashSet<Type> registered = ReasoningModelStrategies.All.Select(s => s.GetType()).ToHashSet();

        foreach (Type type in ConcreteStrategyTypes())
        {
            if (!registered.Contains(type))
            {
                unregistered.Add(type.Name);
            }
        }

        await Assert.That(unregistered).IsEmpty()
            .Because("these IReasoningModelStrategy implementations are never handed out by "
                     + "ReasoningModelStrategies, so writing one has no effect: "
                     + string.Join(", ", unregistered)
                     + ". Add each to the _all array in ReasoningModelStrategies.");
    }

    [Test]
    public async Task Discovery_FindsTheShippedStrategy_AndRegistryIsNonEmpty()
    {
        await Assert.That(ConcreteStrategyTypes().Count).IsGreaterThanOrEqualTo(1)
            .Because("reflection finding no concrete strategy would make the registration guard vacuous");

        await Assert.That(ReasoningModelStrategies.All.Count).IsGreaterThanOrEqualTo(1)
            .Because("an empty registry would make every model look non-reasoning and both guards pass for the wrong reason");
    }

    [Test]
    public async Task Registry_PreservesTheOriginalReasoningModelDetection()
    {
        string[] reasoning = ["o1", "o1-preview", "o3", "o3-mini", "o4-mini", "gpt-5", "GPT-5-turbo"];
        string[] ordinary = ["gpt-4o", "gpt-4.1", "gpt-4o-mini", "text-davinci-003"];

        foreach (string model in reasoning)
        {
            await Assert.That(ReasoningModelStrategies.IsReasoningModel(model)).IsTrue()
                .Because($"'{model}' is a reasoning model under the original o1/o3/o4/gpt-5 convention");
        }

        foreach (string model in ordinary)
        {
            await Assert.That(ReasoningModelStrategies.IsReasoningModel(model)).IsFalse()
                .Because($"'{model}' is not a reasoning model and must keep the /chat/completions path");
        }
    }
}
