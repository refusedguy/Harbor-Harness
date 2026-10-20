using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     #1766: the cap on concurrent parallel-safe tool calls is a CONFIG
///     value, not a constant buried in the dispatcher — it is read from
///     <c>config.json</c>, validated, and persisted back.
/// </summary>
public class ToolSchedulingConfigTests
{
    [Test]
    public async Task Default_IsTen()
    {
        await Assert.That(ToolingConfig.Default.MaxParallelToolCalls).IsEqualTo(10);
        await Assert.That(ToolingConfig.DefaultMaxParallelToolCalls).IsEqualTo(10);
        await Assert.That(ToolingConfig.MinMaxParallelToolCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Normalize_AbsentKey_KeepsTheDefault()
    {
        var result = ConfigNormalizer.Normalize(new RawConfigDto());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Tooling.MaxParallelToolCalls)
            .IsEqualTo(ToolingConfig.DefaultMaxParallelToolCalls);
    }

    [Test]
    public async Task Normalize_PresentKey_Wins()
    {
        var result = ConfigNormalizer.Normalize(new RawConfigDto { MaxParallelToolCalls = 3 });

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Tooling.MaxParallelToolCalls).IsEqualTo(3);
    }

    [Test]
    public async Task Validate_BelowTheFloor_IsRefused()
    {
        var config = new HarborConfig
        {
            Tooling = ToolingConfig.Default with { MaxParallelToolCalls = 0 },
        };

        Result<HarborConfig> result = config.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("maxParallelToolCalls");
    }

    [Test]
    public async Task Validate_AtTheFloor_IsAccepted()
    {
        var config = new HarborConfig
        {
            Tooling = ToolingConfig.Default with { MaxParallelToolCalls = 1 },
        };

        await Assert.That(config.Validate().IsSuccess).IsTrue();
    }

    [Test]
    public async Task ToRaw_KeepsANonDefaultCap_AndDropsTheDefault()
    {
        var custom = new HarborConfig
        {
            Tooling = ToolingConfig.Default with { MaxParallelToolCalls = 4 },
        };
        var plain = new HarborConfig();

        await Assert.That(custom.ToRaw().MaxParallelToolCalls).IsEqualTo(4);
        await Assert.That(plain.ToRaw().MaxParallelToolCalls).IsNull()
            .Because("the default must not add knob noise to config.json.");
    }

    [Test]
    public async Task ToRaw_Normalize_RoundTripsTheCap()
    {
        var original = new HarborConfig
        {
            Tooling = ToolingConfig.Default with { MaxParallelToolCalls = 7 },
        };

        var round = ConfigNormalizer.Normalize(original.ToRaw());

        await Assert.That(round.IsSuccess).IsTrue();
        await Assert.That(round.Value.Tooling.MaxParallelToolCalls).IsEqualTo(7);
    }
}
