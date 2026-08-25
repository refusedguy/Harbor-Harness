using CSharpFunctionalExtensions;
using Harbor.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Tests;
/// <summary>
///     ROP-B П.20: AuthStore.GetApiKeyAsync resolves config → preset env →
///     conventional env as a Compensate chain; the aggregated failure names
///     every env var that would have worked.
/// </summary>
/// <remarks>Serial execution: tests mutate process-wide environment variables.</remarks>
[NotInParallel]
public class AuthStoreTests : IDisposable
{
    private readonly FakeConfigStore _store = new();
    private readonly AuthStore _auth = new(new FakeConfigStore(), NullLogger<AuthStore>.Instance);
    private readonly List<string> _cleanupEnv = [];

    public void Dispose()
    {
        foreach (string name in _cleanupEnv)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private void SetEnv(string name, string value)
    {
        _cleanupEnv.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class FakeConfigStore : IConfigStore
    {
        public string? Key { get; init; }

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new HarborConfig()));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Key is { } key
                ? Result.Success(key)
                : Result.Failure<string>("not configured"));
    }

    [Test]
    public async Task GetApiKey_ConfigHit_WinsOverEnv()
    {
        SetEnv("KILO_API_KEY", "env-key");
        var auth = new AuthStore(new FakeConfigStore { Key = "config-key" }, NullLogger<AuthStore>.Instance);

        var result = await auth.GetApiKeyAsync("kilocode");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("config-key");
    }

    [Test]
    public async Task GetApiKey_PresetEnvUsedWhenConfigMisses()
    {
        SetEnv("KILO_API_KEY", "kilo-env-key");

        var result = await _auth.GetApiKeyAsync("kilocode");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("kilo-env-key");
    }

    [Test]
    public async Task GetApiKey_ConventionalEnvUsedAsLastResort()
    {
        SetEnv("TEST_PROVIDER_API_KEY", "conv-key");

        var result = await _auth.GetApiKeyAsync("test-provider");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("conv-key");
    }

    [Test]
    public async Task GetApiKey_NothingSet_AggregatedHelpNamesAllEnvVars()
    {
        var result = await _auth.GetApiKeyAsync("kilocode");

        await Assert.That(result.IsFailure).IsTrue();
        // kilocode's preset (KILO_API_KEY) and conventional (KILOCODE_API_KEY) both listed.
        await Assert.That(result.Error).Contains("$KILO_API_KEY");
        await Assert.That(result.Error).Contains("$KILOCODE_API_KEY");
        await Assert.That(result.Error).Contains("harbor auth set kilocode");
    }
}
