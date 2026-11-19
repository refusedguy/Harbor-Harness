using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #3401 — <see cref="GitBaseDetector" /> is called by <see cref="BashTool" />
///     itself, not only by its own unit tests. An agent writes
///     <c>$HARBOR_GIT_BASE</c> instead of guessing main/master; the tool queries
///     the repository refs through the process seam and injects the detected
///     branch into the child's environment. Both processes are scripted, so no
///     shell and no real repository are needed.
/// </summary>
public class BashToolGitBaseWiringTests
{
    [Test]
    public async Task RepoWithTrunkAndMain_ResolvesMain_AndLeavesCommandVerbatim()
    {
        (BashTool tool, List<ProcessSpec> specs) = ToolForRefs(["trunk", "origin/main", "refs/heads/main"]);
        var args = JsonDocument.Parse(
            """{"command":"git diff $HARBOR_GIT_BASE...HEAD"}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create())
            .WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(specs.Count).IsEqualTo(2);
        await Assert.That(specs[0].FileName).IsEqualTo("git");
        await Assert.That(specs[0].Arguments[0]).IsEqualTo("for-each-ref");
        await Assert.That(specs[0].Arguments[1]).IsEqualTo("--format=%(refname:short)");
        await Assert.That(specs[0].Arguments[2]).IsEqualTo("refs/heads");
        await Assert.That(specs[0].Arguments[3]).IsEqualTo("refs/remotes");
        ProcessSpec command = specs.Single(s => s.FileName != "git");
        await Assert.That(command.Arguments[^1]).IsEqualTo("git diff $HARBOR_GIT_BASE...HEAD");
        await Assert.That(command.Environment!["HARBOR_GIT_BASE"]).IsEqualTo("main");
    }

    [Test]
    public async Task TrunkOnlyRepo_ResolvesTrunk_FromTheExtraCandidate()
    {
        (BashTool tool, List<ProcessSpec> specs) = ToolForRefs(["trunk", "refs/remotes/origin/trunk"]);
        var args = JsonDocument.Parse(
            """{"command":"git log $HARBOR_GIT_BASE..HEAD --oneline"}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create())
            .WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(specs.Single(s => s.FileName != "git")
            .Environment!["HARBOR_GIT_BASE"]).IsEqualTo("trunk");
    }

    [Test]
    public async Task RepoWithoutDefaultBranch_FailsClosed_BeforeRunningTheCommand()
    {
        (BashTool tool, List<ProcessSpec> specs) = ToolForRefs(["feature/x", "release/1.0"]);
        var args = JsonDocument.Parse(
            """{"command":"git diff $HARBOR_GIT_BASE...HEAD"}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create())
            .WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("no default base branch found");
        await Assert.That(specs.Any(s => s.FileName != "git")).IsFalse();
    }

    [Test]
    public async Task CommandWithoutMarker_DoesNotQueryRefs_OrChangeEnvironment()
    {
        (BashTool tool, List<ProcessSpec> specs) = ToolForRefs(["main"]);
        var args = JsonDocument.Parse("""{"command":"echo plain"}""").RootElement;

        ToolResult result = await tool.ExecuteAsync(args, TestToolContext.Create())
            .WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(specs.Count).IsEqualTo(1);
        await Assert.That(specs[0].FileName).IsNotEqualTo("git");
        await Assert.That(specs[0].Environment).IsNull();
    }

    /// <summary>Scripts the ref query and the command with the shared fake proxy.</summary>
    private static (BashTool Tool, List<ProcessSpec> Specs) ToolForRefs(string[] refs)
    {
        var specs = new List<ProcessSpec>();
        FakeProcessProxy proxy = new()
        {
            Build = spec =>
            {
                specs.Add(spec);
                return spec.FileName == "git"
                    ? new FakeProcessHandle(refs, [], 0)
                    : new FakeProcessHandle(["done"], [], 0);
            }
        };
        return (new BashTool(proxy, NullLogger<BashTool>.Instance), specs);
    }
}
