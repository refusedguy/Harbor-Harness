using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     ROP boundary #101: HarborConfig.TrySet* diagnostics — invalid values
///     report the parse reason as a failure while keeping the legacy
///     silent-fallback setter behavior (unset → built-in default).
/// </summary>
public class HarborConfigTrySetTests
{
    [Test]
    public async Task TrySetProvider_InvalidValue_FailsAndFallsBack()
    {
        var config = new HarborConfig();
        var result = config.TrySetProvider("!!!not-valid!!!");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(config.Provider).IsEqualTo(IdentityConfig.FallbackProvider);
    }

    [Test]
    public async Task TrySetProvider_ValidValue_Succeeds()
    {
        var config = new HarborConfig();
        var result = config.TrySetProvider("ollama");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(config.Provider).IsEqualTo("ollama");
    }

    [Test]
    public async Task TrySetModel_InvalidValue_FailsAndFallsBack()
    {
        var config = new HarborConfig();
        var result = config.TrySetModel("no-slash-model");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(config.Model).IsEqualTo(IdentityConfig.FallbackModel);
    }

    [Test]
    public async Task TrySetModel_ValidValue_Succeeds()
    {
        var config = new HarborConfig();
        var result = config.TrySetModel("ollama/llama3.2");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(config.Model).IsEqualTo("ollama/llama3.2");
    }

    [Test]
    public async Task TrySetAgent_BlankValue_FailsAndFallsBack()
    {
        var config = new HarborConfig();
        var result = config.TrySetAgent("");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(config.Agent).IsEqualTo(IdentityConfig.FallbackAgent);
    }

    [Test]
    public async Task Setter_InvalidValue_MatchesTrySetFallback()
    {
        // The plain setters keep their legacy silent-fallback behavior.
        var viaSetter = new HarborConfig { Provider = "!!!not-valid!!!" };
        var viaTrySet = new HarborConfig();
        _ = viaTrySet.TrySetProvider("!!!not-valid!!!");
        await Assert.That(viaSetter.Provider).IsEqualTo(viaTrySet.Provider);
    }
}
