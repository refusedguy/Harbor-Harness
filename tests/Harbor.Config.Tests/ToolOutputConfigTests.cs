using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     The tool-output budget section (#1748): the inline budget per tool
///     result, the TTL of a spilled output, and the round trip through the
///     persisted config shape.
/// </summary>
public class ToolOutputConfigTests
{
    [Test]
    public async Task Default_MatchesTheSpillPolicyDefaults()
    {
        // One home for the numbers: the config default and the context
        // boundary's default are the SAME constant, so the two cannot drift.
        await Assert.That(ToolOutputConfig.Default.SpillMaxChars)
            .IsEqualTo(ToolOutputSpillPolicy.DefaultMaxInlineChars);
        await Assert.That(ToolOutputConfig.Default.SpillTtlHours)
            .IsEqualTo(ToolOutputSpillPolicy.DefaultTtlHours);
    }

    [Test]
    public async Task Normalize_AbsentSection_KeepsTheDefaults()
    {
        var result = ConfigNormalizer.Normalize(new RawConfigDto());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToolOutput.SpillMaxChars)
            .IsEqualTo(ToolOutputConfig.Default.SpillMaxChars);
        await Assert.That(result.Value.ToolOutput.SpillTtlHours)
            .IsEqualTo(ToolOutputConfig.Default.SpillTtlHours);
    }

    [Test]
    public async Task Normalize_PresentSection_IsHonoured()
    {
        var raw = new RawConfigDto { ToolOutput = new ToolOutputConfig(8000, 48) };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToolOutput.SpillMaxChars).IsEqualTo(8000);
        await Assert.That(result.Value.ToolOutput.SpillTtlHours).IsEqualTo(48);
    }

    [Test]
    public async Task ToRaw_DefaultSection_IsNotPersisted()
    {
        // The knob noise stays out of config.json for users who never touched
        // it: the section is written only when it diverges from the defaults.
        var raw = new HarborConfig().ToRaw();

        await Assert.That(raw.ToolOutput).IsNull();
    }

    [Test]
    public async Task ToRaw_DivergedSection_IsPersisted()
    {
        var config = new HarborConfig { ToolOutput = new ToolOutputConfig(8000, 48) };

        var raw = config.ToRaw();

        await Assert.That(raw.ToolOutput).IsNotNull();
        await Assert.That(raw.ToolOutput!.SpillMaxChars).IsEqualTo(8000);
    }

    [Test]
    public async Task Validate_NegativeBudget_Fails()
    {
        var config = new HarborConfig { ToolOutput = new ToolOutputConfig(-1, 24) };

        var result = config.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("spillMaxChars");
    }

    [Test]
    public async Task Validate_ZeroTtl_Fails()
    {
        var config = new HarborConfig { ToolOutput = new ToolOutputConfig(2000, 0) };

        var result = config.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("spillTtlHours");
    }
}
