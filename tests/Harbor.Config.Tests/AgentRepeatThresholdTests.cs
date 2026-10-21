using Harbor.Abstractions.Agents;
using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     Issue #1756: the repeat-guard threshold a configuration asks for must
///     REACH the agent the run uses.
/// </summary>
/// <remarks>
///     The lesson is #1118's: <c>maxSteps</c> was settable, printed and
///     persisted, and reached no agent at all — the REPL ran to the agent's own
///     default whatever the file said. A configured threshold that stops at the
///     config file is the same defect with a different number.
/// </remarks>
public class AgentRepeatThresholdTests
{
    /// <summary>An agent of the shape the composition root declares.</summary>
    private static AgentDefinition Agent() => AgentDefinition.CodeDefault("test-model", "test");

    [Test]
    public async Task Apply_DefaultConfiguration_LeavesTheAgentAlone()
    {
        var config = HarborConfig.Default;

        await Assert.That(AgentRepeatThreshold.Configured(config)).IsNull()
            .Because("the default is the value every agent already carries, so it says nothing the agent does not");
        await Assert.That(AgentRepeatThreshold.Apply(config, Agent()).RepeatToolThreshold)
            .IsEqualTo(AgentDefinition.DefaultRepeatToolThreshold);
    }

    [Test]
    public async Task Apply_ConfiguredThreshold_ReachesTheAgent()
    {
        var config = HarborConfig.Default;
        config.Run = config.Run with { RepeatToolThreshold = 5 };

        await Assert.That(AgentRepeatThreshold.Configured(config).HasValue).IsTrue();
        await Assert.That(AgentRepeatThreshold.Apply(config, Agent()).RepeatToolThreshold).IsEqualTo(5)
            .Because("the configured value is the threshold of the run");
    }

    [Test]
    public async Task Apply_Zero_ReachesTheAgent_AndTurnsTheGuardOff()
    {
        var config = HarborConfig.Default;
        config.Run = config.Run with { RepeatToolThreshold = 0 };

        await Assert.That(AgentRepeatThreshold.Apply(config, Agent()).RepeatToolThreshold).IsEqualTo(0)
            .Because("zero is how a user turns the guard off, and it has to reach the agent to do that");
    }

    [Test]
    public async Task Normalize_ReadsTheThresholdFromTheFile()
    {
        var raw = new RawConfigDto { RepeatToolThreshold = 7 };

        var normalized = ConfigNormalizer.Normalize(raw);

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.Run.RepeatToolThreshold).IsEqualTo(7)
            .Because("the key in config.json is the one the user edits");
    }

    [Test]
    public async Task Normalize_AbsentKey_KeepsTheDefault()
    {
        var normalized = ConfigNormalizer.Normalize(new RawConfigDto());

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.Run.RepeatToolThreshold)
            .IsEqualTo(AgentDefinition.DefaultRepeatToolThreshold);
    }

    [Test]
    public async Task Validate_RejectsANegativeThreshold()
    {
        var config = HarborConfig.Default;
        config.Run = config.Run with { RepeatToolThreshold = -1 };

        var validation = config.Validate();

        await Assert.That(validation.IsFailure).IsTrue();
        await Assert.That(validation.Error).Contains("run.repeatToolThreshold");
    }
}
