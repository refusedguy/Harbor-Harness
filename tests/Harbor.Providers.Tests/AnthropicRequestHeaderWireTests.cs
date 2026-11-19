using System.Net;
using System.Net.Http;
using Harbor.Abstractions.Models;
using Harbor.Providers.Anthropic;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #3773: the Anthropic request envelope and its three raw headers are
///     produced by the shared <c>ProviderHttp.PostJson</c> /
///     <c>ProviderAuth.ApplyApiKey</c> primitives, called by the real
///     <see cref="AnthropicRequestBuilder" /> (and through it the client). These
///     are behavior tests on the wire artifact, not probes of the primitives'
///     internals: a header that reaches the transport with a rewritten value, a
///     different casing or a missing entry is a provider 401/400 and must fail
///     here. The shape is pinned byte-for-byte against the pre-#3773 builder.
/// </summary>
public class AnthropicRequestHeaderWireTests
{
    private const string Url = "https://api.anthropic.com/v1";
    // Token-shaped with characters header validation would reject: proves the
    // value still goes on the wire verbatim through the unvalidated primitive.
    private const string ApiKey = "sk-ant-raw|key/with=chars";
    private const string Version = "2023-06-01";
    private const string Beta = "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14";

    private static LlmRequest Request() =>
        new("claude-opus-4", [LlmUserMessage.Text("hi")], "", []);

    private static string Header(HttpRequestMessage message, string name) =>
        message.Headers.GetValues(name).Single();

    [Test]
    public async Task Anthropic_BuildRequest_PinsTheWholeWireEnvelope()
    {
        using HttpRequestMessage msg = AnthropicRequestBuilder.BuildRequest(
            Request(), Url, ApiKey, Version, Beta);

        await Assert.That(msg.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(msg.RequestUri!.ToString()).IsEqualTo(Url + "/messages");
        await Assert.That(msg.Content!.Headers.ContentType!.MediaType!).IsEqualTo("application/json");
        await Assert.That(Header(msg, "x-api-key")).IsEqualTo(ApiKey);
        await Assert.That(Header(msg, "anthropic-version")).IsEqualTo(Version);
        await Assert.That(Header(msg, "anthropic-beta")).IsEqualTo(Beta);
        await Assert.That(msg.Headers.Count()).IsEqualTo(3)
            .Because("the builder must emit exactly the three Anthropic wire headers — "
                     + "no attempt to validate or canonicalize the raw values creeps in");
    }

    [Test]
    public async Task Anthropic_BuildRequest_DefaultsVersionAndBetaToTheDocumentedWireValues()
    {
        using HttpRequestMessage msg = AnthropicRequestBuilder.BuildRequest(
            Request(), Url, ApiKey, null, null);

        await Assert.That(Header(msg, "x-api-key")).IsEqualTo(ApiKey);
        await Assert.That(Header(msg, "anthropic-version")).IsEqualTo("2023-06-01");
        await Assert.That(Header(msg, "anthropic-beta")).IsEqualTo(
            "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14");
    }

    [Test]
    public async Task AnthropicClient_BuildRequest_CarriesConfigVersionAndBetaToTheWire()
    {
        var client = new AnthropicLlmClient(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new AnthropicConfig { BaseUrl = Url, ApiVersion = Version, BetaFeatures = Beta },
            new TestAuthResolver(ApiKey),
            NullLogger<AnthropicLlmClient>.Instance);

        using HttpRequestMessage msg = client.BuildRequest(Request(), ApiKey);

        await Assert.That(msg.RequestUri!.ToString()).IsEqualTo(Url + "/messages");
        await Assert.That(msg.Content!.Headers.ContentType!.MediaType!).IsEqualTo("application/json");
        await Assert.That(Header(msg, "x-api-key")).IsEqualTo(ApiKey);
        await Assert.That(Header(msg, "anthropic-version")).IsEqualTo(Version);
        await Assert.That(Header(msg, "anthropic-beta")).IsEqualTo(Beta);
    }
}
