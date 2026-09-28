using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Config.Tests;

/// <summary>
/// Setup-guide detection (KILLER_FEATURES §2.7 Feature 9, issue #383): the
/// Application-layer signals the CLI maps onto the checklist model — config file
/// present, a stored provider key, a resolved workspace, first-run gating via
/// the onboarded flag, and the optional provider health probe.
/// </summary>
public sealed class SetupChecklistDetectorTests
{
    private sealed class StubHealthCheck(Result<ProviderHealth> outcome, Exception? throws = null) : IProviderHealthCheck
    {
        public int Calls { get; private set; }

        public string? LastProviderId { get; private set; }

        public Task<Result<ProviderHealth>> CheckAsync(ProviderId providerId, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastProviderId = providerId.Value;
            return throws is not null
                ? throw throws
                : Task.FromResult(outcome);
        }
    }

    private static (SetupChecklistDetector detector, JsonConfigStore store, string dir, string path) Create(
        IProviderHealthCheck? healthCheck = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-setup-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var detector = new SetupChecklistDetector(
            store,
            auth,
            healthCheck,
            configPath: path,
            workspacePath: dir,
            logger: NullLogger<SetupChecklistDetector>.Instance);
        return (detector, store, dir, path);
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task DetectAsync_MissingConfig_ReportsNoConfigAndNoWorkspace()
    {
        (SetupChecklistDetector detector, _, string dir, _) = Create();
        try
        {
            SetupChecklistSignals signals = await detector.DetectAsync();

            await Assert.That(signals.ConfigFileExists).IsFalse();
            await Assert.That(signals.WorkspaceResolved).IsFalse();
            await Assert.That(signals.ProviderHealthPassed).IsFalse();
            await Assert.That(detector.ConfigFileExists()).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task DetectAsync_SavedConfig_ReportsConfigAndStoredKey()
    {
        (SetupChecklistDetector detector, JsonConfigStore store, string dir, string path) = Create();
        try
        {
            Result<HarborConfig> saved = await store.UpdateAsync(c =>
            {
                c.ApiKeys["kilocode"] = "klo_test_key";
                c.Onboarded = true;
                return c;
            });
            await Assert.That(saved.IsSuccess).IsTrue();

            SetupChecklistSignals signals = await detector.DetectAsync();

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(signals.ConfigFileExists).IsTrue();
            await Assert.That(signals.ProviderKeyStored).IsTrue();
            await Assert.That(signals.WorkspaceResolved).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task HasCompletedSetup_FollowsTheOnboardedFlag()
    {
        (SetupChecklistDetector detector, JsonConfigStore store, string dir, _) = Create();
        try
        {
            await Assert.That(await detector.HasCompletedSetupAsync()).IsFalse();

            await store.UpdateAsync(c =>
            {
                c.Onboarded = true;
                return c;
            });

            await Assert.That(await detector.HasCompletedSetupAsync()).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task CheckProviderHealth_MissingProbe_IsPending()
    {
        (SetupChecklistDetector detector, _, string dir, _) = Create();
        try
        {
            await Assert.That(await detector.CheckProviderHealthAsync()).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task CheckProviderHealth_NoConfiguredProvider_IsPending()
    {
        (SetupChecklistDetector detector, JsonConfigStore store, string dir, _) = Create();
        try
        {
            await store.UpdateAsync(c =>
            {
                c.Onboarded = true;
                return c;
            });

            await Assert.That(await detector.CheckProviderHealthAsync()).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task CheckProviderHealth_Success_MarksTheTaskDone()
    {
        var health = new StubHealthCheck(Result.Success(new ProviderHealth(42, 7)));
        (SetupChecklistDetector detector, JsonConfigStore store, string dir, _) = Create(health);
        try
        {
            await store.UpdateAsync(c =>
            {
                c.Provider = "kilocode";
                c.Onboarded = true;
                return c;
            });

            await Assert.That(await detector.CheckProviderHealthAsync()).IsTrue();
            await Assert.That(health.Calls).IsEqualTo(1);
            await Assert.That(health.LastProviderId).IsEqualTo("kilocode");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task CheckProviderHealth_Failure_IsPendingNotFatal()
    {
        var failed = new StubHealthCheck(Result.Failure<ProviderHealth>("unauthorized"));
        (SetupChecklistDetector detector, JsonConfigStore store, string dir, _) = Create(failed);
        try
        {
            await store.UpdateAsync(c =>
            {
                c.Provider = "kilocode";
                c.Onboarded = true;
                return c;
            });

            await Assert.That(await detector.CheckProviderHealthAsync()).IsFalse();

            var throwing = new StubHealthCheck(Result.Success(new ProviderHealth(1, 1)), new HttpRequestException("offline"));
            var second = new SetupChecklistDetector(
                store,
                new AuthStore(store, NullLogger<AuthStore>.Instance),
                throwing,
                configPath: Path.Combine(dir, "config.json"));
            await Assert.That(await second.CheckProviderHealthAsync()).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
