using CSharpFunctionalExtensions;
using Harbor.Tools.Mcp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2609: the OAuth rule is exercised through <see cref="McpOAuthGate" />
///     alone, without registering a server or opening a transport. A unique
///     server name guarantees a cache miss, so no process environment is
///     changed and no network call is made. The cache resolves HARBOR_HOME, so
///     this class shares the <c>process-env</c> key with the writer of that
///     variable in this project.
/// </summary>
[NotInParallel("process-env")]
public class McpOAuthGateTests
{
    [Test]
    public async Task AuthBlockWithoutCachedToken_ReportsTheLoginHint()
    {
        string server = $"gate-{Guid.NewGuid():N}";
        var endpoint = new McpRemoteEndpoint(
            "http://localhost:1/mcp",
            McpTransportNames.Http,
            null,
            new McpOAuthConfig { ClientId = "cid", TokenEndpoint = "http://localhost:1/token" });
        using var gate = new McpOAuthGate(server, endpoint);

        Result<Maybe<string>> token = await gate.RequireTokenAsync(null, CancellationToken.None);

        await Assert.That(token.IsFailure).IsTrue();
        await Assert.That(token.Error).Contains($"harbor mcp login {server}");
    }
}
