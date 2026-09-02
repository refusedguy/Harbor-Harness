using Harbor.Abstractions.Tools;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     ROP-D Z3: IMcpRegistry.GetInstructions aggregates the static
///     <c>instructions</c> hints from mcp.json (and initialize responses at
///     runtime). These tests pin the config path and the snapshot contract.
/// </summary>
public class McpRegistryInstructionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("harbor-mcp-ins").FullName;
    private readonly McpRegistry _registry = new(NullLogger<McpRegistry>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Test]
    public async Task GetInstructions_NoServers_ReturnsEmpty()
    {
        await Assert.That(_registry.GetInstructions()).IsEmpty();
    }

    [Test]
    public async Task GetInstructions_ServerWithoutInstructions_IsAbsent()
    {
        var result = _registry.Register("bare", "uvx mcp-server-bare");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(_registry.GetInstructions()).IsEmpty();
    }

    [Test]
    public async Task RegisterFromConfig_WithInstructionsHint_SurfacesInSnapshot()
    {
        string path = Path.Combine(_dir, "mcp.json");
        await File.WriteAllTextAsync(path, """
            {
              "mcpServers": {
                "zeta": { "command": "uvx", "args": ["mcp-zeta"], "instructions": "Zeta serves z-files." },
                "alpha": { "command": "uvx", "args": ["mcp-alpha"], "instructions": "Alpha serves a-files." },
                "quiet": { "command": "uvx", "args": ["mcp-quiet"] }
              }
            }
            """);

        var load = _registry.RegisterFromConfig(path);
        var snapshot = _registry.GetInstructions();

        await Assert.That(load.IsSuccess).IsTrue();
        await Assert.That(snapshot.Count).IsEqualTo(2);
        // Deterministic order — sorted by server name.
        await Assert.That(snapshot[0].ServerName).IsEqualTo("alpha");
        await Assert.That(snapshot[0].Instructions).IsEqualTo("Alpha serves a-files.");
        await Assert.That(snapshot[1].ServerName).IsEqualTo("zeta");
    }

    [Test]
    public async Task Unregister_RemovesServerInstructions()
    {
        string path = Path.Combine(_dir, "mcp.json");
        await File.WriteAllTextAsync(path, """
            { "solo": { "command": "uvx", "args": ["mcp-solo"], "instructions": "Solo rules." } }
            """);
        _registry.RegisterFromConfig(path);

        var unregister = _registry.Unregister("solo");

        await Assert.That(unregister.IsSuccess).IsTrue();
        await Assert.That(_registry.GetInstructions()).IsEmpty();
    }
}
