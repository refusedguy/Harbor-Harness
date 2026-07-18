using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
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
///         flags whose <see cref="ProviderId" /> matches the config's id. <see cref="Apply" />
///         is therefore free to assume it only runs on its target provider and does
///         not need to re-check the id internally.
///     </para>
/// </remarks>
public interface IProviderCompatFlag
{
    /// <summary>The provider this quirk targets (informational; used for registration filtering).</summary>
    ProviderId ProviderId { get; }

    /// <summary>
    ///     Mutate the request payload in place to satisfy the provider's quirks.
    ///     Implementations MUST be idempotent and thread-safe (they may be called
    ///     concurrently across StreamAsync invocations on the same client).
    /// </summary>
    /// <param name="payload">The mutable JSON payload being built for the chat-completions request.</param>
    /// <param name="request">The originating <see cref="LlmRequest" /> (read-only context).</param>
    void Apply(Dictionary<string, object?> payload, LlmRequest request);
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
    public void Apply(Dictionary<string, object?> payload, LlmRequest request)
    {
        if (request.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("temperature");
        }
    }
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
    public void Apply(Dictionary<string, object?> payload, LlmRequest request)
    {
        if (!payload.ContainsKey("max_tokens") && !payload.ContainsKey("max_completion_tokens"))
        {
            payload["max_tokens"] = 4096;
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
