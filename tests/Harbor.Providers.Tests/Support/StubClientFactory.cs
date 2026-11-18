// StubClientFactory.cs — the one stub OpenAI-compatible client.
//
// The scaling gate and the payload-identity guard both needed the same client
// with a stub auth resolver and an empty model catalogue. It is built once
// here so the two suites cannot drift apart.

using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Providers.Tests;

internal static class StubClientFactory
{
    internal static OpenAiCompatibleLlmClient Stub() =>
        new(
            new HttpClient(),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            new TestAuthResolver(),
            FakeModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);
}
