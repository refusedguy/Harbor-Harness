namespace Harbor.Application.Resilience;

/// <summary>
///     A provider-stream failure, as a VALUE (#1514).
/// </summary>
/// <remarks>
///     <para>
///         The transport failure used to reach the retry policy as an
///         <c>LlmStreamErrorException</c> thrown by the stream consumer: an
///         expected provider failure (429, 5xx, a timeout) rode as an
///         exception so the policy could catch it and decide whether another
///         attempt was worth it. The failure is a value here, so the retry
///         decision is a READ of the classification instead of a catch, and
///         nothing throws across the attempt boundary for an expected
///         provider failure.
///     </para>
///     <para>
///         The three fields are the ones the exception carried: the
///         classification the verdict is made from, the user-facing message,
///         and the bounded provider body kept for diagnostics.
///     </para>
/// </remarks>
/// <param name="Kind">Transport classification — the retry verdict reads it.</param>
/// <param name="Message">The user-facing failure message.</param>
/// <param name="Details">The provider body / diagnostics, when there is one.</param>
public sealed record StreamFailure(ProviderErrorKind Kind, string Message, string? Details)
{
    /// <summary>
    ///     The failure of a provider's terminal error event, as a value.
    /// </summary>
    /// <param name="err">The error event the provider stream reported.</param>
    public static StreamFailure From(ErrorEvent err) => new(err.Kind, err.Message, err.Exception);
}
