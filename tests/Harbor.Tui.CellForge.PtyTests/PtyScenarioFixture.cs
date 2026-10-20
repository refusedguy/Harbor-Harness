using Harbor.E2E.Framework;
using TUnit.Core.Interfaces;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Per-test fixture for the CellForge PTY scenarios: a private mock LLM
///     server and a private temp <c>$HOME</c>, and nothing else.
///     <para />
///     WHY THIS EXISTS INSTEAD OF <see cref="MockServerFixture" />
///     <para />
///     <see cref="MockServerFixture" /> does three things: it starts a mock
///     server, it creates a temp home, and it swaps the PROCESS
///     <c>HOME</c>/<c>USERPROFILE</c> (MockServerFixture.cs:17-18). The third
///     one is a process-wide write, and the PTY scenarios never needed it: the
///     child receives its <c>HOME</c> explicitly, through
///     <c>ChildEnv</c> (CellForgePtyScenarioBase.cs), and the test process
///     itself reads no home-scoped state. <see cref="PtySession" /> copies the
///     process environment into the child's, and the scenario's own
///     <c>HOME</c>/<c>USERPROFILE</c> entries overwrite that copy, so the
///     swap only ever mattered as a way of making the PROCESS environment
///     wrong for everybody else.
///     <para />
///     That is the whole reason the project could not run in parallel (#1789):
///     a process-wide write is process-wide, so every scenario that made one
///     had to hold a mutex against every other scenario that made one, and
///     with unkeyed readers the repo's rule (<c>docs/TEST_PATTERNS.md</c>
///     §Parallelism, and the guard in
///     <c>tests/Harbor.Architecture.Tests/ProcessEnvIsolationRule.cs</c>)
///     demands the BARE <c>[NotInParallel]</c> form, which means "completely
///     alone". Dropping the write is what makes the honest answer (no key at
///     all) available.
///     <para />
///     The per-test isolation is unchanged by this: a GUID-named temp home,
///     a mock server on a random port, and a child process whose environment
///     points at that temp home. Two scenarios running at the same time cannot
///     see each other's config, sessions, logs or provider catalogue.
/// </summary>
public sealed class PtyScenarioFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The mock LLM server this scenario's child talks to.</summary>
    public MockLlmServer Server { get; private set; } = null!;

    /// <summary>
    ///     The scenario's private <c>$HOME</c>. Handed to the CHILD through
    ///     <c>ChildEnv</c>; never published to the process environment.
    /// </summary>
    public string TempHome { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Server = new MockLlmServer();
        await Server.StartAsync().ConfigureAwait(false);

        TempHome = Path.Combine(Path.GetTempPath(), "harbor-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempHome);

        await E2eHomeInstaller.InstallAsync(TempHome, Server.BaseUri.ToString()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            /* swallow: teardown must not throw */
        }

        try
        {
            if (!string.IsNullOrEmpty(TempHome) && Directory.Exists(TempHome))
            {
                Directory.Delete(TempHome, true);
            }
        }
        catch
        {
            /* swallow: temp dir cleanup is best-effort */
        }
    }
}
