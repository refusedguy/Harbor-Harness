using Harbor.TestKit;
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
///     Writes <c>HARBOR_HOME</c> process state, so the class declares a
///     parallelism form (issue #823's rule) and restores the value it read
///     (#847).
///     <para>
///         KEYED, not bare (#1790). The bare form is a global mutex over the
///         whole assembly; the only thing this class needs excluded is the
///         other <c>HARBOR_HOME</c> writers and readers in this project, and
///         those are exactly the <c>process-env</c> group the other two
///         env-writing classes here already hold. Nothing else in
///         <c>Harbor.Tools.Builtin.Tests</c> resolves the default directory:
///         every other <c>McpOAuthTokenCache</c> in the project is built with
///         an explicit root, and the one production caller that does not
///         (<c>McpOAuthHandler</c>) has no test in this project.
///     </para>
/// </remarks>
[NotInParallel("process-env")]
public class McpOAuthTokenCacheDefaultDirectoryTests
{
    [Test]
    public async Task DefaultDirectory_HangsOffHarborHome()
    {
        // The scope owns both halves of the state: the directory it created
        // and the variable it read before it wrote.
        using var scratch = new TempDirScope("harbor-mcp-oauth-defaultdir");
        using var env = new EnvScope("HARBOR_HOME");
        env.Set("HARBOR_HOME", scratch.Path);

        await Assert.That(McpOAuthTokenCache.DefaultDirectory())
            .IsEqualTo(Path.Combine(scratch.Path, "mcp-oauth"));
    }
}
