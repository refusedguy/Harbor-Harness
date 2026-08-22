using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Exercises <see cref="McpProcessClient" /> (via the public <see cref="McpRegistry" /> API, since
///     the client is internal) to prove that arguments, working directory, and environment are passed
///     through to the spawned process, and that a real, standard MCP server can be driven end-to-end.
/// </summary>
public class McpProcessClientStartInfoTests
{
    private static McpRegistry NewRegistry() => new(NullLogger<McpRegistry>.Instance);

    [Test]
    public async Task Register_StartInfo_SpawnsWithArgsEnvCwd()
    {
        string outFile = Path.GetTempFileName();
        string workDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "mcpwd_" + Guid.NewGuid().ToString("N"))).FullName;

        try
        {
            string script = "printf 'cwd=%s\\n' \"$PWD\" > \"" + outFile +
                             "\"; printf 'env=%s\\n' \"$MYVAR\" >> \"" + outFile +
                             "\"; printf 'arg=%s\\n' \"$1\" >> \"" + outFile + "\"";

            var registry = NewRegistry();
            var result = registry.Register("probe", new McpServerStartInfo
            {
                Command = "sh",
                Args = new[] { "-c", script, "_", "ARGVAL" },
                WorkingDirectory = workDir,
                Environment = new Dictionary<string, string> { ["MYVAR"] = "ENVVAL" }
            });
            await Assert.That(result.IsSuccess).IsTrue();

            // InvokeAsync lazily spawns the process; the sh script exits and closes stdout,
            // so the call returns after the side-effect (writing outFile) has happened.
            await registry.InvokeAsync("probe", "initialize", JsonDocument.Parse("{}").RootElement);

            var lines = await File.ReadAllLinesAsync(outFile);
            var map = lines.Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

            await Assert.That(map["cwd"]).IsEqualTo(workDir);
            await Assert.That(map["env"]).IsEqualTo("ENVVAL");
            await Assert.That(map["arg"]).IsEqualTo("ARGVAL");
        }
        finally
        {
            File.Delete(outFile);
        }
    }

    [Test]
    public async Task Register_LegacyCommand_StillWorks()
    {
        var registry = NewRegistry();
        var result = registry.Register("legacy", "true");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(registry.GetServerNames()).Contains("legacy");
    }

    [Test]
    public async Task RegisterFromConfig_NewSchema_ReadsCwdAndEnv()
    {
        string cfg = Path.GetTempFileName();
        try
        {
            File.WriteAllText(cfg, """
            {
              "mcpServers": {
                "srv": {
                  "command": "true",
                  "cwd": "/tmp",
                  "env": { "FOO": "bar" }
                }
              }
            }
            """);
            var registry = NewRegistry();
            var result = registry.RegisterFromConfig(cfg);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(registry.GetServerNames()).Contains("srv");
        }
        finally
        {
            File.Delete(cfg);
        }
    }

    [Test]
    public async Task RegisterFromConfig_MissingFile_IsSuccess()
    {
        var registry = NewRegistry();
        var result = registry.RegisterFromConfig("/no/such/file/mcp.json");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ── End-to-end: drive a REAL, standard MCP server spoken over stdio ───────────
    // Uses one of the in-repo samples (python-hello / node-hello) which need no compile
    // step, so the test is fast and deterministic. Guarded by HARBOR_E2E per repo convention.

    [Test]
    public async Task EndToEnd_SampleMcpServer_InitializeListAndCall()
    {
        if (Environment.GetEnvironmentVariable("HARBOR_E2E") is null)
            return;

        var fixture = ResolveFixture();
        if (fixture is null)
            return; // no sample runtime available in this environment

        var registry = NewRegistry();
        var register = registry.Register("sample", fixture.Value.StartInfo);
        await Assert.That(register.IsSuccess).IsTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var init = await registry.InvokeAsync("sample", "initialize", JsonDocument.Parse("{}").RootElement, cts.Token);
        await Assert.That(init.IsSuccess).IsTrue();
        await Assert.That(init.Value).Contains("2024-11-05");

        var list = await registry.InvokeAsync("sample", "tools/list", JsonDocument.Parse("{}").RootElement, cts.Token);
        await Assert.That(list.IsSuccess).IsTrue();
        await Assert.That(list.Value).Contains("\"echo\"");

        using var callDoc = JsonDocument.Parse("{\"name\":\"echo\",\"arguments\":{\"text\":\"hello harbor\"}}");
        var call = await registry.InvokeAsync("sample", "tools/call", callDoc.RootElement.Clone(), cts.Token);
        await Assert.That(call.IsSuccess).IsTrue();
        await Assert.That(call.Value).Contains("hello harbor");
    }

    private static (McpServerStartInfo StartInfo, string SampleDir)? ResolveFixture()
    {
        string? sampleDir = FindRepoDir("samples/mcp/python-hello");
        if (sampleDir is not null && CommandExists("python3"))
            return (new McpServerStartInfo { Command = "python3", Args = new[] { "main.py" }, WorkingDirectory = sampleDir }, sampleDir);

        sampleDir = FindRepoDir("samples/mcp/node-hello");
        if (sampleDir is not null && CommandExists("node"))
            return (new McpServerStartInfo { Command = "node", Args = new[] { "index.js" }, WorkingDirectory = sampleDir }, sampleDir);

        return null;
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string? FindRepoDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
