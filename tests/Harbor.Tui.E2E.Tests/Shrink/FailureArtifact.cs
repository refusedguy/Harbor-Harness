namespace Harbor.Tui.E2E.Tests.Shrink;

/// <summary>
///     What a failed shrink run leaves behind. Every field is required: the
///     seed alone is insufficient to reproduce a run.
/// </summary>
public sealed record FailureArtifact(
    int Seed,
    string FullSequence,
    string GeneratorVersion,
    string FailureSignature,
    string CommitSha)
{
    public static FailureArtifact Create(
        int seed,
        IReadOnlyList<UiSequenceAction> fullSequence,
        string generatorVersion,
        string failureSignature,
        string commitSha)
    {
        ArgumentNullException.ThrowIfNull(fullSequence);
        if (fullSequence.Count == 0)
        {
            throw new ArgumentException("Full sequence must not be empty.", nameof(fullSequence));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(generatorVersion, nameof(generatorVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(failureSignature, nameof(failureSignature));
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha, nameof(commitSha));

        return new FailureArtifact(
            seed,
            string.Join(",", fullSequence),
            generatorVersion,
            failureSignature,
            commitSha);
    }
}
