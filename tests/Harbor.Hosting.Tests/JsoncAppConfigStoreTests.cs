// #3113: behavior test for the JSONC read path wired into JsonAppConfigStore.
// A config file carrying // comments, /* */ comments and a trailing comma must
// load through the real store (source-generated JsonTypeInfo, no reflection)
// and hand back the configured Values.

using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Hosting.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Hosting.Tests;

/// <summary>
///     AOT-safe metadata for the test config record — the same kind of
///     context a real app declares next to its own config type.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JsoncAppConfig))]
internal sealed partial class JsoncAppConfigContext : JsonSerializerContext;

/// <summary>
///     Minimal app config used by the JSONC store test. The directory is a
///     static override because <see cref="AppConfigBase.ConfigDirectory" /> is
///     a read-only virtual property; the test class is not-in-parallel so the
///     override cannot race.
/// </summary>
public sealed record JsoncAppConfig : AppConfigBase
{
    public static string TestDirectory = string.Empty;

    public override string AppId => "jsonc-test";
    public override string ConfigFileName => "jsonc-test.json";
    public override string ConfigDirectory => TestDirectory;

    public Dictionary<string, string>? Values { get; init; }
}

[NotInParallel("jsonc-app-config-store")]
public class JsoncAppConfigStoreTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "harbor-jsonc-config-tests-" + Guid.NewGuid().ToString("N"));

    [Test]
    public async Task Load_JsoncFileWithCommentsAndTrailingComma_ReturnsCorrectValues()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            JsoncAppConfig.TestDirectory = dir;
            string path = Path.Combine(dir, "jsonc-test.json");
            const string jsonc = """
                {
                  // the values the app boots with
                  "values": {
                    "provider": "kilocode", // free tier
                    "model": "tencent/hy3:free" /* block comment */
                  }, // trailing comma before the object closes
                }
                """;
            await File.WriteAllTextAsync(path, jsonc);

            var defaults = new JsoncAppConfig { Values = new Dictionary<string, string>() };
            var store = new JsonAppConfigStore<JsoncAppConfig>(
                defaults,
                NullLogger<JsonAppConfigStore<JsoncAppConfig>>.Instance,
                JsoncAppConfigContext.Default.JsoncAppConfig);

            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Values).IsNotNull();
            await Assert.That(result.Value.Values!["provider"]).IsEqualTo("kilocode");
            await Assert.That(result.Value.Values!["model"]).IsEqualTo("tencent/hy3:free");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_MalformedJsoncFile_StillReturnsFailure()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            JsoncAppConfig.TestDirectory = dir;
            string path = Path.Combine(dir, "jsonc-test.json");
            await File.WriteAllTextAsync(path, """{ "values": }""");

            var defaults = new JsoncAppConfig { Values = new Dictionary<string, string>() };
            var store = new JsonAppConfigStore<JsoncAppConfig>(
                defaults,
                NullLogger<JsonAppConfigStore<JsoncAppConfig>>.Instance,
                JsoncAppConfigContext.Default.JsoncAppConfig);

            var result = await store.LoadAsync();

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error.Contains("corrupt")).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
