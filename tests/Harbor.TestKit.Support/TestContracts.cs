// TestContracts.cs: the pinned workspace-contract fixture shared by the run-result suites.
//
// WHY IT IS HERE
// --------------
// Three suites pin a clean workspace contract to exercise RunVerificationRecord
// and RunOutcome: the Abstractions verification suite, the Abstractions
// tri-state suite and the Storage outcome suite. Each built the same eight-line
// record by hand, and the third copy (#2472) pushed the test-duplication gate
// red. The fixture lives in the support kit, which all three already reference,
// so there is one body to maintain. The tests still choose their own revision
// and assert whatever they assert; the fixture only supplies a valid snapshot.

using Harbor.Abstractions.Models;

namespace Harbor.TestKit;

/// <summary>
///     The workspace-contract fixture: a clean snapshot of /proj at a fixed
///     revision, with the default execution limits.
/// </summary>
public static class TestContracts
{
    /// <summary>The revision every fixture contract is pinned to.</summary>
    public const string PinnedRevision = "1111111111111111111111111111111111111111";

    /// <summary>
    ///     Build a clean contract pinned to <paramref name="baseRevision"/>.
    /// </summary>
    /// <param name="baseRevision">The revision the contract pins.</param>
    /// <returns>The contract.</returns>
    public static WorkspaceContract Workspace(string baseRevision = PinnedRevision) => new(
        RepoRoot: "/proj",
        BaseRevision: baseRevision,
        BaseBranch: "dev",
        TrackedDirt: [],
        UntrackedPresent: false,
        Isolation: WorkspaceIsolation.Copy,
        Limits: new WorkspaceLimits());
}
