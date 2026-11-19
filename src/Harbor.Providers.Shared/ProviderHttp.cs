// Shared source, compiled INTO Harbor.Providers.Anthropic via a <Compile Include>
// link item (issue #3773). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the wire envelope helper
// travels as linked source instead of a shared assembly — the same shape as
// SsePump.cs / ProviderLlmClientBase.cs.
//
// AOT: no reflection, no dynamic code, no JSON serialization. The body bytes are
// produced by the caller with Utf8JsonWriter (§PERF-002); this helper only wraps
// them in the HTTP envelope. No HttpClient is constructed here — the client owns
// the one injected into it (HttpClientReuseRules).

using System.Net.Http;
using System.Net.Http.Headers;

namespace Harbor.Providers.Internal;

/// <summary>
///     Minimal HTTP envelope helper shared by provider request builders (#3773).
///     A provider renders its body to UTF-8 bytes and calls <see cref="PostJson" />;
///     the helper fixes POST + <c>ByteArrayContent</c> + the JSON content type in
///     one place so no builder hand-assembles that triplet again.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>
    ///     Wraps a pre-rendered JSON body in a POST request. The content type is
    ///     set on <c>Content.Headers</c>, never on the request headers, matching
    ///     the wire shape the builders produced before #3773 byte-for-byte.
    /// </summary>
    public static HttpRequestMessage PostJson(string url, byte[] body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return message;
    }
}
