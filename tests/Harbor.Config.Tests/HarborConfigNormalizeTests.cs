using CSharpFunctionalExtensions;
using Harbor.Core.Configuration;
namespace Harbor.Config.Tests;
/// <summary>
///     ROP-B П.13/П.14: ConfigNormalizer resolves canonical → legacy aliases as
///     a Maybe ladder, fails fast on invalid mandatory parses, and
///     HarborConfig.Validate aggregates every section error via Result.Combine.
/// </summary>
public class HarborConfigNormalizeTests
{
    [Test]
    public async Task Normalize_LegacyAliasesUsedAsFallback()
    {
        var raw = new RawConfigDto { DefaultProvider = "ollama", DefaultModel = "ollama/llama3.2" };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Provider).IsEqualTo("ollama");
        await Assert.That(result.Value.Model).IsEqualTo("ollama/llama3.2");
    }

    [Test]
    public async Task Normalize_CanonicalFieldsWinOverLegacy()
    {
        var raw = new RawConfigDto { Provider = "openai", DefaultProvider = "ollama" };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Provider).IsEqualTo("openai");
    }

    [Test]
    public async Task Normalize_EmptyCanonicalAndLegacy_SkipsSection()
    {
        var raw = new RawConfigDto { Provider = "", DefaultProvider = "" };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsSuccess).IsTrue();
        // Section skipped → the HarborConfig default identity survives untouched.
        await Assert.That(result.Value.Provider).IsEqualTo(IdentityConfig.FallbackProvider);
    }

    [Test]
    public async Task Normalize_InvalidModel_FailsWithParseError()
    {
        var raw = new RawConfigDto { Model = "no-slash-model" };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Invalid model reference");
    }

    [Test]
    public async Task Normalize_InvalidSecondaryModel_Fails()
    {
        var raw = new RawConfigDto { SecondaryModel = "also-no-slash" };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Validate_ValidDefaultConfig_Succeeds()
    {
        var config = new HarborConfig();

        var result = config.Validate();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(ReferenceEquals(result.Value, config)).IsTrue();
    }

    [Test]
    public async Task Validate_MultipleBadSections_AggregatesAllErrors()
    {
        var config = new HarborConfig
        {
            Cost = new CostConfig(-5m),
            Run = new RunLimitsConfig(0)
        };

        var result = config.Validate();

        // Result.Combine surfaces every failure (CSE 3.7 native separator: ", ").
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("cost.limit must be >= 0");
        await Assert.That(result.Error).Contains("run.maxSteps must be in [1, 1000]");
        await Assert.That(result.Error).Contains(", ");
    }
}
