using Harbor.Tools.Mcp;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     The MCP OAuth cache's default directory hangs off the single
///     <c>HarborPaths</c> home (#1570) — the file it used to hand-copy — so
///     panning the harness home (hermetic tests, portable installs) drags the
///     token store along with it.
/// </summary>
/// <remarks>
///     Writes <c>HARBOR_HOME</c> process state, so the class holds the bare
///     <c>[NotInParallel]</c> gate (issue #823's rule) and restores the value
///     it read (#847).
/// </remarks>
[NotInParallel]
public class McpOAuthTokenCacheDefaultDirectoryTests
{
    [Test]
    public async Task DefaultDirectory_HangsOffHarborHome()
    {
        string scratch = Directory.CreateTempSubdirectory("harbor-mcp-oauth-defaultdir").FullName;
        string? previousHome = Environment.GetEnvironmentVariable("HARBOR_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HARBOR_HOME", scratch);

            await Assert.That(McpOAuthTokenCache.DefaultDirectory())
                .IsEqualTo(Path.Combine(scratch, "mcp-oauth"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBOR_HOME", previousHome);
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
