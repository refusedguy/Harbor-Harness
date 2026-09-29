// RegistryResolveAbsenceTests.cs — behaviour pin for the "absence is Maybe<T>"
// wave in src/Harbor.Hosting.
//
// HarborModeRegistry.TryResolve / SessionStoreRegistry.TryResolve used to be
// `bool TryResolve(..., out IFoo? foo)` and are now `Maybe<IFoo> Resolve(...)`.
// The conversion removed a whole `|| factory is null` arm from both callers —
// that arm could never fire, because a dictionary lookup that returns true
// never yields a null value.
//
// "The arm was dead" is not the same claim as "nothing changed". These tests
// assert the observable contract through the PUBLIC surface (AddHarbor), so
// they keep holding regardless of which shape the registries use internally:
//   * a known id still resolves and still composes;
//   * an unknown id still fails fast, with the SAME ArgumentException message.
// If the Maybe conversion ever starts swallowing an unknown id, or changes the
// message, these go red.
//
// NotInParallel: every case pins a process-wide environment variable.

using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Pins the fail-fast behaviour of the two env-var-selected registries after
///     their absence contract moved from a nullable out-parameter to
///     <c>Maybe&lt;T&gt;</c>.
/// </summary>
[NotInParallel("hosting")]
public class RegistryResolveAbsenceTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Compose the container the way <c>RegistrationCompositionTests</c> does.</summary>
    private static ServiceProvider Compose(string defaultStorage)
    {
        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = defaultStorage });
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Compose with one environment variable pinned, restoring it afterwards.
    /// </summary>
    private static T WithEnv<T>(string name, string? value, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Test]
    public async Task UnknownHarborStorage_StillThrowsTheSameArgumentException()
    {
        ArgumentException ex = WithEnv("HARBOR_STORAGE", "definitely-not-a-backend",
            () => Assert.Throws<ArgumentException>(() => { _ = Compose("memory"); }));

        await Assert.That(ex.Message).Contains("Unknown HARBOR_STORAGE: 'definitely-not-a-backend'")
            .Because(
                "The Maybe conversion moved the absence out of a nullable out-parameter, but an unknown "
                + "backend id must still fail fast with the same message — otherwise a typo in HARBOR_STORAGE "
                + "would silently fall back to a working backend and the user would never learn their setting "
                + "was ignored.");
    }

    [Test]
    public async Task KnownHarborStorage_StillResolves()
    {
        using ServiceProvider sp = WithEnv("HARBOR_STORAGE", "memory", () => Compose("jsonl"));

        await Assert.That(sp.GetRequiredService<ISessionStore>()).IsNotNull()
            .Because(
                "The known-id arm must still produce a store. The caller's second check used to be "
                + "`|| factory is null`, which was dead code, so removing it changed nothing — this test is "
                + "what proves that instead of assuming it.");
    }

    [Test]
    public async Task UnknownHarborMode_StillThrowsTheSameArgumentException()
    {
        ArgumentException ex = WithEnv("HARBOR_MODE", "definitely-not-a-mode",
            () => Assert.Throws<ArgumentException>(() => { _ = Compose("memory"); }));

        await Assert.That(ex.Message).Contains("Unknown HARBOR_MODE: 'definitely-not-a-mode'")
            .Because(
                "Same contract as HARBOR_STORAGE: an unknown mode id is a configuration error the user must "
                + "see by name, and the Maybe conversion must not have turned it into a silent default "
                + "strategy.");
    }

    [Test]
    public async Task KnownHarborMode_StillResolves()
    {
        using ServiceProvider sp = WithEnv("HARBOR_MODE", "inprocess", () => Compose("memory"));

        await Assert.That(sp.GetRequiredService<IAgentRegistry>()).IsNotNull()
            .Because(
                "A known mode id must still compose its strategy. If Maybe.None were ever produced for a "
                + "present key, the strategy would silently not be applied and the host would come up without "
                + "the mode's registrations.");
    }
}
