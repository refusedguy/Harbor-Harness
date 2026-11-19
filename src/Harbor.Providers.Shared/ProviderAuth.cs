// Shared source, compiled INTO Harbor.Providers.Anthropic via a <Compile Include>
// link item (issue #3773). See ProviderHttp.cs for the linked-source rationale.
//
// AOT: no reflection, no dynamic code. The header's name and value come from the
// caller (API key, API version, beta list) — nothing provider-specific is
// hardcoded in this primitive.

using System.Net.Http;

namespace Harbor.Providers.Internal;

/// <summary>
///     Minimal request-header helper shared by provider request builders (#3773).
///     The caller supplies the wire header name and its value; this primitive owns
///     the one act of putting that pair on the request verbatim.
/// </summary>
internal static class ProviderAuth
{
    /// <summary>
    ///     Adds one provider header without validation. Raw values are necessary
    ///     here: API keys and beta lists are token-shaped and must reach the wire
    ///     exactly as resolved, so header parsing must not rewrite or reject them.
    /// </summary>
    public static void ApplyApiKey(HttpRequestMessage message, string header, string value) =>
        message.Headers.TryAddWithoutValidation(header, value);
}
