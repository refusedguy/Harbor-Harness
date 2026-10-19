using System.IO;
using System.Text.Json;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1552: the stdio spawn path (<c>ServerEntry.GetProcess</c>) is a
///     failure Result, not a silent <c>catch → null</c>. The remote guard is a
///     shape contract, cancellation is rethrown (§ROP / CSE bible §4.5 via
///     <c>ResultErrors</c>), and every real spawn cause keeps its message.
///     Poked through the public <see cref="McpRegistry" /> API because
///     <c>ServerEntry</c> is private, same as the start-info passthrough tests.
/// </summary>
public class McpRegistryGetProcessTests
{
    [Test]
    public async Task Invoke_UnspawnableCommand_FailsAsResultInsteadOfThrowing()
    {
        var registry = new McpRegistry(NullLogger<McpRegistry>.Instance, McpTransportResolver.Default);
        var register = registry.Register("ghost", new McpServerStartInfo
        {
            // Cannot exist — Process.Start throws before any pipe exists.
            Command = Path.Join(Path.GetTempPath(), "harbor-1552-ghost-absent"),
        });
        await Assert.That(register.IsSuccess).IsTrue();

        var invoke = await registry.InvokeAsync("ghost", "initialize", JsonDocument.Parse("{}").RootElement);

        // The refactor must keep the observable contract: a failure Result that
        // names the server — no exception escaping from a failed spawn.
        await Assert.That(invoke.IsFailure).IsTrue();
        await Assert.That(invoke.Error).Contains("'ghost'");
        await Assert.That(invoke.Error).Contains("process is not running");
    }

    [Test]
    public async Task Invoke_AfterFailedSpawn_RetriesCleanly()
    {
        var registry = new McpRegistry(NullLogger<McpRegistry>.Instance, McpTransportResolver.Default);
        registry.Register("ghost", new McpServerStartInfo
        {
            Command = Path.Join(Path.GetTempPath(), "harbor-1552-ghost-absent"),
        });

        var first = await registry.InvokeAsync("ghost", "initialize", JsonDocument.Parse("{}").RootElement);
        var second = await registry.InvokeAsync("ghost", "initialize", JsonDocument.Parse("{}").RootElement);

        // A failed spawn left no stale process behind, so the retry is the same
        // honest failure — not a crash and not a half-alive entry.
        await Assert.That(first.IsFailure).IsTrue();
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(second.Error).IsEqualTo(first.Error);
        await Assert.That(registry.GetServerNames()).Contains("ghost");
    }
}
