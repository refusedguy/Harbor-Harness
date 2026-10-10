namespace Harbor.Tui.E2E.Tests.Shrink;

/// <summary>
///     The UI actions a scripted sequence is built from.
/// </summary>
public enum UiSequenceAction
{
    Cancel,
    Approve,
    Commit,
}

/// <summary>
///     Fixed test-order lists come first: cancel, approve, commit, in that order.
///     The fixed list is the default path; the randomized generator
///     (<see cref="UiSequenceGenerator.GenerateRandomized" />) is opt-in.
/// </summary>
public static class UiSequence
{
    /// <summary>
    ///     The default UI order: cancel, then approve, then commit.
    /// </summary>
    public static readonly IReadOnlyList<UiSequenceAction> DefaultFixedOrder =
    [
        UiSequenceAction.Cancel,
        UiSequenceAction.Approve,
        UiSequenceAction.Commit,
    ];
}
