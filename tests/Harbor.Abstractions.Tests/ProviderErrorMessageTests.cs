using Harbor.Abstractions.Events;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #259: provider wire-error blobs (multi-KB 429 JSON) must be bounded
///     before they reach renderers — every backend paints
///     <c>ErrorEvent.Message</c> inline, only CellForge collapses
///     <c>AgentErrorEvent</c> into a card.
/// </summary>
public class ProviderErrorMessageTests
{
    [Test]
    public async Task TruncateErrorBody_ShortBody_PassesThroughUntouched()
    {
        const string body = """{"error":{"code":429,"message":"slow down"}}""";
        await Assert.That(ProviderErrors.TruncateErrorBody(body)).IsEqualTo(body);
    }

    [Test]
    public async Task TruncateErrorBody_BoundaryLength_PassesThrough()
    {
        string body = new('x', ProviderErrors.MaxProviderErrorBodyChars);
        await Assert.That(ProviderErrors.TruncateErrorBody(body)).IsEqualTo(body);
    }

    [Test]
    public async Task TruncateErrorBody_LongBlob_HeadKeptWithMarker()
    {
        string body = new string('x', 5000);
        string truncated = ProviderErrors.TruncateErrorBody(body);

        await Assert.That(truncated.StartsWith(new string('x', ProviderErrors.MaxProviderErrorBodyChars))).IsTrue();
        await Assert.That(truncated).Contains("truncated");
        await Assert.That(truncated).Contains((body.Length - ProviderErrors.MaxProviderErrorBodyChars).ToString());
        await Assert.That(truncated.Length).IsLessThan(body.Length);
    }

    [Test]
    public async Task BuildProviderErrorMessage_ShortBody_KeepsPrefixAndBody()
    {
        string message = ProviderErrors.BuildProviderErrorMessage("OpenAI API", 429, """{"error":"slow"}""");

        await Assert.That(message).IsEqualTo("""OpenAI API error 429: {"error":"slow"}""");
    }

    [Test]
    public async Task BuildProviderErrorMessage_LongBody_BoundedWithMarker()
    {
        string message = ProviderErrors.BuildProviderErrorMessage("API", 429, new string('y', 8000));

        await Assert.That(message.StartsWith("API error 429: ")).IsTrue();
        await Assert.That(message).Contains("truncated");
        await Assert.That(message.Length).IsLessThan(2000);
    }
}
