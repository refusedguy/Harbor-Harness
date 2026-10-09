namespace Harbor.LoadTests;

/// <summary>
///     Skip unless explicitly opted in via <c>HARBOR_LOAD=1</c>.
///     Heavy shapes (50×1 per backend, 100×1) are a nightly/manual lane;
///     the default PR run executes only the fast matrix. Same self-skip
///     shape as the IPC event-stream tests (<c>HARBOR_IPC_EVENTSTREAM=1</c>).
/// </summary>
internal sealed class SkipUnlessHarborLoadAttribute : SkipAttribute
{
    public SkipUnlessHarborLoadAttribute() : base(
        "Heavy load shape: the default run executes only the fast matrix. "
        + "Set HARBOR_LOAD=1 to enable.") { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(Environment.GetEnvironmentVariable("HARBOR_LOAD") != "1");
}
