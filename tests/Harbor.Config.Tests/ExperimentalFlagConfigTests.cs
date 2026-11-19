using CSharpFunctionalExtensions;
using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     #3397 behavior: the experimental-flag policy is the gate in front of a
///     feature being enabled. The switch lives in <see cref="HarborConfig" />;
///     every flag is evaluated before the config is accepted, and a bad value
///     fails closed instead of half-enabling a feature.
/// </summary>
public class ExperimentalFlagConfigTests
{
    [Test]
    [Arguments("yes", ExperimentalFlagDecision.Enabled, true)]
    [Arguments("off", ExperimentalFlagDecision.Disabled, false)]
    [Arguments(" YES ", ExperimentalFlagDecision.Enabled, true)]
    public async Task ConfiguredFlag_GatesFeatureByValue(
        string raw,
        ExperimentalFlagDecision expected,
        bool enabled)
    {
        var config = new HarborConfig();
        config.Experimental["AGENT_MANAGER"] = raw;

        Result<ExperimentalFlagDecision> decision = config.EvaluateExperimentalFlag("AGENT_MANAGER");

        await Assert.That(decision.IsSuccess).IsTrue();
        await Assert.That(decision.Value).IsEqualTo(expected);
        await Assert.That(config.IsExperimentalEnabled("AGENT_MANAGER")).IsEqualTo(enabled);
    }

    [Test]
    public async Task UnsetFlag_FailsClosedToDisabled()
    {
        var config = new HarborConfig();
        config.Experimental["AGENT_MANAGER"] = "   ";

        await Assert.That(config.Validate().IsSuccess).IsTrue();
        await Assert.That(config.IsExperimentalEnabled("AGENT_MANAGER")).IsFalse();
        await Assert.That(config.IsExperimentalEnabled("NEVER_CONFIGURED")).IsFalse();
    }

    [Test]
    [Arguments("AGENT_MANAGER", "perhaps")]
    [Arguments("bad-name", "1")]
    public async Task InvalidFlag_FailsValidationAndFailsClosed(string flag, string raw)
    {
        var config = new HarborConfig();
        config.Experimental[flag] = raw;

        Result<ExperimentalFlagDecision> decision = config.EvaluateExperimentalFlag(flag);
        Result<HarborConfig> validated = config.Validate();

        await Assert.That(decision.IsFailure).IsTrue();
        await Assert.That(decision.Error).Contains(flag);
        await Assert.That(validated.IsFailure).IsTrue();
        await Assert.That(validated.Error).Contains(flag);
        await Assert.That(config.IsExperimentalEnabled(flag)).IsFalse();
    }

    [Test]
    public async Task ExperimentalSwitch_SurvivesRawConfigRoundTrip()
    {
        var config = new HarborConfig();
        config.Experimental["SKILL_FRESHNESS"] = "true";

        RawConfigDto raw = config.ToRaw();
        Result<HarborConfig> reloaded = ConfigNormalizer.Normalize(raw);

        await Assert.That(raw.Experimental).IsNotNull();
        await Assert.That(reloaded.IsSuccess).IsTrue();
        await Assert.That(reloaded.Value.IsExperimentalEnabled("SKILL_FRESHNESS")).IsTrue();
    }

    [Test]
    public async Task EmptyExperimentalSection_IsNotWrittenToConfig()
    {
        var config = new HarborConfig();

        RawConfigDto raw = config.ToRaw();

        await Assert.That(raw.Experimental).IsNull();
    }
}
