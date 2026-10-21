using Harbor.App.Cli.Commands;
using Harbor.TestKit;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #1932: <c>harbor mcp list</c> reads the cached token's expiry from the
///     same clock the OAuth handler reads it from, so the listing and the
///     handler can never disagree about whether a token is still usable.
/// </summary>
/// <remarks>
///     <para>
///         The fake clock starts in <b>2035</b> — years away from the wall
///         clock — so a partial revert (a reader that compares against
///         <c>DateTimeOffset.UtcNow</c>) cannot pass by accident: the wall
///         clock sits on the other side of the token's lifetime, and the
///         assertion that expects "expired" fails instead of agreeing.
///     </para>
///     <para>
///         The class mutates the process environment (the MCP config overlay
///         and the Harbor home the token cache lives in), so it carries the
///         same <c>process-env</c> key as the classes that mutate it too.
///     </para>
/// </remarks>
[NotInParallel("process-env")]
public class McpLoginRunnerClockTests : IDisposable
{
    private static readonly Lock Gate = new();

    /// <summary>
    ///     The instant the fake clock starts at: far from the wall clock, and
    ///     on a whole second so no rounding can move it.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2035, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("harbor-mcp-list-clock").FullName;
    private readonly string? _savedConfig = Environment.GetEnvironmentVariable("HARBOR_MCP_CONFIG");
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("HARBOR_HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HARBOR_MCP_CONFIG", _savedConfig);
        Environment.SetEnvironmentVariable("HARBOR_HOME", _savedHome);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    ///     A token inside its lifetime is listed as "ok".
    /// </summary>
    [Test]
    public async Task List_TokenInsideItsLifetime_ReportsOk()
    {
        var clock = new ManualTimeProvider(Start);
        (int exit, string output, _) = Run(clock, Start.AddHours(1), ["list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("cloud [oauth/ok]");
    }

    /// <summary>
    ///     The same listing, with the clock moved past the token's lifetime:
    ///     it reports "expired". Before #1932 the listing read the wall clock
    ///     directly, so this state could only be reached by waiting.
    /// </summary>
    [Test]
    public async Task List_TokenPastItsLifetime_ReportsExpired()
    {
        var clock = new ManualTimeProvider(Start);
        clock.Advance(TimeSpan.FromHours(2));
        (int exit, string output, _) = Run(clock, Start.AddHours(1), ["list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("cloud [oauth/expired]");
    }

    /// <summary>
    ///     Runs <c>harbor mcp list</c> against an isolated config overlay and
    ///     an isolated Harbor home, so the token cache is the one this test
    ///     wrote.
    /// </summary>
    private (int Exit, string Out, string Err) Run(TimeProvider clock, DateTimeOffset tokenExpiry, string[] args)
    {
        lock (Gate)
        {
            string home = Path.Combine(_root, "home");
            string tokenDir = Path.Combine(home, "mcp-oauth");
            string configPath = Path.Combine(_root, "mcp.json");
            Directory.CreateDirectory(tokenDir);
            File.WriteAllText(
                configPath,
                """{"mcpServers": {"cloud": {"url": "https://mcp.example.com/mcp", "auth": {"clientId": "c"}}}}""");
            File.WriteAllText(
                Path.Combine(tokenDir, "mcp-oauth-cloud.json"),
                $$"""{"access_token": "at", "refresh_token": null, "expires_at_utc": "{{tokenExpiry:O}}"}""");

            Environment.SetEnvironmentVariable("HARBOR_MCP_CONFIG", configPath);
            Environment.SetEnvironmentVariable("HARBOR_HOME", home);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit = McpLoginRunner.RunAsync(stdout, stderr, args, timeProvider: clock).GetAwaiter().GetResult();
            return (exit, stdout.ToString(), stderr.ToString());
        }
    }
}
