// DiffReviewStateTests.cs — issue #2672 (G03).
//
// A review is a claim about one exact diff. These tests pin the rules a
// consumer relies on: a review never covers a changed diff or a different
// scope, seeing a diff is not reviewing it, and an old schema (or a half-written
// state) never counts as a review.

using Harbor.ChangeTracking;

namespace Harbor.ChangeTracking.Tests;

public class DiffReviewStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Create_RejectsBlankFilePath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => DiffReviewState.Create("   "));

        await Assert.That(ex.ParamName).IsEqualTo("filePath");
    }

    [Test]
    public async Task Create_RejectsHalfWrittenReview()
    {
        ArgumentException noTime = Assert.Throws<ArgumentException>(() =>
            DiffReviewState.Create("src/Foo.cs", reviewDiffIdentity: "sha-1"));
        ArgumentException noIdentity = Assert.Throws<ArgumentException>(() =>
            DiffReviewState.Create("src/Foo.cs", reviewedAtUtc: T0));

        await Assert.That(noTime.ParamName).IsEqualTo("reviewedAtUtc");
        await Assert.That(noIdentity.ParamName).IsEqualTo("reviewDiffIdentity");
    }

    [Test]
    public async Task NewState_IsNotReviewed_AndIsStale()
    {
        DiffReviewState state = DiffReviewState.Create("src/Foo.cs", DiffReviewScope.Staged);

        await Assert.That(state.IsReviewed).IsFalse();
        await Assert.That(state.FilePath).IsEqualTo("src/Foo.cs");
        await Assert.That(state.Scope).IsEqualTo(DiffReviewScope.Staged);
        await Assert.That(state.IsStale("sha-1")).IsTrue();
    }

    [Test]
    public async Task MarkReviewed_CoversExactDiff_AndLeavesOriginalUntouched()
    {
        DiffReviewState state = DiffReviewState.Create("src/Foo.cs", DiffReviewScope.Staged, oldPath: "src/Old.cs");

        DiffReviewState reviewed = state.MarkReviewed("sha-1", T1);

        await Assert.That(state.IsReviewed).IsFalse();
        await Assert.That(state.ReviewedAtUtc).IsNull();
        await Assert.That(reviewed.IsReviewed).IsTrue();
        await Assert.That(reviewed.ReviewDiffIdentity).IsEqualTo("sha-1");
        await Assert.That(reviewed.ReviewedAtUtc!.Value).IsEqualTo(T1);
        await Assert.That(reviewed.OldPath).IsEqualTo("src/Old.cs");
        await Assert.That(reviewed.IsStale("sha-1")).IsFalse();
        await Assert.That(reviewed.IsStale("sha-2")).IsTrue();
    }

    [Test]
    public async Task IsStale_DetectsScopeChange()
    {
        DiffReviewState reviewed = DiffReviewState
            .Create("src/Foo.cs", DiffReviewScope.Staged)
            .MarkReviewed("sha-1", T1);

        await Assert.That(reviewed.IsStale(DiffReviewScope.Staged, "sha-1")).IsFalse();
        await Assert.That(reviewed.IsStale(DiffReviewScope.Unstaged, "sha-1")).IsTrue();
        await Assert.That(reviewed.IsStale(DiffReviewScope.Branch, "sha-1")).IsTrue();
    }

    [Test]
    public async Task IsStale_DetectsUnknownCurrentIdentity()
    {
        DiffReviewState reviewed = DiffReviewState
            .Create("src/Foo.cs")
            .MarkReviewed("sha-1", T1);

        await Assert.That(reviewed.IsStale(null)).IsTrue();
        await Assert.That(reviewed.IsStale("   ")).IsTrue();
    }

    [Test]
    public async Task RecordSeen_DoesNotMarkReviewed()
    {
        DiffReviewState state = DiffReviewState.Create("src/Foo.cs");

        DiffReviewState seen = state.RecordSeen("sha-1", T0);

        await Assert.That(state.LastSeenDiffIdentity).IsNull();
        await Assert.That(seen.LastSeenDiffIdentity).IsEqualTo("sha-1");
        await Assert.That(seen.LastOpenedAtUtc!.Value).IsEqualTo(T0);
        await Assert.That(seen.IsReviewed).IsFalse();
    }

    [Test]
    public async Task RecordSeen_KeepsAnyPriorReview()
    {
        DiffReviewState reviewed = DiffReviewState
            .Create("src/Foo.cs")
            .MarkReviewed("sha-1", T0);

        DiffReviewState seen = reviewed.RecordSeen("sha-2", T1);

        await Assert.That(seen.ReviewDiffIdentity).IsEqualTo("sha-1");
        await Assert.That(seen.ReviewedAtUtc!.Value).IsEqualTo(T0);
        await Assert.That(seen.LastSeenDiffIdentity).IsEqualTo("sha-2");
        await Assert.That(seen.LastOpenedAtUtc!.Value).IsEqualTo(T1);
        await Assert.That(seen.IsStale("sha-2")).IsTrue();
        await Assert.That(seen.IsStale("sha-1")).IsFalse();
    }

    [Test]
    public async Task OlderSchemaVersion_IsNotReviewed_EvenWithMatchingIdentity()
    {
        DiffReviewState state = DiffReviewState.Create(
            "src/Foo.cs",
            reviewDiffIdentity: "sha-1",
            reviewedAtUtc: T0,
            schemaVersion: 0);

        await Assert.That(state.SchemaVersion).IsEqualTo(0);
        await Assert.That(state.IsReviewed).IsFalse();
        await Assert.That(state.IsStale("sha-1")).IsTrue();
    }

    [Test]
    public async Task ParseScope_AcceptsAllThreeCaseInsensitively()
    {
        await Assert.That(DiffReviewState.ParseScope("unstaged")).IsEqualTo(DiffReviewScope.Unstaged);
        await Assert.That(DiffReviewState.ParseScope("Staged")).IsEqualTo(DiffReviewScope.Staged);
        await Assert.That(DiffReviewState.ParseScope(" BRANCH ")).IsEqualTo(DiffReviewScope.Branch);
    }

    [Test]
    public async Task ParseScope_RejectsUnknownValue()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => DiffReviewState.ParseScope("working"));

        await Assert.That(ex.ParamName).IsEqualTo("value");
    }

    [Test]
    public async Task MarkReviewed_AndRecordSeen_RejectBlankIdentity()
    {
        DiffReviewState state = DiffReviewState.Create("src/Foo.cs");

        ArgumentException review = Assert.Throws<ArgumentException>(() => state.MarkReviewed("  "));
        ArgumentException seen = Assert.Throws<ArgumentException>(() => state.RecordSeen("  "));

        await Assert.That(review.ParamName).IsEqualTo("diffIdentity");
        await Assert.That(seen.ParamName).IsEqualTo("diffIdentity");
    }
}
