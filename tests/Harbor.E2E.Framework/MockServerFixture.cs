namespace Harbor.E2E.Framework;

public sealed class MockServerFixture : IAsyncInitializer, IAsyncDisposable
{
    public MockLlmServer Server { get; private set; } = null!;

    public string TempHome { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        Server = new MockLlmServer();
        await Server.StartAsync().ConfigureAwait(false);

        TempHome = Path.Combine(Path.GetTempPath(), "harbor-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempHome);

        Environment.SetEnvironmentVariable("HOME", TempHome);
        Environment.SetEnvironmentVariable("USERPROFILE", TempHome);

        await E2eHomeInstaller.InstallAsync(TempHome, Server.BaseUri.ToString()).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.StopAsync().ConfigureAwait(false);
        }
        catch
        { /* swallow — teardown must not throw */
        }

        try
        {
            if (!string.IsNullOrEmpty(TempHome) && Directory.Exists(TempHome))
                Directory.Delete(TempHome, true);
        }
        catch
        { /* swallow — temp dir cleanup is best-effort */
        }
    }
}
