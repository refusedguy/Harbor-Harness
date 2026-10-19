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
///     Writes <c>HARBOR_HOME</c> process state, so the class holds the bare
///     <c>[NotInParallel]</c> gate (issue #823's rule) and restores the value
///     it read — never a typed one (#847).
/// </remarks>
[NotInParallel]
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
