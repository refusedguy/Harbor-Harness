using System.IO;
using System.Linq;
using System.Text.Json;
using Harbor.Tools.Mcp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

public class McpServersConfigLoaderTests
{
    private static McpServerStartInfo? Find(IReadOnlyList<McpServerEntry> entries, string name)
        => entries.FirstOrDefault(e => e.Name == name)?.StartInfo;

    [Test]
    public async Task Load_MissingFile_ReturnsEmpty()
    {
        var loader = new McpServersConfigLoader(ProjectRoot());
        var entries = loader.Load("/nonexistent/path/to/harbor.mcp.json");
        await Assert.That(entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoadFromJson_ParsesSingleServer()
    {
        var loader = new McpServersConfigLoader(ProjectRoot());
        var json = """
        {
          "mcpServers": {
            "python-hello": {
              "command": "python3",
              "args": ["main.py"],
              "cwd": "/tmp/x"
            }
          }
        }
        """;
        var entries = loader.LoadFromJson(json);
        await Assert.That(entries.Count).IsEqualTo(1);
        var info = Find(entries, "python-hello")!;
        await Assert.That(info.Command).IsEqualTo("python3");
        await Assert.That(info.Args).IsEquivalentTo(new[] { "main.py" });
        await Assert.That(info.WorkingDirectory).IsEqualTo("/tmp/x");
    }

    [Test]
    public async Task LoadFromJson_DisabledServer_Skipped()
    {
        var loader = new McpServersConfigLoader(ProjectRoot());
        var json = """
        {
          "mcpServers": {
            "on": { "command": "true" },
            "off": { "command": "true", "disabled": true }
          }
        }
        """;
        var entries = loader.LoadFromJson(json);
        await Assert.That(entries.Any(e => e.Name == "on")).IsTrue();
        await Assert.That(entries.Any(e => e.Name == "off")).IsFalse();
    }

    [Test]
    public async Task Load_Overlay_ProjectWinsOverUser()
    {
        string userFile = Path.GetTempFileName();
        string projectFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(userFile, """
            {
              "mcpServers": {
                "shared": { "command": "user-cmd" },
                "only-user": { "command": "u" }
              }
            }
            """);
            File.WriteAllText(projectFile, """
            {
              "mcpServers": {
                "shared": { "command": "project-cmd" },
                "only-proj": { "command": "p" }
              }
            }
            """);

            var loader = new McpServersConfigLoader(ProjectRoot());
            var entries = loader.Load(userFile, projectFile);

            await Assert.That(Find(entries, "shared")!.Command).IsEqualTo("project-cmd");
            await Assert.That(Find(entries, "only-user")!.Command).IsEqualTo("u");
            await Assert.That(Find(entries, "only-proj")!.Command).IsEqualTo("p");
        }
        finally
        {
            File.Delete(userFile);
            File.Delete(projectFile);
        }
    }

    [Test]
    public async Task LoadFromJson_ExpandsMacros()
    {
        const string home = "/home/harbor";
        const string harborHome = "/home/harbor/.harbor";
        const string projectRoot = "/repo/app";
        var loader = new McpServersConfigLoader(projectRoot, home, harborHome);
        var json = """
        {
          "mcpServers": {
            "svc": {
              "command": "svc",
              "cwd": "${projectRoot}/plugins",
              "args": ["${home}/bin", "--root", "${harborHome}"],
              "env": { "TOKEN": "${projectRoot}/token" }
            }
          }
        }
        """;
        var info = Find(loader.LoadFromJson(json), "svc")!;
        await Assert.That(info.WorkingDirectory).IsEqualTo("/repo/app/plugins");
        await Assert.That(info.Args[0]).IsEqualTo("/home/harbor/bin");
        await Assert.That(info.Args[2]).IsEqualTo("/home/harbor/.harbor");
        await Assert.That(info.Environment!["TOKEN"]).IsEqualTo("/repo/app/token");
    }

    [Test]
    public async Task Expand_UnknownMacro_LeftVerbatim()
    {
        var loader = new McpServersConfigLoader("/repo", "/home", "/home/.harbor");
        var expanded = loader.Expand("${projectRoot}/x/${unknown}");
        await Assert.That(expanded).IsEqualTo("/repo/x/${unknown}");
    }

    private static string ProjectRoot() => Directory.GetCurrentDirectory();
}
