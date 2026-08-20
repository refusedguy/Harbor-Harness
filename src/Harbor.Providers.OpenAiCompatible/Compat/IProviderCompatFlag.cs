namespace Harbor.Providers.OpenAiCompatible.Compat;
/// <summary>
///     Strategy (GOF) for provider-specific request quirks.
/// </summary>
/// <remarks>
///     <para>
///         §OOP-002 (RESOLVED): previously provider-specific payload adjustments
///         (DeepSeek reasoner temperature removal, Groq max_tokens default, …)
///         were hardcoded inside <c>OpenAiCompatibleLlmClient.ApplyCompatFlags</c>
///         as a <c>switch (ProviderId.Value)</c>. That violated Open/Closed: every
///         new provider with quirks required editing the client. Each quirk is now
///         an <see cref="IProviderCompatFlag" /> implementation registered on
///         <see cref="ProviderConfig.Quirks" />, and the client simply iterates
///         the list without knowing what any individual quirk does.
///     </para>
///     <para>
///         <see cref="ProviderId" /> is informational — the registration code is
///         expected to populate <see cref="ProviderConfig.Quirks" /> with only the
///         flags whose <see cref="ProviderId" /> matches the config's id. <see cref="Write" />
///         is therefore free to assume it only runs on its target provider and does
///         not need to re-check the id internally.
///     </para>
/// </remarks>
public interface IProviderCompatFlag
{
    /// <summary>The provider this quirk targets (informational; used for registration filtering).</summary>
    public ProviderId ProviderId { get; }

    /// <summary>
    ///     Return true if the named standard property should be omitted from the JSON payload.
    /// </summary>
    /// <param name="propertyName">The JSON property name being considered.</param>
    /// <param name="request">The originating <see cref="LlmRequest" /> (read-only context).</param>
    /// <returns>True to skip writing this property.</returns>
    public bool IsPropertyOmitted(string propertyName, LlmRequest request);

    /// <summary>
    ///     Write any additional/compat-specific properties into the writer after standard ones.
    /// </summary>
    /// <param name="writer">The <see cref="Utf8JsonWriter" /> targeting the request body.</param>
    /// <param name="request">The originating <see cref="LlmRequest" /> (read-only context).</param>
    public void Write(Utf8JsonWriter writer, LlmRequest request);
}

/// <summary>
///     DeepSeek reasoner models do not accept a <c>temperature</c> field. Strips
///     it from the payload when the model name contains "reasoner".
/// </summary>
public sealed class DeepSeekReasonerCompatFlag : IProviderCompatFlag
{
    /// <inheritdoc />
    public ProviderId ProviderId { get; } = ProviderId.Create("deepseek");

    /// <inheritdoc />
    public bool IsPropertyOmitted(string propertyName, LlmRequest request)
    {
        if (propertyName == "temperature" && request.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, LlmRequest request) { }
}

/// <summary>
///     Groq rejects requests without a <c>max_tokens</c> field. Inserts a 4096
///     default when neither <c>max_tokens</c> nor <c>max_completion_tokens</c> is
///     already present.
/// </summary>
public sealed class GroqMaxTokensCompatFlag : IProviderCompatFlag
{
    /// <inheritdoc />
    public ProviderId ProviderId { get; } = ProviderId.Create("groq");

    /// <inheritdoc />
    public bool IsPropertyOmitted(string propertyName, LlmRequest request) => false;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, LlmRequest request)
    {
        if (!request.MaxOutputTokens.HasValue)
        {
            writer.WriteNumber("max_tokens", 4096);
        }
    }
}

/// <summary>
///     Catalog of all built-in <see cref="IProviderCompatFlag" /> implementations,
///     keyed by provider id. The registration code uses this to populate
///     <see cref="ProviderConfig.Quirks" /> at provider-load time.
/// </summary>
public static class ProviderCompatFlags
{
    private static readonly IProviderCompatFlag[] _all =
    {
        new DeepSeekReasonerCompatFlag(),
        new GroqMaxTokensCompatFlag()
    };

    /// <summary>
    ///     Return all known compat flags whose <see cref="IProviderCompatFlag.ProviderId" />
    ///     matches <paramref name="providerId" />, or <see langword="null" /> if none match.
    /// </summary>
    /// <param name="providerId">The provider id to filter by.</param>
    /// <returns>An array of matching flags, or <see langword="null" /> if no flags apply.</returns>
    public static IReadOnlyList<IProviderCompatFlag>? For(ProviderId providerId)
    {
        List<IProviderCompatFlag>? matches = null;
        for (int i = 0; i < _all.Length; i++)
        {
            if (_all[i].ProviderId == providerId)
            {
                matches ??= new List<IProviderCompatFlag>(1);
                matches.Add(_all[i]);
            }
        }
        return matches;
    }
}
