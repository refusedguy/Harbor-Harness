using Harbor.Abstractions.Filesystem;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     <see cref="HarborPaths" /> — the single Harbor-home resolution (#1570).
///     Hosting forwards to it, and the former verbatim copies in
///     Harbor.Application (<c>WorkspacePaths</c>) and Harbor.Tools.Builtin
///     (<c>McpOAuthTokenCache</c>) now delegate here, so one policy edit
///     reaches hermetic tests, portable installs and OAuth tokens alike.
/// </summary>
/// <remarks>
///     Writes <c>HARBOR_HOME</c> process state, so the class declares a
///     parallelism form (issue #823's rule) and restores the value it read —
///     never a typed one (#847).
///     <para>
///         KEYED, not bare (#1790). The bare form is TUnit's global mutex —
///         "the test runs completely alone" — so it held this class alone in
///         the whole assembly for the sake of one environment variable that
///         nothing else in <c>Harbor.Abstractions.Tests</c> reads:
///         <c>HarborPaths.GetHarborHome</c> has no caller inside
///         <c>Harbor.Abstractions</c> besides itself, and this class is the
///         only writer of <c>HARBOR_HOME</c> in the project. The competitor
///         set is enumerable — it is the <c>process-env</c> writers, the
///         spelling the rest of the repository already uses for a class that
///         writes the process environment — so a named key is the right shape:
///         a future class here that writes or reads the Harbor home has to
///         take the same key, and until it does, the two do not overlap.
///     </para>
/// </remarks>
[NotInParallel("process-env")]
public class HarborPathsTests
{
    [Test]
    public async Task HarborHome_WhenHarborHomeSet_ReturnsCustomValueVerbatim()
    {
        string? previousHome = Environment.GetEnvironmentVariable(HarborPaths.HarborHomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(HarborPaths.HarborHomeVariable, "/tmp/harbor-paths-hermetic");

            await Assert.That(HarborPaths.GetHarborHome()).IsEqualTo("/tmp/harbor-paths-hermetic");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HarborPaths.HarborHomeVariable, previousHome);
        }
    }

    [Test]
    public async Task HarborHome_WhenHarborHomeEmpty_FallsBackToUserProfile()
    {
        string? previousHome = Environment.GetEnvironmentVariable(HarborPaths.HarborHomeVariable);
        try
        {
            // Empty string is the "unset" shape the pattern match treats as
            // absent; tooling that clears the variable must not strand the
            // resolution on a blank value.
            Environment.SetEnvironmentVariable(HarborPaths.HarborHomeVariable, string.Empty);

            await Assert.That(HarborPaths.GetHarborHome()).IsEqualTo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HarborPaths.HarborHomeVariable, previousHome);
        }
    }
}
