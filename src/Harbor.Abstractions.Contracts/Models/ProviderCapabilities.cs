namespace Harbor.Abstractions.Models;

/// <summary>
///     Declared capabilities of one provider adapter (issue #2673, D1).
/// </summary>
/// <remarks>
///     <para>
///         Callers that currently branch on a provider name need one typed
///         answer to "can this provider do X". This value is that answer. It
///         is data only: reading it from <c>providers/*.json</c> and consuming
///         it in the agent loop are separate slices.
///     </para>
///     <para>
///         Every flag defaults to false. An adapter that has not declared a
///         capability must be treated as not supporting it, so an unknown
///         provider degrades safely instead of promising a feature it cannot
///         deliver.
///     </para>
///     <para>
///         <see cref="Intersect" /> applies the degradation policy: the
///         effective capability set is the common denominator of what the
///         caller wants and what the provider can do. The version survives
///         only when both sides report the same one.
///     </para>
///     <para>
///         Pure BCL value, no serialization or I/O.
///     </para>
/// </remarks>
public sealed record ProviderCapabilities
{
    /// <summary>
    ///     Create a capability declaration. All flags default to false. A
    ///     blank version is stored as null, since blank text is not a version.
    /// </summary>
    /// <param name="supportsStreaming">Whether the adapter streams tokens.</param>
    /// <param name="supportsToolCalls">Whether the adapter emits tool calls.</param>
    /// <param name="supportsInterrupt">Whether an in-flight turn can be interrupted.</param>
    /// <param name="supportsSteer">Whether a running turn can be steered.</param>
    /// <param name="supportsNativeFork">Whether the adapter can fork a session natively.</param>
    /// <param name="supportsRollback">Whether the adapter can roll back a turn.</param>
    /// <param name="emitsTodoList">Whether the adapter emits native todo-list updates.</param>
    /// <param name="supportsPromptCaching">Whether the adapter supports prompt caching.</param>
    /// <param name="version">Version of the capability set, if known.</param>
    public ProviderCapabilities(
        bool supportsStreaming = false,
        bool supportsToolCalls = false,
        bool supportsInterrupt = false,
        bool supportsSteer = false,
        bool supportsNativeFork = false,
        bool supportsRollback = false,
        bool emitsTodoList = false,
        bool supportsPromptCaching = false,
        string? version = null)
    {
        SupportsStreaming = supportsStreaming;
        SupportsToolCalls = supportsToolCalls;
        SupportsInterrupt = supportsInterrupt;
        SupportsSteer = supportsSteer;
        SupportsNativeFork = supportsNativeFork;
        SupportsRollback = supportsRollback;
        EmitsTodoList = emitsTodoList;
        SupportsPromptCaching = supportsPromptCaching;
        Version = string.IsNullOrWhiteSpace(version) ? null : version;
    }

    /// <summary>Whether the adapter streams tokens.</summary>
    public bool SupportsStreaming { get; }

    /// <summary>Whether the adapter emits tool calls.</summary>
    public bool SupportsToolCalls { get; }

    /// <summary>Whether an in-flight turn can be interrupted.</summary>
    public bool SupportsInterrupt { get; }

    /// <summary>Whether a running turn can be steered.</summary>
    public bool SupportsSteer { get; }

    /// <summary>Whether the adapter can fork a session natively.</summary>
    public bool SupportsNativeFork { get; }

    /// <summary>Whether the adapter can roll back a turn.</summary>
    public bool SupportsRollback { get; }

    /// <summary>Whether the adapter emits native todo-list updates.</summary>
    public bool EmitsTodoList { get; }

    /// <summary>Whether the adapter supports prompt caching.</summary>
    public bool SupportsPromptCaching { get; }

    /// <summary>Version of the capability set, or null when unknown.</summary>
    public string? Version { get; }

    /// <summary>True when no capability and no version were recorded.</summary>
    public bool IsEmpty =>
        !SupportsStreaming &&
        !SupportsToolCalls &&
        !SupportsInterrupt &&
        !SupportsSteer &&
        !SupportsNativeFork &&
        !SupportsRollback &&
        !EmitsTodoList &&
        !SupportsPromptCaching &&
        Version is null;

    /// <summary>
    ///     Degrade to the capabilities both sides support. Each flag is the
    ///     logical AND of the two declarations. The version survives only when
    ///     both sides report the same one, or when only one side reports it.
    /// </summary>
    /// <param name="other">The provider-side declaration to intersect with.</param>
    /// <returns>The effective capability set.</returns>
    public ProviderCapabilities Intersect(ProviderCapabilities other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new ProviderCapabilities(
            supportsStreaming: SupportsStreaming && other.SupportsStreaming,
            supportsToolCalls: SupportsToolCalls && other.SupportsToolCalls,
            supportsInterrupt: SupportsInterrupt && other.SupportsInterrupt,
            supportsSteer: SupportsSteer && other.SupportsSteer,
            supportsNativeFork: SupportsNativeFork && other.SupportsNativeFork,
            supportsRollback: SupportsRollback && other.SupportsRollback,
            emitsTodoList: EmitsTodoList && other.EmitsTodoList,
            supportsPromptCaching: SupportsPromptCaching && other.SupportsPromptCaching,
            version: IntersectVersion(other.Version));
    }

    private string? IntersectVersion(string? other)
    {
        if (Version is null)
        {
            return other;
        }

        if (other is null)
        {
            return Version;
        }

        return string.Equals(Version, other, StringComparison.Ordinal) ? Version : null;
    }
}
