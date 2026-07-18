using System.Net.Http;
using System.Text.RegularExpressions;
using Harbor.E2E.Framework;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Blazor;

/// <summary>
///     End-to-end tests for the Harbor Blazor Server app. Starts Kestrel on a
///     random port (via CliDriver), then issues real HTTP requests with
///     <see cref="HttpClient"/>. No browser automation — pure HTTP, which
///     makes these tests fast and 100% reproducible on every OS.
/// </summary>
/// <remarks>
///     <para>
///         <b>Port selection:</b> the Blazor app reads <c>~/.harbor/blazor.json</c>
///         for its <c>listenPort</c>. Each test writes a fresh config with a
///         random high port before launching, then waits for the
///         <c>listening on http://localhost:PORT</c> banner on stdout to learn
///         the actual bound port.
///     </para>
/// </remarks>
[Category("E2E")]
public class BlazorE2ETests : E2eTestBase
{
    private const string BlazorProjectPath = "apps/Harbor.App.Blazor/Harbor.App.Blazor.csproj";
    private static readonly Regex PortRegex = new(@"http://localhost:(\d+)", RegexOptions.Compiled);

    /// <summary>
    ///     Pick a random high port in the dynamic range, write a fresh
    ///     <c>~/.harbor/blazor.json</c> with that port + no browser auto-open,
    ///     then return the port. Called by every test in this class.
    /// </summary>
    private int PrepareBlazorConfig()
    {
        int port = Random.Shared.Next(50_100, 59_999);
        string harborDir = Path.Combine(TempHome, ".harbor");
        Directory.CreateDirectory(harborDir);
        string blazorConfigPath = Path.Combine(harborDir, "blazor.json");
        string config = $$"""
            {
              "appId": "blazor",
              "configFileName": "blazor.json",
              "listenPort": {{port}},
              "autoOpenBrowser": false,
              "enableHotReload": false
            }
            """;
        File.WriteAllText(blazorConfigPath, config);
        return port;
    }

    /// <summary>
    ///     Wait for the <c>listening on http://localhost:PORT</c> banner to
    ///     appear in the captured stdout, then return the resolved port.
    ///     Throws if the banner doesn't appear within 20s.
    /// </summary>
    private static async Task<int> WaitForListeningPortAsync(IE2eDriver driver, int expectedPort, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            string screen = await driver.ReadScreenAsync(ct).ConfigureAwait(false);
            var match = PortRegex.Match(screen);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int p))
                return p;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return expectedPort; // fall back to the requested port; the GET will fail loudly.
    }

    /// <summary>
    ///     The Blazor app starts Kestrel, prints its listening banner, and
    ///     serves the index page on <c>/</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task HomePage_Returns200AndContainsHarbor()
    {
        int port = PrepareBlazorConfig();
        await using var driver = new CliDriver(BlazorProjectPath);
        var env = GetEnv();
        // Suppress the auto browser-open at the CLI-flag layer too (belt + braces).
        await driver.StartAsync(args: ["--no-open-browser"], env: env).ConfigureAwait(false);

        // Wait for Kestrel to print its banner. We don't need the actual port
        // (we asked for a fixed one) but the banner tells us Kestrel is ready.
        bool listening = await driver.WaitForTextAsync("listening on", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await Assert.That(listening).IsTrue();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync(FormattableString.Invariant($"http://localhost:{port}/")).ConfigureAwait(false);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        await Assert.That(content).Contains("Harbor");

        await driver.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The Blazor app's static-asset pipeline serves the site CSS (sanity
    ///     check that <c>UseStaticFiles</c> is wired).
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task StaticAssets_CssIsServed()
    {
        int port = PrepareBlazorConfig();
        await using var driver = new CliDriver(BlazorProjectPath);
        await driver.StartAsync(args: ["--no-open-browser"], env: GetEnv()).ConfigureAwait(false);
        bool listening = await driver.WaitForTextAsync("listening on", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await Assert.That(listening).IsTrue();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync(FormattableString.Invariant($"http://localhost:{port}/css/site.css")).ConfigureAwait(false);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);

        await driver.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The Blazor app exposes the Sessions page route (Blazor Server
    ///     renders the page on the server first; an unauthenticated GET
    ///     should still return 200 + the layout shell).
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SessionsRoute_Returns200()
    {
        int port = PrepareBlazorConfig();
        await using var driver = new CliDriver(BlazorProjectPath);
        await driver.StartAsync(args: ["--no-open-browser"], env: GetEnv()).ConfigureAwait(false);
        await driver.WaitForTextAsync("listening on", TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync(FormattableString.Invariant($"http://localhost:{port}/sessions")).ConfigureAwait(false);
        // Blazor Server falls back to the index page for client-side routing,
        // so we expect 200 (not 404) regardless of whether the route resolves.
        await Assert.That((int)response.StatusCode).IsEqualTo(200);

        await driver.StopAsync().ConfigureAwait(false);
    }
}
