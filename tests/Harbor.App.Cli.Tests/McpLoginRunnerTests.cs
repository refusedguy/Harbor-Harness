using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Tests for <see cref="McpLoginRunner" /> — list/login/logout against an
///     isolated mcp.json pointed at by <c>HARBOR_MCP_CONFIG</c>. A class-wide
///     lock serializes the env mutation (no other suite touches the variable);
///     blocking waits stay inside the lock, TUnit assertions outside it.
/// </summary>
public class McpLoginRunnerTests : IDisposable
{
    private static readonly Lock Gate = new();
    private readonly string _root = Directory.CreateTempSubdirectory("harbor-mcp-login").FullName;
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable("HARBOR_MCP_CONFIG");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HARBOR_MCP_CONFIG", _savedEnv);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        lock (Gate)
        {
            return CoreRun(args);
        }
    }

    private void WriteConfig(string json)
    {
        string path = Path.Combine(_root, $"mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        Environment.SetEnvironmentVariable("HARBOR_MCP_CONFIG", path);
    }

    [Test]
    public async Task List_NoRemotes_ReportsNone()
    {
        (int exit, string output, _) = RunWithConfig(
            """{"mcpServers": {"local": {"command": "npx", "args": ["-y", "x"]}}}""",
            ["list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("(no remote MCP servers configured)");
    }

    [Test]
    public async Task List_RemoteServers_ShowsAuthAndTokenState()
    {
        (int exit, string output, _) = RunWithConfig(
            "{\"mcpServers\": {" +
            "\"cloud\": {\"url\": \"https://mcp.example.com/mcp\", \"auth\": {\"clientId\": \"c\"}}," +
            "\"plain\": {\"url\": \"https://plain.example.com/mcp\"}}}",
            ["list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("cloud [oauth/no-token]");
        await Assert.That(output).Contains("plain [no-auth/no-token]");
    }

    [Test]
    public async Task Login_WithoutAuthBlock_FailsWithHint()
    {
        (int exit, _, string err) = RunWithConfig(
            """{"mcpServers": {"plain": {"url": "https://plain.example.com/mcp"}}}""",
            ["login", "plain"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err).Contains("no auth block");
    }

    [Test]
    public async Task Login_UnknownServer_Fails()
    {
        (int exit, _, string err) = RunWithConfig("""{"mcpServers": {}}""", ["login", "ghost"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err).Contains("ghost");
    }

    [Test]
    public async Task Logout_ClearsCache_AndReports()
    {
        (int exit, string output, _) = RunWithConfig("""{"mcpServers": {}}""", ["logout", "srv"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Logged out");
    }

    [Test]
    public async Task Usage_UnknownSubcommand_ReturnsTwo()
    {
        (int exit, _, _) = Run(["bogus"]);
        await Assert.That(exit).IsEqualTo(2);
    }

    private (int Exit, string Out, string Err) RunWithConfig(string configJson, string[] args)
    {
        lock (Gate)
        {
            WriteConfig(configJson);
            return CoreRun(args);
        }
    }

    private static (int Exit, string Out, string Err) CoreRun(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = McpLoginRunner.RunAsync(stdout, stderr, args).GetAwaiter().GetResult();
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
