namespace Harbor.E2E.Framework;
/// <summary>
///     Base class for all Harbor E2E test fixtures. Owns a per-class
///     <see cref="MockLlmServer" /> + a temporary <c>HOME</c> directory so each
///     test class runs in isolation (no cross-test config leakage, no port
///     collisions between concurrent test classes).
/// </summary>
/// <remarks>
///     <para>
///         <b>Lifecycle:</b> TUnit's <c>[Before(HookType.Class)]</c> /
///         <c>[After(HookType.Class)]</c> hooks run before/after every method
///         in TUnit 0.50 — this is the documented behaviour and is why the
///         Avalonia DI tests (task A1) had to drop their After-hook. For E2E
///         we lean into it: each test method gets a fresh server + fresh temp
///         home, so tests can't bleed state into each other even when running
///         in parallel. (Cost: ~30 ms per spin-up; acceptable for E2E.)
///     </para>
///     <para>
///         <b>Temp home:</b> the <c>HOME</c> env var is mutated at process
///         scope to a fresh temp dir. The Harbor CLI writes its config to
///         <c>$HOME/.harbor/</c>; by redirecting <c>HOME</c> we prevent tests
///         from clobbering the developer's real <c>~/.harbor/</c> directory.
///         On Windows the same effect is achieved via <c>USERPROFILE</c>.
///     </para>
/// </remarks>
[ParallelLimiter<MockServerLimit>]
public abstract class E2eTestBase
{
    [ClassDataSource<MockServerFixture>(Shared = SharedType.None)]
    public required MockServerFixture Fixture { get; init; }

    /// <summary>
    ///     The in-process mock LLM server. Shared once per test session via
    ///     <see cref="MockServerFixture" />; <see cref="MockLlmServer.BaseUri" />
    ///     is non-null inside a test body.
    /// </summary>
    protected MockLlmServer Server => Fixture.Server;

    /// <summary>
    ///     Path to the shared temporary <c>$HOME</c>. Created once per test
    ///     session; deleted when the fixture is disposed.
    /// </summary>
    protected string TempHome => Fixture.TempHome;

    /// <summary>
    ///     Build the standard env-var dict that points a Harbor app at the
    ///     <see cref="Server" /> mock. Tests can extend the result with extra
    ///     vars before passing to <see cref="IE2eDriver.StartAsync" />.
    /// </summary>
    protected Dictionary<string, string> GetEnv() => new()
    {
        ["HOME"] = TempHome,
        ["USERPROFILE"] = TempHome,
        ["HARBOR_MODEL"] = "mock/test-model",
        ["MOCK_API_KEY"] = "test-key",
        // Quiet logging so test stdout isn't drowned in MS log noise.
        ["HARBOR_LOGLEVEL"] = "Warning",
        // Bypass the onboarding wizard gate (also set in config.json, belt + braces).
        ["HARBOR_SKIP_ONBOARDING"] = "1",
        // Keep the child .NET process quiet: a fresh CI runner + fresh temp
        // HOME would otherwise print telemetry / first-run banners into the
        // captured stdout of the very first spawned CLI.
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    };

    /// <summary>
    ///     Per-test guard for TUI E2E tests: throws TUnit's
    ///     <see cref="TUnit.Core.Exceptions.SkipTestException" /> (via
    ///     <see cref="Skip.Test" />) when the current sandbox does not support
    ///     PTY allocation, causing the test to appear as <b>Skipped</b> in test
    ///     reports rather than silently passing.
    /// </summary>
    /// <remarks>
    ///     TUI renderers (SpectreTui, Termina, Terminal.Gui, RazorConsole)
    ///     require a real PTY because they call <c>Console.ReadKey(true)</c>
    ///     in raw mode and ANSI-write directly to <c>Console.Out</c>. Some CI
    ///     sandboxes (and this dev box) SIGKILL <c>script(1)</c> via a seccomp
    ///     profile that blocks <c>forkpty</c>/<c>openpty</c>. Calling this
    ///     guard at the top of each TUI test keeps the suite green in
    ///     PTY-restricted environments without ripping the tests out.
    /// </remarks>
    protected static void EnsurePtyAvailable()
    {
        if (TuiDriver.IsPtyAvailable()) return;
        Skip.Test(TuiDriver.NoPtySkipReason);
    }

    /// <summary>
    ///     Assert screenshot PNG artifacts exist and are not byte-identical.
    /// </summary>
    protected static async Task AssertPngArtifactsExistAndDifferAsync(params string[] paths)
    {
        var hashes = new byte[paths.Length][];
        for (int i = 0; i < paths.Length; i++)
        {
            await Assert.That(File.Exists(paths[i])).IsTrue();
            hashes[i] = await File.ReadAllBytesAsync(paths[i]).ConfigureAwait(false);
            await Assert.That(hashes[i].Length).IsGreaterThan(0);
        }

        for (int i = 0; i < hashes.Length; i++)
        {
            for (int j = i + 1; j < hashes.Length; j++)
            {
                bool same = hashes[i].AsSpan().SequenceEqual(hashes[j]);
                await Assert.That(same).IsFalse();
            }
        }
    }
}
