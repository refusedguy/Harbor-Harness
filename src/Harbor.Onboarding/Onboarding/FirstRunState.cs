namespace Harbor.Application.Configuration;

/// <summary>
///     The first-run disclosure acknowledgement, kept separate from folder trust
///     (issue #2672, F13).
/// </summary>
/// <remarks>
///     <para>
///         Showing a workspace/security-policy disclosure is a fact about what
///         the user was told. It is not a statement that the workspace is
///         trusted, and it is not a place to keep credentials. This record
///         carries only the acknowledgement, the identity of what was shown
///         (workspace plus policy digest) and when it was shown. Trust stays
///         in the folder-trust ruleset; secrets stay in the auth store.
///     </para>
///     <para>
///         The policy digest is part of the acknowledgement's identity. When
///         the policy changes, the old acknowledgement describes something the
///         user was never told, so a fresh disclosure is owed even though the
///         workspace path is unchanged. <see cref="NeedsDisclosure" /> is the
///         one place that decides this.
///     </para>
/// </remarks>
public sealed record FirstRunState
{
    /// <summary>Schema version of the acknowledgement shape.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     The state of a user who has not been shown anything. It claims no
    ///     workspace and no policy digest, so every caller owes a disclosure.
    /// </summary>
    public static FirstRunState NotAcknowledged { get; } = new();

    /// <summary>Schema version carried by this state.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Canonical workspace path the disclosure was shown for.</summary>
    public string WorkspacePath { get; init; } = string.Empty;

    /// <summary>Digest of the security policy the disclosure described.</summary>
    public string SecurityPolicyDigest { get; init; } = string.Empty;

    /// <summary>Only the fact that the disclosure was shown is recorded here.</summary>
    public bool DisclosureAcknowledged { get; init; }

    /// <summary>When the disclosure was acknowledged, in UTC.</summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }

    /// <summary>
    ///     Whether a caller must show the disclosure before proceeding.
    /// </summary>
    /// <param name="workspacePath">Canonical path of the workspace being opened.</param>
    /// <param name="securityPolicyDigest">Digest of the policy that would be shown.</param>
    /// <returns><c>true</c> until this exact pair has been acknowledged.</returns>
    public bool NeedsDisclosure(string workspacePath, string securityPolicyDigest) =>
        SchemaVersion != CurrentSchemaVersion ||
        !DisclosureAcknowledged ||
        !string.Equals(WorkspacePath, workspacePath ?? string.Empty, StringComparison.Ordinal) ||
        !string.Equals(
            SecurityPolicyDigest,
            securityPolicyDigest ?? string.Empty,
            StringComparison.Ordinal);

    /// <summary>
    ///     Return the acknowledged state for a workspace/policy pair. The
    ///     original is untouched. An acknowledgement is a new fact, not a
    ///     mutation of the state that was inspected.
    /// </summary>
    /// <param name="workspacePath">Canonical path of the workspace that was shown.</param>
    /// <param name="securityPolicyDigest">Digest of the policy that was shown.</param>
    /// <param name="acknowledgedAtUtc">Acknowledgement time; defaults to now in UTC.</param>
    /// <returns>The acknowledged state.</returns>
    public FirstRunState Acknowledge(
        string workspacePath,
        string securityPolicyDigest,
        DateTimeOffset? acknowledgedAtUtc = null) =>
        this with
        {
            SchemaVersion = CurrentSchemaVersion,
            WorkspacePath = workspacePath ?? string.Empty,
            SecurityPolicyDigest = securityPolicyDigest ?? string.Empty,
            DisclosureAcknowledged = true,
            AcknowledgedAtUtc = acknowledgedAtUtc ?? DateTimeOffset.UtcNow
        };
}
