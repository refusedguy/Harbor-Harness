using Harbor.ChangeTracking;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     #2548: the four TryLoad entry points share one run-id validator. A bad
///     run id must fail at every one of them, with the same error text as before.
/// </summary>
public class RunIdValidationTests
{
    private static readonly string[] BadRunIds =
    [
        "",
        "   ",
        ".",
        "..",
        "../escape",
        "sub/dir/run",
        "/absolute/run",
    ];

    [Test]
    public async Task TryLoad_RejectsBadRunIdsInAllFourPaths()
    {
        foreach (string bad in BadRunIds)
        {
            string expected = bad is "" or "   "
                ? "Run ID cannot be empty."
                : $"Invalid run id '{bad}': path separators are not allowed.";

            await Assert.That(ChangeSetFreezer.TryLoad(bad).IsFailure).IsTrue();
            await Assert.That(ChangeSetFreezer.TryLoad(bad).Error).IsEqualTo(expected);

            await Assert.That(CheckRunner.TryLoad(bad).IsFailure).IsTrue();
            await Assert.That(CheckRunner.TryLoad(bad).Error).IsEqualTo(expected);

            await Assert.That(OwnerReport.TryLoad(bad).IsFailure).IsTrue();
            await Assert.That(OwnerReport.TryLoad(bad).Error).IsEqualTo(expected);

            await Assert.That(WorkspaceMaterializer.TryLoad(bad).IsFailure).IsTrue();
            await Assert.That(WorkspaceMaterializer.TryLoad(bad).Error).IsEqualTo(expected);
        }
    }
}
