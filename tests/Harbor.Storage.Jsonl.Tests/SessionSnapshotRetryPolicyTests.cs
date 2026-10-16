using System.Reflection;
using Harbor.Abstractions.Resilience;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
/// #1586 — the bounded snapshot retry is a Decorator over a single attempt
/// (<see cref="SessionSnapshotRetryPolicy" />), not a loop inside the reader:
/// the attempt budget and the backoff are the shared
/// <see cref="TransientFailurePolicy" /> verdict, and the last attempt is
/// returned as-is.
/// </summary>
public class SessionSnapshotRetryPolicyTests
{
    [Test]
    public async Task ExecuteAsync_CompleteOnFirstAttempt_RunsOnceAndReturnsIt()
    {
        int calls = 0;

        string result = await SessionSnapshotRetryPolicy.ExecuteAsync(
            attempt: _ =>
            {
                calls++;
                return Task.FromResult("stable");
            },
            isComplete: _ => true,
            CancellationToken.None);

        await Assert.That(result).IsEqualTo("stable");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_NeverComplete_StopsAtSharedBudgetAndReturnsLastAttempt()
    {
        int calls = 0;

        int result = await SessionSnapshotRetryPolicy.ExecuteAsync(
            attempt: _ =>
            {
                calls++;
                return Task.FromResult(calls);
            },
            isComplete: _ => false,
            CancellationToken.None);

        // The budget is the shared verdict's, not a reader-private constant.
        await Assert.That(calls).IsEqualTo(TransientFailurePolicy.DefaultMaxAttempts);
        await Assert.That(result).IsEqualTo(TransientFailurePolicy.DefaultMaxAttempts);
    }

    [Test]
    public async Task ExecuteAsync_CompletesLate_ReturnsWinningAttempt()
    {
        int calls = 0;

        string result = await SessionSnapshotRetryPolicy.ExecuteAsync(
            attempt: _ =>
            {
                calls++;
                return Task.FromResult(calls < 2 ? "moving" : "settled");
            },
            isComplete: value => value == "settled",
            CancellationToken.None);

        await Assert.That(result).IsEqualTo("settled");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_UnretryableOutcome_ReturnsImmediately()
    {
        // A failure (e.g. an unbounded file) is complete on arrival: retrying
        // cannot fix it, so the decorator must not burn the budget on it.
        int calls = 0;

        string result = await SessionSnapshotRetryPolicy.ExecuteAsync(
            attempt: _ =>
            {
                calls++;
                return Task.FromResult("fatal");
            },
            isComplete: value => value == "fatal",
            CancellationToken.None);

        await Assert.That(result).IsEqualTo("fatal");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_CancelledToken_ThrowsWithoutRunning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int calls = 0;
        await Assert.That(async () =>
            await SessionSnapshotRetryPolicy.ExecuteAsync(
                attempt: _ =>
                {
                    calls++;
                    return Task.FromResult(0);
                },
                isComplete: _ => true,
                cts.Token)
        ).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task SessionFileReader_DeclaresNoRetryBudgetOfItsOwn()
    {
        // #1586 pin: the attempt cap and the first-retry delay belong to the
        // shared policy (TransientFailurePolicy), not to the reader — otherwise
        // the same knob is edited in two places and the copies quietly disagree
        // (#572 shape). Mirrors TransportRetryOwnershipRules for the MCP side.
        string[] banned = ["MaxSnapshotAttempts", "MaxAttempts", "FirstRetryDelay"];

        FieldInfo[] ownFields = typeof(SessionFileReader).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly);

        List<string> violations = ownFields
            .Where(f => banned.Contains(f.Name, StringComparer.Ordinal))
            .Select(f => f.Name)
            .ToList();

        await Assert.That(violations).IsEmpty();
    }
}
