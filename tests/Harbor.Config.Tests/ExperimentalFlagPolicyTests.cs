using CSharpFunctionalExtensions;
using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     #2680 F172 / #3397. <see cref="ExperimentalFlagPolicy" /> is a pure
///     policy: these tests pin the canonical HARBOR_EXPERIMENTAL_* name and the
///     opt-in value resolution without reading or setting any process
///     environment variable.
/// </summary>
public class ExperimentalFlagPolicyTests
{
    [Test]
    public async Task PrefixIsStable()
    {
        await Assert.That(ExperimentalFlagPolicy.Prefix).IsEqualTo("HARBOR_EXPERIMENTAL_");
    }

    [Test]
    public async Task CanonicalNameIsBuiltFromAValidFlag()
    {
        string? name = ExperimentalFlagPolicy.VariableName("AGENT_MANAGER");
        await Assert.That(name).IsEqualTo("HARBOR_EXPERIMENTAL_AGENT_MANAGER");
    }

    [Test]
    [Arguments("lowercase")]
    [Arguments("TRAILING_")]
    [Arguments("HAS-DASH")]
    [Arguments("HAS SPACE")]
    [Arguments("1LEADING")]
    public async Task InvalidFlagNamesAreRejected(string flag)
    {
        await Assert.That(ExperimentalFlagPolicy.VariableName(flag)).IsNull();
        await Assert.That(ExperimentalFlagPolicy.IsValidFlagName(flag)).IsFalse();
    }

    [Test]
    public async Task DigitsAndInnerUnderscoresAreAllowed()
    {
        await Assert.That(ExperimentalFlagPolicy.IsValidFlagName("AOT2_MODE")).IsTrue();
        await Assert.That(ExperimentalFlagPolicy.VariableName("AOT2_MODE"))
            .IsEqualTo("HARBOR_EXPERIMENTAL_AOT2_MODE");
    }

    [Test]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments(" YES ")]
    [Arguments("On")]
    public async Task TruthyValuesEnableTheFlag(string raw)
    {
        await Assert.That(ExperimentalFlagPolicy.Classify(raw))
            .IsEqualTo(ExperimentalFlagDecision.Enabled);
    }

    [Test]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("no")]
    [Arguments("off")]
    public async Task FalsyValuesDisableTheFlag(string raw)
    {
        await Assert.That(ExperimentalFlagPolicy.IsEnabled(raw)).IsFalse();
    }

    [Test]
    public async Task UnsetAndBlankValuesDefaultToDisabled()
    {
        await Assert.That(ExperimentalFlagPolicy.Classify(null))
            .IsEqualTo(ExperimentalFlagDecision.Disabled);
        await Assert.That(ExperimentalFlagPolicy.IsEnabled("  \t ")).IsFalse();
    }

    [Test]
    public async Task UnknownValueIsInvalidAndFailsClosed()
    {
        await Assert.That(ExperimentalFlagPolicy.Classify("perhaps"))
            .IsEqualTo(ExperimentalFlagDecision.Invalid);
        await Assert.That(ExperimentalFlagPolicy.TryParse("perhaps", out bool enabled)).IsFalse();
        await Assert.That(enabled).IsFalse();
    }

    [Test]
    public async Task TryParseSeparatesAValidDenyFromBadInput()
    {
        bool validOff = ExperimentalFlagPolicy.TryParse("off", out bool off);
        bool validNonsense = ExperimentalFlagPolicy.TryParse("nonsense", out bool _);

        await Assert.That(validOff && !off).IsTrue();
        await Assert.That(validNonsense).IsFalse();
    }

    [Test]
    public async Task EvaluateReturnsTheDecisionForAWellFormedFlag()
    {
        Result<ExperimentalFlagDecision> result = ExperimentalFlagPolicy.Evaluate("AGENT_MANAGER", "yes");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(ExperimentalFlagDecision.Enabled);
    }

    [Test]
    public async Task EvaluateRejectsAMalformedFlagName()
    {
        Result<ExperimentalFlagDecision> result = ExperimentalFlagPolicy.Evaluate("bad-name", "1");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("bad-name");
    }

    [Test]
    public async Task EvaluateRejectsAnUnrecognisedValue()
    {
        Result<ExperimentalFlagDecision> result = ExperimentalFlagPolicy.Evaluate("AGENT_MANAGER", "perhaps");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("AGENT_MANAGER");
        await Assert.That(result.Error).Contains("perhaps");
    }
}
