using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2674 H12. <c>harbor project add</c> is a thin git wrapper: the
///     behaviour worth pinning is what argv reaches git and where the clone
///     lands, not the process plumbing (already owned by the drained proxy
///     seam).
/// </summary>
public class ProjectAddTests
{
    [Test]
    public async Task Parse_AcceptsHttpsUrlWithDefaults()
    {
        var parsed = ProjectAddArgs.Parse(["add", "https://github.com/refusedguy/Harbor-Harness.git"]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Url).IsEqualTo("https://github.com/refusedguy/Harbor-Harness.git");
        await Assert.That(parsed.Value.Name).IsNull();
        await Assert.That(parsed.Value.ParentDirectory).IsNull();
        await Assert.That(parsed.Value.Branch).IsNull();
        await Assert.That(parsed.Value.DryRun).IsFalse();
    }

    [Test]
    public async Task Parse_ReadsEveryFlag()
    {
        var parsed = ProjectAddArgs.Parse(
        [
            "add", "git@github.com:refusedguy/Harbor-Harness.git",
            "--name", "harbor", "--dir", "/tmp/projects", "--branch", "dev", "--dry-run",
        ]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Name).IsEqualTo("harbor");
        await Assert.That(parsed.Value.ParentDirectory).IsEqualTo("/tmp/projects");
        await Assert.That(parsed.Value.Branch).IsEqualTo("dev");
        await Assert.That(parsed.Value.DryRun).IsTrue();
    }

    [Test]
    public async Task Parse_RejectsMissingUrlUnknownFlagsAndDuplicates()
    {
        await Assert.That(ProjectAddArgs.Parse([]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["list", "https://host/o/r.git"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "ftp://host/o/r.git"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--nope"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "https://host/o/x.git"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--name", "a", "--name", "b"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--dry-run", "--dry-run"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--name"]).IsFailure).IsTrue();
    }

    [Test]
    public async Task Parse_ValidatesDerivedNameDirectoryAndBranch()
    {
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/..", "--dir", "/tmp/x"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--name", "../escape"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--dir", "relative"]).IsFailure).IsTrue();
        await Assert.That(ProjectAddArgs.Parse(["add", "https://host/o/r.git", "--branch", "--dry-run"]).IsFailure).IsTrue();
    }

    [Test]
    public async Task ToHttps_ConvertsBothSshFormsAndKeepsHttp()
    {
        await Assert.That(ProjectUrls.ToHttps("git@github.com:o/r.git"))
            .IsEqualTo("https://github.com/o/r.git");
        await Assert.That(ProjectUrls.ToHttps("ssh://git@github.com/o/r.git"))
            .IsEqualTo("https://github.com/o/r.git");
        await Assert.That(ProjectUrls.ToHttps("https://github.com/o/r.git"))
            .IsEqualTo("https://github.com/o/r.git");
        await Assert.That(ProjectUrls.ToHttps("not-a-url")).IsNull();
    }

    [Test]
    public async Task RepoName_StripsGitSuffixAndTrailingSlash()
    {
        await Assert.That(ProjectUrls.RepoName("https://github.com/o/r.git")).IsEqualTo("r");
        await Assert.That(ProjectUrls.RepoName("https://github.com/o/r/")).IsEqualTo("r");
        await Assert.That(ProjectUrls.RepoName("git@github.com:o/r.git")).IsEqualTo("r");
    }

    [Test]
    public async Task Plan_BuildsCloneAndHttpsFallbackArguments()
    {
        var request = ProjectAddArgs.Parse(
            ["add", "git@github.com:o/r.git", "--branch", "dev", "--dir", "/tmp/projects"])
            .Value;

        ProjectClonePlan plan = ProjectClonePlan.Build(request, "/default/projects");

        await Assert.That(plan.Name).IsEqualTo("r");
        await Assert.That(plan.TargetDirectory).IsEqualTo(Path.Combine("/tmp/projects", "r"));
        await Assert.That(plan.Arguments).IsEquivalentTo(new[]
        {
            "clone", "--progress", "--branch", "dev",
            "git@github.com:o/r.git", Path.Combine("/tmp/projects", "r"),
        });
        await Assert.That(plan.FallbackUrl).IsEqualTo("https://github.com/o/r.git");
        await Assert.That(plan.FallbackArguments!).IsEquivalentTo(new[]
        {
            "clone", "--progress", "--branch", "dev",
            "https://github.com/o/r.git", Path.Combine("/tmp/projects", "r"),
        });
    }

    [Test]
    public async Task Plan_HttpsUrlHasNoFallback()
    {
        var request = ProjectAddArgs.Parse(["add", "https://github.com/o/r.git", "--name", "custom"]).Value;

        ProjectClonePlan plan = ProjectClonePlan.Build(request, "/default/projects");

        await Assert.That(plan.Name).IsEqualTo("custom");
        await Assert.That(plan.TargetDirectory).IsEqualTo(Path.Combine("/default/projects", "custom"));
        await Assert.That(plan.FallbackUrl).IsNull();
        await Assert.That(plan.FallbackArguments).IsNull();
    }

    [Test]
    public async Task Projection_DescribesPlanWithAndWithoutBranch()
    {
        var plain = ProjectClonePlan.Build(
            ProjectAddArgs.Parse(["add", "https://github.com/o/r.git"]).Value, "/projects");
        var branched = ProjectClonePlan.Build(
            ProjectAddArgs.Parse(["add", "https://github.com/o/r.git", "--branch", "dev"]).Value, "/projects");

        await Assert.That(ProjectProjection.Describe(plain))
            .IsEqualTo("project add: r <- https://github.com/o/r.git -> /projects/r");
        await Assert.That(ProjectProjection.Describe(branched))
            .IsEqualTo("project add: r <- https://github.com/o/r.git (branch: dev) -> /projects/r");
    }

    [Test]
    public async Task IsSafeSegment_RejectsTraversalAndEmptyNames()
    {
        await Assert.That(ProjectUrls.IsSafeSegment("harbor")).IsTrue();
        await Assert.That(ProjectUrls.IsSafeSegment("harbor_2")).IsTrue();
        await Assert.That(ProjectUrls.IsSafeSegment("..")).IsFalse();
        await Assert.That(ProjectUrls.IsSafeSegment(".hidden")).IsFalse();
        await Assert.That(ProjectUrls.IsSafeSegment("a/b")).IsFalse();
        await Assert.That(ProjectUrls.IsSafeSegment(string.Empty)).IsFalse();
    }
}
