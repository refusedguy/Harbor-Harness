using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F179 — <see cref="GitBaseDetector" /> is a pure classifier over the
///     available refs. These tests pin its observable verdicts (default priority,
///     accepted ref forms, custom candidates and rejection of nested local
///     branches) without touching a repository.
/// </summary>
public class GitBaseDetectorTests
{
    [Test]
    public async Task PrefersMainOverOtherDefaults_RegardlessOfRefOrder()
    {
        string[] refs = ["develop", "master", "main"];
        await Assert.That(GitBaseDetector.Detect(refs)).IsEqualTo("main");
    }

    [Test]
    public async Task FallsThroughDefaultPriorityList()
    {
        await Assert.That(GitBaseDetector.Detect(["master", "develop"])).IsEqualTo("master");
        await Assert.That(GitBaseDetector.Detect(["develop"])).IsEqualTo("develop");
    }

    [Test]
    public async Task RecognisesLocalAndRemoteTrackingRefForms()
    {
        string[][] cases =
        [
            ["main"],
            ["refs/heads/main"],
            ["origin/main"],
            ["upstream/main"],
            ["refs/remotes/origin/main"],
            ["remotes/upstream/main"]
        ];

        foreach (string[] refs in cases)
        {
            await Assert.That(GitBaseDetector.Detect(refs)).IsEqualTo("main");
        }
    }

    [Test]
    public async Task ReturnsNullWhenNoDefaultBranchIsPresent()
    {
        await Assert.That(GitBaseDetector.Detect(["feature/x", "release/1.0"])).IsNull();
        await Assert.That(GitBaseDetector.Detect(Array.Empty<string>())).IsNull();
    }

    [Test]
    public async Task HonoursCustomCandidatesInSuppliedOrder()
    {
        string[] refs = ["develop", "trunk", "main"];

        await Assert.That(GitBaseDetector.Detect(refs, ["trunk", "main"])).IsEqualTo("trunk");
        await Assert.That(GitBaseDetector.Detect(refs, ["main", "trunk"])).IsEqualTo("main");
        await Assert.That(GitBaseDetector.Detect(refs, ["release"])).IsNull();
    }

    [Test]
    public async Task NestedLocalBranch_IsNotMistakenForDefault()
    {
        await Assert.That(GitBaseDetector.Detect(["feature/main"])).IsNull();
        await Assert.That(GitBaseDetector.Detect(["refs/heads/feature/main"])).IsNull();
    }

    [Test]
    public async Task MultiSegmentRemoteBranch_MatchesCustomCandidate()
    {
        string[] refs = ["origin/release/1.0"];
        await Assert.That(GitBaseDetector.Detect(refs, ["release/1.0"])).IsEqualTo("release/1.0");
    }

    [Test]
    public async Task IsDefaultBaseBranch_MatchesOnlyDefaults()
    {
        await Assert.That(GitBaseDetector.IsDefaultBaseBranch("main")).IsTrue();
        await Assert.That(GitBaseDetector.IsDefaultBaseBranch("master")).IsTrue();
        await Assert.That(GitBaseDetector.IsDefaultBaseBranch("develop")).IsTrue();
        await Assert.That(GitBaseDetector.IsDefaultBaseBranch("trunk")).IsFalse();
        await Assert.That(GitBaseDetector.IsDefaultBaseBranch("MAIN")).IsFalse();
        await Assert.That(GitBaseDetector.IsDefaultBaseBranch(null)).IsFalse();
    }

    [Test]
    public async Task ThrowsOnNullArguments()
    {
        await Assert.That(() => GitBaseDetector.Detect(null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => GitBaseDetector.Detect(["main"], null!))
            .Throws<ArgumentNullException>();
    }
}
