using Harbor.Abstractions.Models;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.OpenAiCompatible.Compat;
namespace Harbor.Providers.Tests;
/// <summary>
///     #195 (immutability batch): ProviderConfig is immutable after load —
///     init-only properties, read-only collection views over snapshots,
///     Result-returning <see cref="ProviderConfig.Create" />, and
///     <see cref="ProviderConfig.WithQuirks" /> instead of post-registration mutation.
/// </summary>
public class ProviderConfigImmutabilityTests
{
    [Test]
    public async Task Create_MissingId_ReturnsFailure()
    {
        var result = ProviderConfig.Create("", "https://api.example.com");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("missing 'id'");
    }

    [Test]
    public async Task Create_MissingBaseUrl_ReturnsFailure()
    {
        var result = ProviderConfig.Create("test", "");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("missing 'baseUrl'");
    }

    [Test]
    public async Task Create_Valid_SnapshotsCallerCollections()
    {
        var models = new List<ModelInfo>
        {
            new("m1", "p", "M1", 4096, 1024, false, false, true, Pricing.Unknown, "openai")
        };
        var headers = new Dictionary<string, string> { ["X-A"] = "1" };

        var result = ProviderConfig.Create("p", "https://api.example.com", models: models, headers: headers);

        await Assert.That(result.IsSuccess).IsTrue();
        models.Add(new("m2", "p", "M2", 4096, 1024, false, false, true, Pricing.Unknown, "openai"));
        headers["X-B"] = "2";
        await Assert.That(result.Value.Models!.Count).IsEqualTo(1);
        await Assert.That(result.Value.Headers!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LoadFromJson_HeadersAndCapabilities_DeserializeAsReadOnly()
    {
        string json = """
                      {
                        "id": "k",
                        "baseUrl": "https://api.example.com",
                        "headers": { "X-Title": "Harbor" },
                        "capabilities": { "streaming": "true" }
                      }
                      """;

        var result = ProviderConfig.LoadFromJson(json);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Headers!["X-Title"]).IsEqualTo("Harbor");
        await Assert.That(result.Value.Capabilities!["streaming"]).IsEqualTo("true");
    }

    [Test]
    public async Task WithQuirks_ReturnsNewInstance_OriginalUnchanged()
    {
        var config = new ProviderConfig { Id = "deepseek", BaseUrl = "https://api.deepseek.com" };
        var quirks = new List<IProviderCompatFlag> { new DeepSeekReasonerCompatFlag() };

        var withQuirks = config.WithQuirks(quirks);

        await Assert.That(config.Quirks is null).IsTrue();
        await Assert.That(withQuirks.Quirks!.Count).IsEqualTo(1);
        await Assert.That(withQuirks.Id).IsEqualTo("deepseek");
    }
}
