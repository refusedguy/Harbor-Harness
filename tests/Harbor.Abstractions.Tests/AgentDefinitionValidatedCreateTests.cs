using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Issue #2576: the built-in agent factories validate the provider and
///     model through <see cref="AgentDefinition.TryCreate" /> instead of
///     writing arbitrary strings into the record.
/// </summary>
public class AgentDefinitionValidatedCreateTests
{
    private static Result<AgentDefinition> Define(string model, string providerId)
        => AgentDefinition.TryCreate(
            AgentName.Create("probe"),
            "Probe",
            "Validation probe",
            model,
            providerId,
            PermissionRuleset.Default);

    [Test]
    public async Task TryCreate_NormalizesTheProviderThroughProviderId()
    {
        // "KiloCode" used to reach the definition verbatim and only failed on
        // the first prompt, when the registry looked up "KiloCode".
        AgentDefinition agent = Define("tencent/hy3:free", "KiloCode")
            .Match(definition => definition, error => throw new InvalidOperationException(error));

        await Assert.That(agent.ProviderId).IsEqualTo("kilocode");
        await Assert.That(agent.Model).IsEqualTo("tencent/hy3:free");
    }

    [Test]
    public async Task TryCreate_KeepsABareMultiSegmentModelIdIntact()
    {
        AgentDefinition agent = Define("tencent/hy3:free", "kilocode")
            .Match(definition => definition, error => throw new InvalidOperationException(error));

        await Assert.That(agent.ProviderId).IsEqualTo("kilocode");
        await Assert.That(agent.Model).IsEqualTo("tencent/hy3:free");
    }

    [Test]
    public async Task TryCreate_RejectsABlankProvider()
    {
        await Assert.That(Define("some-model", "  ").IsFailure).IsTrue();
    }

    [Test]
    public async Task TryCreate_RejectsAProviderWithInvalidCharacters()
    {
        await Assert.That(Define("some-model", "kilo.code").IsFailure).IsTrue();
    }

    [Test]
    public async Task TryCreate_RejectsABlankModel()
    {
        await Assert.That(Define("  ", "kilocode").IsFailure).IsTrue();
    }

    [Test]
    public async Task CodeDefault_UsesTheSameValidatedPath()
    {
        AgentDefinition agent = AgentDefinition.CodeDefault("some-model", "KiloCode");

        await Assert.That(agent.ProviderId).IsEqualTo("kilocode");
        await Assert.That(agent.Model).IsEqualTo("some-model");
        await Assert.That(agent.MaxSteps).IsEqualTo(50);
    }

    [Test]
    public async Task EveryBuiltinFactory_KeepsItsDeclaredIdentity()
    {
        AgentDefinition[] agents =
        [
            AgentDefinition.CodeDefault("m", "p"),
            AgentDefinition.PlanDefault("m", "p"),
            AgentDefinition.ExploreDefault("m", "p"),
            AgentDefinition.AskDefault("m", "p"),
            AgentDefinition.ArchitectDefault("m", "p"),
            AgentDefinition.DebugDefault("m", "p"),
            AgentDefinition.OrchestratorDefault("m", "p")
        ];

        await Assert.That(agents.Select(static a => a.Name.Value)).IsEquivalentTo(
            ["code", "plan", "explore", "ask", "architect", "debug", "orchestrator"]);
        await Assert.That(agents.All(static a => a.ProviderId == "p")).IsTrue();
    }
}
