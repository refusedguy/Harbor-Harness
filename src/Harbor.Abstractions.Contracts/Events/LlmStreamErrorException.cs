namespace Harbor.Abstractions.Events;

/// <summary>
///     Internal signal that the provider stream reported a terminal error
///     event. Carries the user-facing message plus the transport error
///     classification (<see cref="ProviderErrorKind" />, ROP-A ПР.5) so
///     <c>RetryPolicy.IsTransient</c> can retry rate limits / server errors /
///     timeouts / network blips.
/// </summary>
/// <remarks>
///     <para>
///         <b>ROP-C П.6:</b> this exception used to be nested inside
///         <c>Harbor.Core.Agents.AgentLoop</c>, which forced the Resilience
///         layer to reach back into the Agents namespace just to classify a
///         transport failure — an inverted dependency. It now lives beside
///         <see cref="ErrorEvent" /> and <see cref="ProviderErrorKind" />, the
///         contracts it classifies, so both producers (providers/loop) and the
///         consumer (retry policy) depend on Abstractions only.
///     </para>
/// </remarks>
public sealed class LlmStreamErrorException : Exception
{
    /// <summary>Transport classification of the failure (Unknown for legacy call sites).</summary>
    public ProviderErrorKind Kind { get; }

    /// <summary>HTTP status code when the failure came from a non-success response.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates the error from the terminal stream event.</summary>
    /// <param name="err">The error event reported by the provider stream.</param>
    public LlmStreamErrorException(ErrorEvent err)
        : base(err.Message)
    {
        Kind = err.Kind;
        StatusCode = err.StatusCode;
    }

    /// <summary>Creates the error with the user-facing failure message.</summary>
    /// <param name="message">The message reported by the provider stream.</param>
    public LlmStreamErrorException(string message) : base(message)
    {
    }

    /// <summary>Creates the error with a message and an inner cause.</summary>
    /// <param name="message">The message reported by the provider stream.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public LlmStreamErrorException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the error for deserialization paths.</summary>
    public LlmStreamErrorException()
    {
    }
}
