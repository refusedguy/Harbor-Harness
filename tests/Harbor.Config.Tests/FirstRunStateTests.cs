// FirstRunStateTests.cs — issue #2672 (F13).
//
// The first-run acknowledgement must stay a disclosure fact: it identifies what
// was shown, not whether the folder is trusted and not any credential. These
// tests pin the identity of that fact (workspace + policy digest + schema) and
// the immutability of the transition to "acknowledged".

using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

public class FirstRunStateTests
{
    private static readonly DateTimeOffset AcknowledgedAt =
        new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task NotAcknowledged_OwesADisclosureForEveryWorkspace()
    {
        FirstRunState state = FirstRunState.NotAcknowledged;

        await Assert.That(state.DisclosureAcknowledged).IsFalse();
        await Assert.That(state.SchemaVersion).IsEqualTo(FirstRunState.CurrentSchemaVersion);
        await Assert.That(state.NeedsDisclosure("/repo", "policy-1")).IsTrue();
    }

    [Test]
    public async Task Acknowledge_RecordsOnlyTheDisclosureFact()
    {
        FirstRunState state = FirstRunState.NotAcknowledged
            .Acknowledge("/repo", "policy-1", AcknowledgedAt);

        await Assert.That(state.DisclosureAcknowledged).IsTrue();
        await Assert.That(state.AcknowledgedAtUtc.HasValue).IsTrue();
        await Assert.That(state.AcknowledgedAtUtc!.Value).IsEqualTo(AcknowledgedAt);
        await Assert.That(state.WorkspacePath).IsEqualTo("/repo");
        await Assert.That(state.SecurityPolicyDigest).IsEqualTo("policy-1");
        await Assert.That(state.NeedsDisclosure("/repo", "policy-1")).IsFalse();
    }

    [Test]
    public async Task NeedsDisclosure_TracksWorkspaceAndPolicyDigest()
    {
        FirstRunState state = FirstRunState.NotAcknowledged
            .Acknowledge("/repo", "policy-1", AcknowledgedAt);

        await Assert.That(state.NeedsDisclosure("/other", "policy-1")).IsTrue();
        await Assert.That(state.NeedsDisclosure("/repo", "policy-2")).IsTrue();
        await Assert.That(state.NeedsDisclosure("/repo", "policy-1")).IsFalse();
    }

    [Test]
    public async Task NeedsDisclosure_StaleSchemaOwesTheDisclosureAgain()
    {
        FirstRunState acknowledged = FirstRunState.NotAcknowledged
            .Acknowledge("/repo", "policy-1", AcknowledgedAt);
        FirstRunState state = acknowledged with
        {
            SchemaVersion = FirstRunState.CurrentSchemaVersion + 1
        };

        await Assert.That(state.NeedsDisclosure("/repo", "policy-1")).IsTrue();
    }

    [Test]
    public async Task Acknowledge_DoesNotMutateTheInspectedState()
    {
        FirstRunState inspected = FirstRunState.NotAcknowledged;

        _ = inspected.Acknowledge("/repo", "policy-1", AcknowledgedAt);

        await Assert.That(inspected.DisclosureAcknowledged).IsFalse();
        await Assert.That(inspected.WorkspacePath).IsEqualTo(string.Empty);
        await Assert.That(inspected.NeedsDisclosure("/repo", "policy-1")).IsTrue();
    }
}
