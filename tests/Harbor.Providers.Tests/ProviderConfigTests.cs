using System.Net;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Providers.Tests;
/// <summary>
///     Tests for ProviderConfig.LoadFromFile — JSON parsing and validation.
/// </summary>
public class ProviderConfigTests
{
    private static string WriteTempJson(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-provider-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task LoadFromFile_ValidJson_ReturnsSuccess()
    {
        string json = """
                      {
                        "id": "openrouter",
                        "displayName": "OpenRouter",
                        "description": "Multi-provider router",
                        "baseUrl": "https://openrouter.ai/api/v1",
                        "apiType": "openai-compatible",
                        "authType": "bearer",
                        "authEnvVar": "OPENROUTER_API_KEY",
                        "modelsUrl": "https://openrouter.ai/api/v1/models",
                        "modelsRefreshHours": 24,
                        "modelsPath": "data",
                        "timeout": 120,
                        // #3838: deliberately left in the fixture to prove the
                        // source-gen context ignores this dead key. The bundled
                        // providers/*.json no longer carry it.
                        "retries": 3
                      }
                      """;
        string path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Id).IsEqualTo("openrouter");
            await Assert.That(result.Value.DisplayName).IsEqualTo("OpenRouter");
            await Assert.That(result.Value.BaseUrl).IsEqualTo("https://openrouter.ai/api/v1");
            await Assert.That(result.Value.AuthEnvVar).IsEqualTo("OPENROUTER_API_KEY");
            await Assert.That(result.Value.ModelsRefreshHours).IsEqualTo(24);
            await Assert.That(result.Value.Timeout).IsEqualTo(120);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     #3838: "retries" is a dead key. ProviderConfig has no Retries
    ///     property, the source-gen context ignores unknown members, and retry
    ///     is owned by RetryLlmClientWiring.WithTransientRetry — not by JSON.
    ///     Removing the key from the bundled providers/*.json must therefore be
    ///     a no-op: the deserialized config (and everything the retry wiring
    ///     reads from it) is identical with and without it.
    /// </summary>
    [Test]
    public async Task LoadFromJson_DeadRetriesKey_DoesNotChangeConfig()
    {
        // One payload, with the dead key injected by a single-line replace: no
        // second copy of the JSON for the duplication gate to flag.
        const string withoutRetries = """
                                      {
                                        "id": "kilocode",
                                        "displayName": "Kilo Code Gateway",
                                        "baseUrl": "https://api.kilo.ai/api/gateway",
                                        "authType": "bearer",
                                        "authEnvVar": "KILO_API_KEY",
                                        "modelsRefreshHours": 12,
                                        "timeout": 120
                                      }
                                      """;
        string withRetries = withoutRetries.Replace(
            "\"timeout\": 120",
            "\"timeout\": 120,\n  \"retries\": 3");

        var withResult = ProviderConfig.LoadFromJson(withRetries);
        var withoutResult = ProviderConfig.LoadFromJson(withoutRetries);

        await Assert.That(withResult).IsSuccessful();
        await Assert.That(withoutResult).IsSuccessful();

        var withConfig = withResult.Value;
        var withoutConfig = withoutResult.Value;

        await Assert.That(withConfig.Id).IsEqualTo(withoutConfig.Id);
        await Assert.That(withConfig.DisplayName).IsEqualTo(withoutConfig.DisplayName);
        await Assert.That(withConfig.Description).IsEqualTo(withoutConfig.Description);
        await Assert.That(withConfig.BaseUrl).IsEqualTo(withoutConfig.BaseUrl);
        await Assert.That(withConfig.ApiType).IsEqualTo(withoutConfig.ApiType);
        await Assert.That(withConfig.ApiVersion).IsNull();
        await Assert.That(withoutConfig.ApiVersion).IsNull();
        await Assert.That(withConfig.AuthType).IsEqualTo(withoutConfig.AuthType);
        await Assert.That(withConfig.AuthHeader).IsNull();
        await Assert.That(withoutConfig.AuthHeader).IsNull();
        await Assert.That(withConfig.AuthEnvVar).IsEqualTo(withoutConfig.AuthEnvVar);
        await Assert.That(withConfig.ModelsUrl).IsNull();
        await Assert.That(withoutConfig.ModelsUrl).IsNull();
        await Assert.That(withConfig.ModelsRefreshHours).IsEqualTo(withoutConfig.ModelsRefreshHours);
        await Assert.That(withConfig.ModelsPath).IsNull();
        await Assert.That(withoutConfig.ModelsPath).IsNull();
        await Assert.That(withConfig.Timeout).IsEqualTo(withoutConfig.Timeout);
        await Assert.That(withConfig.ModelMapping).IsNull();
        await Assert.That(withoutConfig.ModelMapping).IsNull();
        await Assert.That(withConfig.Models).IsNull();
        await Assert.That(withoutConfig.Models).IsNull();
        await Assert.That(withConfig.Headers).IsNull();
        await Assert.That(withoutConfig.Headers).IsNull();
        await Assert.That(withConfig.Capabilities).IsNull();
        await Assert.That(withoutConfig.Capabilities).IsNull();
        await Assert.That(withConfig.Quirks).IsNull();
        await Assert.That(withoutConfig.Quirks).IsNull();
    }

    [Test]
    public async Task LoadFromFile_AllowsCommentsAndTrailingCommas()
    {
        // JsonOptions has AllowTrailingCommas=true and ReadCommentHandling=Skip.
        string json = """
                      {
                        // This is a comment
                        "id": "deepseek",
                        "displayName": "DeepSeek",
                        "baseUrl": "https://api.deepseek.com/v1",
                        "authEnvVar": "DEEPSEEK_API_KEY",
                      }
                      """;
        string path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);
            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Id).IsEqualTo("deepseek");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_InvalidJson_ReturnsFailure()
    {
        string path = WriteTempJson("{ this is not valid json ]");
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("Failed to load provider config");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_MissingId_ReturnsFailure()
    {
        string json = """
                      {
                        "displayName": "NoId",
                        "baseUrl": "https://api.example.com"
                      }
                      """;
        string path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("missing 'id'");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_MissingBaseUrl_ReturnsFailure()
    {
        string json = """
                      {
                        "id": "test",
                        "displayName": "NoBaseUrl"
                      }
                      """;
        string path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("missing 'baseUrl'");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_NonexistentPath_ReturnsFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-nonexistent-{Guid.NewGuid():N}.json");
        var result = ProviderConfig.LoadFromFile(path);

        await Assert.That(result).IsFailed();
    }

    [Test]
    public async Task GetProviderId_ReturnsCreatedProviderId()
    {
        var config = new ProviderConfig
        {
            Id = "groq",
            DisplayName = "Groq",
            BaseUrl = "https://api.groq.com/openai/v1"
        };
        var pid = config.GetProviderId();

        await Assert.That(pid.Value).IsEqualTo("groq");
    }
}

/// <summary>
///     Tests for EnvVarAuthResolver — verifies env var lookup, overrides, and provider-id normalization.
/// </summary>
/// <remarks>
///     #823, and a correction to the issue while we are here: it names
///     <c>ProviderConfigTests</c> as a writer of the process environment at lines
///     193-255. Those lines are in THIS class. <c>ProviderConfigTests</c> above
///     ends at line 168 and writes nothing — it is JSON-file parsing throughout.
///     The four variables below are all names this file invents
///     (<c>TESTPROVIDER_API_KEY</c>, <c>KILO_CODE_API_KEY</c>,
///     <c>MISSINGPROVIDER_API_KEY</c>, <c>EMPTYPROVIDER_API_KEY</c>), and the real
///     kilocode variable is <c>KILO_API_KEY</c>, so no product surface reads them.
///     That makes this the one case in #823 with no co-victim in its assembly —
///     the key below excludes nobody today and exists so the next writer or reader
///     of these names does not have to rediscover that.
///     <para>
///         #870, seven writes and zero reads. Three of the four tests that write
///         here did write <c>null</c> back, which reads like a teardown and
///         restores nothing — <c>null</c> is a value the class never had. The
///         fourth, <c>ResolveApiKeyAsync_FailsWhenNeitherOverrideNorEnvVar</c>, had no
///         teardown at all, so its pin outlived even the test that made it. All four
///         now read the ambient value and hand that identifier back instead.
///     </para>
///     <para>
///         They do it inline rather than through a class-level drain because every
///         write goes through an <c>envName</c> local holding a DIFFERENT provider per
///         test, so there is no set of names to name up front. A wrapper method would
///         not help either: the call site is the only place the value that was read
///         is in scope, and hiding the write behind a helper is the very indirection
///         that made this defect invisible to grep in the first place.
///     </para>
/// </remarks>
[NotInParallel("process-env")]
public class EnvVarAuthResolverTests
{
    [Test]
    public async Task ResolveApiKeyAsync_OverrideTakesPriority()
    {
        var resolver = new EnvVarAuthResolver(new Dictionary<string, string>
        {
            ["anthropic"] = "override-key-123"
        });

        var result = await resolver.ResolveApiKeyAsync("anthropic");

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value).IsEqualTo("override-key-123");
    }

    [Test]
    public async Task ResolveApiKeyAsync_EnvVarUsedWhenNoOverride()
    {
        string envName = "TESTPROVIDER_API_KEY";
        // #847: read before pinning, hand back after. The finally used to write
        // `null`, which is a value this test never had.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "env-key-456");
        try
        {
            var resolver = new EnvVarAuthResolver();
            var result = await resolver.ResolveApiKeyAsync("testprovider");

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value).IsEqualTo("env-key-456");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_NormalizesDashesToUnderscores()
    {
        // Provider id "kilo-code" should map to env var "KILO_CODE_API_KEY".
        string envName = "KILO_CODE_API_KEY";
        // #847: KILO_API_KEY is the real variable the product reads
        // (providers/kilocode.json), and this one is its dash-normalised twin.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "kilo-key");
        try
        {
            var resolver = new EnvVarAuthResolver();
            var result = await resolver.ResolveApiKeyAsync("kilo-code");

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value).IsEqualTo("kilo-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_FailsWhenNeitherOverrideNorEnvVar()
    {
        string envName = "MISSINGPROVIDER_API_KEY";
        // #847: this one had no teardown at all, so the pin outlived the test that
        // made it. Reading first and restoring in a finally is what makes the pin
        // safe to write; the test is unchanged by either.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            var resolver = new EnvVarAuthResolver();

            var result = await resolver.ResolveApiKeyAsync("missingprovider");

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("MISSINGPROVIDER_API_KEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_EmptyEnvVar_Fails()
    {
        string envName = "EMPTYPROVIDER_API_KEY";
        // #847: pinned to the empty string, restored to `null` — two different
        // values, and only the first was ever read.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "");
        try
        {
            var resolver = new EnvVarAuthResolver();
            var result = await resolver.ResolveApiKeyAsync("emptyprovider");

            await Assert.That(result).IsFailed();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_ErrorMentionsEnvVarName()
    {
        var resolver = new EnvVarAuthResolver();
        var result = await resolver.ResolveApiKeyAsync("neuro-flash");

        await Assert.That(result).IsFailed();
        await Assert.That(result.Error).Contains("NEURO_FLASH_API_KEY");
    }
}

/// <summary>
///     Tests for DynamicModelCatalog — verifies parsing of fetched responses
///     using a stub HttpMessageHandler (no real network).
/// </summary>
public class DynamicModelCatalogTests
{
    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), $"harbor-catalog-{Guid.NewGuid():N}");

    [Test]
    public async Task GetModelsAsync_HardcodedModels_ReturnedDirectly()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "static",
                DisplayName = "Static",
                BaseUrl = "https://api.example.com",
                Models = new List<ModelInfo>
                {
                    new("m1", "static", "Model 1", 8192, 4096, false, false, true, Pricing.Unknown, "openai"),
                    new("m2", "static", "Model 2", 32768, 8192, false, true, true, Pricing.Unknown, "openai")
                }
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Id).IsEqualTo("m1");
            await Assert.That(result.Value[1].Id).IsEqualTo("m2");
            // Should not have made any HTTP calls when models are hardcoded.
            await Assert.That(handler.CapturedRequests.Count).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_NoUrlNoModels_ReturnsFailure()
    {
        var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "empty",
                DisplayName = "Empty",
                BaseUrl = "https://api.example.com"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("no modelsUrl");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_ParsesDataArray()
    {
        // Standard OpenAI-compatible /v1/models response shape.
        string json = """
                      {
                        "data": [
                          { "id": "gpt-4o", "name": "GPT-4o", "context_length": 128000 },
                          { "id": "gpt-4o-mini", "name": "GPT-4o mini", "context_length": 128000 }
                        ]
                      }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "openrouter",
                DisplayName = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                ModelsUrl = "https://openrouter.ai/api/v1/models"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Id).IsEqualTo("gpt-4o");
            await Assert.That(result.Value[0].DisplayName).IsEqualTo("GPT-4o");
            await Assert.That(result.Value[0].ContextWindow).IsEqualTo(128_000);
            await Assert.That(result.Value[1].Id).IsEqualTo("gpt-4o-mini");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    /// <summary>
    ///     One catalog fetch, with the HTTP stub and the throwaway cache the
    ///     /models tests all need. Only the wire JSON and the config vary.
    /// </summary>
    private static async Task<Result<IReadOnlyList<ModelInfo>>> FetchModels(string json, ProviderConfig config)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            return await catalog.GetModelsAsync(config);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    /// <summary>
    ///     Regression: a /models entry without context_length must not be given
    ///     a fabricated 4096 window. That fake value looked authoritative to
    ///     TokenTracker, whose guard (window &lt;= reserve) silently disabled
    ///     compaction for the whole session. The unknown sentinel is 0, which
    ///     the tracker reports at Warning and refuses to compact.
    /// </summary>
    [Test]
    public async Task GetModelsAsync_MissingContextLength_UsesUnknownWindowSentinel()
    {
        var config = new ProviderConfig
        {
            Id = "test-provider",
            DisplayName = "Test",
            BaseUrl = "https://api.example.com",
            ModelsUrl = "https://api.example.com/v1/models",
            ModelsPath = "data"
        };
        var result = await FetchModels(
            """
            {
              "data": [
                { "id": "contextless", "name": "Contextless Model" }
              ]
            }
            """,
            config);

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Id).IsEqualTo("contextless");
        // 0 = "window not reported", never a fabricated 4096.
        await Assert.That(result.Value[0].ContextWindow).IsEqualTo(0);
    }

    [Test]
    public async Task GetModelsAsync_ParsesModelsArray_WhenNoDataField()
    {
        string json = """
                      {
                        "models": [
                          { "id": "llama3.2", "name": "Llama 3.2", "context_length": 4096 }
                        ]
                      }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "ollama",
                DisplayName = "Ollama",
                BaseUrl = "http://localhost:11434",
                ModelsUrl = "http://localhost:11434/api/tags"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].Id).IsEqualTo("llama3.2");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_ParsesModelsPath_NestedProperty()
    {
        // ProviderConfig.ModelsPath "data" — like OpenRouter.
        var result = await FetchModels(
            """
            {
              "data": [
                { "id": "anthropic/claude-3.5-sonnet", "name": "Claude 3.5 Sonnet", "context_length": 200000 }
              ]
            }
            """,
            new ProviderConfig
            {
                Id = "openrouter",
                DisplayName = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                ModelsUrl = "https://openrouter.ai/api/v1/models",
                ModelsPath = "data"
            });

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Id).IsEqualTo("anthropic/claude-3.5-sonnet");
        await Assert.That(result.Value[0].ContextWindow).IsEqualTo(200_000);
    }

    [Test]
    public async Task GetModelsAsync_UsesModelMapping_ForCustomFieldNames()
    {
        // Ollama-style: top-level "models" array, name field, context_length.
        string json = """
                      {
                        "models": [
                          {
                            "name": "llama3.2:latest",
                            "model_info": { "context_length": 4096 }
                          }
                        ]
                      }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "ollama",
                DisplayName = "Ollama",
                BaseUrl = "http://localhost:11434",
                ModelsUrl = "http://localhost:11434/api/tags",
                ModelMapping = new ModelMapping
                {
                    Id = "name",
                    DisplayName = "name",
                    ContextWindow = "model_info.context_length"
                }
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].Id).IsEqualTo("llama3.2:latest");
            await Assert.That(result.Value[0].ContextWindow).IsEqualTo(4096);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_HttpFailure_ReturnsFailure()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error", Encoding.UTF8)
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "broken",
                DisplayName = "Broken",
                BaseUrl = "https://api.broken.com",
                ModelsUrl = "https://api.broken.com/v1/models"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsFailed();
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_PersistsCacheFile()
    {
        string json = """
                      { "data": [ { "id": "m1", "name": "M1", "context_length": 8192 } ] }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "cachedprov",
                DisplayName = "Cached",
                BaseUrl = "https://api.example.com",
                ModelsUrl = "https://api.example.com/models"
            };

            var first = await catalog.GetModelsAsync(config);
            await Assert.That(first).IsSuccessful();

            string cacheFile = Path.Combine(cacheDir, "cachedprov.json");
            await Assert.That(File.Exists(cacheFile)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_EntryWithoutId_IsSkippedAndCounted()
    {
        // #1581 partial skip: the catalog keeps the parsable entries, the
        // corrupted one is dropped — but the skip is accounted, not silent.
        string json = """
                      { "data": [
                          { "name": "no id here", "context_length": 8192 },
                          { "id": "good-model", "name": "Good", "context_length": 8192 }
                        ] }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "partialskip",
                DisplayName = "PartialSkip",
                BaseUrl = "https://api.example.com",
                ModelsUrl = "https://api.example.com/models"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].Id).IsEqualTo("good-model");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_AllEntriesWithoutId_ReturnsFailureWithSkipReasons()
    {
        // #1581: when every entry is skipped, the old code returned an empty
        // Success and the corridor below reported "Model 'x' not found in
        // provider catalog" — the real cause had nowhere to go. The skip list
        // now rides the Result's error.
        string json = """
                      { "data": [
                          { "name": "no id", "context_length": 8192 },
                          { "name": "still no id", "context_length": 4096 }
                        ] }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "allskip",
                DisplayName = "AllSkip",
                BaseUrl = "https://api.example.com",
                ModelsUrl = "https://api.example.com/models"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("all 2 model entries were skipped");
            await Assert.That(result.Error).Contains("no 'id'");
            await Assert.That(result.Error).Contains("[0]");
            await Assert.That(result.Error).Contains("[1]");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_MalformedEntryDoesNotAbortRemainingEntries()
    {
        // A bad-read entry (counted skip) must not poison the entries after it.
        string json = """
                      { "data": [
                          { "id": "first", "name": "First", "context_length": 8192 },
                          null,
                          { "id": "second", "name": "Second", "context_length": 4096 }
                        ] }
                      """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler);
        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "skipnull",
                DisplayName = "SkipNull",
                BaseUrl = "https://api.example.com",
                ModelsUrl = "https://api.example.com/models"
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Id).IsEqualTo("first");
            await Assert.That(result.Value[1].Id).IsEqualTo("second");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }
}
